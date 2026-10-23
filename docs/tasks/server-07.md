# SERVER-07 — encrypted overlay and owned lifecycle prerequisites

Status: **needs-triage** — the bounded upstream-source audit and finite local
secure-session contract test are complete, with a tested six-line candidate
guard. Remaining ingress paths and production overlay ownership are not yet
ready for a trustworthy runtime probe. No EasyTier core/CLI runtime was started
and no native Public gate was changed. SERVER-06's real interface-bound socket
tests remain a separate, useful prerequisite.

## Evidence and execution boundary

This audit inspected upstream EasyTier **2.6.4**, exact official tag revision
`8428a89d2dabc94c97d370ec607c6ca142473626`. The Linux x86_64 archive and its core/CLI
members were already independently verified in
[public-network-dependencies.md](public-network-dependencies.md) and the fixed
[dependency lock](../../scripts/public-network-dependencies.lock.json). Hash
verification identifies the official bytes; it is not a reproduced build or an
independent confirmation of every enabled Rust feature.

The current cloud process has no effective `CAP_NET_ADMIN`, `CAP_NET_RAW` or
`CAP_SYS_ADMIN`; `/dev/net/tun` is absent. These were read from `/proc/self/status`
and the filesystem without printing environment variables or changing privileges.
No multi-node, TUN, packet, driver or gameplay test is claimed. The inspected
source and SHA256 receipts are in ignored
`.local/evidence/cloud/server07-source/`. Runtime work would require an explicitly
capable GitHub-hosted Linux runner and a failing preflight when capabilities or
an existing TUN device are missing; an unavailable test must not silently pass.

## Exact STUN initialization path

The two visible TOML lists do not disable every STUN path in this revision:

