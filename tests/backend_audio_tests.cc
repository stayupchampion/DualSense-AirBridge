// SPDX-License-Identifier: MIT
#include "usb_audio_controls.hh"
#include <stdexcept>
#include <iostream>
namespace {
void check(bool ok, const char* message) { if (!ok) throw std::runtime_error(message); }
}
void test_audio_controls() {
  dsbridge::UsbAudioControls a;
  const std::array<std::uint8_t,1> mute{1}, unmute{0};
  const std::array<std::uint8_t,2> minus6{0,0xfa}, unity{0,0};
  check(a.speaker_gain()==32768 && a.microphone_gain()==32768,"default gains changed");
  check(a.control(0x21,1,0x100,0x500,1,mute).supported,"mic master mute rejected");
  check(a.microphone_gain()==0 && a.speaker_gain()==32768,"mic mute leaked into playback");
  check(a.control(0xa1,0x81,0x100,0x200,1,{}).bytes[0]==0,"playback mute read contaminated");
  check(a.control(0xa1,0x81,0x100,0x500,1,{}).bytes[0]==1,"capture mute not retained");
  check(a.control(0x21,1,0x200,0x200,2,minus6).supported,"playback volume rejected");
  check(a.speaker_gain()>16000 && a.speaker_gain()<17000 && a.microphone_gain()==0,"volume isolation failed");
  check(a.control(0x21,1,0x100,0x500,1,unmute).supported && a.microphone_gain()==32768,"capture unmute failed");
  check(a.control(0x21,1,0x100,0x200,1,mute).supported && a.microphone_gain()==32768,"playback mute affected capture");
  check(a.control(0x21,1,0x200,0x500,2,minus6).supported && a.speaker_gain()==0,"capture volume affected playback");
  const auto min=a.control(0xa1,0x82,0x200,0x200,2,{});
  check(min.supported && min.bytes[0]==1 && min.bytes[1]==0xda,"advertised volume range changed");
  check(a.control(0xa1,0x84,0x200,0x500,2,{}).bytes[0]==1,"volume resolution changed");
  check(a.control(0xa1,0x81,0x200,0x500,1,{}).length==1,"short control IN exceeded wLength");
  for (auto index : {0x000,0x100,0x300,0x201,0x501})
    check(!a.control(0x21,1,0x100,index,1,unmute).supported,"unknown entity/interface accepted");
  for (auto value : {0x101,0x201,0x300})
    check(!a.control(0x21,1,value,0x200,1,unmute).supported,"unsupported selector/channel accepted");
  check(!a.control(0x21,1,0x200,0x200,1,unmute).supported,"short volume payload accepted");
  check(!a.control(0x21,1,0x100,0x200,1,unity).supported,"oversize mute accepted");
  check(!a.control(0xa1,0x82,0x100,0x200,1,{}).supported,"undefined mute GET_MIN accepted");
  check(!a.control(0x22,1,0x100,0x200,1,unmute).supported,"endpoint recipient accepted as feature unit");
  check(a.speaker_gain()==0,"rejected command changed state");
  // All PCM16 values in both speaker channels: haptic bytes must remain bit exact.
  for (int gain : {0,1,16423,32768}) {
    for (int sample=-32768;sample<=32767;++sample) {
      const auto bits=static_cast<std::uint16_t>(sample);
      const auto other = static_cast<std::int16_t>(-1-sample);
      const auto other_bits = static_cast<std::uint16_t>(other);
      std::array<std::uint8_t,8> pcm{static_cast<std::uint8_t>(bits),static_cast<std::uint8_t>(bits>>8),
          static_cast<std::uint8_t>(other_bits),static_cast<std::uint8_t>(other_bits>>8),
          static_cast<std::uint8_t>(bits),static_cast<std::uint8_t>(bits>>8),
          static_cast<std::uint8_t>(other_bits),static_cast<std::uint8_t>(other_bits>>8)};
      const auto original=pcm;
      dsbridge::scale_audio_channels(pcm,gain,false);
      const auto expected=static_cast<std::int16_t>((sample*gain)>>15);
      check(pcm[0]==static_cast<std::uint8_t>(expected) && pcm[1]==static_cast<std::uint8_t>(static_cast<std::uint16_t>(expected)>>8),"speaker PCM regression");
      const auto expected_other=static_cast<std::int16_t>((other*gain)>>15);
      check(pcm[2]==static_cast<std::uint8_t>(expected_other) && pcm[3]==static_cast<std::uint8_t>(static_cast<std::uint16_t>(expected_other)>>8),"second speaker channel regression");
      check(std::equal(pcm.begin()+4,pcm.end(),original.begin()+4),"haptic samples modified");
    }
  }
  std::array<std::uint8_t,5> capture{0xff,0x7f,0,0x80,0xab};
  dsbridge::scale_audio_channels(capture,0,true);
  check(capture==std::array<std::uint8_t,5>{0,0,0,0,0xab},"capture mute or incomplete frame handling failed");
  std::cout<<"audio control checks passed: entity/channel validation, independent gains, unchanged range, exhaustive PCM/haptic preservation\n";
}
