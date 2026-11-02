// SPDX-License-Identifier: MIT
#pragma once
#include <algorithm>
#include <array>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <span>

namespace dsbridge {
struct AudioControlReply {
  bool supported = false;
  std::array<std::uint8_t, 2> bytes{};
  std::size_t length = 0;
};

// UAC1 topology stays unchanged: interface 0, playback FU 2, capture FU 5.
// Only the master channel advertises mute and volume in the pinned descriptor.
class UsbAudioControls {
  struct Unit {
    bool muted = false;
    std::int16_t volume = 0;
    std::atomic<int> gain{32768};
    void update() {
      gain = muted ? 0 : static_cast<int>(std::pow(10.0, volume / 256.0 / 20.0) * 32768 + .5);
    }
  } speaker_, microphone_;
public:
  // Preserve the actual range advertised by the existing backend (about -38 dB).
  static constexpr std::int16_t minimum = -9727;
  AudioControlReply control(std::uint8_t type, std::uint8_t request,
      std::uint16_t value, std::uint16_t index, std::uint16_t length,
      std::span<const std::uint8_t> data) {
    AudioControlReply reply;
    if ((type != 0x21 && type != 0xa1) || (index & 0xff) != 0 || (value & 0xff) != 0) return reply;
    const auto entity = index >> 8;
    Unit* unit = entity == 2 ? &speaker_ : entity == 5 ? &microphone_ : nullptr;
    const auto selector = value >> 8;
    if (!unit || (selector != 1 && selector != 2)) return reply;
    const std::size_t size = selector == 1 ? 1 : 2;
    if (type == 0x21) {
      if (request != 1 || length != size || data.size() != size) return reply;
      if (selector == 1) {
        if (data[0] > 1) return reply;
        unit->muted = data[0] != 0;
      } else {
        const auto db = static_cast<std::int16_t>(static_cast<std::uint16_t>(data[0]) | (static_cast<std::uint16_t>(data[1]) << 8));
        unit->volume = std::clamp(db, minimum, static_cast<std::int16_t>(0));
      }
      unit->update();
    } else {
      if (!data.empty() || length == 0) return reply;
      std::int16_t result = 0;
      if (selector == 1) {
        if (request != 0x81) return reply;
        result = unit->muted ? 1 : 0;
      } else {
        switch (request) {
          case 0x81: result = unit->volume; break;
          case 0x82: result = minimum; break;
          case 0x83: result = 0; break;
          case 0x84: result = 1; break;
          default: return reply;
        }
      }
      const auto bits = static_cast<std::uint16_t>(result);
      reply.bytes = {static_cast<std::uint8_t>(bits), static_cast<std::uint8_t>(bits >> 8)};
      reply.length = std::min<std::size_t>(length, size);
    }
    reply.supported = true;
    return reply;
  }
  int speaker_gain() const { return speaker_.gain.load(); }
  int microphone_gain() const { return microphone_.gain.load(); }
};

// The gain changes samples only. No stream state, packet timing or format changes.
inline void scale_audio_channels(std::span<std::uint8_t> pcm, int gain, bool capture) {
  if (gain == 32768) return;
  const std::size_t frame_bytes = capture ? 4 : 8;
  for (std::size_t off = 0; off + frame_bytes <= pcm.size(); off += frame_bytes) {
    for (std::size_t ch = 0; ch < 2; ++ch) {
      const auto idx = off + ch * 2;
      const auto sample = static_cast<std::int16_t>(static_cast<std::uint16_t>(pcm[idx]) | (static_cast<std::uint16_t>(pcm[idx + 1]) << 8));
      const auto scaled = static_cast<std::int16_t>((static_cast<int>(sample) * gain) >> 15);
      pcm[idx] = static_cast<std::uint8_t>(scaled);
      pcm[idx + 1] = static_cast<std::uint8_t>(static_cast<std::uint16_t>(scaled) >> 8);
    }
  }
}
}
