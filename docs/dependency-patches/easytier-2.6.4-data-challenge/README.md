# One-use authenticated Data response correlation

This independent source candidate extends the reviewed authenticated-receive
candidate. It is **not adopted** by the successful Linux 44-check recipe, the
Linux guard3 runtime matrix, or any Windows recipe/runtime. Those identities and
ordinary Public startup remain unchanged. It adds no admission factory.

The production patch adds `peer_conn_data_challenge.rs` and a small connection to
the actual `PeerSessionTunnelFilter::after_received` acceptance path. The separate
test patch adds production-linked tests. Apply them, in the receipt's order, to
the exact authenticated-receive baseline identified in `receipt.json`; reversing
both restores that complete baseline, including absence of the newly added file.

`PeerConn::send_data_challenge(expected_peer_id, expected_static_key, endpoints,
timeout)` requires this exact connection's real Noise result to have auth level 3
and the supplied expected static key/peer ID. The expected key remains caller
policy, not a key learned from a received JSON observation. The method reserves
one pending slot before sending, uses a new OS-random 32-byte nonce, and binds it
to the current session, installation, generation and even synchronization revision.
It sends a valid IPv4/UDP packet through the existing `PeerConn::send_msg` Data
and encryption path. Endpoints are packet fields, not interface ownership proof.

The UDP payload is the versioned `FF-DATA-CHALLENGE-V1` marker, a zero terminator
and the nonce. The fixed IP datagram is 81 bytes. A responder must return that
payload with the IPv4 addresses and UDP ports reversed. Request IPv4 and UDP
checksums are computed, including the UDP pseudo-header and the specified
`0xffff` representation for a computed zero checksum. The receive matcher requires
exact datagram/IP/UDP lengths, IPv4 header length 5, no fragment offset/MF/reserved
flag, UDP protocol, both reversed addresses and ports, valid header and nonzero UDP
checksums, and exact marker/nonce. Compressed packets do not complete a challenge.
This deliberately bounded exchange does not implement a general IP parser.

Completion is possible only after the production receive filter has accepted
locally addressed encrypted Data from the current peer and actual session AEAD
and replay checks have succeeded. The same installation, generation and even sync
revision must still hold; unrelated recently accepted Data cannot complete it.
The slot records a matching response only once. It does not consume, modify or
reroute ordinary received packets, which continue through the existing pipeline.
Plaintext, relay pass-through, malformed, tag-invalid, replayed, wrong-peer,
wrong-tuple or wrong-nonce packets cannot complete the attempt.

The deadline is monotonic and bounded from 10 milliseconds to 3 seconds. The
production send future owns its ticket, with no mutex held across an await.
Failure, cancellation, timeout and dropping a ticket release only that ticket's
slot. Post-send checks reject an attempt that lost its owner/session/revision or
expired while the send was pending. A fast response received before send completes
is retained. Dropping an expired old attempt cannot clear a replacement attempt.

`DataChallenge::take_response` can return one `DataChallengeResponse` object only
once. The response's `round_trip_if_current` is a repeatable **read** of that
observation while its exact connection/session and original deadline remain valid;
it is not a globally single-use authorization. Neither type has a public constructor,
Clone/Debug or serde implementation. Weak references do not prolong the connection
or session. Sync, replacement, changed peer IDs, close, owner loss and expiry make
retained responses unusable. In particular, queued success cannot cross a same-Arc,
same-generation root-key sync or a generation round trip.

Local evidence is **15 new checks plus all 44 retained checks**, with zero ignored
or failed tests on the final source. One positive uses a real in-memory Noise
handshake, an independently configured synthetic server-key anchor and the actual
`PeerConn::send_msg` encryption/receive path in both directions. Its UDP echo
response models the remote kernel/service; it does not open a UDP listener or TUN.
Raw request bytes are also checked by an independent one's-complement checksum
sum, rather than only the packet-building library's matching parser. Send
cancellation/error/deadline tests exercise the same production helper with bounded
injected send futures; they do not claim a real congested network measurement.

A deliberate mutation replacing response nonce equality with length-only checking
compiled and ran: **13 passed, 2 assertion failures**, zero ignored, exit 101. Both
failures concern wrong/stale responses. The exact final source was then restored
and its preserved, hash-bound test executable passed all 15 again. This verifies
the negative tests; it is not an allegation of an upstream vulnerability. The
receipt preserves exact source, patch, executable and log hashes. Local raw logs,
originals and both test executables remain private. Earlier compiler/cancelled
attempts are retained separately and are not counted as failing contract tests.

This result proves packet correlation through the real local encryption/decryption
code. It does **not** prove local NIC injection, TUN traversal, a remote UDP
service's process identity, remote application policy, or that the remote endpoint
has completed a newly synchronized key epoch outside the existing sync/grace
policy. There is no automatically installed responder, core ownership IPC,
launcher/native lease, new full core/CLI or Windows build, runtime publication or
production deployment. The next adoption needs its own clean-source recipe with
the explicitly added source file, then bounded owned-runtime/endpoint evidence;
none of the prior successful binary identities may be relabelled as this source.
