"""Pinned backend changes for bounded haptic latency and orderly output release."""
def one(s, old, new):
    if s.count(old) != 1:
        raise RuntimeError(f"runtime patch expected one match: {old[:100]!r}, got {s.count(old)}")
    return s.replace(old,new,1)

def usbip(s):
    # This is a loopback transport carrying latency-sensitive USB completions.
    # Never change machine-wide TCP parameters. Each accepted socket owns this
    # option and it disappears with the session.
    s=one(s, '''      if (!client.valid()) {
        continue;
      }
      if (stop_requested.load()) {''', '''      if (!client.valid()) {
        continue;
      }
      const BOOL no_delay = TRUE;
      if (setsockopt(client.get(), IPPROTO_TCP, TCP_NODELAY,
                     reinterpret_cast<const char *>(&no_delay), sizeof(no_delay)) != 0) {
        continue;
      }
      if (stop_requested.load()) {''')
    s=one(s, '''void VirtualPort::Impl::handle_import_session(SOCKET client) {
  const ProfileDescriptors desc''', '''void VirtualPort::Impl::handle_import_session(SOCKET client) {
  logger.log(LogScope::Usb, LogLevel::Info,
             "USB/IP low-latency socket: TCP_NODELAY=on; contiguous replies");
  const ProfileDescriptors desc''')
    s=one(s, '''  std::lock_guard<std::mutex> send_guard(socket_send_mutex);
  if (!send_exact(client, &ret, sizeof(ret))) {
    return false;
  }
  if (!reply_data.empty() &&
      !send_exact(client, reply_data.data(), reply_data.size())) {
    return false;
  }
  if (cmd.number_of_packets > 0) {
    // reply_iso was already built above (needed early so the RET_SUBMIT
    // header's actual_length could be the sum of these packets' actual
    // lengths). Just send it here.
    if (!reply_iso.empty() &&
        !send_exact(client, reply_iso.data(),
                    reply_iso.size() * sizeof(UsbipIsoPacketDescriptor))) {
      return false;
    }
  }
  return true;
}''', '''  // Keep the unchanged wire bytes together: sending a short header and
  // waiting on another small write can couple Nagle with delayed ACKs.
  const std::size_t iso_bytes = reply_iso.size() * sizeof(UsbipIsoPacketDescriptor);
  std::vector<std::uint8_t> reply_bytes(sizeof(ret) + reply_data.size() + iso_bytes);
  std::memcpy(reply_bytes.data(), &ret, sizeof(ret));
  if (!reply_data.empty())
    std::memcpy(reply_bytes.data() + sizeof(ret), reply_data.data(), reply_data.size());
  if (iso_bytes)
    std::memcpy(reply_bytes.data() + sizeof(ret) + reply_data.size(), reply_iso.data(), iso_bytes);
  std::lock_guard<std::mutex> send_guard(socket_send_mutex);
  return send_exact(client, reply_bytes.data(), reply_bytes.size());
}''')
    return s

