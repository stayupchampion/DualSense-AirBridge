// SPDX-License-Identifier: MIT
#pragma once
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <span>
#include "vds_protocol.hh"

namespace dsbridge {
// One managed backend invocation owns exactly one physical controller.
inline std::atomic_bool speaker_muted{true};
inline std::atomic_uint64_t speaker_revision{0};
inline std::atomic_uint64_t battery_sample{0}; // boot milliseconds << 8 | report byte

inline bool mute_speaker_report(std::span<std::uint8_t> report,bool muted) {
  if(!muted||report.empty())return false;
  std::size_t offset=0;
  if(report.size()==78&&report[0]==0x31)offset=3;
  else if(report.size()==142&&report[0]==0x32&&report[2]==0x90&&report[3]==63)offset=4;
  else if(report.size()==398&&report[0]==0x36&&report[11]==0x90&&report[12]==63)offset=13;
  else return false; // Includes microphone commands, which share report ID 0x32.
  // Override only speaker volume and speaker mute, preserving haptics, headset,
  // microphone, triggers and lights. Never mute the four-channel USB endpoint.
  const bool changed=(report[offset]&0x20)==0||(report[offset+1]&2)==0||
      report[offset+5]!=0||(report[offset+9]&0x20)==0;
  report[offset]|=0x20;
  report[offset+1]|=2;
  report[offset+5]=0;
  report[offset+9]|=0x20;
  if(changed)vds::fill_output_report_checksum(report);
  return changed;
}
}
