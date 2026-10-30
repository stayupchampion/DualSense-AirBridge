"""Keep routine diagnostics off controller I/O; preserve critical log writes."""
import re
from backend_runtime_patch import one


def logger_header(s):
    s = one(s, '#include <fstream>', '#include <fstream>\n#include <chrono>\n#include <memory>\n#include "diagnostic_worker.hh"')
    s = one(s, '  void reopen();', '''  ~Logger();
  void reopen();
  // Info/Debug may be dropped under load. Warn/Error keep synchronous logging.
  bool log_diagnostic(LogScope scope, LogLevel level, std::string_view message);''')
    s = one(s, '  std::ofstream open_file() const;', '''  struct DiagnosticRecord {
    std::chrono::system_clock::time_point timestamp{};
    LogScope scope{};
    LogLevel level{};
    std::size_t size = 0;
    std::array<char, 4096> message{};
  };
  void log_at(LogScope scope, LogLevel level, std::string_view message,
              std::chrono::system_clock::time_point timestamp);
  std::ofstream open_file() const;''')
    return one(s, '  std::ofstream file_;', '''  std::ofstream file_;
  // Destroyed before file_/mutex_; the destructor explicitly drains it.
  std::unique_ptr<dsbridge::DiagnosticWorker<DiagnosticRecord, 256>> diagnostics_;''')


def logger_source(s):
    s = one(s, '#include <chrono>', '#include <chrono>\n#include <cstring>')
    s = one(s, '''std::string local_timestamp() {
  const auto now = std::chrono::system_clock::now();''',
            'std::string local_timestamp(std::chrono::system_clock::time_point now) {')
    s = one(s, 'Logger::Logger(const std::string &path) : path_(path) { file_ = open_file(); }', '''Logger::Logger(const std::string &path) : path_(path) {
  file_ = open_file();
  diagnostics_ = std::make_unique<dsbridge::DiagnosticWorker<DiagnosticRecord, 256>>(
      [this](const DiagnosticRecord& record) {
        log_at(record.scope, record.level,
               std::string_view(record.message.data(), record.size), record.timestamp);
      },
      [this](std::uint64_t dropped, std::uint64_t failed) {
        log(LogScope::Daemon, LogLevel::Warn,
            "diagnostic logging loss dropped=" + std::to_string(dropped) +
            " failed=" + std::to_string(failed));
      });
}

Logger::~Logger() { diagnostics_.reset(); }

bool Logger::log_diagnostic(LogScope scope, LogLevel level, std::string_view message) {
  if (level == LogLevel::Warn || level == LogLevel::Error) {
    log(scope, level, message);
    return true;
  }
  DiagnosticRecord record{};
  if (message.size() > record.message.size()) {
    diagnostics_->note_drop();
    return false;
  }
  record.timestamp = std::chrono::system_clock::now();
  record.scope = scope;
  record.level = level;
  record.size = message.size();
  if (!message.empty()) std::memcpy(record.message.data(), message.data(), message.size());
  return diagnostics_->try_submit(record);
}''')
    return one(s, '''void Logger::log(LogScope scope, LogLevel level, std::string_view message) {
  std::lock_guard guard(mutex_);
  file_ << local_timestamp()''', '''void Logger::log(LogScope scope, LogLevel level, std::string_view message) {
  log_at(scope, level, message, std::chrono::system_clock::now());
}

void Logger::log_at(LogScope scope, LogLevel level, std::string_view message,
                    std::chrono::system_clock::time_point timestamp) {
  std::lock_guard guard(mutex_);
  file_ << local_timestamp(timestamp)''')


def state_snapshot(expression, name, extra=()):
    """Only copy numeric state, not mutexes, audio queues or protocol builders."""
    expression = expression.replace('state.pending_audio_chunks.size()', f'{name}.pending')
    fields = sorted(set(re.findall(r'state\.(\w+)', expression)))
    declarations = [f'    decltype(BridgeState::{field}) {field}{{}};' for field in fields]
    assignments = [f'    {name}.{field} = state.{field};' for field in fields]
    expression = re.sub(r'state\.(\w+)', rf'{name}.\1', expression)
    if f'{name}.pending' in expression:
        declarations.append('    std::size_t pending = 0;')
        assignments.append(f'    {name}.pending = state.pending_audio_chunks.size();')
    for variable, kind in extra:
        declarations.append(f'    {kind} {variable}{{}};')
        assignments.append(f'    {name}.{variable} = {variable};')
        # Preserve log field names inside C++ string literals: parsers consume
        # dropped_delta= etc., not snapshot.dropped_delta=.
        expression = re.sub(r'"(?:\\.|[^"\\])*"|' + rf'\b{variable}\b',
                            lambda m: m[0] if m[0].startswith('"') else f'{name}.{variable}',
                            expression)
    return '\n'.join(declarations), '\n'.join(assignments), expression


