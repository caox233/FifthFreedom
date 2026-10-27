# Quick matching does not put players into the same game

Status: **needs-triage**. Reported on 2026-10-27 (Asia/Hong_Kong).

The user reports that players using the original 1.1 **quick matching** option
cannot match into the same game. This is not a report about quick adaptation or
the launcher's Fast/Compatible transport selector. The symptom is newly retained;
its cause and an independently reproduced failing case are not yet confirmed.

Continue this investigation alongside the existing six migration goals. Compare
the retained LAN implementation with the donor at actual matchmaking protocol
boundaries. Use previously supplied evidence only within its demonstrated scope;
do not infer a network or version mismatch merely from this symptom. Private
logs are not public fixtures and must not be uploaded.

## Required investigation and regression

Trace at least two independent clients on the same server and game mode through
queue admission, search filters, group/session selection, membership updates,
notifications and final entry into the same room. Include concurrent entry,
cancellation/disconnection and incompatible-mode controls where the real protocol
supports them. An offline regression must exercise production protocol/state
transitions with distinct client identities; a single client's successful search
or a fake returning the expected room cannot establish multiplayer matching.

No game or capture is started automatically. No additional user capture is
requested at this stage. A source finding or offline regression does not establish
that the reported live problem is fixed.

## First source trace

The retained server routes `ModeProtocol::handle` into
`GameSessionExProtocolServerImpl::search_sessions` (RMC123/1), then
`Storage::search_sessions_async` and the real query-attribute filter. A client
joins the returned key through `GameSessionProtocolServerImpl::join_session`
(RMC42/22) and `Storage::join_game_session_unbounded`. The latter queues Party
follow invitations through `queue_party_follow_invites_async`; their automatic
delivery uses `MyMisc::event` and admission uses `MyMisc::accept_invite`, with
current mode/publication checks. The manual query8 path is a different contract.

This inspected server path searches existing rooms. `create_game_session`
inserts a new room per create request; no authoritative shared matchmaking queue
or atomic find-or-create operation was found in that path. Retail-client request
order and native P2P selection are not established by the available public
evidence. Therefore a two-client production-protocol regression against an
already advertised compatible room cannot prove that two clients starting with
no room will create/select the same room. Strict slot/query filters likewise
remain possible investigation points, not confirmed explanations of this report.

## Actual offline result

Test-only commit `0fe3b743746315375382559613eaf08ac0c04363` adds two
Official/MOD cases in `storage/quick_match_protocol_tests.rs`. The actual command
`cargo test --offline --locked -p dedicated_server quick_match_protocol_ -- --nocapture --test-threads=1`
passed 2, failed 0 and ignored 0 (370 other cases filtered). Two independently
bound tickets serialize production SearchSessions/JoinSession, use their actual
returned key, converge on a three-member room, preserve the social Party and
verify the exact queued follow target and consumption. Private/cross-mode search
controls and incompatible direct join refusal are included. Production logic is
unchanged. First-run log SHA256 is
`71e9442cd33237db6a6fe7eb7636ca8a4eb3e3f0889c7ebe9a5cc22a592a63cb`.

This is an existing-room server protocol regression, not actual UDP dispatcher,
notification delivery, native P2P or empty-lobby convergence. No real user failure
was reproduced or fixed, and the task remains **needs-triage**.

## Follow-up protocol regression

Test-only `790b1793085d6f940c107945e8c0e987598ba365` extends the focused suite
to **six passed, zero failed/ignored**, independently rerun with the same result.
Official/MOD cases now use independently scheduled clients for concurrent
existing-room Search/Join, exercise exact old-ticket closure without invalidating
a previously established replacement ticket, and call real LeaveSession while
checking serialized final rosters retain the other client and host. This is room
departure, not an established matchmaking-queue cancellation contract. The first
test-helper compile failure and corrected result are retained in
[the regression receipt](../evidence/quick-match-protocol-regressions.json).

Seven key LAN source files remain byte-identical to the handoff `fc8ef510`.
Compared with donor, retained LAN uses a single WAL read snapshot, current
run/ticket/release/mode admission, complete host encoding and exact-ticket
disconnect cleanup. Donor uses separate pool reads, username/JSON mode snapshots
and user-wide session cleanup. These are implementation differences, not a
confirmed cause; the user's installed original1.1 binary is not bound to the
handoff commit. The available public historical references contain no matching
two-client matchmaking request replay. Empty-lobby Search/Create/Join order,
native selection/P2P, notification delivery and real same-game entry remain open.

Further static inspection at `4e633bd` does not close empty-lobby ordering:
native RMC/state hooks delegate to original game functions, and address tables
identify entry points rather than retaining the retail matchmaking state
machine. Server Search/Create behavior alone therefore cannot prove the order,
retry or convergence of two real clients. No matching two-client replay was
found in the available public reference material; private logs remain private.

