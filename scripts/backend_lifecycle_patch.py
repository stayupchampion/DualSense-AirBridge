"""Initialize the listener safely and bound only its pre-import handshake reads."""
from backend_runtime_patch import one


def usbip(source):
    source = one(source, '#include "vds_usbip.hh"', '#include "vds_usbip.hh"\n#include "usbip_handshake.hh"')
    source = one(source, '  Server() : listener_thread([this] { listener_loop(); }) {', '''  Server() {
    // All members, including ready_mutex/ready_cv/startup_error, exist before
    // the listener can touch them. Member initializer order follows declaration.
    listener_thread = std::thread([this] { listener_loop(); });''')
    source = one(source, '''  void handle_client(Socket client) {
    OpCommonHeader header{};
    if (!recv_exact(client.get(), &header, sizeof(header))) {''', '''  void handle_client(Socket client) {
    // A local probe or partial request must not hold the listener or shutdown forever.
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(10);
    OpCommonHeader header{};
    if (!dsbridge::receive_handshake(client.get(), &header, sizeof(header), deadline, stop_requested)) {''')
    return one(source, '''    if (!recv_exact(client.get(), requested_busid.data(),
                    requested_busid.size())) {''', '''    if (!dsbridge::receive_handshake(client.get(), requested_busid.data(),
                    requested_busid.size(), deadline, stop_requested)) {''')
