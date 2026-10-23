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
