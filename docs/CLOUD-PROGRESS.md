# Cloud implementation evidence

The cloud checkout fetched and verified handoff commit
`fc8ef510b51be83c31f9f8233997f758b0477acc` before creating
`codex/cloud-integration`. The original checkout branch and private local
evidence were retained. This is continuing implementation of all six milestones
in `PROJECT-STATE.json`, not a completed migration or live acceptance.

## Implemented boundaries

- [UI-01](tasks/ui-01.md): donor shell presentation with retained LAN controls,
  event bindings, launch path and DX11 policy; rendered-layout probes adapted to
  the scaled shell. Public remains unavailable.
- [UI-02](tasks/ui-02.md): typed LAN/Public lifecycle model with serialized
  transitions, rollback, owned-resource disposal and live lease health. Real
  adapters are still required before exposing the selector.
- [SERVER-01](tasks/server-01.md): explicit deployment profile, unchanged LAN
  default, fail-closed Public startup and read-only current-session admission.
- [SERVER-02](tasks/server-02.md): authenticated query bridge preserving the
  existing token envelope and current-run/revocation checks. Trusted issuer-key
  and observer-grant handoff and protected transport remain unconnected.
- [CI-01](tasks/ci-01.md): GitHub-hosted component builds and isolated no-game
  checks; an independent strict complete-package gate preserves the withheld
  SAV/META boundary. No Release or deployment action is configured.

## Executed local cloud checks

| Check | Actual result and scope |
| --- | --- |
| Initial public `tests/build` | 37 methods, 16 missing-input errors, 2 Windows skips; exit 1 retained |
| Source logic before integration | 35 methods, 33 passed, 2 Windows skips; complete-resource methods separately gated |
| Native `api --lib` | 29 passed |
| Retained server/protocol suites | 390 passed after supplying owned synthetic fixture directories; initial 18 fixture-directory failures retained |
| Donor native reference suites | 183 passed; separate donor workspace, not an integrated Public server |
| Donor manager | 153 passed, 1 systemd integration skip |
| WPF cross-target build | 0 warnings, 0 errors; no Windows rendering claim |
| Background-task regression | 10 checks passed |
| Observer-session regression | Passed |
| Network-environment lifecycle | 28 checks passed |
| Latest source checks in Actions environment | 120 methods: 118 passed, 2 Windows-only skips; no failures |
| Authenticated reader and bridge | 47 Python tests passed; synthetic databases only |
| Current native producer interoperability | 2 Rust and 5 Python tests passed using the same generated synthetic fixture |
| Port-plan on Linux | Exit 134: Windows-specific Winsock numeric assertion; original Windows CI assertion retained |
| Real native Public startup rejection | Exit 1 with `PUBLIC_PROFILE_NOT_READY`; fresh owned data directory remained absent |

Representative commands use .NET SDK 10.0.302, Rust nightly-2025-10-15 and
checksum-verified protoc 35.1:

```sh
python3 -B scripts/check_migration.py --require-fifth-freedom
python3 -B scripts/ci_checks.py source
python3 -B scripts/ci_checks.py package-inputs
dotnet build src/launcher/src/SplinterCellCNLauncher.csproj -c Release -p:EnableWindowsTargeting=true
dotnet run --project tests/NetworkEnvironment -c Release
# Working directory: src/native; use fresh test-owned identity/database dirs.
cargo +nightly-2025-10-15 test --locked -p api --lib
cargo +nightly-2025-10-15 test --locked -p dedicated_server -p quazal -p sc_bl_protocols
```

The package-input command is expected to fail until privacy-admitted matching
inputs exist. It is not a successful build. Unfiltered raw logs, environment
configuration and private local four-role records are not published here.

## First hosted candidate

