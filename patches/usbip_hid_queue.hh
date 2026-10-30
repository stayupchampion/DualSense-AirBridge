// SPDX-License-Identifier: MIT
#pragma once
#include <algorithm>
#include <atomic>
#include <cstdint>
#include <deque>
#include <optional>
#include <utility>

namespace dsbridge {
// Transitions and the accepts check at send are serialized by socket_send_mutex.
// Producers may take atomic snapshots without holding the send mutex.
class ReplyEpoch {
  std::atomic_uint64_t epoch_{0};
public:
  std::uint64_t activate(){const auto next=(epoch_.load()+2)|std::uint64_t{1};epoch_=next;return next;}
  void invalidate(){epoch_=(epoch_.load()+1)&~std::uint64_t{1};}
  std::uint64_t current()const{return epoch_.load();}
  bool accepts(std::uint64_t token)const{return (token&1)!=0 && token==epoch_.load();}
};

// Caller owns synchronization. An input request must never wait on the USB/IP
// receive thread, which also accepts time-sensitive audio OUT submissions.
template<class Request, class Report> class PendingHidQueue {
  std::deque<Request> requests_;
  std::deque<Report> reports_;
public:
  bool submit(Request request) {
    if(requests_.size()>=64 || std::any_of(requests_.begin(),requests_.end(),
        [&](const auto& r){return r.seqnum==request.seqnum;}))return false;
    requests_.push_back(std::move(request)); return true;
  }
  void report(Report value) {
    if(reports_.size()>=8)reports_.pop_front();
    reports_.push_back(std::move(value));
  }
  std::optional<std::pair<Request,Report>> take_ready() {
    if(requests_.empty()||reports_.empty())return std::nullopt;
    auto result=std::pair{std::move(requests_.front()),std::move(reports_.front())};
    requests_.pop_front();reports_.pop_front();return result;
  }
  bool cancel(std::uint32_t seqnum) {
    return std::erase_if(requests_,[&](const auto& r){return r.seqnum==seqnum;})!=0;
  }
  void clear(){requests_.clear();reports_.clear();}
};
}
