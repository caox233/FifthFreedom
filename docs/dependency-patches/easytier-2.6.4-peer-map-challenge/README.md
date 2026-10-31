# Exact current-map challenge observations

These additive patches extend the independently built
[nonce59 source](../../tasks/data-challenge-build.md). They do not modify that
successful recipe, its ten patches or any current Linux/Windows runtime pins.
They add a real `Instance` → `PeerManager` → `PeerMap` entry to the existing
authenticated Data challenge, with exact peer and connection membership checks.
There is no new admission factory, IPC command, automatic echo service or Public
activation.

`Instance::send_owned_data_challenge` forwards to its actual peer manager's map.
`PeerMap::send_data_challenge_on_connection` requires an explicit peer ID,
connection ID, independently expected Noise static key, typed IPv4/UDP endpoints
and a 10 ms–3 s budget. It selects the exact existing objects and never chooses a
default connection, relay, different peer or historical protobuf record. `Peer`
only gains a crate-internal exact-connection lookup; callers receive no public
strong connection accessor.

Selection captures weak references to the exact map, peer and connection before
returning a future. An unpolled future retains none of those owners. Every table
guard is released before another lookup or an await. When polled, the operation
checks both the current map-to-peer pointer identity and the current
peer-to-connection pointer identity. Sending temporarily retains that connection
for the already bounded operation; cancellation drops the future and its real
inner nonce ticket. One original monotonic deadline covers selection, delayed
polling, sending, consuming a response and subsequent currentness reads.

The same double membership check runs after the send await, around consumption
and around response reads. The underlying ticket independently checks the actual
Noise peer/static key, session installation and synchronization revision, AEAD
and replay result, response nonce, reversed IPv4/UDP tuple and checksums. Dropping
an old attempt only clears its exact nonce slot. `PeerDataChallengeResponse` is
generated at most once per attempt and can be read repeatedly while the original
deadline and all checked objects remain current. It has no external constructor,
clone or deserializer.

These are **sampled, bounded membership observations**. The separate table reads
are not an atomic global snapshot, and membership may change immediately after
the last check. Once missing or different membership is actually observed, that
request is sticky-invalid even if the same objects are later reinserted. An
unobserved remove-and-reinsert of the exact same objects is not claimed to be
detected. Other tasks can retain an old map after an `Instance` is dropped;
therefore current map membership does not itself prove `Instance`, process, NIC,
driver or encrypted-overlay ownership, nor instantaneous revocation of all I/O.
The original session synchronization/grace boundary is unchanged.

The production changes are confined to `peer.rs`, `peer_map.rs`, a new
`peer_map_data_challenge.rs`, and a thin method in `instance/instance.rs`. The
test patch adds a test-only map replacement helper and real in-memory Noise
fixtures; no production constructor accepts synthetic identities. One positive
case calls the actual `Instance` method. It constructs the instance with P2P
disabled and replaces its STUN collector with the existing mock before yielding;
it never calls `Instance::run`, starts core, opens a listener or TUN, or runs a
game. The UDP echo is synthetic, and does not establish a remote application's
ownership or a kernel NIC path.

The twelve focused cases exercise actual accepted Data and existing nonce
objects: normal one-use response; wrong IDs/key/budget; no owners retained by an
unpolled future; removed peers with deliberately retained old connections;
same-ID replacement peer and connection objects; sticky observed loss after
reinsertion; map owner drop; membership changing during a delayed send;
cancellation and timeout; delayed first polling; and dropping an expired wrapper
after a new nonce is reserved. Queued-response negative cases wait for the real
client receive loop to deliver the authenticated packet before changing
membership, so a not-yet-arrived response cannot make those tests pass.

The [receipt](receipt.json) binds the exact 701-file source, patches, executable
and command evidence. All 12 new checks and the prior 59 checks passed on the
same preserved executable. Replacing both map-to-peer pointer checks with mere
presence checks compiled and yielded 11 passes and one assertion failure for a
same-ID replacement peer retaining the same connection. Exact source restoration
returned 12/12; the mutation was never adopted. Both patches were applied,
reversed to the complete baseline, and reapplied to the exact tested tree. Local
Rust checks do not establish a new hosted full core/CLI or Windows build. Future
owner IPC must retain and revalidate these actual objects together with its own
retained NIC and process chain; two JSON snapshots or a pipe heartbeat cannot be
combined into a launcher/native lease. Ordinary Public startup remains closed.
