# SERVER-01 — deployment boundary and protocol/database compatibility

Status: **ready-for-agent**. The startup boundary and read-only session adapter
are implemented; public runtime integration and its lifecycle/security review
remain open. The fixed destination
is Windows/LAN and Linux/public, with the accepted LAN gameplay implementation
retained. This task does not complete either the six-milestone migration or the
remaining game bugs.

## Inputs and implementation

Baseline: `fc8ef510b51be83c31f9f8233997f758b0477acc`, cloud branch
`codex/cloud-integration`. Read `AGENTS.md`, `PROJECT-STATE.json`,
`docs/CLOUD-HANDOFF.md`, `docs/SERVER-INTEGRATION.md`, `docs/UI-INTEGRATION.md`
and `docs/ACCEPTANCE.md` before changing the startup boundary.

- The retained `dedicated_server` now accepts `--deployment-profile lan|public`.
  Omitting the option preserves LAN behavior. Host/Join and Fast/Compatible are
  not deployment profile values and are rejected by argument parsing.
- `DeploymentProfile::startup_plan()` resolves before package reads, directory
  creation, logging, SQLite initialization/migration and listeners. `public`
  returns `PUBLIC_PROFILE_NOT_READY`; it cannot fall back to LAN or use
  `--init-lan` to bypass that decision. Public adapters are not implemented yet.
- A validated LAN startup plan computes the existing executable-relative
  `lan-server-data` default, while preserving explicit `--data-dir`. Existing
  release verification, initialization, ports, observer permission, readiness
  and owned-service supervision are retained. No gameplay hooks or SQL migration
  files were modified.
- Portable protobuf comparisons preserve all donor field numbers/types/RPCs,
  both LAN copies, fixed package-role tags and readiness/observer fields.
  Executable Prost golden tests retain login/register product tag 10 and package
  tag 11, keep legacy identities absent and preserve unknown protocol values.
  These schema/wire checks do not claim a complete launcher/server roundtrip;
  the existing launcher still rejects unknown protocol and missing capabilities.

## Real validation receipts

The first run of
`python3 -B -m unittest discover -s tests/server -p test_protocol_contracts.py -v`
had **7 tests: 6 passed, 1 failed**, exit 1, because the deployment startup gate
did not exist. The source-wiring guard then passed after implementation; it is a
static guard, not a substitute for the executable Rust tests.

Executed in this cloud checkout:

| Command | Result and scope |
| --- | --- |
| `python3 -B -m unittest discover -s tests/server -v` | **28 passed**, exit 0; 6 schema comparisons, 1 startup-order guard, 7 donor SQLite/authentication/migration checks and 14 production read-adapter checks |
| `rustc +nightly-2025-10-15 --edition=2021 --test src/native/server/dedicated-server/src/deployment_profile.rs -o .local/evidence/cloud/server01/deployment-profile-tests` followed by that test executable | **5 passed**, exit 0; actual Rust profile parsing, public rejection and LAN path selection |
| From `src/native`: `cargo +nightly-2025-10-15 test --locked -p api --lib` | Root agent executed **29 passed**, including the two new Prost golden tests; exit 0 |

Root agent executed the combined native server/quazal/protocol suite using owned
`SCBL_IDENTITY_TEST_DIR` and `SCBL_RELEASE_TEST_DIR`: **390 passed** (314 dedicated,
60 quazal unit, 6 + 5 integration, 4 protocol, 1 documentation). The earlier run
had 18 failures and 296 passes in the dedicated suite because the test-fixture
directories were unset; its log was retained. The successful combined log is
`.local/cloud-receipts/cargo-server-owned-fixtures.log`. New `Args` parsing tests
are part of the native server suite, not the standalone profile harness.
Windows builds and public
deployment remain distinct checks. No game, live server, capture, real database,
user credentials or private local evidence were used by this task.

The cloud-only red log, scoped patch, command receipts and hashes are retained
under ignored `.local/evidence/cloud/server01/`. These supplement the preserved
local four-role evidence; they do not update or re-execute those local roles.

## SERVER-01B database mapping and observed blockers

The compatibility tests apply all four real LAN migration SQL files to an
in-memory SQLite database. Only synthetic fixture accounts/runs/rooms are added.
They call the actual donor `control_plane_runtime` and `control_plane_auth`
functions without starting services or making network requests.