def daemon(s):
    # First-frame diagnostics used to format and flush while holding state.mutex.
    s = one(s, '  const auto extract_start = Clock::now();',
            '  Clock::duration extract_duration{};\n  const auto extract_start = Clock::now();')
    s = one(s, '    const auto extract_duration = Clock::now() - extract_start;',
            '    extract_duration = Clock::now() - extract_start;')
    start = s.index('    if (queued_chunks > 0 &&')
    end = s.index('\n\n  if (log_dropped_summary)', start)
    block = s[start:end]
    if not block.endswith('\n  }') or 'state.' in block:
        raise RuntimeError('Unexpected ingress diagnostic block')
    s = s[:start] + '  }\n\n' + '\n'.join(line[2:] for line in block[:-4].splitlines()) + s[end:]

    # Summary capture/reset remains atomic. String allocation/formatting is outside
    # the shared state lock; all field values still describe the same snapshot.
    start = s.index('void maybe_log_audio_summary(')
    end = s.index('\nvoid audio_flush_loop(', start)
    summary = s[start:end]
    a = summary.index('    message =\n')
    b = summary.index('\n\n    state.audio_last_summary_time', a)
    expression = summary[a:b]
    extras = [('interval', 'Clock::duration')] + [(v, 'std::uint64_t') for v in
              re.findall(r'const std::uint64_t (\w+) =', summary)]
    decl, capture, expression = state_snapshot(expression, 'snapshot', extras)
    summary = summary[:a] + capture + summary[b:]
    summary = one(summary, '  std::string message;', '  struct Snapshot {\n' + decl + '\n  } snapshot;\n  std::string message;')
    summary = one(summary, '\n  logger.log(vds::LogScope::Output,',
                  '\n' + expression + '\n\n  logger.log(vds::LogScope::Output,')
    s = s[:start] + summary + s[end:]

    # Underflow warnings stay reliable, but neither disk I/O nor the transport
    # diagnostic mutex should be held underneath the shared controller mutex.
    start = s.index('Clock::duration flush_pending_audio_chunk(')
    end = s.index('\nvoid maybe_log_audio_summary(', start)
    flush = s[start:end]
    a = flush.index('          logger.log(\n')
    b = flush.index('\n        }\n        state.audio_pcm_stream_active', a)
    block = flush[a:b]
    decl, capture, expression = state_snapshot(block, 'underflow')
    flush = flush[:a] + '          report_underflow = true;\n' + capture + flush[b:]
    flush = one(flush, '  vds::BtReport packet{};',
                '  struct UnderflowSnapshot {\n' + decl + '\n  } underflow;\n  bool report_underflow = false;\n  vds::BtReport packet{};')
    # Issue the captured stop report before any newly moved synchronous warning.
    # Otherwise a disk stall could let a new PCM stream start before we send the
    # old stop state. Preserve the original action order across the unlock.
    flush = one(flush, '''  if (audio_route_deactivated) {
    if (stream_stop_report) {
      (void)try_send_bt_output_report(bluetooth, *stream_stop_report, logger,
                                      bluetooth_mutex, "state");
    }''', '''  if (audio_route_deactivated && stream_stop_report) {
    (void)try_send_bt_output_report(bluetooth, *stream_stop_report, logger,
                                    bluetooth_mutex, "state");
  }
  if (report_underflow) {
''' + expression + '''
  }

  if (audio_route_deactivated) {''')
    s = s[:start] + flush + s[end:]

    # Only runtime telemetry opts into lossy Info/Debug. Warnings remain synchronous,
    # and startup/shutdown/recovery records keep the original Logger::log path.
    for first, last in [('void handle_virtual_frame(', 'void send_initial_bluetooth_reports('),
                        ('Clock::duration flush_pending_audio_chunk(', 'void audio_flush_loop(')]:
        start, end = s.index(first), s.index(last)
        s = s[:start] + s[start:end].replace('logger.log(', 'logger.log_diagnostic(') + s[end:]
    return s


def transport(s):
    start = s.index('  std::optional<std::string> take_output_diagnostics(')
    end = s.index('\n  void write_shutdown_packet(', start)
    part = s[start:end]
    part = one(part, 'std::lock_guard guard(output_mutex_);', 'std::unique_lock guard(output_mutex_);')
    a = part.index('    std::string message =')
    b = part.index('\n\n    output_diagnostic_pending_ = false;', a)
    expression = part[a:b].replace('output_queue_.size()', 'pending')
    fields = sorted(set(re.findall(r'\boutput_\w+_', expression)))
    captures = ['    const auto pending = output_queue_.size();']
    for field in fields:
        local = 'snapshot_' + field
        captures.append(f'    const auto {local} = {field};')
        expression = expression.replace(field, local)
    part = part[:a] + '\n'.join(captures) + part[b:]
    part = one(part, '    return message;', '    guard.unlock();\n\n' + expression + '\n    return message;')
    return s[:start] + part + s[end:]
