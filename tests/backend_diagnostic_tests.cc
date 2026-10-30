// SPDX-License-Identifier: MIT
#include "diagnostic_worker.hh"
#include "vds_log.hh"
#include <chrono>
#include <filesystem>
#include <fstream>
#include <future>
#include <iostream>
#include <iterator>
#include <stdexcept>
#include <string>
#include <vector>

using namespace std::chrono_literals;
extern void require(bool, const char*);
namespace {
template<class Queue, class Record> void submit(Queue& queue, const Record& record) {
  const auto until = std::chrono::steady_clock::now() + 2s;
  while (!queue.try_submit(record)) {
    require(std::chrono::steady_clock::now() < until, "diagnostic producer stalled");
    std::this_thread::yield();
  }
}
std::string read(const std::filesystem::path& path) {
  std::ifstream file(path);
  return {std::istreambuf_iterator<char>(file), {}};
}
}

void test_diagnostics() {
  // The sink deliberately hangs until the producer has filled/overflowed the
  // queue. Completion before release proves producers do not wait for sink I/O.
  std::promise<void> entered, release;
  auto released = release.get_future().share();
  std::vector<int> received;
  std::uint64_t reported_drops = 0;
  dsbridge::DiagnosticWorker<int, 4> queue([&](int value) {
    if (value == 0) { entered.set_value(); released.wait(); }
    received.push_back(value);
  }, [&](auto dropped, auto) { reported_drops += dropped; });
  submit(queue, 0);
  const bool started = entered.get_future().wait_for(2s) == std::future_status::ready;
  if (!started) { release.set_value(); require(false, "diagnostic worker did not start"); }
  auto producer = std::async(std::launch::async, [&] {
    int accepted = 0;
    for (int i = 1; i <= 100; ++i) accepted += queue.try_submit(i) ? 1 : 0;
    return accepted;
  });
  const bool returned_while_blocked = producer.wait_for(500ms) == std::future_status::ready;
  release.set_value();
  const int accepted = producer.get();
  queue.stop();
  queue.stop(); // Owner stop is idempotent; no background callback remains.
  require(returned_while_blocked, "diagnostic producer waited for blocked sink");
  require(accepted == 4 && received == std::vector<int>({0,1,2,3,4}), "bounded diagnostic FIFO/drain failed");
  require(queue.dropped() == 96 && reported_drops == 96, "diagnostic loss is not accounted for");
  require(!queue.try_submit(200), "closed diagnostic worker accepted a record");

  // Multiple real producers: every accepted record is delivered once, in each
  // producer's order; rejected diagnostics are explicit, never silently lost.
  struct Record { int producer, sequence; };
  std::vector<Record> delivered;
  std::atomic_uint64_t attempted{0}, accepted_count{0};
  std::uint64_t losses = 0;
  dsbridge::DiagnosticWorker<Record, 64> concurrent(
      [&](const Record& r) { delivered.push_back(r); },
      [&](auto dropped, auto) { losses += dropped; });
  std::vector<std::thread> producers;
  for (int p = 0; p < 4; ++p) producers.emplace_back([&, p] {
    for (int i = 0; i < 2000; ++i) {
      ++attempted;
      if (concurrent.try_submit({p, i})) ++accepted_count;
    }
  });
  for (auto& thread : producers) thread.join();
  concurrent.stop();
  int previous[4] = {-1,-1,-1,-1};
  for (auto r : delivered) {
    require(r.sequence > previous[r.producer], "diagnostic duplicated or reordered");
    previous[r.producer] = r.sequence;
  }
  require(delivered.size() == accepted_count && delivered.size() + losses == attempted,
          "diagnostic concurrent stop/drain accounting failed");

  int last = 0;
  std::uint64_t failed_reports = 0;
  dsbridge::DiagnosticWorker<int, 4> failing([&](int value) {
    if (value == 1) throw std::runtime_error("injected sink failure");
    last = value;
  }, [&](auto, auto failures) { failed_reports += failures; });
  submit(failing, 1); submit(failing, 2); failing.stop();
  require(last == 2 && failing.failed() == 1 && failed_reports == 1,
          "sink failure terminated diagnostics or hid failure");

  const auto root = std::filesystem::current_path() / ".local" / "tests" /
      ("diagnostics-" + std::to_string(std::chrono::steady_clock::now().time_since_epoch().count()));
  std::filesystem::create_directories(root);
  const auto path = root / "logger.log";
  {
    vds::Logger logger(path.string());
    const auto ordinary = [&](const char* message) {
      const auto until = std::chrono::steady_clock::now() + 2s;
      while (!logger.log_diagnostic(vds::LogScope::Output, vds::LogLevel::Info, message)) {
        require(std::chrono::steady_clock::now() < until, "logger could not accept ordinary record");
        std::this_thread::yield();
      }
    };
    ordinary("ordinary|line\nend");
    logger.log_diagnostic(vds::LogScope::Output, vds::LogLevel::Warn, "critical-warning");
    logger.log_diagnostic(vds::LogScope::Output, vds::LogLevel::Error, "critical-error");
    logger.log(vds::LogScope::Output, vds::LogLevel::Info, "shutdown-neutral-confirmed");
    auto before_drain = read(path);
    require(before_drain.find("critical-warning") != std::string::npos &&
            before_drain.find("critical-error") != std::string::npos &&
            before_drain.find("shutdown-neutral-confirmed") != std::string::npos,
            "critical/recovery log was deferred or dropped");
    logger.log_diagnostic(vds::LogScope::Output, vds::LogLevel::Debug, std::string(4097, 'x'));
    logger.reopen();
    ordinary("after-reopen");
  }
  const auto contents = read(path);
  require(contents.find("ordinary line end") != std::string::npos &&
          contents.find("after-reopen") != std::string::npos &&
          contents.find("diagnostic logging loss dropped=") != std::string::npos,
          "logger drain, escaping, reopen or oversize accounting failed");
  std::cout << "diagnostic checks passed: blocked sink isolation, bounded FIFO, loss accounting, concurrent producers, stop/drain, sink failure, critical/recovery logs, reopen\n";
}
