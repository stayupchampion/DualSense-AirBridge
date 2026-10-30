"""Build a reviewable, pinned vDS fork for a managed foreground session.
No driver/service installation; changes are applied to a separate build tree.
"""
from pathlib import Path
import argparse, difflib, hashlib, json, shutil
import backend_runtime_patch as runtime
import backend_async_hid_patch as async_hid
import backend_diagnostics_patch as diagnostics

COMMIT = 'ec531599dfdd02e14ce68efd8c0d2dcb439894a5'

def replace_once(text, old, new):
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f'Expected one pinned source match, got {count}: {old[:100]!r}')
    return text.replace(old, new, 1)

def patch_daemon(s):
    s = replace_once(s, '#include "vds_hidhide.hh"\n', '')
    s = replace_once(s, '  std::string pipe = kDefaultControlPipe;\n', '  std::string pipe;\n  std::string stop_event;\n')
    s = replace_once(s, '  vds::VdsdCommonOptions common = vds::default_vdsd_common_options();', '  vds::VdsdCommonOptions common{};')
    s = replace_once(s, '      std::string("[--pipe ") + kDefaultControlPipe + "]";', '      "--pipe <private-pipe> --stop-event <Local\\\\event>";')
    s = replace_once(s, '      options.pipe = next_value(i, arg);\n    } else {', '      options.pipe = next_value(i, arg);\n    } else if (arg == "--stop-event") {\n      options.stop_event = next_value(i, arg);\n    } else {')
    s = replace_once(s, '  options.log_path = common.log_path;\n  return options;', r'''  options.log_path = common.log_path;
  if (options.db_path.empty() || options.log_path.empty() ||
      options.pipe.rfind(R"(\\.\pipe\)", 0) != 0 ||
      options.pipe == kDefaultControlPipe ||
      options.stop_event.rfind(R"(Local\)", 0) != 0 ||
      options.stop_event.size() < 12) {
    throw std::runtime_error("managed backend requires explicit --db-path, --log, a private --pipe, and --stop-event Local\\<unique-name>");
  }
  return options;''')
    s = replace_once(s, '''  std::unique_ptr<vds::win::hidhide::DeviceGuard> hidden_device =
      vds::win::hidhide::hide_device(device->instance_path, logger);
''', '  // DSBridge manager owns all HidHide state and rollback.\n')
    s = replace_once(s, '''      if (!vds::win::hidhide::provider_available()) {
        logger.log(vds::LogScope::Bluetooth, vds::LogLevel::Error,
                   std::string(kVirtualPortProviderUnavailableReason) +
                       " detail=" + kWindowsHidHideProviderUnavailable);
        throw std::runtime_error(kWindowsHidHideProviderUnavailable);
      }
      vds::win::hidhide::register_daemon();
''', '')
    s = replace_once(s, '''    g_stop_event = stop_event.get();
    if (!service_mode) {''', r'''    g_stop_event = stop_event.get();
    UniqueHandle external_stop(OpenEventA(SYNCHRONIZE, FALSE, options.stop_event.c_str()));
    if (!external_stop) {
      throw std::runtime_error("manager stop event does not exist or is inaccessible");
    }
    std::jthread stop_watcher([external = external_stop.get(), internal = stop_event.get()](std::stop_token token) {
      while (!token.stop_requested()) {
        const DWORD status = WaitForSingleObject(external, 100);
        if (status == WAIT_OBJECT_0 || status == WAIT_FAILED) {
          g_stop_requested = true;
          SetEvent(internal);
          return;
        }
      }
    });
    if (!service_mode) {''')
    s = replace_once(s, 'pipe_name_.c_str(), PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED,\n          PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE | PIPE_WAIT,', 'pipe_name_.c_str(), PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED | FILE_FLAG_FIRST_PIPE_INSTANCE,\n          PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,')
    s = replace_once(s, '            !enabled_ports.empty() && vds::win::usbip::client_available() &&\n            vds::win::hidhide::provider_available();', '            !enabled_ports.empty() && vds::win::usbip::client_available();')
    s = replace_once(s, '              detail = kWindowsHidHideProviderUnavailable;', '              detail = "no virtual ports enabled";')
    if 'vds::win::hidhide::' in s:
        raise RuntimeError('Unexpected HidHide call remains')
    # Bound control-pipe IO waits so shutdown is not held by a stuck client.
    s = replace_once(s, 'WaitForMultipleObjects(2, wait_handles, FALSE, INFINITE);\n    if (wait_result == WAIT_OBJECT_0 + 1)', 'WaitForMultipleObjects(2, wait_handles, FALSE, 5000);\n    if (wait_result == WAIT_OBJECT_0 + 1)')
    s = replace_once(s, '                        const std::atomic_uint32_t *trace_flags = nullptr) {', '                        const std::atomic_uint32_t *trace_flags = nullptr,\n                        std::atomic_bool *ready = nullptr) {')
    s = replace_once(s, '  std::thread to_bluetooth([&] {', '  if (ready != nullptr) { ready->store(true); }\n\n  std::thread to_bluetooth([&] {')
    s = replace_once(s, '  std::atomic_bool done = false;\n};\n\nclass ControlPipeServer', '  std::atomic_bool done = false;\n  std::atomic_bool ready = false;\n};\n\nclass ControlPipeServer')
    s = replace_once(s, '        .connected = worker_is_connected(*worker),\n        .path = worker_is_connected(*worker)', '        .connected = worker->ready.load() && worker_is_connected(*worker),\n        .path = worker->ready.load() && worker_is_connected(*worker)')
    s = replace_once(s, '                                  std::atomic_bool &done,\n                                  std::string &last_error,', '                                  std::atomic_bool &done,\n                                  std::atomic_bool &ready,\n                                  std::string &last_error,')
    s = replace_once(s, '                       &stop_requested, &trace_flags);', '                       &stop_requested, &trace_flags, &ready);')
    s = replace_once(s, '  done = true;\n}', '  ready = false;\n  done = true;\n}')
    s = replace_once(s, '                                         worker_ptr->done,\n                                         worker_ptr->last_error, trace_flags);', '                                         worker_ptr->done, worker_ptr->ready,\n                                         worker_ptr->last_error, trace_flags);')
    # A/B software test: nonempty trace reply is lost when server disconnects
    # before .NET's separate Write/Read consumes buffered output. Keep the pipe
    # until client close or a bounded 5s read timeout; empty lists close at once.
    s = replace_once(s, '    (void)write_reply(pipe, reply);', '    if (!reply.empty() && write_reply(pipe, reply)) {\n      (void)read_command(pipe);\n    }')
    start = s.index('\nvoid WINAPI service_main(')
    s = s[:start] + r'''
// DSBridge fork: no service dispatcher and no install/self-registration path.
int main(int argc, char **argv) {
  if (argc == 2 && std::string_view(argv[1]) == "--bridge-info") {
    std::cout << R"({"bridgeProtocol":1,"upstreamCommit":"ec531599dfdd02e14ce68efd8c0d2dcb439894a5","managedHidHide":true,"stopEvent":true,"exclusiveUsbip":true,"serviceMode":false})" << "\n";
    return 0;
  }
  return run_windows_daemon(argc, argv, false);
}
'''
    return s

