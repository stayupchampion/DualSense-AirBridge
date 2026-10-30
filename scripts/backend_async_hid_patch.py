"""Remove input-poll head-of-line blocking from the pinned USB/IP receiver."""
from backend_runtime_patch import one


def usbip(s):
    # The pinned receive loop already converts this union slot to host order.
    # Fail generation if that contract changes, instead of double-swapping it.
    if s.count('cmd.transfer_flags = ntoh32(cmd.transfer_flags);') != 1:
        raise RuntimeError('Unexpected USB/IP transfer_flags decoding contract')
    s=one(s,'#include "vds_usbip.hh"','#include "vds_usbip.hh"\n#include "usbip_hid_queue.hh"')
    s=one(s,'  std::deque<Frame> hid_in_queue;', '''  struct PendingHidRequest {
    SOCKET client;
    std::uint32_t seqnum;
    std::int32_t length;
    std::int32_t number_of_packets;
  };
  dsbridge::PendingHidQueue<PendingHidRequest, Frame> hid_requests;
  bool hid_session_active = false;''')
    s=one(s,'  bool wait_for_hid_in_frame(Frame &out, DWORD timeout_ms);',
            '  void dispatch_hid_replies_locked();')
    s=one(s,'''  struct PacedReply {
    SOCKET client;
    std::vector<std::uint8_t> bytes;
  };''', '''  struct PacedReply {
    SOCKET client;
    std::vector<std::uint8_t> bytes;
    std::optional<std::uint32_t> hid_seqnum;
    std::uint64_t generation = 0;
  };''')
    s=one(s,'  std::mutex socket_send_mutex;', '  std::mutex socket_send_mutex;\n  dsbridge::ReplyEpoch reply_epoch;')
    s=one(s,'''      send_exact(reply.client, reply.bytes.data(), reply.bytes.size());''', '''      // Cleanup invalidates even already-dequeued replies under this lock.
      if (reply_epoch.accepts(reply.generation) && reply.client == client_socket.load())
        send_exact(reply.client, reply.bytes.data(), reply.bytes.size());''')
    s=one(s,'''    PacedReply paced{};
    paced.client = client;''', '''    PacedReply paced{};
    paced.generation = reply_epoch.current();
    paced.client = client;''')
    s=one(s,'TCP_NODELAY=on; contiguous replies','TCP_NODELAY=on; contiguous replies; asynchronous HID input')
    s=one(s,'''  attached = true;
  auto cleanup_connection = [this]() noexcept {
    attached = false;
    try {''', '''  {
    std::lock_guard<std::mutex> guard(socket_send_mutex);
    reply_epoch.activate();
  }
  attached = true;
  {
    std::lock_guard<std::mutex> guard(demux_mutex);
    hid_requests.clear();
    hid_session_active = true;
  }
  auto cleanup_connection = [this]() noexcept {
    attached = false;
    try {
      // Same demux -> pacer lock order as dispatch; no late HID reply can
      // be enqueued after this connection's reply queue is cleared.
      std::lock_guard<std::mutex> guard(demux_mutex);
      hid_session_active = false;
      hid_requests.clear();
    } catch (...) {
    }
    try {
      std::lock_guard<std::mutex> guard(socket_send_mutex);
      reply_epoch.invalidate();
    } catch (...) {
    }
    try {''')
    s=one(s,'''        // Best-effort: acknowledge unlink without tracking in-flight URBs
        // individually. vDS's interrupt/iso endpoints are effectively
        // always-pending polling loops, so an unlink just means "stop
        // expecting a reply for that seqnum," which our per-request
        // synchronous handling already satisfies.
        UsbipRetUnlink unlink_reply{};''', '''        // CMD_UNLINK's target seqnum occupies the transfer_flags slot.
        // Pending HID requests and unsent HID replies are cancellable. Once
        // the sender owns a reply, completion wins and UNLINK returns zero.
        const auto target_seqnum = cmd.transfer_flags;
        bool cancelled_hid = false;
        {
          std::lock_guard<std::mutex> guard(demux_mutex);
          cancelled_hid = hid_requests.cancel(target_seqnum);
          std::lock_guard<std::mutex> paced_guard(pacer_mutex);
          for (auto it = paced_replies.begin(); it != paced_replies.end();) {
            if (it->second.client == client && it->second.hid_seqnum == target_seqnum) {
              it = paced_replies.erase(it);
              cancelled_hid = true;
            } else ++it;
          }
        }
        UsbipRetUnlink unlink_reply{};
        unlink_reply.status = hton32(cancelled_hid ? static_cast<std::uint32_t>(-104) : 0);''')
    s=one(s,'''      hid_in_queue.push_back(std::move(frame));
      // Bound the queue: if the HID_IN handler stalls (e.g. blocked on
      // something else) we want to drop stale reports, not build
      // unbounded latency.
      if (hid_in_queue.size() > 8)
        hid_in_queue.pop_front();''', '''      if (hid_session_active) {
        hid_requests.report(std::move(frame));
        dispatch_hid_replies_locked();
      }''')
    start=s.index('bool VirtualPort::Impl::wait_for_hid_in_frame(')
    end=s.index('bool VirtualPort::Impl::wait_for_feature_reply(',start)
    s=s[:start]+'''// Called only under demux_mutex. Queueing replies involves no socket IO.
void VirtualPort::Impl::dispatch_hid_replies_locked() {
  if (!hid_session_active || stop_requested.load()) return;
  while (auto ready = hid_requests.take_ready()) {
    const auto &[request, frame] = *ready;
    const auto count = std::min(frame.payload.size(), static_cast<std::size_t>(request.length));
    UsbipRetSubmit ret{};
    ret.base.command = hton32(kRetSubmit);
    ret.base.seqnum = hton32(request.seqnum);
    ret.actual_length = hton32(static_cast<std::uint32_t>(count));
    ret.number_of_packets = hton32(static_cast<std::uint32_t>(request.number_of_packets));
    PacedReply reply{};
    reply.generation = reply_epoch.current();
    reply.client = request.client;
    reply.hid_seqnum = request.seqnum;
    reply.bytes.resize(sizeof(ret)+count);
    std::memcpy(reply.bytes.data(), &ret, sizeof(ret));
    if (count) std::memcpy(reply.bytes.data()+sizeof(ret), frame.payload.data(), count);
    {
      std::lock_guard<std::mutex> guard(pacer_mutex);
      paced_replies.emplace(std::chrono::steady_clock::now(), std::move(reply));
    }
    pacer_cv.notify_all();
  }
}

'''+s[end:]
    s=one(s,'''  } else if (cmd.base.ep == kEpHidIn && cmd.base.direction == kDirIn) {
    Frame frame;
    if (wait_for_hid_in_frame(frame, 50)) {
      reply_data = frame.payload;
    }
    ret.status = 0;
  } else if''', '''  } else if (cmd.base.ep == kEpHidIn && cmd.base.direction == kDirIn) {
    std::lock_guard<std::mutex> guard(demux_mutex);
    if (!hid_session_active || cmd.number_of_packets > 0 ||
        !hid_requests.submit({client, cmd.base.seqnum, cmd.transfer_buffer_length, cmd.number_of_packets}))
      return false;
    dispatch_hid_replies_locked();
    return true; // Audio OUT can be received while HID IN awaits a report.
  } else if''')
    if 'wait_for_hid_in_frame' in s or 'hid_in_queue' in s:
        raise RuntimeError('Blocking HID input path remains')
    return s
