// SPDX-License-Identifier: MIT
#include "usbip_handshake.hh"
#include <array>
#include <stdexcept>
#include <thread>
#include <iostream>
using namespace std::chrono_literals;
namespace {
void verify(bool value,const char* why){if(!value)throw std::runtime_error(why);}
struct SocketPair {
  SOCKET listener=INVALID_SOCKET,client=INVALID_SOCKET,server=INVALID_SOCKET;
  SocketPair() {
    try {
      listener=socket(AF_INET,SOCK_STREAM,IPPROTO_TCP);
      verify(listener!=INVALID_SOCKET,"fixture listener creation");
      sockaddr_in address{};address.sin_family=AF_INET;address.sin_addr.s_addr=htonl(INADDR_LOOPBACK);
      verify(bind(listener,reinterpret_cast<sockaddr*>(&address),sizeof(address))==0,"fixture bind");
      verify(listen(listener,1)==0,"fixture listen");int size=sizeof(address);
      verify(getsockname(listener,reinterpret_cast<sockaddr*>(&address),&size)==0,"fixture ephemeral port");
      client=socket(AF_INET,SOCK_STREAM,IPPROTO_TCP);
      verify(client!=INVALID_SOCKET&&connect(client,reinterpret_cast<sockaddr*>(&address),sizeof(address))==0,"fixture connect");
      server=accept(listener,nullptr,nullptr);verify(server!=INVALID_SOCKET,"fixture accept");
      closesocket(listener);listener=INVALID_SOCKET;
    }catch(...){close();throw;}
  }
  void close(){for(auto* s:{&listener,&client,&server})if(*s!=INVALID_SOCKET){closesocket(*s);*s=INVALID_SOCKET;}}
  ~SocketPair(){close();}
};
}
void test_handshake() {
  WSADATA data{};verify(WSAStartup(MAKEWORD(2,2),&data)==0,"fixture Winsock startup");
  struct WinsockCleanup{~WinsockCleanup(){WSACleanup();}} cleanup;
  std::atomic_bool stopping=false;std::array<char,40> bytes{};
  {
    SocketPair pair;auto start=std::chrono::steady_clock::now();
    verify(!dsbridge::receive_handshake(pair.server,bytes.data(),8,start+50ms,stopping),"silent peer accepted");
    verify(std::chrono::steady_clock::now()-start<1s,"silent peer blocked beyond deadline");
  }
  {
    SocketPair pair;auto start=std::chrono::steady_clock::now();
    std::jthread writer([&](std::stop_token token){while(!token.stop_requested()){if(send(pair.client,"x",1,0)!=1)return;std::this_thread::sleep_for(10ms);}});
    verify(!dsbridge::receive_handshake(pair.server,bytes.data(),bytes.size(),start+75ms,stopping),"slow trickle reset absolute deadline");
    writer.request_stop();writer.join();
    verify(std::chrono::steady_clock::now()-start<1s,"slow trickle stalled listener");
  }
  {
    SocketPair pair;auto start=std::chrono::steady_clock::now();
    std::jthread stopper([&]{std::this_thread::sleep_for(20ms);stopping=true;});
    const bool received=dsbridge::receive_handshake(pair.server,bytes.data(),8,start+5s,stopping);
    stopper.join();verify(!received&&std::chrono::steady_clock::now()-start<1s,"listener stop did not interrupt handshake");
    stopping=false;
  }
  {
    SocketPair pair;std::array<char,40> expected;for(std::size_t i=0;i<expected.size();++i)expected[i]=static_cast<char>(i);
    std::jthread writer([&]{send(pair.client,expected.data(),3,0);std::this_thread::sleep_for(5ms);send(pair.client,expected.data()+3,37,0);});
    const auto deadline=std::chrono::steady_clock::now()+2s;
    const bool header=dsbridge::receive_handshake(pair.server,bytes.data(),8,deadline,stopping);
    const bool busid=dsbridge::receive_handshake(pair.server,bytes.data()+8,32,deadline,stopping);
    writer.join();verify(header&&busid&&bytes==expected,"valid fragmented handshake changed");
    shutdown(pair.client,SD_SEND);
    verify(!dsbridge::receive_handshake(pair.server,bytes.data(),1,deadline,stopping),"closed peer accepted");
  }
  std::cout<<"handshake checks passed: fragmented headers, silent peer, slow trickle, peer EOF, interruptible stop (ephemeral loopback only)\n";
}
