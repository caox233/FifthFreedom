# 联测网络拒绝与端口 0

Status: **needs-triage**. Received 2026-10-31 (Asia/Hong_Kong).

The local reviewer supplied aggregate observations only. Raw traces, account or
player identifiers, addresses, credentials and ticket bytes are not public
fixtures. This task does not launch a game or collect a new trace.

## Protocol 10 / method 1

The reported host has **799 distinct** `InternalError` records: their timestamps,
calls and sessions differ. These are not one repeated log entry. The native
ticket error `PACKAGE_IDENTITY_REQUIRED` is separate from the new player's
launcher/server `release_id: RELEASE_MISMATCH` screen. Installing a newer package
does not by itself prove that every native ticket refusal will stop.

The source dispatch is `TicketGrantingProtocol.Login`, the plain native login
method. `ticket.rs::get_session_key` calls the actual SQLite admission in
`storage/gameplay.rs::create_bound_user_session`. Before inserting a native
ticket, that query requires a current, non-retired game run for the same user and
source IP, the server's exact approved release identity, and a heartbeat no older
than 90 seconds. Failure returns `PACKAGE_IDENTITY_REQUIRED: approved game run
required`, mapped to the observed RMC `InternalError`. A successful password or
account lookup alone does not admit that run. `bind_after_ticket` preserves the
transport's previous identity when issuance fails.

This identifies the refusal path and necessary conditions, **not which predicate
failed in those 799 attempts**. Missing run, source mismatch, stale heartbeat,
retirement and foreign release share this error. Do not turn it into success,
fabricate a game run, remove authentication, lengthen the freshness bound or
change the player's data to suppress the report. The existing bounded
`SCBL_NATIVE_LOGIN` diagnostic contains only booleans; no new account/session
fields are added here.

The existing PRUDP response cache is keyed by the request packet sequence and
replays a retransmission without calling the handler again. A new admitted
request may issue a new ticket; distinct calls are not treated as retransmissions
merely because they use the same method. The reported distinct records therefore
must not be explained by a duplicate logger or by the cache regression below.

## Real offline regressions

New `protocol_feedback_tests.rs` uses the actual RMC serializer, RVSec handler,
ticket producer and SQLite admission. Fresh synthetic accounts and temporary
databases are used. The positive game run is created through the existing
authenticated API/interceptor test path; a foreign historical row is a negative
fixture, never an admitted substitute.

- 64 distinct calls across fresh and previously bound synthetic transports are
  denied without writing a ticket, marking a user online or replacing identity.
- Actual admitted requests issue tickets; separate source, stale, retired and
  foreign-release controls return the same real refusal without another row.
- The feature-enabled PRUDP check enters the production dispatch/cache and
  captures the existing send leaf before socket I/O. For both denied and admitted
  requests, eight identical packet retransmissions return the exact original
  encoded response and keep the ticket-row count at zero or one. This includes
  real ACK/frame/checksum validation, not a fake ticket producer. The synthetic
  transport is seeded through the existing maintenance seam; no SYN/CONNECT or
  real-client acceptance is claimed.

The first run passed two and failed one new test because its negative fixture
tried to change an admitted row's immutable release identity. The database
correctly rejected that write. The fixture now inserts a separate foreign
historical row; the trigger and production code remain intact. The corrected
focused command passes **4**, with **0 failed/ignored**:

```text
cargo test --offline --locked -p dedicated_server --features maintenance-test-seams protocol_feedback_ -- --nocapture
```

Three of those checks also belong to the default server suite. This is added
coverage of existing refusal/retransmission behavior, not a failing production
case repaired by this change; no baseline/rollback pass is labeled a bug fix.
Exact evidence is in [the receipt](../evidence/protocol-feedback-20261031.json).

## One port-0 send failure per trace

Nine supplied trace summaries each contain one send to destination port 0 that
fails with Winsock 10049. The relevant native `hooks/storm.rs::sendto` detour
reads the caller's complete IPv4 socket metadata, optionally binds the selected
local interface, and passes the original destination pointer/length and payload
to Winsock. It preserves the returned error after logging. In this path, the
local bind's port 0 selects an ephemeral **source** port; it does not rewrite the
destination to port 0. No evidence supplied so far identifies the retail caller
which formed the invalid destination or proves a relationship to camera exit,
matching or the 799 ticket refusals.

The added Windows-only regression uses the actual safe socket-address reader:
a complete zero-port IPv4 destination stays zero and leaves caller bytes intact,
while null/truncated metadata is refused and an ordinary port 13000 control is
preserved. Its execution is pending the hosted Windows run. This is a parser
boundary check, not a reproduction of the upstream caller or a claim that port
0 should succeed. Do not replace it with a guessed server/game port or return a
fake successful send. Upstream producer and first-use lifecycle remain
**needs-triage**.

The [upstream source review](../evidence/port-zero-upstream-source-review-20261031.json)
traces generated service endpoints, native authentication return fields,
SYN/CONNECT peer ownership, room URL storage/search and NAT forwarding. Normal
LAN/Public builders use explicit nonzero ports. Two zero-valued inputs remain
distinct: the empty optional special-protocol endpoint returned with an empty
special-protocol list, and an explicit client-supplied station port 0 that the
room/NAT path can preserve. Neither is linked to the nine actual sends without
the native consumer/callsite; no protocol field or port is changed on speculation.
The Raw online-config IP-rewrite branch also drops a `SandboxUrlWS` port, but the
current production rewrite uses typed defaults and LAN writes its complete Raw
value directly, so this separate latent branch is not assigned as the cause.
This follow-up is static review with file hashes, not another parser test or a
real handshake replay. Authentication and all separate refusal boundaries remain.

## Version and remaining boundaries

The reviewed real runs' main/native gameplay component hashes match the earlier
`da1805e` candidate, not the rebuilt `dcb97d0` binaries; those native source paths
are unchanged between the two. The launcher and running server identities were
not established by that comparison. Thus source inspection applies to the
retained native path, but the observations cannot be called an exact `dcb97d0`
full-package acceptance. Follow the separate complete-package compatibility
task without weakening its identity gate.

The music-init report is lower priority and is tracked separately in
[EMF/performance feedback](emf-performance-feedback.md); it is not used to
explain multiplayer refusal. Next, correlate only already available local
game-run admission/heartbeat outcomes against the distinct refusal transitions
with the local reviewer. A public candidate should preserve these guards and
the focused regressions. No new private-log upload or repeated user capture is
requested by this task.