def patch_usbip(s):
    s = replace_once(s, '''      const BOOL reuse = TRUE;
      setsockopt(listener.get(), SOL_SOCKET, SO_REUSEADDR,
                 reinterpret_cast<const char *>(&reuse), sizeof(reuse));
''', '''      const BOOL exclusive = TRUE;
      if (setsockopt(listener.get(), SOL_SOCKET, SO_EXCLUSIVEADDRUSE,
                     reinterpret_cast<const char *>(&exclusive), sizeof(exclusive)) != 0) {
        error = "failed to reserve exclusive USB/IP listener";
      }
''')
    s = replace_once(s, '''      if (bind(listener.get(), reinterpret_cast<sockaddr *>(&address),
               sizeof(address)) != 0) {''', '''      if (!error.empty()) {
        // Do not continue without exclusive ownership of this endpoint.
      } else if (bind(listener.get(), reinterpret_cast<sockaddr *>(&address),
               sizeof(address)) != 0) {''')
    return s

def patch_cmake(s):
    start = s.index('  add_library(vds_hidhide OBJECT')
    end = s.index('  add_library(vds_usbip OBJECT', start)
    s = s[:start] + s[end:]
    s = replace_once(s, '            vds_hidhide\n', '')
    return s + '\nadd_executable(dsbridge_runtime_tests src/dsbridge_runtime_tests.cc src/dsbridge_diagnostic_tests.cc)\ntarget_link_libraries(dsbridge_runtime_tests PRIVATE vds_protocol vds_log ${VDS_OPUS_TARGET})\n'

