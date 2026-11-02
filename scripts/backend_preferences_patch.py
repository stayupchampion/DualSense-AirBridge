"""Read cached battery data and enforce a speaker-only mute at the HID writer."""

def one(s,old,new):
    if s.count(old)!=1:
        raise RuntimeError(f'Preferences patch expected one match: {old[:100]!r}; got {s.count(old)}')
    return s.replace(old,new,1)

def daemon(s):
    s=one(s,'#include "bridge_runtime.hh"','#include "bridge_runtime.hh"\n#include "controller_preferences.hh"')
    s=one(s,'''    const auto report = bt_input_to_usb_input(frame.payload);
    if (report) {''','''    const auto report = bt_input_to_usb_input(frame.payload);
    if (report) {
      dsbridge::battery_sample.store((GetTickCount64() << 8) | report->at(53), std::memory_order_relaxed);''')
    # Use the existing audio worker for a single state update when the preference
    # changes. Normal packet/input pacing is untouched; no new hardware thread.
    s=one(s,'''  while (!g_stop_requested.load() && !bridge_restart_requested.load()) {
    {
      std::lock_guard guard(state.mutex);
      const auto now = Clock::now();''','''  std::uint64_t applied_speaker_revision=0;
  while (!g_stop_requested.load() && !bridge_restart_requested.load()) {
      const auto revision=dsbridge::speaker_revision.load();
      if(revision!=applied_speaker_revision) {
        vds::BtStateReport report;
        { std::lock_guard guard(state.mutex); report=state.output_state.build_bt_state_report(); }
        // Re-apply the game's retained speaker state when unmuting, even if its
        // last update did not include these enable bits. The write filter only
        // changes the outgoing copy, so game mute/volume values stay intact.
        report[3]|=0x20; report[4]|=2;
        vds::fill_output_report_checksum(report);
        if(try_send_bt_output_report(bluetooth,report,logger,bluetooth_mutex,"speaker preference"))
          applied_speaker_revision=revision;
      }
    {
      std::lock_guard guard(state.mutex);
      const auto now = Clock::now();''')
    s=one(s,'''  std::vector<vds::VdsdControlControllerStatus> controller_statuses;
  controller_statuses.reserve(workers.size());''','''  try {
    const auto fields=vds::parse_jsonl_object(command,"managed control");
    const auto name=vds::require_jsonl_string(fields,"command","managed control");
    if(name=="bridge-status"||name=="speaker") {
      if(name=="speaker") {
        const std::array<std::string_view,2> allowed{"command","mode"};
        vds::reject_unknown_jsonl_fields(fields,allowed,"speaker preference");
        const auto mode=vds::require_jsonl_string(fields,"mode","speaker preference");
        if(mode!="mute"&&mode!="unmute")return "{\\"OK\\":false,\\"error\\":\\"Invalid speaker mode\\"}\\n";
        const bool muted=mode=="mute";
        if(dsbridge::speaker_muted.exchange(muted)!=muted)++dsbridge::speaker_revision;
      }
      const bool connected=workers.size()==1&&workers.front()->ready.load()&&worker_is_connected(*workers.front());
      const auto sample=dsbridge::battery_sample.load(std::memory_order_relaxed);
      const auto age=sample==0?0:GetTickCount64()-(sample>>8);
      return "{\\"OK\\":true,"+vds::jsonl_bool_field("connected",connected)+","+
          vds::jsonl_string_field("address",connected?workers.front()->address:"")+","+
          "\\"batteryRaw\\":"+std::to_string(connected&&sample!=0?static_cast<int>(sample&255):-1)+","+
          "\\"batteryAgeMs\\":"+std::to_string(age)+","+
          vds::jsonl_bool_field("speakerMuted",dsbridge::speaker_muted.load())+"}\\n";
    }
  }catch(const std::exception&) {
    return "{\\"OK\\":false,\\"error\\":\\"Invalid managed request\\"}\\n";
  }
  std::vector<vds::VdsdControlControllerStatus> controller_statuses;
  controller_statuses.reserve(workers.size());''')
    s=one(s,'"singleConnectionSession":true','"singleConnectionSession":true,"controllerPreferences":true')
    return s

def transport(s):
    s=one(s,'#include "vds_bt.hh"','#include "vds_bt.hh"\n#include "controller_preferences.hh"')
    s=one(s,'''  struct QueuedOutputWrite {
    std::vector<std::uint8_t> buffer;
  };''','''  struct QueuedOutputWrite {
    std::vector<std::uint8_t> buffer;
    std::size_t report_size=0;
  };''')
    s=one(s,'return QueuedOutputWrite{std::move(buffer)};','return QueuedOutputWrite{std::move(buffer),report.size()};')
    # Filter immediately before WriteFile, after dequeue, so a queued game
    # report cannot undo a newly selected mute preference.
    # Both final shutdown and ordinary writes are targeted below using the
    # complete enclosing lines to keep a mismatch fail-closed.
    s=one(s,'''    if (!WriteFile(output_handle_.get(), write.buffer.data(),''','''    dsbridge::mute_speaker_report(std::span(write.buffer).first(write.report_size),dsbridge::speaker_muted.load());
    if (!WriteFile(output_handle_.get(), write.buffer.data(),''')
    s=one(s,'''      try {
        std::size_t offset = 0;
        while (offset < write.buffer.size()) {''','''      try {
        dsbridge::mute_speaker_report(std::span(write.buffer).first(write.report_size),dsbridge::speaker_muted.load());
        std::size_t offset = 0;
        while (offset < write.buffer.size()) {''')
    return s