1. [`GlobalCtx::new`](https://github.com/EasyTier/EasyTier/blob/8428a89d2dabc94c97d370ec607c6ca142473626/easytier/src/common/global_ctx.rs#L282)
   constructs `StunInfoCollector::new_with_default_servers()`. It then replaces
   the UDP and IPv6 lists when `stun_servers` or `stun_servers_v6` is present.
2. [`new_with_default_servers`](https://github.com/EasyTier/EasyTier/blob/8428a89d2dabc94c97d370ec607c6ca142473626/easytier/src/common/stun.rs#L1096)
   initializes **three** lists, including `get_default_tcp_servers()`.
   [`Config`](https://github.com/EasyTier/EasyTier/blob/8428a89d2dabc94c97d370ec607c6ca142473626/easytier/src/common/config.rs#L526)
   exposes the UDP/IPv6 fields but no corresponding TCP STUN field. A setter
   exists inside the collector, but that is not an exposed configuration setting.
3. [`get_stun_info`](https://github.com/EasyTier/EasyTier/blob/8428a89d2dabc94c97d370ec607c6ca142473626/easytier/src/common/stun.rs#L936)
   starts the collector routines. The TCP task at
   [`start_stun_routine`](https://github.com/EasyTier/EasyTier/blob/8428a89d2dabc94c97d370ec607c6ca142473626/easytier/src/common/stun.rs#L1241)
   uses the retained TCP list. Peer information generation calls this getter in
   [`peer_manager.rs`](https://github.com/EasyTier/EasyTier/blob/8428a89d2dabc94c97d370ec607c6ca142473626/easytier/src/peers/peer_manager.rs#L1970).

Thus `stun_servers=[]`, `stun_servers_v6=[]` and the hole-punching flags cannot be
reported as proof that the original binary makes no automatic STUN attempts.
An isolated probe could prohibit external traffic with owned namespaces, no
default routes, and namespace-local egress rules allowing only the explicit
private underlay peer/port and required owned interfaces. This would **contain**
the upstream behavior, not remove it. Denied attempts must be described accurately.
If the required contract is that the process itself makes no STUN attempt, a
reviewed upstream change and separately identified rebuild are needed; the
original official binary digest cannot be retained for a modified build.

## Handshake, receive and TUN path

The source establishes why an `enable_encryption` flag alone is insufficient
evidence of mandatory authenticated encryption. It does **not** establish a
demonstrated unauthenticated exploit or a completed upstream security audit.

| Stage | Exact source and observed behavior |
| --- | --- |
| Algorithm selection | [`config.rs`](https://github.com/EasyTier/EasyTier/blob/8428a89d2dabc94c97d370ec607c6ca142473626/easytier/src/common/config.rs#L136) selects an AEAD default when built with supported features, otherwise XOR; [`encrypt/mod.rs`](https://github.com/EasyTier/EasyTier/blob/8428a89d2dabc94c97d370ec607c6ca142473626/easytier/src/peers/encrypt/mod.rs#L59) falls back to the build's default for an unknown algorithm. For AES, backend selection is OpenSSL, then wireguard/ring, then aes-gcm according to compiled features. |
| Legacy/secure choice | [`PeerConn::is_secure_mode_enabled`](https://github.com/EasyTier/EasyTier/blob/8428a89d2dabc94c97d370ec607c6ca142473626/easytier/src/peers/peer_conn.rs#L414) defaults false when the separate secure-mode configuration is absent. `enable_encryption=true` does not itself select Noise secure mode. |
| Initial handshake | [`do_handshake_as_server_ext`](https://github.com/EasyTier/EasyTier/blob/8428a89d2dabc94c97d370ec607c6ca142473626/easytier/src/peers/peer_conn.rs#L1234) has both a secure Noise branch and a legacy handshake branch. This is an initial parsing path, not sufficient proof of peer admission. |
| Same-network admission | [`add_new_peer_conn`](https://github.com/EasyTier/EasyTier/blob/8428a89d2dabc94c97d370ec607c6ca142473626/easytier/src/peers/peer_manager.rs#L534) rejects a mismatch between local secure mode and the presence of the peer's Noise public key. Therefore the legacy initial branch does **not** by itself demonstrate a successful same-network secure-mode downgrade. It also checks network identity; the empty-digest/credential branch compares network names, which must be assessed with the prior credential/Noise admission path. |
| Session filter | [`PeerConn` construction](https://github.com/EasyTier/EasyTier/blob/8428a89d2dabc94c97d370ec607c6ca142473626/easytier/src/peers/peer_conn.rs#L341) installs `PeerSessionTunnelFilter` before the receiver. Its [`after_received`](https://github.com/EasyTier/EasyTier/blob/8428a89d2dabc94c97d370ec607c6ca142473626/easytier/src/peers/peer_conn.rs#L180) calls `session.decrypt_payload` for a matching secure session or passes selected header/session cases onward. The call itself is not yet proof of mandatory AEAD verification; reachability must be evaluated with admitted peer and routing identities. |
| Registered receive | [`PeerMap::add_new_peer_conn`](https://github.com/EasyTier/EasyTier/blob/8428a89d2dabc94c97d370ec607c6ca142473626/easytier/src/peers/peer_map.rs#L60) reaches [`Peer::add_peer_conn`](https://github.com/EasyTier/EasyTier/blob/8428a89d2dabc94c97d370ec607c6ca142473626/easytier/src/peers/peer.rs#L169), which starts the peer receive loop. [`start_recv_loop`](https://github.com/EasyTier/EasyTier/blob/8428a89d2dabc94c97d370ec607c6ca142473626/easytier/src/peers/peer_conn.rs#L1344) handles Ping/Pong and sends other packets to the manager channel. |
| Payload handling | [`peer_manager.rs`](https://github.com/EasyTier/EasyTier/blob/8428a89d2dabc94c97d370ec607c6ca142473626/easytier/src/peers/peer_manager.rs#L1102) calls the configured legacy decryptor outside secure mode; the secure/relay handling is a different branch. The legacy [`ring`](https://github.com/EasyTier/EasyTier/blob/8428a89d2dabc94c97d370ec607c6ca142473626/easytier/src/peers/encrypt/ring.rs#L79), [`OpenSSL`](https://github.com/EasyTier/EasyTier/blob/8428a89d2dabc94c97d370ec607c6ca142473626/easytier/src/peers/encrypt/openssl.rs#L49) and [`aes-gcm`](https://github.com/EasyTier/EasyTier/blob/8428a89d2dabc94c97d370ec607c6ca142473626/easytier/src/peers/encrypt/aes_gcm.rs#L36) implementations all return without decrypting when the encrypted bit is absent. This is a source-level policy observation, not proof that an unauthenticated sender can reach it. |
| ACL and NIC delivery | The manager then decompresses, applies ACL checks and dispatches packet pipelines. [`NicPacketProcessor`](https://github.com/EasyTier/EasyTier/blob/8428a89d2dabc94c97d370ec607c6ca142473626/easytier/src/peers/peer_manager.rs#L1184) rejects Data packets still marked encrypted/compressed before sending decoded data to the NIC channel. [`NicCtx::do_forward_peers_to_nic`](https://github.com/EasyTier/EasyTier/blob/8428a89d2dabc94c97d370ec607c6ca142473626/easytier/src/instance/virtual_nic.rs#L996) writes received channel packets to the TUN sink. |

The bounded proof obligation is the exact Public profile's reachable account/game
Data ingress and egress: explicit secure mode and allowed peers, with unused
legacy/credential/relay paths disabled or rejected. It does not require auditing
all upstream features. A successful two-node exchange shows the selected path
works, but does not replace negative cases at this specific trust boundary.

One concrete negative-test target is
[`SecureDatagram::decrypt_payload`](https://github.com/EasyTier/EasyTier/blob/8428a89d2dabc94c97d370ec607c6ca142473626/easytier/src/peers/secure_datagram.rs#L722):
it checks session validity, parses a nonce tail and checks replay state, then calls
the configured decryptor. It does not first require the encrypted bit, while the
inspected decryptor implementations return success when that bit is absent. The
minimal proposed guard/test is to reject a plaintext or flag-cleared Data frame
with a syntactically valid tail before accepting it as authenticated. The selected
direct-peer profile also needs negative tests for sender ID zero/mismatch and
disabled relay admission. The finite matching-session guard has now been tested
as a local source candidate (below); those other ingress paths remain unresolved.
The actual outer QUIC/WSS behavior is recorded below so transport labels are not
mistaken for authentication guarantees.

No crafted traffic was sent and no vulnerability was reproduced. These source
facts must not be presented as “EasyTier encryption is broken.”

The outer transports are not independent proof of peer authentication for this
profile. The exact revision's
[`quic.rs`](https://github.com/EasyTier/EasyTier/blob/8428a89d2dabc94c97d370ec607c6ca142473626/easytier/src/tunnel/quic.rs#L41)
uses `quinn_plaintext::{server_config,client_config}`. Its locked
[`quinn-plaintext` 0.3.0 source](https://docs.rs/crate/quinn-plaintext/0.3.0/source/src/lib.rs)
was read from the Cargo archive, whose SHA256
`f3e617feaeb6493018fa35fc47ae8b630ac8903d8159e9e747018841b99bad3d`
matches `Cargo.lock`: `PlaintextPacketKey` leaves payload bytes unencrypted and
uses an unkeyed eight-byte SeaHasher checksum; `PlaintextHeaderKey` does nothing,
and `peer_identity()` returns `None`. This is an implementation observation,
not an assumption based on the dependency's name. The WSS client calls
[`get_insecure_tls_client_config`](https://github.com/EasyTier/EasyTier/blob/8428a89d2dabc94c97d370ec607c6ca142473626/easytier/src/tunnel/websocket.rs#L247),
whose
[`verify_server_cert`](https://github.com/EasyTier/EasyTier/blob/8428a89d2dabc94c97d370ec607c6ca142473626/easytier/src/tunnel/insecure_tls.rs#L16)
accepts any server certificate; the generated server certificate is self-signed.
WSS therefore does not establish the expected remote identity independently of
the inner session policy. These observations do not remove Noise admission or
establish a remotely reachable exploit; the finite inner-session contract
has separately recorded execution evidence below.

## Executed finite secure-session contract

The [candidate patch and reproduction receipt](../dependency-patches/easytier-2.6.4-secure-session/README.md)
use an ignored checkout of that exact official commit and its real `PeerSession`
encrypt/decrypt APIs, fresh synthetic sessions and the default-feature ring
AES-256-GCM backend. With production source untouched, the four tests returned
**3 passed / 1 failed**: a real encrypted frame with only its encrypted flag
cleared was accepted. Valid encrypted control, a modified authentication tag and
an authenticated-frame replay behaved as expected. An initial compilation error
for a missing bindgen C header was separately preserved and is not counted as
test evidence.

A six-line guard in `SecureDatagramSession::decrypt_payload` now requires the
encrypted flag before tail/replay processing. The identical four tests then
passed; the existing nine secure-datagram and two peer-session unit tests also
passed. Both malformed-frame tests verify that rejecting the modified clone
does not consume the untouched original sequence. Exact source/patch/log hashes
and forward/reverse application checks are retained in the receipt.

This confirms the limited secure-session API contract and a local candidate fix.
It does not reproduce a remotely reachable exploit or complete the sender-ID,
filter/pass-through, routing, allowed-peer or owned-lifecycle contracts. The
GNU/default-feature test binary is not the official musl/jemalloc release binary.
No patched runtime has been distributed or adopted, and the official artifact
lock still identifies the original unmodified bytes.

## Production ownership needed before a lifecycle claim

The new native `SocketPolicy::interface_bound` constrains address, device and
interface generation when sockets are created. It does not own an EasyTier child
or revoke already accepted connections after that child exits. A test-only
process supervisor cannot substitute for the missing production behavior.

The next production contract needs an owned overlay lease with:

- An explicitly verified executable and owned process handle, private explicit
  configuration, namespace identity, interface generation and overlay address.
  It must not adopt another operator's process/interface by name.
- Readiness only after the owned process, expected interface/address and reviewed
  authenticated-session policy are valid. Loss or replacement must be a sticky
  revocation for that lease, not an automatic fallback to LAN/wildcard sockets.
- A child-exit watcher that revokes application readiness and shuts down owned
  listeners **and already accepted connections**, with bounded awaited cleanup.
  Dropping one listener handle or waiting for packets to time out is insufficient.
- Cancellation/failed-start/late-ready behavior and cleanup receipts that only
  refer to resources this lease created. A same-name replacement or unrelated
  process must remain untouched.

This is an implementation contract to bind into production startup/lifecycle and
exercise through the production SocketPolicy probe; no unused owner/probe helper
has been added here. The native Public gate remains closed while that connection
and the ingress-security policy are unresolved.

## Conditions for a future isolated hosted test

After the policy and production lease are reviewed, a candidate test can use two
owned network namespaces linked only by a private veth pair, explicit numeric
peer addresses, synthetic per-run identity/secret, fixed overlay IPv4 addresses,
disabled IPv6/UPnP/hole punching/exit-node/proxy discovery, empty UDP/IPv6 STUN
lists, and the egress containment above. No existing host namespace route or
operator configuration should be modified. The fixed archive/member hashes must
be checked before execution; downloaded executables and private configuration
must remain outside candidate artifacts.

The observable checks should include genuine native SocketPolicy traffic over
the owned overlay, wrong-secret rejection, an explicit plaintext positive control
for any synthetic packet-inspection method, and evidence that the tested encrypted
data path carries no plaintext marker on the physical veth. Flags and absence of
a marker alone do not prove authenticated encryption: sampled encrypted frames,
packet classification and the validated ingress policy must support the exact
claim. Packet observations must be synthetic, bounded, in memory, and unrelated
to gameplay or a real user's data. Only fixed outcomes/counts belong in receipts.

Terminate the exact owned EasyTier handle and assert readiness revocation, closure
of existing protected TCP sessions and rejection of new traffic through the real
production lease. Mere network loss is not evidence of lifecycle revocation.

Two additional source facts constrain the runner: [`VirtualNic::ensure_tun_device_node`](https://github.com/EasyTier/EasyTier/blob/8428a89d2dabc94c97d370ec607c6ca142473626/easytier/src/instance/virtual_nic.rs#L278)
attempts `mknod` if TUN is absent, so preflight must require an existing usable
device and never install/load a driver or create the device. Also the
[`core` startup path](https://github.com/EasyTier/EasyTier/blob/8428a89d2dabc94c97d370ec607c6ca142473626/easytier/src/core.rs#L1444)
logs `cfg.dump()`, whose [`implementation`](https://github.com/EasyTier/EasyTier/blob/8428a89d2dabc94c97d370ec607c6ca142473626/easytier/src/common/config.rs#L1035)
serializes configuration including network identity. Raw core output/configuration
must therefore never enter public workflow logs/artifacts, even when a test uses
synthetic credentials. Explicit environment isolation must also prevent inherited
`ET_CONFIG_SERVER`, config-directory or peer settings from adding an instance or
contacting a service; `--disable-env-parsing` alone is not proof that Clap's
environment-backed CLI options have been removed.

Independent source review confirmed the same-network secure-mode rejection and
the unresolved receive-policy distinction. This task ends with this bounded
source/design evidence, not a runtime pass, new vulnerability claim, deployment
or completion of the Linux public-server milestone.
