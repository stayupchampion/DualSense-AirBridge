// SPDX-License-Identifier: MIT
#pragma once
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <winsock2.h>
#include <algorithm>
#include <atomic>
#include <chrono>
#include <climits>
#include <cstddef>

namespace dsbridge {
// Only the pre-import header/busid uses this deadline. Once imported, the existing
// HID/ISO receive path is untouched. The listener is the sole reader of this socket.
inline bool receive_handshake(SOCKET socket,void* destination,std::size_t length,
    std::chrono::steady_clock::time_point deadline,const std::atomic_bool& stopping) {
  auto* bytes=static_cast<char*>(destination);
  while(length) {
    if(stopping.load())return false;
    auto remaining=std::chrono::duration_cast<std::chrono::microseconds>(deadline-std::chrono::steady_clock::now());
    if(remaining.count()<=0)return false;
    auto slice=std::min(remaining,std::chrono::microseconds(25000));
    timeval wait{0,static_cast<long>(slice.count())};
    fd_set ready;FD_ZERO(&ready);FD_SET(socket,&ready);
    const auto available=select(0,&ready,nullptr,nullptr,&wait);
    if(available==SOCKET_ERROR)return false;
    if(available==0)continue;
    const int count=recv(socket,bytes,static_cast<int>(std::min<std::size_t>(length,INT_MAX)),0);
    if(count<=0)return false;
    bytes+=count;length-=static_cast<std::size_t>(count);
  }
  return !stopping.load()&&std::chrono::steady_clock::now()<=deadline;
}
}
