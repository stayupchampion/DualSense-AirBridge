// SPDX-License-Identifier: MIT
#pragma once
#include <algorithm>
#include <chrono>
#include <deque>
#include "vds_protocol.hh"

namespace dsbridge {
using Clock = std::chrono::steady_clock;
inline constexpr std::size_t kPrimeChunks = 3; // 32 ms of PCM after removing HID head-of-line blocking.
inline constexpr std::size_t kMaxChunks = 12;  // Absorb Windows bursts; real queue age is capped below.
inline constexpr auto kPrimeTimeout = std::chrono::milliseconds(35);
inline constexpr auto kMaxChunkAge = std::chrono::milliseconds(100);

template<class T> class TimedQueue {
  struct Entry { T value; Clock::time_point at; };
  std::deque<Entry> entries_;
public:
  bool empty() const { return entries_.empty(); }
  std::size_t size() const { return entries_.size(); }
  void clear() { entries_.clear(); }
  void push_back(const T& value, Clock::time_point now = Clock::now()) {
    entries_.push_back({value,now});
  }
  void pop_front() { entries_.pop_front(); }
  const T& front() const { return entries_.front().value; }
  Clock::duration front_age(Clock::time_point now = Clock::now()) const {
    return empty() ? Clock::duration::zero() : now-entries_.front().at;
  }
  std::size_t discard_expired(Clock::time_point now = Clock::now()) {
    std::size_t count=0;
    while(!empty() && front_age(now)>=kMaxChunkAge) { pop_front(); ++count; }
    return count;
  }
};

// Release forces and stop bridge-owned audio without undoing the user's mic mute.
// This is intermediate: the caller closes the microphone and hands lighting back
// to firmware after all ordinary writers have stopped.
inline vds::BtStateReport neutral_output(vds::DsOutputState& state, bool headset) {
  std::array<std::uint8_t,48> usb{};
  usb[0]=VDS_USB_OUTPUT_REPORT_ID;
  usb[1]=0x2f; // zero rumble, both trigger effects off, speaker volume zero.
  state.set_audio_out_stream_active(false,headset);
  state.apply_usb_output_report(usb);
  return state.build_bt_state_report();
}

// Terminal reports only: release the host lighting, then enable the firmware's
// native Bluetooth light. Never replay the retained yellow/custom RGB or audio
// controls here. The transport must bypass the speaker-preference filter for
// these reports; that filter would otherwise add audio/mute writes.
inline vds::BtStateReport lighting_handoff(vds::DsOutputState& state, bool native_light) {
  auto report=state.build_bt_state_report(); // Retain the next sequence/header.
  std::fill(report.begin()+3,report.end(),std::uint8_t{0});
  if(native_light) {
    report[41]=0x02; // Lightbar setup enable.
    report[44]=0x01; // Firmware light on, not a custom RGB value.
  } else {
    report[4]=0x18; // Release host LEDs and clear bridge/game player indicators.
  }
  vds::fill_output_report_checksum(report);
  return report;
}
}