| Donor consumer | Actual fields it reads | LAN mapping/result |
| --- | --- | --- |
| Token identity/revocation | `users.id`, `username`, `auth_version` | Direct columns exist; valid synthetic token accepted; wrong source, expiry, missing/wrong key and incremented revocation version rejected |
| Account/health lookup | `users.username`, user row count | Direct queries execute against the migrated schema; missing identity columns raise instead of inventing defaults |
| Room projection | `game_sessions.id,type_id,creator_id,destroyed_at`; `users.id,username`; `participants.game_id,user_id`; `station_urls.user_id,url` | Names/types support the query; active two-participant synthetic room projected, destroyed room omitted |
| Manager account recovery | `users.recovery_code_hash,updated_at` | Columns exist; mutation semantics and auth-version revocation require separate review before wiring this manager operation |

**Concrete blocker reproduced:** the donor room query still returns a room after
its native `game_runs` have been retired. It does not consult run freshness,
gameplay mode or release identity. The retained LAN schema rejects stale
participant mutations. The test named
`test_audit_donor_projection_is_not_release_or_run_admission` records this
observed gap; its passing result means the gap was reproduced, not that donor
admission is approved. Do not use this projection as an authorization decision.

The synthetic upgrade test verifies that additive migrations preserve users and
revocation values. The failure test injects an invalid statement inside the same
transaction and verifies schema and fixture rows roll back. This is SQLite
transaction evidence, not a claim that a deployed server upgrade/systemd rollback
has been exercised. Existing SQL migrations remain unchanged.

## Production read adapter: stale-session seam implemented

`src/public/fifth_freedom_control/session_reader.py` provides a reusable,
standard-library-only `SessionReader`. It has no listener or process entry point
and does not import the donor server. The donor remains unchanged as the reference
that reproduces the old stale-session gap.

The new adapter requires explicit server-owned release/network/observer policy,
the complete four-role package declaration, protocol 1 and a verified upstream
game identity. Account-only tokens and unknown game products cannot stand in for
a native game run. The requester's username, revocation version, nonce, source,
mode, release and 90-second freshness are checked in the same SQLite read
transaction as the room projection. Only the requester's own rooms are returned;
host and members must have current, fresh matching-mode/release runs. Retired,
stale, future-dated, legacy NULL, different-mode/release and ambiguous-source
identities cannot be silently relabeled. Endpoint IPs come from authenticated
run records, not potentially stale `station_urls` strings. Observer requests need
both server permission and the exact admitted run nonce.

The adapter opens SQLite with `mode=ro`, never creates a missing database, and
returns an explicit schema-unavailable error on incompatible input. Its test
checks that reading does not change the database SHA256. Synthetic fixture
tests exercise the production adapter, not a copied query implementation.

**Unfinished integration:** `AuthenticatedGameIdentity` is an internal data type,
not evidence of authentication on its own. A future protected transport must
validate token signature, expiry/source, server release provenance and observer
admission before constructing it; unpacking request JSON into it is forbidden.
The adapter deliberately exposes only active-game session projection. Account
panel/discovery contracts, credential transport, public token issuance, Linux
service lifecycle and launcher Public adapter remain unconnected. No public
readiness or deployment is claimed; the Rust `public` startup gate stays closed.

## Next executable work and rollback

1. Connect the tested read-only adapter to a verified token/observer/release
   policy boundary. Preserve the retired/legacy/null/mixed-identity regressions;
   do not route unvalidated request fields directly into its identity type.
2. Review recovery/reset semantics and signed token scope against the retained
   server API. A matching table name or valid donor token alone is insufficient.
3. Wire a separately reviewed public startup plan, protected account transport,
   configuration, package admission and Linux lifecycle only after those tests.
   Until then `--deployment-profile public` must remain a nonzero, fail-closed
   result with no data or listeners created.
4. Run hosted Windows and Linux candidate lanes; keep complete-package asset
   privacy gate and all live/GPU/network acceptance boundaries explicit.

Rollback only the scoped SERVER-01 patch (startup file, new deployment module,
API golden tests, `src/public/` adapter and `tests/server/`) from this branch. No schema or user database
migration was introduced, so this change does not require a data rollback. Keep
the red/green evidence and the original LAN/donor sources available.