def daemon(s):
    s=one(s, '#include "vds_bt.hh"', '#include "vds_bt.hh"\n#include "bridge_runtime.hh"')
    s=one(s,'constexpr std::size_t kMaxPendingAudioChunks = 64;', 'constexpr std::size_t kMaxPendingAudioChunks = dsbridge::kMaxChunks;')
    s=one(s,'constexpr std::size_t kAudioPrimeChunks = 24;', 'constexpr std::size_t kAudioPrimeChunks = dsbridge::kPrimeChunks;')
    s=one(s,'constexpr std::size_t kAudioFreshQueueChunks = 48;', 'constexpr std::size_t kAudioFreshQueueChunks = dsbridge::kMaxChunks;')
    s=one(s,'constexpr auto kAudioJitterBufferMaxDelay = std::chrono::milliseconds(300);',
            'constexpr auto kAudioJitterBufferMaxDelay = dsbridge::kPrimeTimeout;')
    s=one(s,'  std::deque<vds::AudioChunk> pending_audio_chunks;',
            '  dsbridge::TimedQueue<vds::AudioChunk> pending_audio_chunks;\n  std::int64_t audio_queue_age_last_us = 0;\n  std::int64_t audio_queue_age_max_us = 0;')
    s=one(s,'''    const auto now = Clock::now();
    if (state.pending_audio_chunks.empty()) {''', '''    const auto now = Clock::now();
    const auto expired = state.pending_audio_chunks.discard_expired(now);
    state.audio_dropped_count += expired;
    state.audio_stale_dropped_count += expired;
    if (state.pending_audio_chunks.empty()) {''')
    s=one(s,'''      const vds::AudioChunk chunk = state.pending_audio_chunks.front();
      has_signal = chunk.has_signal;''', '''      state.audio_queue_age_last_us =
          duration_us(state.pending_audio_chunks.front_age(now));
      state.audio_queue_age_max_us =
          std::max(state.audio_queue_age_max_us, state.audio_queue_age_last_us);
      const vds::AudioChunk chunk = state.pending_audio_chunks.front();
      state.pending_audio_chunks.pop_front(); // Own this item before releasing the queue lock.
      has_signal = chunk.has_signal;''')
    s=one(s,'''        " pending=" + std::to_string(state.pending_audio_chunks.size()) +
        " usb_frames="''', '''        " pending=" + std::to_string(state.pending_audio_chunks.size()) +
        " queue_age_last_us=" + std::to_string(state.audio_queue_age_last_us) +
        " queue_age_max_us=" + std::to_string(state.audio_queue_age_max_us) +
        " queue_capacity_ms=128 max_queue_age_ms=100 prime_ms=32" +
        " usb_frames="''')
    s=one(s,'''  if (stop_monitor.joinable()) {
    stop_monitor.join();
  }
}

struct BridgeWorker''', '''  if (stop_monitor.joinable()) {
    stop_monitor.join();
  }
  // All producers have joined. A naturally exiting loop may have bypassed
  // request_bridge_restart's cancellation once the restart flag was set.
  bluetooth->cancel(); // Cancels this HID handle/writer only; no radio reset.
  // Send final neutral reports only after the normal writer has joined.
  try {
    const auto neutral = dsbridge::neutral_output(state.output_state, state.headset_plugged);
    bluetooth->write_shutdown_packet(hidp_output_packet(neutral));
    const auto mic_close = state.output_state.build_bt_mic_report(false);
    bluetooth->write_shutdown_packet(hidp_output_packet(mic_close));
    logger.log(vds::LogScope::Output, vds::LogLevel::Info,
               "shutdown neutral reports written; microphone close written");
  } catch (const std::exception &error) {
    logger.log(vds::LogScope::Output, vds::LogLevel::Warn,
               std::string("shutdown neutralization not confirmed: ") + error.what());
  }
}

struct BridgeWorker''')
    s=one(s,'''      if (!sending_keepalive && !sending_concealment &&
          !state.pending_audio_chunks.empty()) {
        state.pending_audio_chunks.pop_front();
      }
''','')
    s=one(s,'''    if (!sending_keepalive && !sending_concealment &&
        !state.pending_audio_chunks.empty()) {
      state.pending_audio_chunks.pop_front();
    }
''','')
    return s

def transport_header(s):
    return one(s,'  virtual void cancel() = 0;',
        '  virtual void write_shutdown_packet(std::span<const std::uint8_t> packet) = 0;\n  virtual void cancel() = 0;')

def transport(s):
    return one(s,'''  void cancel() override {''', '''  // Used only after every producer and the normal writer have joined.
  // It has its own completion event because the normal stop event is latched.
  void write_shutdown_packet(std::span<const std::uint8_t> packet) override {
    if (packet.empty() || packet[0] != kBtHidpOutputPrefix)
      throw std::runtime_error("invalid shutdown HID packet");
    {
      std::lock_guard guard(output_mutex_);
      if (!output_stop_requested_ || output_thread_.joinable())
        throw std::runtime_error("shutdown write requires stopped output writer");
    }
    auto write = make_output_write(packet.subspan(1));
    UniqueHandle event(CreateEventA(nullptr, TRUE, FALSE, nullptr));
    if (!event) throw std::runtime_error("shutdown completion event failed");
    OVERLAPPED op{};
    op.hEvent = event.get();
    if (!WriteFile(output_handle_.get(), write.buffer.data(),
                   static_cast<DWORD>(write.buffer.size()), nullptr, &op) &&
        GetLastError() != ERROR_IO_PENDING)
      throw std::runtime_error("shutdown HID write failed: " + win32_error_message(GetLastError()));
    if (WaitForSingleObject(event.get(), 500) != WAIT_OBJECT_0) {
      CancelIoEx(output_handle_.get(), &op);
      DWORD ignored = 0;
      // Reap cancellation before releasing the OVERLAPPED and its buffer.
      GetOverlappedResult(output_handle_.get(), &op, &ignored, TRUE);
      throw std::runtime_error("shutdown HID write timed out");
    }
    DWORD written = 0;
    if (!GetOverlappedResult(output_handle_.get(), &op, &written, FALSE) ||
        written != write.buffer.size())
      throw std::runtime_error("shutdown HID write incomplete");
  }

  void cancel() override {''')
