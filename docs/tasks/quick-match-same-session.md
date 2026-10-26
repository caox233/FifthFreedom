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

## Next unified candidate acceptance

Once a complete, admitted candidate is downloadable, use at least two real
clients on the same server and mode. Select quick matching independently and
confirm both enter the **same room/game**, rather than each reaching a separate
successful lobby. Retain failures and which queue/room transition was observed;
do not include private account details or raw logs in public evidence. This check
is added to [candidate testing](../DEVELOPMENT-CANDIDATE-TESTING.md), without
reopening accepted F3, MAC-11 HUD, Chinese/world-camera or VSS EMF results.
