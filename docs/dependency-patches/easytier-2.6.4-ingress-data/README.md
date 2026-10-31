# EasyTier 2.6.4: reject plaintext Data before secure-filter pass-through

Status: **tested local candidate patch; not an adopted runtime**.

The production `PeerSessionTunnelFilter::after_received` has early returns for
sender ID zero, a sender different from the directly connected peer, a missing
peer ID, and a missing session. With the previous secure-session guard already
present, these paths still returned plaintext Data without calling that guard.
This second patch requires encrypted Data before those early returns. It keeps
encrypted relay frames available for the manager's separate end-to-end session
validation, and leaves handshake/Ping/Pong controls and disabled-secure-mode
behavior unchanged.

Both patches are based on EasyTier commit
`8428a89d2dabc94c97d370ec607c6ca142473626`. This directory is a distinct additional
candidate; it does not change the identity or results of the
[first secure-session patch](../easytier-2.6.4-secure-session/README.md).

## Why the later checks do not replace this guard

The exact upstream receive path sends non-Ping/Pong packets from
[`PeerConn::start_recv_loop`](https://github.com/EasyTier/EasyTier/blob/8428a89d2dabc94c97d370ec607c6ca142473626/easytier/src/peers/peer_conn.rs#L1344)
to the manager. For locally addressed packets,
[`start_peer_recv`](https://github.com/EasyTier/EasyTier/blob/8428a89d2dabc94c97d370ec607c6ca142473626/easytier/src/peers/peer_manager.rs#L1096)
in secure mode calls the relay decryptor only if the packet is still encrypted.
A plaintext pass-through does not take that branch. The ACL layer is not an AEAD
check; it is disabled by default and does not recover the physical connection's
authenticated peer identity. The final NIC processor rejects remaining
encrypted/compressed Data, not this plaintext condition.

An **encrypted** sender mismatch is different: valid relay traffic must retain
its original sender header. The manager's
[`RelayPeerMap::decrypt_if_needed`](https://github.com/EasyTier/EasyTier/blob/8428a89d2dabc94c97d370ec607c6ca142473626/easytier/src/peers/relay_peer_map.rs#L615)
looks up the sender/network session and calls the production session decryptor.
The new test checks that the filter preserves that encrypted frame byte for byte;
it does not claim to test relay authentication itself.

Existing `disable_relay_data` blocks forwarded Data and the foreign-network data
wrapper. It does not mean that locally addressed relayed Data is forbidden.
This patch does not rename that setting, add a new direct-only setting or disable
all upstream relay behavior. The project's exact direct-peer profile still needs
its explicit allowed-peer configuration and the separately owned runtime lease.

These are source-path observations and executed local API contracts, not a
demonstrated network exploit or an account/game traffic acceptance result.

## Executed results and retained inputs

| Stage | Exit | Result |
| --- | --- | --- |
| Previous session guard + new filter tests, production filter unchanged | 101 | 6 passed, 4 failed: plaintext zero/mismatched sender, missing peer, missing session. |
| Same source plus the new filter guard | 0 | 10 passed. |
| Previous secure-session contract after both guards | 0 | 4 passed. |
| Existing secure-datagram and peer-session units | 0 / 0 | 9 / 2 passed. |
| Existing exact in-memory secure PeerConn round trip | 0 | 1 passed. |

The ten filter tests call the actual production filter with fresh synthetic
sessions. Valid encrypted data decrypts normally; replay and a modified tag are
rejected. Where a session exists, rejecting a malformed packet is followed by
acceptance of its untouched valid control. The encrypted relay compatibility
case, Ping/Pong/Noise controls and legacy-mode case prevent a blanket-drop fix
from passing. The existing PeerConn positive test uses `create_ring_tunnel_pair`
(memory buffers) and `MockStunInfoCollector`; it exercises a real Noise handshake
and the production filter without a socket, TUN or core runtime.

[receipt.json](receipt.json) records original, test-only, guard-only and final
source SHA256 values, both patch identities, commands and six raw-log hashes.
All four patches from the two investigations were applied in an isolated tree
and compared with the tested files, then reversed to exact original Git blobs.
The two production guards were also applied without test patches and compared
with the recorded guard-only source. Raw evidence remains ignored under
`.local/evidence/cloud/server07-ingress/`.

The build used the same Rust 1.95.0, GNU x86_64, default features and ring backend
as the first investigation. It does not reproduce the official musl/jemalloc
release. No core/CLI runtime, external network, driver or gameplay was started.
No new binary was distributed, and official artifact pins and the Public gate
remain unchanged. Preserve the upstream LGPL notice and corresponding sources
with any future separately reviewed distribution.

## Reproduction order

In a fresh exact upstream checkout, first apply the earlier `contract-tests.patch`
and `require-encrypted-flag.patch`. Then apply
[ingress-contract-tests.patch](ingress-contract-tests.patch) alone and run:

```sh
cargo +1.95.0 test --locked -p easytier --lib fifth_freedom_ingress_contract -- --nocapture
# Expected before: exit 101, 6 passed / 4 failed.
```

Apply [require-encrypted-data-ingress.patch](require-encrypted-data-ingress.patch)
and repeat that command: expected exit 0, 10 passed. Then run the three earlier
unit filters and the exact existing in-memory positive control:

```sh
cargo +1.95.0 test --locked -p easytier --lib fifth_freedom_secure_profile_contract -- --nocapture
cargo +1.95.0 test --locked -p easytier --lib peers::secure_datagram::tests -- --nocapture
cargo +1.95.0 test --locked -p easytier --lib peers::peer_session::tests -- --nocapture
cargo +1.95.0 test --locked -p easytier --lib peers::peer_conn::tests::peer_conn_secure_mode_data_roundtrip -- --exact --nocapture
```

The earlier README documents this workspace's C-header/linker/protoc adjustments.
Do not replace a failing test result with a compilation failure or run the full
upstream networking test suite as part of this bounded reproduction.