[Run 39858405093](https://github.com/caox233/FifthFreedom/actions/runs/39858405093)
executed against `0e186f0afeb2756527d0f1eabaa88e5069129d39` and completed with
overall **Failure**, duration 6m 32s:

| Job | Result |
| --- | --- |
| Linux LAN baseline | Passed; component artifact 8.9 MB |
| Linux public donor reference | Passed; reference artifact 3.82 MB |
| Windows components | Failed after 1m 41s in source/UI/server/CI checks; subsequent compile and no-game probe step skipped |
| Strict package inputs | Failed after 6s; withheld resources remain mandatory |
| Complete Windows package | Skipped because its input dependency failed |

The public job page confirms that tool installation succeeded and source checks
failed; the individual failing method was not yet exposed at this observation.
This is a separate unresolved result and is not attributed to withheld saves
without evidence. The original baseline source-check failure
[39855999361](https://github.com/caox233/FifthFreedom/actions/runs/39855999361)
also remains part of the record. Future successful runs do not erase either.

The [diagnostic candidate 39859917025](https://github.com/caox233/FifthFreedom/actions/runs/39859917025)
exposed 14 `PublicSessionReaderTests` errors and one diagnostic-test assertion
under `GITHUB_ACTIONS=true`. The CI environment assumption was reproduced and
fixed. Eleven SQLite fixture connections now explicitly close after committing
or rolling back; an owned audit retained 113 real connections and verified them
closed before 33 directory cleanups. The production reader already closed its
connections and was unchanged. The Windows source-check step in PR run 39860607037 subsequently passed; the
compile/test/no-game step then started. This confirms the source regression on
Windows without claiming its later component or UI checks had passed. The diagnostic run was superseded by the
next push; its already observed Windows failure is retained.

The reviewed code through `d0ee78e59b3d6c0691c00a224383d27dc6500ada` is in
[draft PR #1](https://github.com/caox233/FifthFreedom/pull/1).
[PR source checks 39860607056](https://github.com/caox233/FifthFreedom/actions/runs/39860607056)
passed. The [PR candidate 39860607037](https://github.com/caox233/FifthFreedom/actions/runs/39860607037)
was started for actual Windows verification. No merge or release occurred.

## Reusable environment and continuation

The current managed instance is connected Linux x86_64. Python, native compiler,
Rust, protoc and .NET workflows have executed. Tested idempotent installation and
startup instructions were saved through the official environment draft tools.
Those tools distinguish saving a draft from publishing it or changing the
current runtime; publication and a new-task restore have not been verified.

Git HTTPS fetch and development-branch push work. Official `gh api` requests
initially failed at proxy CONNECT, then succeeded with the existing configuration;
repository read/push permissions were independently observed. The status API
still reported the original restricted list, so uniform endpoint access or a
published policy change is not inferred. Actions log downloads redirected to
`results-receiver.actions.githubusercontent.com` and remained denied. Narrow
additions for that host and `api.github.com`, preserving the package-manager
preset, were saved through the official draft tool for environment review and
publication. No secret is needed in chat and no network bypass is used. Continue
supported PR/API operations and bounded CI diagnostics while logs are restricted.

The available official tools expose environment status and draft configuration,
but no persistent Goal or execution scheduler. The current turn and GitHub-hosted
jobs do not depend on the owner's computer. Later agent execution requires a new
message or a platform wake-up; this record is not an infinite-running schedule.
All six milestones and retained gameplay bugs remain active in project state.

Supplemental patches, command logs and hashes are retained under ignored
`.local/cloud-receipts/` and `.local/evidence/cloud/`. They supplement, and do not
claim to regenerate, the private original four-role evidence on the local PC.

For implementation commit `d0ee78e59b3d6c0691c00a224383d27dc6500ada`, the
supplemental patch against the handoff has SHA256
`50a1fed18d85d67985cb019ca0db021350bded8804dd74ba50bdf660036c70cb`.
All 40 changed files reconstructed byte-for-byte from the preserved handoff in a
fresh temporary directory; applying the reverse patch restored all original
bytes. This is a cloud source receipt, not a rerun of private local role evidence.
