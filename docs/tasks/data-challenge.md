# Current-session Data challenge correlation

Status: **ready-for-agent**. The independent source slice is implemented and
locally tested; ordinary Public startup remains closed.

The [new patch and evidence](../dependency-patches/easytier-2.6.4-data-challenge/README.md)
provide a real `PeerConn::send_data_challenge` entry. It sends a fresh random nonce
in a bounded IPv4/UDP Data packet through that connection's existing encryption
path. A one-use response object is created only when the production receive filter
accepts the matching reverse tuple/checksums/nonce after AEAD and replay validation,
while the exact session and sync revision are still current. Send cancellation,
late completion, timeout, duplicate/stale replies, close and owner loss cannot
turn into a successful current observation.

Fifteen new tests and the existing forty-four checks passed locally. The positive
uses an actual in-memory Noise handshake and both production send/receive paths;
the remote UDP echo is synthetic. Deliberately removing exact nonce matching
caused two real assertion failures, then restoring the final source returned all
fifteen to passing. This establishes correlation, not a deployed active-liveness
service, kernel/TUN traversal, endpoint process ownership or a Public lease.

The [hosted auth44 build](authenticated-receive-build.md) remains a separate,
earlier source identity; it does not include this patch. No existing source lock,
runtime/driver pin, CI runtime matrix or Windows recipe was changed here. Adoption
must preserve the old identity, add the exact new module to a separately reviewed
clean-source recipe, and rebuild/test that recipe before any owned network probe.

Next work must combine this exact packet result with the real retained core
process and kernel-session observations through owned IPC. Each side still needs
its independently verified process/package/config and expected peer policy. An
adapter name, configured source address, serialised receive age, plaintext Ping/Pong,
CLI auth history or pipe heartbeat cannot replace those owners. The remote
responder and local source/interface route also need explicit ownership. Continue
the full launcher/UI/LAN/public-server migration and dependency/privacy closure;
this prerequisite neither replaces those goals nor reopens accepted gameplay
verification.
