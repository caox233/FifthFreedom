# Task tracking

The migration order and acceptance gates are in `../MIGRATION.md`.
Remaining game issues are in `../ACCEPTANCE.md`; keep them alongside migration.

States: needs-triage, needs-info, ready-for-agent, ready-for-human, wontfix.
For each implementation task, record its source inputs, changed interface or
branch, regression command and result, rollback and actual live-acceptance scope.
Closed tasks keep their evidence; do not silently delete older outcomes.

## Current implementation records

The executable next action and all six project milestones are authoritative in
[`PROJECT-STATE.json`](../../PROJECT-STATE.json). These records describe the
implemented boundary and actual evidence, including work still blocked:

| Area | Records |
| --- | --- |
| Donor UI and retained LAN ownership | [UI-01](ui-01.md), [UI-02](ui-02.md), [UI-03](ui-03.md) |
| Explicit Public profile, credential isolation and protected session reader | [UI-04](ui-04.md), [UI-05](ui-05.md) |
| Native profile, signed admission and private Linux state | [SERVER-01](server-01.md), [SERVER-02](server-02.md), [SERVER-03](server-03.md) |
| Protected HTTPS and component packaging | [Transport](public-transport.md), [control candidate](public-candidate.md) |
| Platform identities and read-only native preflight | [SERVER-04](server-04.md), [SERVER-05](server-05.md) |
| Native socket boundary and real ingress checks | [SERVER-06](server-06.md), [namespace harness](socket-isolation.md) |
| Pinned overlay source, finite secure-session contracts and dependency holds | [SERVER-07](server-07.md), [dependency evidence](public-network-dependencies.md) |
| Explicit Public source admission before native request processing | [SERVER-08](server-08.md) |
| Hosted Windows/Linux checks and retained failures | [CI-01](ci-01.md), [cloud progress](../CLOUD-PROGRESS.md) |
| Retained gameplay defects and missing native replay evidence | [Remaining bugs](remaining-bugs.md), [VSS audit](vss-lifecycle-audit.md) |

Passing a row does not close the whole migration. In particular, component
artifacts, read-only preflight and interface isolation do not establish a ready
Public runtime, complete resource package or new live gameplay acceptance.
