"""Separate the pinned virtual USB speaker/microphone controls; keep USB identity and timing."""
from backend_runtime_patch import one


def daemon(s):
    return one(s, '"controllerPreferences":true', '"controllerPreferences":true,"independentAudioControls":true')


def usbip(s):
    s = one(s, '#include "vds_usbip.hh"', '#include "vds_usbip.hh"\n#include "usb_audio_controls.hh"')
    s = one(s, '  std::atomic<int> speaker_gain_q15{1 << 15};\n  std::atomic<int> fu_volume_db256{0};\n  std::atomic<bool> fu_muted{false};', '  dsbridge::UsbAudioControls audio_controls;')
    s = one(s, '  void update_speaker_gain();\n', '')
    start = s.index('void VirtualPort::Impl::update_speaker_gain() {')
    end = s.index('void VirtualPort::Impl::handle_import_session', start)
    s = s[:start] + s[end:]
    start = s.index('      // USB Audio Class 1.0 Feature Unit control request')
    end = s.index('    } else {\n      // Unhandled/unknown control request: STALL.', start)
    s = s[:start] + '''      const auto result = audio_controls.control(bmRequestType, bRequest,
          wValue, wIndex, wLength, out_data);
      reply_data.assign(result.bytes.begin(), result.bytes.begin() + result.length);
      ret.status = result.supported ? 0 : -32;
''' + s[end:]
    s = one(s, 'const int gain_q15 = speaker_gain_q15.load();', 'const int gain_q15 = audio_controls.speaker_gain();')
    start = s.index('      for (std::size_t off = 0; off + 8 <= gained.size(); off += 8) {')
    end = s.index('      pcm_to_forward = gained;', start)
    s = s[:start] + '      dsbridge::scale_audio_channels(gained, gain_q15, false);\n' + s[end:]
    s = one(s, '''    reply_data =
        take_mic_bytes(static_cast<std::size_t>(cmd.transfer_buffer_length));''', '''    reply_data =
        take_mic_bytes(static_cast<std::size_t>(cmd.transfer_buffer_length));
    dsbridge::scale_audio_channels(reply_data, audio_controls.microphone_gain(), true);''')
    return s
