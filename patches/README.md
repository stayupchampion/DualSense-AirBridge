# Managed vDS backend patch

Upstream: https://github.com/hurryman2212/vds/tree/ec531599dfdd02e14ce68efd8c0d2dcb439894a5
License: MIT (see ../licenses/vDS-MIT.txt).

`../scripts/patch-backend.py` copies the pinned source into `.local/buildsrc/vds` and emits the unified patch and per-file hashes here. Original downloaded source remains untouched.

Changes:

1. Foreground-only entry point, no Windows service dispatch; explicit private DB/log/pipe/stop-event required.
2. `--bridge-info` is a side-effect-free identity/protocol query.
3. No HidHide library is linked and no HidHide configuration is touched; the session manager owns transaction/rollback.
4. Open the manager's pre-existing manual-reset named event (SYNCHRONIZE only). A watcher signals the internal stop event and requests normal cleanup when it is set.
5. USB/IP loopback port 3240 uses SO_EXCLUSIVEADDRUSE; control pipe rejects remote clients and requires first-instance ownership.
6. `list` connected becomes true only after USB/IP import attachment and Bluetooth initialization. This does not prove Windows audio enumeration or game HD-haptics support.
7. Control-pipe IO waits are bounded to five seconds. A nonempty reply waits for the client to close or for a bounded read before the server disconnects. A/B software tests with a nonempty trace response reproduced zero bytes without this drain wait and valid JSON with it. Empty JSONL lists still close immediately. Clients must read one message/line and close, never wait for EOF for a nonempty reply.

Build:

    powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-backend.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-backend.ps1

Output: `.local/backend/vdsd.exe`, `.local/backend/backend-manifest.json`.
Opus v1.5.2 and MSVC runtime are statically linked. `dumpbin /dependents` showed only Windows DLL imports (WINMM, HID, SETUPAPI, bthprops, WS2_32, KERNEL32, ADVAPI32).

Runtime contract:

    vdsd.exe --db-path <absolute session JSONL> --log <absolute log>
             --pipe \\.\pipe\DsBridge-<GUID> --stop-event Local\DsBridge-<GUID>

The manager must create and retain the named event before starting this process, and must keep a recovery worker alive until the backend exits and HidHide state is verified. This backend has no automatic startup, no driver installation, and no HidHide rollback of its own. Abrupt power loss or simultaneous manager/worker termination cannot execute rollback; the persisted session journal is needed on the next launch.

The normal backend destructor detaches only its matching `usbip://127.0.0.1:3240/1-N` endpoint. No `detach --all` is added. Backend process completion is not by itself proof of successful detach: the manager must reread port/device state and report unresolved cleanup.

Historical initial tests were software-only. The empty-session stop test substitutes only the USB/IP executable presence check and never attaches a virtual port, installs a driver, writes physical HID output, or changes HidHide. Drivers were subsequently approved and installed. See ../docs/latency-and-exit-fix.md for current real-device evidence and unresolved game/audio issues.


0.1.1 runtime additions are in ../scripts/backend_runtime_patch.py and bridge_runtime.hh:
6 PCM chunks prime (~64 ms), 12 chunks burst capacity, discard enqueued data aged 100 ms, queue-age diagnostics, and a dequeue performed while holding the queue lock. These bound this queue, not end-to-end device latency.
After producer threads join, cancel and join the ordinary HID writer, then write neutral state and mic-close reports to the same physical device. No adapter reset is involved. Each final write has a 500 ms wait threshold; cancellation completion can take longer, so the manager's overall shutdown deadline remains in force.
The build also runs ../tests/backend_runtime_tests.cc. It verifies final nonzero force reset, output enable bits, LED preservation, CRC, queue expiry and burst bounds.

0.1.3 additionally removes HID-input head-of-line blocking from the USB/IP receive
thread via ../scripts/backend_async_hid_patch.py and usbip_hid_queue.hh. Pending
HID requests are bounded and cancellable. All paced HID/ISO replies carry a
session epoch checked under the send lock, preventing stale replies after socket
reuse. TCP_NODELAY and contiguous replies apply only to accepted loopback sockets.
After real-device verification, priming is now **3 chunks (~32 ms of PCM)** with a
35 ms timeout; burst capacity and 100 ms age limit remain unchanged. Tests cover
request pairing, cancellation, FIFO, bounds, clear and stale session epochs.
See ../docs/haptics-latency-optimization.md for the staged results and limits.

0.1.4 adds ../scripts/backend_diagnostics_patch.py and diagnostic_worker.hh.
Ordinary runtime Info/Debug logs use a 256-entry, 4096-byte-per-message bounded
try-lock queue; slow logging may lose diagnostics but never controller data.
Warn/Error and startup/shutdown/recovery records remain synchronous. Summary
formatting moves outside controller and HID-writer locks. The stop-state report
is issued before the newly moved underflow warning. Logger shutdown drains and
joins its worker after controller producers finish; no detached logging thread.
Priming, pacing, wire protocol and restoration parameters are unchanged.
Tests include a blocked sink, concurrent producers, loss accounting, critical
logs, drain/reopen and preservation of generated log schemas. Hardware acceptance
is separate from the 0.1.3 evidence. The 0.1.4 feedback/cancel/pulse test and
19 recovery observations passed, with user-confirmed normal haptics, trigger and
Bose playback. See ../docs/diagnostic-jitter-optimization.md for evidence/limits.