def main():
    p = argparse.ArgumentParser()
    p.add_argument('--root', default=str(Path(__file__).resolve().parents[1]))
    args = p.parse_args()
    root = Path(args.root).resolve()
    source = root / '.local/upstream' / ('vds-' + COMMIT)
    dest = root / '.local/buildsrc/vds'
    if not source.is_dir():
        raise SystemExit(f'Pinned source missing: {source}')
    shutil.copytree(source, dest, dirs_exist_ok=True)
    patches = root / 'patches'
    patches.mkdir(exist_ok=True)
    diffs = []
    records = []
    for relative, patcher in [('src/platform/win32/vdsd.cc', lambda s: diagnostics.daemon(runtime.daemon(patch_daemon(s)))), ('src/platform/win32/vds_usbip.cc', lambda s: async_hid.usbip(runtime.usbip(patch_usbip(s)))), ('src/platform/win32/vds_bt.cc', lambda s: diagnostics.transport(runtime.transport(s))), ('src/platform/win32/vds_bt.hh', runtime.transport_header), ('src/vds_log.hh', diagnostics.logger_header), ('src/vds_log.cc', diagnostics.logger_source), ('CMakeLists.txt', patch_cmake)]:
        original = (source / relative).read_text(encoding='utf-8')
        updated = patcher(original)
        (dest / relative).write_text(updated, encoding='utf-8', newline='\n')
        diffs.extend(difflib.unified_diff(original.splitlines(True), updated.splitlines(True), fromfile='a/'+relative, tofile='b/'+relative))
        records.append({'path':relative,'upstreamSha256':hashlib.sha256(original.encode()).hexdigest(),'patchedSha256':hashlib.sha256(updated.encode()).hexdigest()})
    for origin, relative in [('patches/bridge_runtime.hh','src/bridge_runtime.hh'), ('patches/usbip_hid_queue.hh','src/usbip_hid_queue.hh'), ('patches/diagnostic_worker.hh','src/diagnostic_worker.hh'), ('tests/backend_runtime_tests.cc','src/dsbridge_runtime_tests.cc'), ('tests/backend_diagnostic_tests.cc','src/dsbridge_diagnostic_tests.cc')]:
        updated=(root/origin).read_text(encoding='utf-8')
        (dest/relative).write_text(updated,encoding='utf-8',newline='\n')
        diffs.extend(difflib.unified_diff([],updated.splitlines(True),fromfile='/dev/null',tofile='b/'+relative))
        records.append({'path':relative,'patchedSha256':hashlib.sha256(updated.encode()).hexdigest()})
    (patches / 'vds-managed-session.patch').write_text(''.join(diffs), encoding='utf-8', newline='\n')
    (patches / 'vds-managed-session.json').write_text(json.dumps({'upstreamCommit':COMMIT,'bridgeProtocol':1,'files':records},indent=2)+'\n', encoding='utf-8')
    (root / 'licenses').mkdir(exist_ok=True)
    shutil.copyfile(source / 'LICENSE', root / 'licenses/vDS-MIT.txt')
    print(dest)
if __name__ == '__main__':
    main()
