// SPDX-License-Identifier: MIT
#pragma once
#include <array>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstddef>
#include <cstdint>
#include <functional>
#include <memory>
#include <mutex>
#include <thread>
#include <type_traits>
#include <utility>

namespace dsbridge {
// Ordinary diagnostics only. Producers never wait for the sink or for a queue
// lock. Storage is fixed at construction; a slow sink loses diagnostics, not
// controller data. The owner must join all producers before destruction.
template<class Record, std::size_t Capacity> class DiagnosticWorker {
  static_assert(Capacity > 0 && std::is_trivially_copyable_v<Record>);
public:
  using Sink = std::function<void(const Record&)>;
  using LossSink = std::function<void(std::uint64_t, std::uint64_t)>;
  DiagnosticWorker(Sink sink, LossSink loss)
      : records_(std::make_unique<std::array<Record, Capacity>>()),
        sink_(std::move(sink)), loss_(std::move(loss)),
        worker_([this] { run(); }) {}
  ~DiagnosticWorker() { stop(); }
  DiagnosticWorker(const DiagnosticWorker&) = delete;
  DiagnosticWorker& operator=(const DiagnosticWorker&) = delete;

  bool try_submit(const Record& record) noexcept {
    std::unique_lock lock(mutex_, std::try_to_lock);
    if (!lock.owns_lock() || closing_ || size_ == Capacity) {
      note_drop();
      return false;
    }
    (*records_)[(head_ + size_) % Capacity] = record;
    ++size_;
    lock.unlock();
    ready_.notify_one();
    return true;
  }

  void note_drop() noexcept {
    dropped_.fetch_add(1, std::memory_order_relaxed);
    pending_drops_.fetch_add(1, std::memory_order_relaxed);
    ready_.notify_one();
  }

  // Owner thread only; drains all accepted entries before joining the sink.
  // No detached worker can outlive the logger/file it refers to.
  void stop() {
    {
      std::lock_guard lock(mutex_);
      closing_ = true;
    }
    ready_.notify_one();
    if (worker_.joinable()) worker_.join();
  }
  std::uint64_t dropped() const { return dropped_.load(); }
  std::uint64_t failed() const { return failed_.load(); }

private:
  void report_loss(std::uint64_t dropped, std::uint64_t failed) noexcept {
    if (!dropped && !failed) return;
    try { loss_(dropped, failed); } catch (...) {
      // A broken diagnostic sink must not terminate controller threads.
      failed_.fetch_add(1, std::memory_order_relaxed);
    }
  }
  void run() noexcept {
    for (;;) {
      Record record{};
      bool have_record = false;
      bool done = false;
      std::uint64_t dropped = 0;
      {
        std::unique_lock lock(mutex_);
        // Timeout also covers a loss-only notification racing the wait;
        // ordinary records and shutdown use the same mutex as this predicate.
        ready_.wait_for(lock, std::chrono::seconds(1), [this] {
          return closing_ || size_ || pending_drops_.load();
        });
        dropped = pending_drops_.exchange(0);
        if (size_) {
          record = (*records_)[head_];
          head_ = (head_ + 1) % Capacity;
          --size_;
          have_record = true;
        }
        done = closing_ && !have_record;
      }
      // No queue lock is held during formatting, file I/O or callbacks.
      report_loss(dropped, 0);
      if (have_record) {
        try { sink_(record); } catch (...) {
          failed_.fetch_add(1, std::memory_order_relaxed);
          report_loss(0, 1);
        }
      }
      if (done) return;
    }
  }
  std::unique_ptr<std::array<Record, Capacity>> records_;
  Sink sink_;
  LossSink loss_;
  std::mutex mutex_;
  std::condition_variable ready_;
  std::size_t head_ = 0;
  std::size_t size_ = 0;
  bool closing_ = false;
  std::atomic_uint64_t dropped_{0}, pending_drops_{0}, failed_{0};
  std::thread worker_; // Last: every field it uses is initialized first.
};
} // namespace dsbridge