The first full hosted run at `9aede37`, candidate `40387245159`, exposed a
failure that the focused local run did not: Linux LAN job `121798674986` passed
375 tests in the group and failed the MOD old-ticket case. Its direct
`try_close_secure_ticket(...).unwrap()` saw SQLite code5, `database is locked`;
the join workers then observed disconnected permit channels, not timeouts.
The real configured close callback reports refusal on Deferred or error; the
transport retains the uncommitted owner and retries. The test incorrectly required the first attempt to
commit. [The exact failure receipt](../evidence/hosted-lan-failure-9aede37.json)
remains retained; the unique cause of that database contention is not established.
Correct the test to exercise the real callback, prove refusal under a held
writer, and require confirmed commit before releasing clients. No gameplay or
storage-admission change follows merely from this test failure.

Test-only correction `e4f12132bb09d7cd98a8e3156d32831cfd493e06` now transfers
the actual searched ClientInfo back to its owner and calls the real configured
secure-close callback. A held SQLite writer must produce no commit acknowledgement
and preserve both old and replacement tickets. After releasing that writer, a
bounded retry must receive acknowledgement and verify only the old ticket is
gone before Join workers are released. Persistent refusal still fails; join and
serialized roster assertions remain. This tests the callback contract, not the
transport retry scheduler or the unique cause of the hosted database contention.

The corrected focused suite passes6 with default parallelism, independently
rerun6/6. The same five-package Linux test selection passes519, zero failed or
ignored, also using default parallelism and fresh owned fixtures. Its retained
log hash was independently checked; the reviewer did not repeat that full run.
See [the correction receipt](../evidence/quick-match-close-callback-regression.json).
Those counts exclude the thirteen unavailable C71 replay fixtures. The retained
candidate `40420957576` at `329f18a` subsequently passed the full Linux LAN
selection of 519; see [the hosted receipt](../evidence/hosted-artifact-native-329f18a.json).
The original quick-match symptom remains needs-triage.

## Empty-lobby protocol schedules, 2026-10-28 (Asia/Hong_Kong)

Four additional Official/MOD cases now begin with **zero active rooms** and two
different users, game runs and real bound secure tickets. They call the actual
serialized Search/Create/Join/Leave protocols and mode wrappers, using real
SQLite state. There is no fake search response or pre-created match. The two
request orders are explicit test schedules; no retail trace establishes that
the original 1.1 clients use either order.

In the ordered control, A searches an empty server and creates a public room;
B then searches and joins the key returned by that response. B's subsequent
serialized response and the actual stored roster contain both users in that
room. A's search response is empty because production search intentionally
excludes rooms created by its caller.

In the overlapping schedule, two independent worker threads must both report
empty Search responses before a bounded controller barrier releases either
Create. The actual calls create **two distinct rooms with one host each**.
Re-search returns B's room to A and A's room to B; it does not merge the rooms.
The test then chooses A's returned key for B's explicit Join. That adds B to A's
roster while B's own room remains active. B's explicit Leave from its own room
then retires that separate room and preserves both users in A's room. The test
controller supplies this target choice and Leave order, not an implemented or
verified retail matchmaking policy. There is no evidence here of an authoritative
queue or host-election agreement between the clients.

The first run passed the six retained tests and failed all four new tests:
the fixture had incorrectly expected a caller to discover its own hosted room.
`game_session_ex.rs` passes `Some(user_id)` to production search, and
`storage/sessions.rs` explicitly filters `creator_id != caller`. Only those
test expectations were corrected; the original failure log is retained.
The corrected command
`cargo test --offline --locked -p dedicated_server quick_match_protocol_ -- --nocapture`
passed **10, failed 0, ignored 0**, with default test parallelism and an independent
build directory. This includes four new empty-lobby cases and the six retained
existing-room cases. Production matching, ticket admission and room lifetime
rules are unchanged. Exact hashes and scope are recorded in
[the empty-lobby receipt](../evidence/quick-match-empty-lobby-regressions.json).

This is new empty-lobby **server protocol evidence**, not acceptance of retail
quick matching. Actual request order, game/native host selection, UDP dispatch,
notification delivery, P2P entry and two real players reaching the same game
remain unverified. Do not introduce automatic room merging or relax filters
solely to force these modeled schedules to converge. The next useful evidence
is a candidate's paired-client transition outcome, with any already available
private logs inspected locally rather than uploaded or newly requested.

## Next unified candidate acceptance

Once a complete, admitted candidate is downloadable, use at least two real
clients on the same server and mode. Select quick matching independently and
confirm both enter the **same room/game**, rather than each reaching a separate
successful lobby. Retain failures and which queue/room transition was observed;
do not include private account details or raw logs in public evidence. This check
is added to [candidate testing](../DEVELOPMENT-CANDIDATE-TESTING.md), without
reopening accepted F3, MAC-11 HUD, Chinese/world-camera or VSS EMF results.
