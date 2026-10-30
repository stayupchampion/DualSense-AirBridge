// SPDX-License-Identifier: MIT
#include <iostream>
#include <stdexcept>
#include "bridge_runtime.hh"
#include "usbip_hid_queue.hh"
using namespace std::chrono_literals;
void test_diagnostics();
void require(bool value, const char* why) {
  if(!value) throw std::runtime_error(why);
}
int main() {
  try {
    test_diagnostics();
    dsbridge::ReplyEpoch epoch;
    require(!epoch.accepts(epoch.current()),"inactive session accepts a reply");
    const auto first_session=epoch.activate();
    require(epoch.accepts(first_session),"current session reply rejected");
    const auto popped_reply=epoch.current();
    epoch.invalidate();
    require(!epoch.accepts(popped_reply),"dequeued reply survives disconnect");
    const auto reused_socket_session=epoch.activate();
    require(epoch.accepts(reused_socket_session) && !epoch.accepts(popped_reply),"old reply accepted after socket reuse");
    struct Request {std::uint32_t seqnum;};
    dsbridge::PendingHidQueue<Request,int> hid;
    require(hid.submit({10}) && !hid.take_ready(),"HID submit without input must return pending immediately");
    require(!hid.submit({10}),"duplicate pending HID sequence accepted");
    require(hid.submit({11}) && hid.cancel(10),"pending HID unlink failed");
    hid.report(42);
    auto ready=hid.take_ready();
    require(ready && ready->first.seqnum==11 && ready->second==42,"cancelled request consumed a report or FIFO order lost");
    require(!hid.cancel(11),"completed request must not report unlink success");
    for(int i=0;i<12;++i)hid.report(i);
    require(hid.submit({12}),"request rejected with queued reports");
    require(hid.take_ready()->second==4,"stale report queue is not bounded to eight");
    hid.clear();
    for(unsigned i=0;i<64;++i)require(hid.submit({i}),"normal pending request rejected");
    require(!hid.submit({65}),"unbounded pending requests");
    hid.clear();hid.report(7);
    require(!hid.take_ready(),"old session request survived clear");
    hid.clear();require(hid.submit({100}) && !hid.take_ready(),"old session report survived clear");
    dsbridge::TimedQueue<int> q;
    const auto t=dsbridge::Clock::time_point{}+1s;
    q.push_back(1,t); q.push_back(2,t+20ms); q.push_back(3,t+90ms);
    require(q.discard_expired(t+99ms)==0,"fresh chunks were dropped");
    require(q.discard_expired(t+100ms)==1 && q.front()==2,"expiry boundary/order");
    require(q.discard_expired(t+150ms)==1 && q.front()==3,"late arrival must stay");
    require(q.front_age(t+150ms)==60ms,"queue age measures enqueue to dequeue");
    q.clear();
    require(q.empty() && q.front_age(t)==0ms,"reset must clear timestamps");
    // A producer burst and a paused consumer must not replay stale feedback.
    for(int i=0;i<40;++i) {
      if(q.size()>=dsbridge::kMaxChunks)q.pop_front();
      q.push_back(i,t);
    }
    require(q.size()==12 && q.front()==28,"burst must retain newest bounded data");
    require(q.discard_expired(t+101ms)==12 && q.empty(),"resume replayed stale burst");
    require(dsbridge::kPrimeChunks*512.0/48000 < .035,"priming budget regression");

    vds::DsOutputState state;
    std::array<std::uint8_t,48> game{};
    game[0]=2; game[1]=0x2f; game[2]=4;
    game[3]=200; game[4]=200; game[6]=90;
    game[11]=1; game[12]=40; game[13]=80;
    game[22]=1; game[23]=40; game[24]=80;
    game[45]=11;game[46]=22;game[47]=33;
    require(state.apply_usb_output_report(game),"game report setup failed");
    const auto stopped=dsbridge::neutral_output(state,false);
    require((stopped[3]&0x0c)==0x0c,"trigger disable flags missing");
    require(stopped[5]==0 && stopped[6]==0,"rumble left running");
    for(int i=13;i<35;++i)require(stopped[i]==0,"trigger effect left running");
    require(stopped[8]==0 && (stopped[3]&0x20)!=0,"speaker zero volume must have enable flag");
    require(stopped[47]==11 && stopped[48]==22 && stopped[49]==33,"light preference overwritten");
    auto checksum=stopped;
    vds::fill_output_report_checksum(checksum);
    require(checksum==stopped,"shutdown report CRC mismatch");
    auto mic=state.build_bt_mic_report(false);
    require(mic[4]==0xfe,"mic close opcode incorrect");
    std::cout<<"runtime checks passed: asynchronous HID pairing, unlink, FIFO, bounds, session reset; audio expiry, burst bound, priming, neutral forces, light, CRC, mic close\n";
    return 0;
  } catch(const std::exception& e) { std::cerr<<e.what()<<"\n";return 1; }
}
