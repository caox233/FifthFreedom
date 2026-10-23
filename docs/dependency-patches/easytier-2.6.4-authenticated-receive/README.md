# Local authenticated Data receive observation

Status: **separate source candidate; no runtime adoption or Public admission**.
These patches add observations to the real `PeerSessionTunnelFilter` and expose
them through the actual `PeerConn`, based on the unchanged
[guard3 source recipe](../../../scripts/easytier-source.lock.json). They do not
replace that recipe, the earlier Windows nine-patch recipe or their build receipts.

The existing `PeerConn::get_conn_info` projects the completed Noise handshake's
key and authentication level. `PeerSessionTunnelFilter::should_skip_encrypt`
explicitly excludes Ping/Pong from session encryption. Querying that JSON again,
seeing `is_closed=false`, or receiving another Ping/Pong therefore does not prove
recent authenticated peer traffic. Existing bounded traffic probes retain their
original scope; this source change is not retroactive runtime evidence.

`record-authenticated-data.patch` records `std::time::Instant` only after a
locally addressed encrypted Data frame passes the current session's real AEAD
and replay checks. Legacy, plaintext, handshake/Ping/Pong, RPC and encrypted relay
pass-through paths cannot refresh it. This observation does not alter their
existing packet behavior. Reads also require the live connection's actual Noise
session, the same session installation and generation, and a valid session.

An additional **nonsecret local sync revision** is necessary: different connections
may share a `PeerSession`, and `PeerSessionStore::apply_initiator_action` can
synchronize that Arc even when its u32 generation remains the same. `sync_root_key` serializes its revision,
marks it unavailable before changing the datagram state, and publishes a new even
revision only after completion. Checked exhaustion permanently disables these
observations. Data receipt and retained observations compare the revision, so
same-generation sync and generation round trips cannot revive an old observation.
No root key, private key, token or serializable constructor is added to the type.

`AuthenticatedDataObservation` keeps only Weak references. It cannot keep the
filter, session or close notifier alive. `received_ago()` rechecks ownership,
installation, generation, sync revision and closure; it returns `None` after loss.
Its age uses the local monotonic clock. No new background monitor, IPC endpoint,
lease issuer, kernel-session assertion or Public switch is introduced.

This is **the time of an accepted authenticated Data receive**, under the existing
datagram key-sync and receive-grace policy. It is not a fresh remote challenge,
proof of a newly rotated remote key, automatic freshness while idle, authorized
peer policy, or a promise that the connection remains alive after a read. A future
owner must bind the expected peer key, current retained kernel session, owned core
process and a bounded active challenge before using this as lease input.

## Reproduction

Use a fresh copy of the exact six-patch guard3 source, retain its original lock,
then apply the production patch and `authenticated-receive-tests.patch` in that
order. The patches touch only `easytier/src/peers/peer_conn.rs` and
`easytier/src/peers/peer_session.rs`. Forward application must match the recorded
tested SHA256 values; reversing both must restore the recorded guard3 bytes.

```sh
cargo +1.95.0 test --offline --locked --target x86_64-unknown-linux-gnu \
  -p easytier --lib fifth_freedom_receive_ -- --test-threads=1
```

These focused tests call the production filter and decryptor with synthetic
AES-GCM sessions. The positive control also completes a real in-memory Noise
handshake through the production PeerConn and validates the public observation
method and owner drop. No core/CLI runtime, socket, TUN, driver or game is started.
The full upstream suite contains networking tests and is outside this command.

The initial observation implementation passed eleven tests. Independent review
identified the shared-Arc synchronization boundary; the added same-generation
and generation-roundtrip tests then genuinely failed (12 passed, 2 failed,
exit 101, no compiler errors). This was a defect in the initial new observation,
not a claimed upstream network exploit. The raw failure is retained separately
from an earlier discarded mutation compilation error, which is not test evidence.
Final results and source/log identities are recorded in [receipt.json](receipt.json).
After the revision fix, all 15 new checks passed with zero ignored. The same
compiled test executable then passed 29 existing secure-session, ingress,
datagram, explicit-pin and in-memory handshake checks. Both source patches were
applied and reversed, matching the tested and original guard3 hashes exactly.

The local build reuses the existing GNU target cache, Rust 1.95.0, unchanged default
features and ring backend. It does not establish a new full Windows build or a
published runtime. Original official artifact pins and the earlier successful
Windows build remain immutable. Preserve upstream LGPL-3.0 notices and matching
sources if a future separately reviewed candidate is distributed.
