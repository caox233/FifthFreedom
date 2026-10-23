# Cloud implementation evidence

The cloud checkout fetched and verified handoff commit
`fc8ef510b51be83c31f9f8233997f758b0477acc` before creating
`codex/cloud-integration`. The original checkout branch and private local
evidence were retained. This is continuing implementation of all six milestones
in `PROJECT-STATE.json`, not a completed migration or live acceptance.

## Initial implemented boundaries (later progress below)

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

## Initial local cloud checks (later progress below)

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
completed with Windows and both Linux component jobs successful. The Windows
job includes WPF publishing and isolated no-game UI checks, four managed suites,
native core/API tests and x86 hooks/gameplay plus Windows LAN compilation. The
overall workflow remains failed because the strict privacy gate failed; complete
packaging was skipped. No merge or release occurred. The separate source-bound knowledge snapshot is in
[DevDocs draft PR #1](https://github.com/caox233/FifthFreedom-DevDocs/pull/1).

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

The parallel [VSS lifecycle audit](tasks/vss-lifecycle-audit.md) directly compiled
four unchanged production pure-policy modules: seven tests passed. It found no
confirmed root cause for intermittent scope loss and identifies a narrowly scoped
late-geometry native replay as the next missing evidence. Accepted VSS EMF/F3
results and the unresolved bug status remain unchanged.

The first successful Windows component run was
[39860535649](https://github.com/caox233/FifthFreedom/actions/runs/39860535649),
against the same implementation commit. Public artifact metadata and exact
archive digests are recorded in
[evidence/hosted-candidate-d0ee78e.json](evidence/hosted-candidate-d0ee78e.json).
These are artifact archive digests; component-specific checksums remain inside
the corresponding artifact. Full download redirects remained restricted in this
cloud session. Candidate entry points now use PR/manual only to avoid duplicate
push-plus-PR builds; no checks or privacy gates were removed.

## 2026-10-23: production LAN connection and protected control service

The final prior revision `814c5c1ae930afc9887c5a6b08ec4a95e3c74597` completed
[candidate run 39863077851](https://github.com/caox233/FifthFreedom/actions/runs/39863077851)
with Windows and both Linux component jobs successful. The strict package input
gate failed and complete packaging skipped. That earlier result does not cover
the following new implementation.

- [UI-03](tasks/ui-03.md) now connects actual Host/Join operations, launch-time
  host preparation and owned cleanup through the environment controller and WPF
  dispatcher. Initial Join preserves a saved local endpoint; late capability
  responses cannot publish into another lease/address/bind generation. Managed
  tests passed 34/34, source UI contracts 12/12, and WPF cross-compilation had no
  warnings/errors. New real-dispatcher probes are in the Windows no-game entry.
- [SERVER-03](tasks/server-03.md) adds explicit Linux owner-only key-file loading,
  native signed run-bound observer grants and a held-FD data-directory lock.
  Default LAN still gets a random process key. Dedicated-server tests passed
  330/330; focused final key tests passed 6/6 and non-test compilation passed.
  The previous native producer fixture remains byte-identical.
- [Protected transport](tasks/public-transport.md) adds a real HTTPS endpoint
  and CLI, actual socket-source binding, bounded HTTP/TLS/query work, rate
  limits, read-only SQLite, and owned shutdown. Sixteen integration tests and
  all 69 server Python methods passed using synthetic private inputs. An
  independent reviewer repeated the 16 tests and found no unresolved blocker.
- [Control candidate](tasks/public-candidate.md) produces a deterministic
  standalone zipapp, exact hash manifest and separate deployment templates.
  Ten packaging regressions passed. The actual local lane built, verified and
  ran the entry point; its receipt explicitly records a dirty working tree,
  rather than attributing uncommitted code to an old clean revision. A dedicated
  hosted Linux lane now exercises the real control component.
- Source checks ran 163 methods: 161 passed, two existing Windows-only skips.
  CI helper checks passed 30/30, actionlint and whitespace checks passed. New
  native authentication filters on Windows require nonzero passing tests.

These changes retain the native Public startup gate. Its current package verifier
is bound to the four-role Windows catalog: a Linux server binary needs an explicit
server-package identity plus admitted Windows-client catalog contract. Skipping
the checks or silently changing the server digest would break admission. The
protected game/overlay transport, launcher Public adapter and complete Linux
installation/switch/rollback transaction remain work; a tested HTTPS component
does not complete them. No production service, game or capture was started.

All remote heads were checked again: the promised Capture helper source supplement
was still absent. The new read-only receipt tool verified the preserved EXE's
hash, size, x86 PE and asset pin, then exited 2 for `blocked_source_not_supplied`.
Its seven regression tests passed. It does not reconstruct missing source or
claim source/binary equivalence. The 12 privacy-held SAV/META files and original
evidence remain preserved and excluded from publication.

The six ordered milestones and existing accepted gameplay scopes remain intact.
The environment API reports the same connected cloud instance, but still exposes
no persistent Goal/scheduler interface. This turn executes autonomously; later
agent turns require a message or platform wake-up. The saved reusable environment
draft remains distinct from a published snapshot.

## 2026-10-23: confirmed four-lane hosted result and dual package identity

[Run 39895749771](https://github.com/caox233/FifthFreedom/actions/runs/39895749771)
completed against branch head `f54c9358c0e8df6775c4f29ed91ea3a91e870a80`.
Windows components and the Linux LAN, donor reference and protected-control
component jobs all passed. The source workflow also passed. The strict SAV/META
input gate failed and full packaging skipped; the overall candidate workflow
therefore remains failed. [Official API evidence](evidence/hosted-candidate-f54c935.json)
records the job IDs and artifact archive digests. The artifacts were not
downloaded in this cloud session; their names bind the PR merge checkout SHA.

The later owned-launch cleanup fix refreshes environment availability after
actual session disposal, including replacement-session protection. Its new WPF
probe is not covered by the preceding run and needs the next Windows run.

[SERVER-04](tasks/server-04.md) adds an independent Linux service identity plus
an exact pin to the original four-role Windows client catalog. It preserves
the original Windows verifier and all admission roles. All 45 native API tests
passed, including 16 new synthetic-format tests; the read-only CLI compiled.
Real same-run Windows/Linux component assembly remains a separate CI result.
The native Public startup gate stays closed.

[Dependency closure](tasks/public-network-dependencies.md) verified three pinned
EasyTier 2.6.4 archives and WinDivert 2.2.2, two retained notices and eight tests.
The Linux donor's automatic latest-version selection is not reused. Npcap
redistribution rights and the exact Wintun binary license remain unresolved;
no new driver/runtime binary was published or installed. The original WinDivert
license is retained byte for byte, including its upstream trailing blank line.
Formal network-draft additions for the official Wintun and Npcap hosts are
saved but unpublished; current networking was not bypassed or changed.

[UI-04](tasks/ui-04.md) now scopes actual stored credentials and recovery
operations by explicit deployment identity, preserving the old LAN entropy and
filenames. Changing deployment clears captured/manual/autofill/recovery state.
Twelve production-linked managed cases and 12 UI source contracts passed; WPF
cross-compilation had zero warnings/errors. Real Windows DPAPI and five new WPF
assertions are queued for a new hosted run, distinct from the f54c935 result.

The new candidate job consumes only same-run, same-attempt Windows/Linux
artifacts, verifies their receipts/checksums, and invokes the real Rust identity
checker over the assembled components. Sixteen Python builder tests and an
actual Rust consumption of explicitly inert synthetic component data passed.
The synthetic data was never executed or published. Source checks now cover
187 methods: 185 passed and two existing Windows-only skips. Actual hosted
component assembly still needs its own result.

Next native runtime work has a concrete security boundary: Linux bind-to-address
alone does not constrain the ingress interface. The distributed TCP, UDP, gRPC
and temporary peer sockets need an enforced interface/namespace policy plus
owned encrypted-overlay liveness. Static configuration/identity checks cannot
substitute for that proof; the Public startup gate remains closed. This cloud
instance lacks effective NET_ADMIN/NET_RAW/SYS_ADMIN and a TUN device, and an
owned empty `unshare --net true` probe returned EPERM. Isolated privileged
network testing belongs in an explicitly checked hosted Linux job, not a claim
of local runtime verification or a connection to an old deployment.

[Run 39898671437](https://github.com/caox233/FifthFreedom/actions/runs/39898671437)
against `463aa521e5cdb3345e460442bacf8050bd42aef8` then completed with a new
Windows source-test failure in `public_server_candidate`. Windows compilation,
new DPAPI/WPF checks and the dependent dual-identity assembly did not execute.
Linux LAN, donor and control jobs passed. This failure is separate from the
unchanged strict resource gate and requires its own fix/rerun;
[official job evidence](evidence/hosted-candidate-463aa52.json) is retained.

The supplemental source patch from the original handoff to `463aa52` has SHA256
`ecb523925d1b1c65670895affe83271a553014b04ac3c4ff63708f87076830f8`.
All 98 changed paths reconstructed with exact bytes and modes, and reverse
application restored all originals in owned temporary directories. An initial
check had 98 mode-only mismatches under inherited umask 077, with every byte
already matching. The successful rerun set umask 022 only inside the patch
subprocess for these public source files; both receipts are retained. Private
original four-role evidence was not regenerated or changed.

## Native preflight and enforced socket policy

The Windows metadata compatibility correction passed the actual Windows source
step in [run 39899786790](https://github.com/caox233/FifthFreedom/actions/runs/39899786790)
at `7dcecd96d27d7aa6490bab1614a64092b79671ef`. That run then completed with
Windows and all three Linux component jobs successful. The new combined job
120244277083 also passed using the actual same-run Windows/Linux artifacts and
native dual-identity checker. This includes real Windows DPAPI and no-game WPF
context/owned-exit checks. The strict resource gate failed and complete packaging
skipped; overall workflow failure is preserved. Exact public artifact metadata
is in [the hosted receipt](evidence/hosted-candidate-7dcecd9.json); redirected
archive contents were not downloaded in this cloud session. The prior Windows
failure remains recorded separately. New SERVER05/06 checks below await their
own hosted revision.

[SERVER-05](tasks/server-05.md) connects an explicit read-only native preflight
to the actual Linux/Windows package identities, private issuer key/data directory
and retained native configuration. Actual Debug and Release programs returned
the strictly static successful report with `startup_allowed:false`; ordinary
Public startup and mixed arguments still failed, with test inputs unchanged.
Those local runs used explicitly inert synthetic Windows role files, not hosted
client artifacts. They exposed two complete identity markers in the real Linux
binary when both verifier paths were linked. A shared non-inline retention
anchor fixed both actual binaries to exactly one marker; the strict count and
original catalog roles were not relaxed. Final API45 and dedicated341 passed.

[SERVER-06](tasks/server-06.md) routes every native TCP/UDP listener and temporary
peer socket through explicit immutable socket policy. Linux interface binding
checks the actual stored interface index; LAN preserves its original binding,
Tonic nodelay and kernel source information. The final committed Quazal suite
passed75 (63unit+11integration+1doc), with no warning/error. Real local loopback
checks passed but do not establish a two-interface boundary.

The [three-namespace harness](tasks/socket-isolation.md) has26 focused checks
and a dedicated hosted Linux job. It requires successful wrong-path traffic
under ordinary IP binding before testing wrong-interface rejection, repeats
correct-path checks and requires all owned processes/namespaces to be removed.
The real cloud execution failed for missing privilege and created zero namespaces;
no successful skip was substituted. Hosted privileged execution is still required.

CI adds9 native-preflight adapter and5 socket-result checks. A real local Release
preflight adapter run passed using synthetic Windows inputs, preserving candidate
bytes and removing every temporary key/config/data input. The fresh hosted
assembly job explicitly installs the Ubuntu libraries needed by its system-sodium
build; a locally static binary does not prove hosted dynamic dependency closure.
A clean checkout of `5e5ef05940a387bc3f275132acf474ba14674860` passed232 source
methods:230 passed and two existing Windows-only skips. Full-package readiness
remains false. None of these checks proves owned encrypted-overlay liveness,
Public source admission or live gameplay, so Public activation remains closed.


## Source admission, owned lifecycle and protected session client

[Run 39901945731](https://github.com/caox233/FifthFreedom/actions/runs/39901945731)
at `2b66f307a8b4fd1106ca529a72f974bebc362d54` completed with Windows and all three
Linux components successful. Real privileged three-namespace ingress isolation
and same-run native dual-identity/static Public preflight also passed. The
unchanged resource gate failed and complete packaging skipped; this is still
an overall failed workflow. [The official receipt](evidence/hosted-candidate-2b66f30.json)
records all eight jobs and seven artifacts without claiming redirected archive
downloads, Public activation or gameplay.

[SERVER-08](tasks/server-08.md) applies immutable kernel-peer source admission
before HTTP, API, PRUDP and ticket effects while retaining LAN policy and signed
current-run/package checks. Dedicated348 and Quazal81 passed. [UI-05](tasks/ui-05.md)
adds the actual bounded HTTPS session reader, with 15 real temporary-CA TLS
cases and independent review. [SERVER-09](tasks/server-09.md) adds exact owned
Linux process execution, pidfds, parent-death cleanup and revoke-before-stop
ordering; 16 real synthetic-child cases passed twice. These implementations
retain closed production admission pending profile, encrypted-peer and native
parent-lease integration. No static manifest alone enables Public service.

Pinned EasyTier source tests preserved both original failures: an unencrypted
Data packet reached the secure session, and secure filter branches could pass
unencrypted Data onward. Two minimal reviewed guards now pass all 26 selected
contracts, including an actual in-memory Noise handshake/Data path. Both clean
GNU Release builds completed; [guard2 build evidence](evidence/easytier-clean-linux-build-guard2.json)
records exact output hashes and unchanged prepared sources. Those binaries were
not executed or published. This bounded audit is not a complete upstream
security or license review. Original archives, prior failures and each built
revision remain separately preserved.

The build-only workflow prepares the exact Git archive, verifies each excluded
binary/archive and applied patch, then tests and compiles on hosted Linux. Only
three bounded JSON receipts are upload candidates. Windows candidate checks now
include source admission, UI-05 and actual executable FileVersionInfo branding.
These new hosted checks await the next pushed revision; local builds do not
stand in for their results. A fresh detached checkout of
`d37854d39ee68e54fbf931af598f471b43a93b77` passed 265 source methods: 263 passed
and two existing Windows-only skips. Full-package readiness remained false.

Work continues on native inherited owner leases with actual listener readiness,
explicit private overlay profiles and exact owned-process native token return.
The full six milestones remain active, including unresolved gameplay evidence,
Capture source closure and private resource gates. No persistent Goal or
scheduler interface is exposed in this session.

## Owned activation and source-based overlay build

The first hosted reviewed-overlay source build,
[run39905205148](https://github.com/caox233/FifthFreedom/actions/runs/39905205148),
completed successfully. It prepared the guard2 sources, ran26 finite contracts
and compiled GNU Release, uploading only three bounded JSON receipts. No core
runtime or dependency binary was executed or published. The build step ran from
05:31:54 to05:42:47 UTC; the earlier package installation delay was not a failure.
The source archives and binaries were not downloaded through the denied artifact
redirect here. Guard3 adds a separately reviewed explicit-peer-key check: a
correct shared secret previously overrode a wrong configured server key. Its
before2/3 and after3/3 actual Noise contracts are retained, alongside35 selected
regressions. A new clean build has its own source/patch identity; it does not
inherit guard2's hosted result.

The [4a273bf candidate](https://github.com/caox233/FifthFreedom/actions/runs/39905205347)
exposed two source-test defects: Windows Git line-ending conversion changed
strict prepared-source hashes, and Linux synthetic-child tests tried to use a
shared tool-cache executable under the production private-path policy. Actual
CRLF Git reproduction and private owned interpreter fixtures fixed these without
loosening production guards. The subsequent
[8389cb6 source run](https://github.com/caox233/FifthFreedom/actions/runs/39906263839)
passed. Its [candidate run](https://github.com/caox233/FifthFreedom/actions/runs/39906263847)
passed all Linux components and actual namespace isolation, and passed Windows
source checks, but failed later in `ci-test-PublicSessionControl`. Native Windows
build/branding checks and dependent dual identity did not complete. This new
UI05 failure needs its own diagnostics and resolution; the earlier2b66f30 result
is not evidence for later source revisions.

[SERVER-10](tasks/server-10.md) now accepts an explicit inherited owner lease
only after real package/config/key/interface validation, and arms an independent
watchdog before storage or listeners. Actual Debug and Release main programs
rejected a wrong nonce after HELLO with all fixture bytes/directories unchanged.
Measured preparation was50.34s for Debug and0.698s for Release; an initial30s
Debug timeout remains recorded. The parent therefore has a distinct bounded
pre-HELLO preparation budget, without changing native's three-second grant
deadline. Dedicated367 passed. [SERVER-11](tasks/server-11.md) passed10 actual
Unix parent cases plus the retained16 owned-lifecycle cases. A separate actual
Python/Rust test-peer composition exercised five loopback listeners and revoke
exit92; it is not production bootstrap or real-main activation evidence.

[UI-06 launcher](tasks/ui-06.md) and [native](tasks/ui-06-native-return.md)
preserve the original config pipe framing/ACK, add independent exact-process
anchors and bounded generation-aware credential return, and cancel context on
revocation/EOF. Linux managed14 and native API54 passed; Windows managed11 and
native8 runtime cases still require hosted execution. Production Public remains
unavailable: final-login ownership plumbing, real source-route/overlay ownership
and actual cross-language Windows evidence are separate requirements.

A fresh detached `99e3c6245dd8c1af70bab46c79e6407d6e49e3e2` checkout passed285
source methods (283 passed, two existing Windows-only skips). The development
knowledge repository separately verified75 snapshot files bound to4a273bf.
Only a reproducible incremental build cache was cleaned, releasing2,052,317,184
bytes; all four guard1/2 original ELFs and receipts were independently hashed
before and after and retained. No original source or private four-role evidence
was removed. Full package readiness and the complete six-milestone goal remain
false; original SAV/META, Capture source and live-acceptance gates are unchanged.

## Actual Windows diagnosis and owned runtime probe preparation

The later [c3bac15 candidate](https://github.com/caox233/FifthFreedom/actions/runs/39908170962)
again passed Linux LAN, donor, protected control and namespace isolation. Windows
source checks passed, but UI05 stopped at its normal HTTPS session query, case05.
The [a569ae7 diagnostic run](https://github.com/caox233/FifthFreedom/actions/runs/39909129801)
then reported the bounded server-side classification
`TLS:AuthenticationException:NO_CREDENTIALS`. This is an actual Schannel fixture
failure, not a generic failed native build. The independent source run39909129706
passed. Subsequent Windows native compilation, branding and dependent assembly
were not reached; those results must not be inferred from earlier revisions.

Commit4668f96 changes only the synthetic Windows leaf certificate: its private
key is reloaded from a temporary in-memory PFX into a disposable user key
container. PFX bytes are cleared and no certificate is installed or uploaded.
Production TLS, chain trust and hostname verification are unchanged. Official
.NET documentation and Windows loader source support this correction; local15
TLS cases passed, but the correction still needs a real Windows run. A separate
source review found the managed/native fixture's empty password violated the
retained native validator. One shared nonempty synthetic TOML and an actual
parser regression in ec4e403 fix that fixture without weakening the validator.

[UI-07](tasks/ui-07.md) retains the actual final login response and verified
package in one prepared configuration owner. Nine real loopback HTTP/2 checks
passed, including original config bytes and transfer/failure cleanup. The
recorded local/remote socket endpoints are historical observations, not a live
overlay capability. The Rust/C# interoperability entry is now implemented with
three owned synthetic-child cases; it still requires actual Windows execution
and deliberately does not claim loaded-DLL or full-package admission.

The distinct guard3 [local build receipt](evidence/easytier-clean-linux-build-guard3.json)
now records35 passing finite checks and a successful GNU Release build. Its
source tree is `60c85f25267ce640bb27901b167047722478fd91d39693a1609e925e28440181`;
the original guard1/guard2 outputs remain separately preserved. The first hosted
guard3 build39909129708 is still running at this checkpoint. Build-only jobs
continue to upload bounded receipts only and do not execute the core runtime.

[SERVER-11](tasks/server-11.md) adds five actual-main lifecycle cases to an owned
isolated namespace harness. Twelve local guard/cleanup checks passed, including
real subprocess failures, exact cleanup and retained unrelated processes.
[SERVER-13](tasks/server-13.md) adds separate QUIC/WSS/TCP positive, wrong-key and
restore probes using a same-job guard3 build, real TUN/peer observations and the
native socket-policy echo helper. Seventeen local guard/cleanup tests passed.
Both actual entrypoints refused this cloud container's missing privileges and
started no namespace/runtime. Their positive results remain pending dedicated
GitHub execution; the ordinary production Public provider remains closed.

A supplemental cloud patch from the original fc8ef510 handoff to
`1c1bf67b0d30d7865a839c34c5aae531c8f2b06b` was actually applied and reversed.
It matched all1312 original Git paths, all1467 target paths, then restored1312
paths with clean Git status, comparing every canonical blob byte and file mode.
Patch SHA256 is `b243b83921fdb043a289b702f31812716bfdea4eeeb0e18ca6f2d508240a41c9`.
The first ordinary checkout comparison exposed238 attribute-driven CRLF
expansions; every mismatch was explained and the original failure was retained.
The exact proof materializes canonical Git blobs; it does not claim ordinary
CRLF checkout bytes or the private PC four-role originals were replaced.

The updated environment installer was actually executed successfully with both
Rust toolchains, .NET, protoc and the isolated pinned Python crypto provider.
It and updated startup instructions are saved in the official environment draft.
Saving does not publish/apply that draft or prove fresh snapshot restoration.
No official Goal/scheduler or environment-publish tool is available. The full
six milestones remain active; this checkpoint is not completed migration.


## Actual hosted Schannel correction and next runtime failures

At source0ac6695 the synthetic Schannel correction passed the actual Windows
UI05 test. That candidate then failed PublicNativeReturn without an exact case.
The later [117a60d run](https://github.com/caox233/FifthFreedom/actions/runs/39912156375)
passed that managed suite, but failed final-login preparation at its required
loopback port80 with WSAEACCES. This one successful retry does not identify or
resolve the earlier return-suite failure. The bounded diagnostics preserve both
outcomes. All three Linux component lanes and native ingress namespaces passed;
Windows native/interop/branding and dependent lifecycle/assembly were not reached.

The [b6e0687 read-only prerequisite probe](https://github.com/caox233/FifthFreedom/actions/runs/39913526182)
confirmed a System-owned port80 listener, running W3SVC, an excluded-port entry,
and the same failed owned bind. No service or reservation was changed by that
probe. A scoped preparation/restoration of the verified default IIS site in the
disposable hosted runner is the next test; the production port contract remains80.

The [first hosted guard3 build](https://github.com/caox233/FifthFreedom/actions/runs/39909129708)
passed35 finite contracts and GNU Release. The separate
[first actual owned overlay run](https://github.com/caox233/FifthFreedom/actions/runs/39910723452)
also built core/CLI and the native socket probe, but its runtime step failed with
CONTAINMENT_CHANGED and TRANSPORT_MATRIX_INCOMPLETE. Those codes alone do not
establish which runtime operations occurred before failure. The strict firewall
readback remains unchanged; a containment-only diagnostic now runs before long
builds to identify the precise structural difference.

A fresh detached0ac6695 source checkout passed331 methods:329 passed and two
existing Windows-only skips, with tracked files unchanged. Its canonical Git
patch from fc8ef510 was applied and reversed with exact bytes, modes and pathsets:
1312 baseline paths,1492 target paths,1312 restored paths and clean Git status.
Patch SHA256 is6545f70219ae473021f1038abe7bf2df68f6fee455fe69ad6f08cd268b01fea8.
Private original four-role evidence is unchanged. The separate knowledge snapshot
at d92ed600 binds94 files, including92 exact source mirrors, to0ac6695.


## Owned observations connected to the native lease

[SERVER-14](tasks/server-14.md), commit6a8e8fe, adds a concrete overlay monitor
for the existing owned supervisor and native parent lease. It checks the core
image/pidfd, namespace and TUN, requires the configured loopback RPC listener's
sole inode to belong to that core, executes the pinned CLI under a bounded
deadline, and rechecks ownership after the await. Accepted peer loss permanently
revokes the lease; restoring old JSON cannot revive it. A distinct query child
handles verified fast completion without relaxing long-lived core/native startup.

The18 new real process/IPC tests and original16 lifecycle plus10 parent-lease
tests passed both owner and independent review. The actual supervisor/monitor/
lease composition reached READY then revoked and reaped its children. Its TUN
and peer JSON/native peer are explicitly synthetic; this is not a real EasyTier
or native-main positive. Same-build core/CLI provenance, private config and
namespace containment remain separate trusted caller prerequisites.

A fresh detached6a8e8fe checkout passed358 methods:356 passed and two existing
Windows-only skips, with tracked files unchanged. The next executable integration
uses the actual same-build core/CLI and same-run native component package in an
owned namespace. It will distinguish lease revocation while the route remains
alive (real held TCP closure) from peer disappearance (process/FD cleanup, without
calling an unreachable remote TCP timeout an EOF). Ordinary Public admission
remains closed and no complete package or gameplay acceptance is inferred.


## Actual fixed-port and strict containment prerequisites passed

The [568c3c5 Windows quick run](https://github.com/caox233/FifthFreedom/actions/runs/39915438709)
completed successfully: exact default-IIS identity/HTTP80 member, scoped stop,
owned port80 bind, original final-login nine checks, and restoration to Started.
Production ports and exclusion rules were unchanged. The original failures and
extra default WCF binding observations remain in the bounded official receipts.

The [f85c515 Linux quick run](https://github.com/caox233/FifthFreedom/actions/runs/39916016496)
also passed exact nft readback and owned namespace cleanup. Official nft source
confirmed that a typed TCP/UDP port selector generates its own protocol check;
readback elides the redundant explicit condition. Commita689dd5 avoids emitting
that duplicate only for typed port rules. Portless protocol checks, default drop
and full normalized-rule equality remain intact;20 independent checks passed.
The quick probe executes no EasyTier/TUN/native runtime. The separate full owned
transport run39916016529 remains required.

The full568c3c5 Windows candidate is retained while later runs queue, so new pushes
do not destroy its first native pipe/interop evidence. No cancellation is called
a pass. The standalone knowledge repository is pushed at92a1342 and binds101
snapshot files, including99 exact mirrors, to source11181ce. Official environment
draft11 was saved and read back after complete workspace discovery:19,699
directories, two projects and24 explicitly classified dependency/evidence Git
roots, no errors. It selects source568c3c5 and knowledge92a1342; the tested install
script, startup instructions and narrow network draft were preserved. Saving
still does not publish/apply the draft or verify fresh snapshot restoration.


## Combined runtime harness, source-bound transport and retained failures

[SERVER-15](tasks/server-15.md), commit31f1e5e, connects the real owned overlay
monitor, supervisor and native lease in a new finite hosted harness. Its two
controls distinguish actual held TCP closure on native lease revocation while
the overlay stays alive from cleanup after the remote peer disappears. Seventeen
local and independent checks passed; their synthetic observations are identified.
Actual same-run native and same-job core/CLI positives remain pending. CI wiring
at6c11c8b preserves strict prerequisite checks and publishes only bounded receipts.

[UI-08](tasks/ui-08.md), commit75db71b, constrains each native API connection and
reconnect to a validated Windows source/interface and numeric destination, and
revokes pending and retained sockets with the shared lease. Linux API56 passed;
local i686 type checks are not Windows execution. Eight real Windows socket
contracts are registered. The production issuer remains closed because a real
owned adapter/session and secure-peer liveness chain is not implemented yet.

The [568c3c5 candidate](https://github.com/caox233/FifthFreedom/actions/runs/39915438707)
passed its managed suites, including return25 and final-login9, then failed the
hooks test build with official compiler code E0425. Native IPC and interop had
not executed. A local full-Rust diagnostic reproduced an absent external C71
fixture causing E0425. The unchanged13 replay tests now require an explicit
feature and retain their real fixture requirement; a distinct strict CI input
gate fails while those inputs are absent. Targeted IPC/source contracts never
count the held replays as passed. The unrelated authored fixture is not used as
a replacement, and the original0ac managed-return failure remains unexplained.

The [f85c515 full overlay run](https://github.com/caox233/FifthFreedom/actions/runs/39916016529)
passed strict containment and real builds, then failed WORKER_REPLY_EOF,
OWNED_WORKER_CLEANUP_FAILED and TRANSPORT_MATRIX_INCOMPLETE. Those codes do not
prove core/TUN/peer startup. Reviewed3b0fadc adds fixed phase diagnostics and
corrects an independently reproduced exact-child reap race; it is not yet proved
to explain the hosted EOF. The next actual run retains strict cleanup failures.

A fresh detached31f1e5e checkout passed376 methods:374 passed and two existing
Windows-only skips, tracked files unchanged. Report SHA256 is
e1864699e94efe24405d4539b1221f73159a434f0d73357b804a69a17df079fc.
The canonical patch fromfc8ef510 to31f1e5e applied and reversed with exact Git
bytes/modes/pathsets:1312 baseline,1520 target,1312 restored and clean status.
Patch SHA256 is4cd14dbc41681767d3d43b70524f65023e2165aed2a8d5e1eed24eadd0b18be2.
This supplements cloud receipts and does not replace the private PC originals.
Complete resource packaging, native replay closure and live acceptance remain
separate open work within the original six milestones.


## Actual native interop passed; Windows cleanup and overlay readiness isolated

The [1b6cdab Windows run](https://github.com/caox233/FifthFreedom/actions/runs/39918375705)
actually passed native return8 and same-run Rust/C# interop3, then failed the
new source-transport suite before branding/component completion. The original
configuration framing/ACK and owned pipe path were exercised. Whole-package and
loaded-DLL admission were explicitly outside the synthetic helper seam.

Microsoft documents that IP_UNICAST_IF accepts network order on set and returns
host order on get. Reviewed2ace336 removes the erroneous second conversion on
readback while keeping the exact interface/source/length checks. Its hosted
verification is still pending: the later6c run failed managed case15, and
[9e1c7a1 quick](https://github.com/caox233/FifthFreedom/actions/runs/39921420637)
and full Windows checks precisely located that later failure at owned-directory
cleanup after child exit and process/config disposal, with no read-only attribute.
The earlier coarse CONFIG_ACK label alone had not established a pipe failure.
No production ACL or TLS requirement was relaxed. The original0ac unexplained
return failure is not retroactively assigned this cause.

The [6c11c8b full overlay run](https://github.com/caox233/FifthFreedom/actions/runs/39919260513)
passed containment and real builds then failed OVERLAY_READY_TIMEOUT and
TRANSPORT_MATRIX_INCOMPLETE. The earlier worker EOF/reap codes did not recur.
That progression is not proof of a ready core/TUN/secure peer; exact timeout
observations and pinned QUIC address semantics remain the next diagnostic work.

Exact static comparison identifies the retained loader as official Ultimate ASI
Loader v9.7.0 Win32, and matches its three embedded binary inputs to tagged source
blobs. Commit116555b retains seven exact upstream notices and unresolved ASI,
embedded terms/SDK/MPL distribution and independent-rebuild holds. Reviewed
c7ffe7a carries the seven texts and exact provenance JSON into Windows components,
same-run assembled candidates and complete-package outputs. An initially proposed
shared checksum list would violate the downstream fixed-component contract;
review caught it before commit. Separate notice checksums preserve that contract.
Actual collector-to-consumer regression plus retained assembly/provenance checks
passed29/29 independently. No frozen archive or binary pin changed.

A fresh detachedc7ffe7a checkout passed405 methods:403 passed and two existing
Windows-only skips, tracked files unchanged. Report SHA256 is
5403395e8551f026b2263151db7245cc52c0b20734c9924fbf300dfb54827ea3.
The canonical patch fromfc8ef510 to this revision applied and reversed with exact
bytes/modes/pathsets:1312→1536→1312, clean status. Patch SHA256 is
4ac4f3b3f2cc37613dbba4f370a3007ddd8adaf1e200738bd286b2a1ecf0e954.
Private original evidence remains unchanged. DevDocs896c632 is separately pushed
and binds110 snapshot files, including108 exact mirrors, to source00e6971;
subsequent source work requires a later explicitly bound snapshot.


## Windows cleanup passed; paired build and runtime reruns started

The [1974fad Windows quick run](https://github.com/caox233/FifthFreedom/actions/runs/39922765064)
passed all27 owned-return checks and the original9 final-login checks, with the
default IIS site restored. The official artifact ZIP digest was independently
verified before reading its four bounded JSON receipts. The fixture cleanup fix
retries only Windows access/share/lock errors under one finite deadline and only
after its exact child and both owned handles are released. Real delayed-lock and
persistent-denial tests retain that boundary. This result does not identify the
external holder responsible for the earlier denial or rewrite the original0ac
unknown failure. The separate full1974 Windows component job then succeeded.
Its official74,830,972-byte artifact ZIP matches digest
87d447a8cf3e7658ecaea0fe335b6fa41bc3e78246cc18e0dcc530e989af372c.
Actual native8, interop3 and source8 all passed with zero ignored/skipped tests;
the four component hashes match their receipt/checksum entries and checked PE
branding passed. This verifies the getter correction in the registered Windows
socket contracts, not a live overlay/game session. Same-run assembly and static
preflight also passed. Native lifecycle and combined-stack jobs then failed
without positive runtime evidence; the hosted result exposes only
UNCLASSIFIED_PROBE_FAILURE. Independently, the exact isolated Python command
reproduces a missing scripts import path in the stack containment entry. The
next fixes retain isolation and add only the reviewed checkout module path;
this reproduction does not reveal the separate native job's exact hosted cause.

Normal official API downloads now work under the environment's existing
unrestricted network selection. Downloaded1b and6c artifacts supplement earlier
metadata-only records; their ZIP hashes and receipt checkout identities are
retained separately from run source heads. Raw logs, signed URLs and credentials
are not public evidence. The6c overlay receipt confirms a QUIC readiness timeout,
zero completed phases, two namespaces removed and private stage cleanup; it does
not prove TUN/peer readiness or that every child was reaped.

Reviewed45e879b uses the pinned real Rust producer types, URL builder and serde
projection to correct a specific QUIC initiator bind mismatch: its local endpoint
can be a wildcard bind. That bind is not an observed outgoing source address.
Remote endpoint, key/authentication, route and nft requirements remain exact.
The new bounded last-readiness observations distinguish missing TUN, failed query
and rejected peer projection. Profile12, probe27, monitor18 and stack20 local
checks passed. [Actual full runtime39924391718](https://github.com/caox233/FifthFreedom/actions/runs/39924391718)
must still complete correct-pin, wrong-pin and restored-key controls; the parser
repair alone is not successful runtime evidence.

The first native lifecycle command also had a reproducible sudo checkout trust
failure. Reviewedce0df6d scopes Git trust to each exact repository command and
preserves expected HEAD/clean-source rejection;13 independently rerun checks
passed, including a genuine foreign-owner Git reproduction. No global Git config
or repository ownership changed. The next candidate contains this correction;
the older running1974 candidate does not.

[UI-09](tasks/ui-09.md) now includes the real paired Windows session backend and
EasyTier caller patches and a separate full MSVC
[build-only run39925069086](https://github.com/caox233/FifthFreedom/actions/runs/39925069086).
Exact preparation of700 EasyTier and49 tun source files, locked dependency/feature
resolution and12 focused regressions passed. A separate modern-Windows feature
avoids the upstream legacy Win7 thunk downloader's unverified archives. It does
not promise Win7 compatibility. The workflow compiles core/CLI and inspects their
PE headers without executing them, loading a driver or uploading binaries.
The first hosted run passed its12 regressions, then failed before a recorded
cargo stage with fixed WINDOWS_BUILD_FAILED. Exact stage/root cause requires
bounded diagnosis; no compilation success is inferred. Full Windows compile/link,
actual returned adapter/session identity, secure peer liveness and local owner
IPC remain pending; ordinary Public stays closed.

A fresh detached1a6b2e6 checkout passed425 methods:423 passed and two existing
Windows-only skips, tracked files unchanged. Report SHA256 is
61ff965e042f9a2f9e706cdc1e377864d36b99578ca57a668df3a6878e89f55d.
The canonical patch fromfc8ef510 applied and reversed with exact bytes/modes/
pathsets:1312→1556→1312, clean status. Patch SHA256 is
5ff12babe168c4299fa53915174e7de69558d15e03e820ff8c80661217f72173.
DevDocs557d3e8 independently binds120 snapshot files, including118 exact mirrors,
to sourcec893c0b. Environment draft15 was saved/read back with both project
revisions and preserved setup/network selection after a complete workspace walk;
it still requires publication and does not verify fresh snapshot restoration.
The platform exposes no Goal scheduler or publish action in this session.
All six milestones, held resources and original private evidence remain tracked.

The completed45e879b actual overlay run now gives a narrower observation:
QUIC initiator readiness sampled123 times, with its owned core image and TUN
confirmed, while the peer projection failed PEER_RESOLVED_ENDPOINT_INVALID.
No correct-pin/wrong-pin/restore phase completed and WSS/TCP were not reached.
The official receipt ZIP digest matches
16b7e82176aef2d12e41a70f71a328abf2b86e2f0b8bf9fd952a541052e6a8e6.
Two namespace removals and private-stage cleanup are explicit. A rejected
observation does not prove that the underlying peer lacked authentication; the
exact resolved-address producer/consumer boundary remains the next correction.

An ordinary HTTPS download also obtained the official Wintun0.14.1 ZIP and its
prebuilt-binary license. Reviewed7930de9 records all seven members, exact matches
to the previously pinned amd64/x86 DLLs, and the original CRLF license bytes.
Ten focused checks passed independently. This closes the missing official
origin/license input, while final API-use/distribution conditions, Npcap's
separate hold and actual driver behavior remain unresolved. The finite
[Windows session probe](tasks/wintun-retained-session-probe.md) is being
implemented for a clean disposable hosted VM only; it has not executed a driver,
configured a peer or enabled Public. DevDocs38f5efc has meanwhile been pushed and
verified, binding135 files, including133 exact source mirrors, to source0604651.

## Paired Windows build and finite Wintun lifecycle verified

The [paired Windows build39927010635](https://github.com/caox233/FifthFreedom/actions/runs/39927010635)
at41fe262 succeeded:14 focused regressions, locked metadata, and the complete
MSVC Release core/CLI compile and link passed. The official ZIP digest and its
three allowed JSON members were independently checked against the exact source,
nine patches and transformed Cargo lock. The runner inspected both AMD64 PE32+
outputs; no core/CLI binary ran or was published. The first failed1a6b2e6 build
is retained. The canonical archive correction preserved all original pins.
See the [source-bound receipt](evidence/hosted-windows-easytier-41fe262.json).

The separate [Wintun run39930667783](https://github.com/caox233/FifthFreedom/actions/runs/39930667783)
at319bf44 passed two actual driver/session lifecycle cases on a clean disposable
GitHub Windows2025 VM. Owner drop and async shutdown revoked retained observers;
the receive worker reached EOF and both owned adapters disappeared. The seven
unrelated PnP devices and kernel adapter identities remained unchanged. The
official driver package/service/file remain in that disposable VM: there was no
global driver uninstall and no claim of restoration to a driver-free machine.
The harness configured no addresses/routes/peers and sent no packets. The
[verified receipt](evidence/hosted-wintun-319bf44.json) includes the official ZIP
digest and exact source/lock/DLL identities. The earlier HOST rejection remains
recorded; its fix accepts only the two exact official Windows2025 image aliases,
with all clean-host and owned-device gates retained. Fifteen portable checks and
independent receipt review passed. This closes a finite backend prerequisite,
not the actual EasyTier owner/source-route/secure-peer/IPC chain or Public lease.

The [41fe262 candidate](evidence/hosted-candidate-41fe262.json) completed with
Windows/Linux component, namespace and same-run identity/static jobs successful
at official job-metadata level. Its stack job failed before runtime compilation
with the exact missing module ci_socket_isolation, matching the local isolated
Python reproduction. The reviewed1f62862 fix keeps -I and adds only the trusted
checkout scripts path. Its separate native lifecycle failure remained
UNCLASSIFIED_PROBE_FAILURE even after the scoped Git trust correction; no causal
claim is inferred. Both strict withheld-input gates stayed red and full packaging
was skipped. These checks are prerequisites, not a completed runtime stack.

A real production QUIC loopback reproduced an IPv4-mapped IPv6 resolved form
rejected at the same parser boundary as45e879b; that older run did not retain
its raw endpoint. Reviewed54a62c4 allows only the
canonical equivalent of the configured IPv4 address and port in the QUIC
initiator resolved-remote field. Wrong IP/port, other roles and other fields
remain strict. Its14 profile,30 probe and38 retained monitor/stack tests passed.
Separately, source inspection found that handshake authentication/key fields and
plaintext Ping/Pong do not establish fresh authenticated traffic. The existing
monitor's age is the time of its local CLI/kernel observation. A new separate
production decrypt-boundary observation is being implemented; no ordinary
Public factory has been opened on the strength of handshake history.

A fresh detachedb613861 checkout passed451 methods:449 passed and two existing
Windows-only skips, with tracked files unchanged. Report SHA256 is
bf11040886ac5c85097c6ee0148843f13abcc02fa6a311833a0370ff05699dd3.
The canonical patch fromfc8ef510 applied and reversed with exact bytes/modes/
pathsets:1312→1574→1312 and clean status. Its SHA256 is
0397607c72be9856fba38542a4c7b9ccddb1e483af84bf1e9c4f1c7ce260a639.
These receipts bindb613861, not later untested work. Original private four-role
evidence remains unchanged. Environment draft16 was saved and read back with
source319bf445 and DevDocs38f5efc after a complete workspace inventory; setup,
network selection and requirements were preserved. It still requires manual
publication in the environment UI. No Goal/scheduler or publish API is exposed,
and fresh snapshot restoration has not been verified.

The next actualb613 Linux matrix39929836302/job120335089272 passed its build
stages but stopped at worker observe/OVERLAY_OBSERVATION_UNAVAILABLE, with zero
completed transport phases. Its official receipt ZIP matches SHA256
e38d93d4ad3a3395b5aeea910c9b6884a0c535c10517e8c68bde568e0941197d.
The worker explicitly reports cleanup_reaped=true, both created namespaces were
removed and no cleanup errors were recorded. This different failure establishes
neither a positive matrix nor an authentication failure. The319 candidate
separately stopped in two new Windows isolated-entry tests before Windows
components or downstream native lifecycle jobs could run. Reviewed528668e fixes
their actual CRLF line comparison and unavailable Win32 fchmod call, preserving
all three isolated CLI checks and POSIX file modes;31 focused checks passed.
Both actual failures remain recorded and the fixes require hosted verification;
Linux-only source success is not Windows success.

## Current session observations and cross-platform source evidence

Reviewed2408d6a adds a separate production-linked authenticated Data receive
observation, after actual AEAD/replay success. Plaintext, Ping/Pong, RPC and relay
pass-through cannot refresh it. Reads recheck connection ownership, current
session installation, generation and a nonsecret synchronization revision.
Independent review reproduced two failures when the same shared session Arc was
resynchronized without changing its generation, including a generation round
trip. The revision fence fixes those cases:15 new checks and29 retained crypto/
ingress/pin/handshake checks passed; independent final15 and exact patch reversal
also passed. [The source candidate](tasks/authenticated-receive.md) remains
separate from current runtime recipes. It records an accepted event, without
proving active challenge response, idle-peer liveness or a production lease.

Reviewedad60fe0 connects the actual Windows Instance/NicCtx ownership path to a
typed retained-session read. One shared deadline bounds waiting on the container
and inner read; wrong type, missing context, owner loss, cancellation and timeout
remain failures. The returned snapshot is historical and cannot grant a lease.
Eight tests link the exact production helper, including a combined elapsed-budget
case. Independent preparation and patch reverse/forward checks match the new
ten-patch source inventory. The
[23d592 Windows build39935015346](https://github.com/caox233/FifthFreedom/actions/runs/39935015346)
has passed20 builder regressions and is compiling; its full result remains pending.

Roota093045 and the reviewed23d592 hook also add bounded static normal/delay-load
DLL inventories to the paired outputs. Fourteen parser checks passed independently.
Four actual retained candidate PEs, including the144.9MB launcher and its two
delay-load DLL names, were independently compared with binutils without execution
or original-byte changes. Actual new paired core/CLI imports await the running
builder. Static import names do not resolve search paths, symbols, forwarded
imports or later dynamic loads, and do not close runtime/license requirements.
The exact existing source separately shows a Windows pnet fallback outside the
faketcp feature; simply disabling faketcp would not prove removal of Packet input.

The first [dual-platform source run39935015323](https://github.com/caox233/FifthFreedom/actions/runs/39935015323)
at23d592 succeeded on Ubuntu and Windows with both official artifact ZIP digests
verified. Ubuntu executed477 methods:475 passed, two existing Windows-only skips.
Windows executed477:390 passed,87 explicit platform-specific skips, including
Linux pidfd/namespace/file-mode checks and Windows symlink privilege checks. Neither receipt reports complete-package readiness. The new
lane uses the same source command and fixed Python dependency, without installing
compiler SDKs or changing strict resource gates.

The latest actual Linux probe49193b5run39933668757/job120347846855 completed the
QUIC correct-pin-before phase: real TCP, fixed UDP and ephemeral UDP echoes all
passed. It then failed observe/OVERLAY_ADDRESS_QUERY_UNAVAILABLE; no complete
wrong-pin/restore or WSS/TCP result follows. The official ZIP digest is
f56b796189a3f7634ccc139378616a416e6103c16202dfebb6e0eb4356f61cae.
Worker reaping, both namespace removals and private-stage cleanup are explicit.
This receipt does not identify which role/errno caused the later address error.
Separate read-only Linux ioctl controls and exact upstream startup order show an
initial visible-UP/no-IPv4 window. Reviewed1bc552c makes only an initial UP
interface's exact EADDRNOTAVAIL pending, without committing readiness or index.
Other errors and address loss after initial success remain fatal.20 lifecycle,
19 monitor and31 probe local checks passed; the two new regressions passed
independently. Actual hosted verification of this correction is still pending.

A fresh detached23d592 independently passed475 methods with two Windows-only
skips and unchanged tracked files. Report SHA256 is
b65315f6fcd01c68e11ebe34c39b2f41ce1546bb9977a14480afba31cc5a8463.
Its canonicalfc8ef510 patch applied and reversed exactly:1312→1593→1312, clean;
SHA256e9abcd8c9f361335a47129d3af98655c396fe9d6b160e115f00e3b25af469b26.
The original private four-role evidence remains unchanged. DevDocsa7e8b65 was
pushed and its remote SHA verified;149 files, including147 exact public mirrors,
bind source02e35ef. Both draft PR descriptions now identify that reviewed
checkpoint and the remaining resource/runtime boundaries. No release or
production deployment has occurred, and all six milestones remain tracked.

The528 candidate39932526946 subsequently passed Windows component job120344867895
and same-run identity/static job120354930050 at official job-metadata level.
The native lifecycle job120355233722 now reports a concrete failure:
WORKER_NATIVE_LISTENER_OWNERSHIP_INVALID, after checkout/download/runtime
prerequisites succeeded. No native failure receipt artifact was produced; the
exact case/listener/cause is still under investigation. This new result is not
assigned retroactively to older generic native failures. The combined stack
job120355233682 remains running. No independent new component-byte rehash or
positive native lifecycle result is claimed by this metadata follow-up.

## Typed Windows build, listener state and retained diagnostics

The actual [23d5923 Windows build](evidence/hosted-windows-easytier-23d5923.json)
completed successfully: 20 builder checks, eight typed current-session contracts
with zero ignored tests, locked metadata, and full MSVC core/CLI compilation and
linking. The official three-receipt artifact digest was independently verified.
Its exact ten-patch source inventory is
efb765a226be8ad35826c71868b105f09d5d6971b9d58580754389efec058fcd.
Runner inspection of the compiled outputs confirms that core directly imports
Packet.dll; CLI does not, and both delay-import lists are empty. The executable
bytes were not published or independently downloaded. This is a successful
build with known runtime dependencies, not core execution or a Public lease.
The earlier nine-patch build and separate two-case Wintun driver evidence remain
unchanged.

Reviewed2f10f82 fixes a real native listener-checker defect: established and
TIME_WAIT TCP connections sharing a local tuple were incorrectly counted as
additional listeners. Actual owned sockets reproduced both failures before the
fix and passed afterward. Only LISTEN rows now enter TCP listener cardinality;
exact owned inode, address, port and uniqueness remain required. Accepted-only,
foreign-owner and duplicate-LISTEN controls still fail. Sixteen native and20
stack checks passed; independent review reran three actual socket controls.
This identifies a checker defect, not the exact unrecorded case/port of the528
hosted failure. New candidate39938418762 still must verify native execution.

Reviewed6a60418 extracts the retained diagnostic text reader into one shared
production implementation and adds12 real-file streaming regressions. Tests
cover UTF-8/redaction across16KiB reads, line and JSON limits, snapshot extent,
cancellation and output failure while verifying unchanged original bytes.
Independent12/12 and full WPF cross-target compilation with zero warnings/errors
passed. These checks do not rerun the Windows full-ZIP24 cases or resolve the
user's historical export exception. The independent hosted Windows job120363584181
in run39938418815 subsequently passed all12 with zero skips. Its official
single-receipt artifact SHA256 is
c203db5c00f0d7bdee38ced6313098607d86a4b02ddde3e9e3809567faf4123b,
independently verified; the receipt explicitly keeps full-ZIP coverage false.
The [task record](tasks/diagnostic-streaming.md) also preserves the actual chat
boundary: historical391 checks explicitly simulated delivery, and exact retail
extraction/generated inputs are absent. Inventing three socket clients would
not test the uploaded native serializer or establish three-client acceptance.

The [05c127a source matrix](evidence/hosted-source-matrix-05c127a.json) passed with
both official ZIP digests verified: Linux484 passed plus two existing Windows
skips; Windows397 passed plus89 explicit platform-specific skips, including two
symlink privilege cases. Separately, a fresh detachedb67af6c checkout passed489
with two existing Windows-only skips and unchanged tracked files. Its report
SHA256 is ed804c635988cfaca6755dd609727d8e721bd5580b6ffdb3410418f6c2a53613.
The canonical patch fromfc8ef510 applied and reversed with exact bytes, modes
and path sets:1312→1607→1312, clean. Patch SHA256 is
4c0a6eb1d9e15a8d114e0d86e7f1ef955496af7170088412e1a316d3d85a0deb.
Original private four-role evidence remains unchanged; full packaging remains
blocked by the explicit withheld inputs.

DevDocs68779b3 was pushed and its remote SHA verified:162 files, including160
exact public source mirrors, bind source9d823da. Official environment draft17
was saved and read back with source05c127a and that DevDocs revision after a
complete workspace inventory:28281 directories, two projects,26 classified
dependency/evidence Git roots and zero errors. Existing setup, network selection
and requirements were preserved. The draft still requires publication through
the environment UI; no publish or Goal/scheduler tool is exposed, and a fresh
restoration has not been verified. All six milestones remain tracked; no formal
Release, production deployment or game/capture launch has occurred.

## Complete Linux transport matrix and partial native composition

The first complete finite guard3 runtime matrix succeeded in
[05c127a run39936896183](evidence/hosted-owned-overlay-05c127a.json), actual
merge checkoutd7d819317a4e3ff72efa53400ce258c4154eb783. The official artifact
SHA256 is a72a3b2cacc7857b4cd4bb8c1bf4a330f2a648dd4568414de7930f9faea00699.
The strict receipt consumer verified all nine phases: QUIC, WSS and development
TCP each passed correct-pin-before, wrong-pin rejection and correct-pin restore.
Both positive phases exercised TCP, fixed UDP and ephemeral UDP echoes. Every
wrong-pin phase retained the owned TUN while rejecting all three kinds of new
traffic over the bounded observation. Core reaping, new-traffic denial, TUN
removal, all six namespace removals and private-stage cleanup are recorded.
This closes that development matrix, without asserting packet-capture encryption
proof, the newer active nonce challenge, native owner lease or Public admission.
Prior failures and their unknown exact errno/role remain unchanged.

The older [528 native composition](evidence/hosted-owned-stack-528668e.json)
completed with an overall failure. Its lease-revoke case recorded real native
READY, five owned listeners, watchdog exit92, five rebound ports and held TCP
EOF/reset while the overlay remained alive. This source predates the explicit
pre-revocation OPEN barrier, so those samples alone do not prove revocation
caused the connection closure. The second peer-loss case failed during observe
with WORKER_UNCLASSIFIED_ERROR before combined readiness was confirmed.
The missing result field does not establish that the native process never
reached READY in the background. Its exact cause is unknown;
the two cases used separate namespaces, so no cross-case TIME_WAIT explanation
is inferred. Both namespace pairs, worker and private stage were cleaned up.
The official artifact SHA256 is
29adfe90b748e878d7123237f0b3e28dc7743d10ea0e7de970dc79438c7a6d8e.

Reviewed8b25109 now requires a bounded OPEN observation on the same held TCP
socket before revocation or peer shutdown. Real early-FIN/reset and excess-data
controls reject; native21 and stack22 checks passed, including independent5+2
new controls. The original deadline is not extended. This improves before/after
evidence without promising continuous observation or strict causal proof.
Later hosted runs must exercise this corrected path; the old528 result is not
rewritten as a pass.

The independent authenticated-receive job120358530925 in run39936896405 also
succeeded: all44 actual production-linked contracts passed with zero ignored
tests, followed by locked GNU Release core/CLI compilation. The official three
JSON receipts match ZIP SHA256
09e287ffda1ac7b0f63073c02fe47dc4243a7a71c2180be786947225e31f230c,
the exact699-file source inventory and eight-patch recipe. Independent review
verified the artifacts. Core/CLI identities are runner static ELF observations;
no runtime binary was uploaded, downloaded or executed. The successful recipe
does not yet include the new nonce-correlation work. The separate old35
compatibility job120358530652 subsequently passed its own35 contracts with zero
ignored tests and a locked Release build; its official ZIP digest
c27ca2d0f06942c3e112f7da046e48efb05c0a0905ea6f9482efe19b76c410a0
and full source inventory were independently verified. The unchanged default
entry remains distinct from the new44 recipe.

Reviewedf79ecc6 implements atomic Windows Job containment from process creation,
retains exact process identity and independently checked image/configuration
handles, and observes the full owned job draining before releasing inputs.
Local tests passed5 with12 explicit Windows skips; retained return tests passed14
with13 Windows skips, and WPF cross-compilation had zero warnings/errors. The
08b3d07 hosted lane requires a real x86 self-contained helper and17 cases with
zero skips. Actual run39939697022/job120367800235 passed all17 with zero
skips, including the real32-bit assertion, and the helper apphost hash matched
before/after. The official single-receipt artifact SHA256 is
a587b7102370bcfed7e3a63f4c230962fb50550aad0e62ff423168bcf41578b1.
A synthetic helper is not an executed EasyTier core, owner IPC or live Public
network; those production-path prerequisites remain in progress.

Six inactive native incremental compiler caches were reclaimed after their owners
confirmed they were unused:681 generated files,490245263 bytes. All6887 retained
entries matched before and after by regular-file bytes/modes/path or symlink
target; symlinks were not traversed. Sources, final binaries, logs and original
evidence remain. Active EasyTier targets and ongoing compilation were untouched.
