# UI-06 native owned authentication return

Status: ready-for-agent for remaining Public integration. Production-linked
implementation, portable checks and hosted Windows pipe/native/interoperation
results are recorded at their precise revisions below. The local-only return
pipe P2 is now verified on actual production handles at `6ecfdb4`; this does not
establish complete Public admission. No game was started.

The original configuration pipe, TOML bytes, length header and receipt ACK are
unchanged. An optional `FF_PUBLIC_RETURN_V1` contains only a pipe name, launch ID,
expected launcher PID / creation FILETIME / image path / image SHA-256 and schema.
It contains no bearer or account credentials. A normal LAN launch has no descriptor
(the launcher removes inherited descriptors); it retains existing login behavior.

The native side observes and retains the real configuration-pipe server process
handle before reading its content. The return channel requires all three anchors:
that kernel observation, the independent private Process.Start descriptor, and
the launcher role of the independently verified release package. It checks the
actual return pipe server against the same pinned handle and liveness. The
observed process alone grants no authority. This retains the trusted immutable
local installation boundary; it is not a defense against a hostile same-user
process changing admitted images in place. Only the launcher's exact original
owned game process is eligible. Unproven retail successors stay closed.

The return wire is strict UTF-8 JSON with a u32 little-endian length (1..8192).
Unknown / duplicate fields, zero generations, invalid nonce and token shapes fail
closed. The server sends `{schema,kind:challenge,launch_id,challenge}`; native
replies `{schema,kind:hello,launch_id,challenge,game_instance}`. `game_instance` is
its existing per-loaded-DLL game nonce, never the launcher's launch ID. Then
`invalidate` and `heartbeat` contain schema and generation; `authenticated` also
contains token, account_id and username. The latter two come from the same actual
successful native LoginResponse.user and are compared by the launcher to its
independently captured account login. Decoded numeric user_id remains unverified
context metadata until the protected server authenticates the signed bearer.
No token enters a configuration, environment variable, file or diagnostic.

Authentication owns a bounded latest-state slot. Only after acquiring LOGIN_LOCK
does an attempt advance the generation and invalidate the previous bearer.
Failure, cancellation and guard drop invalidate that owner; a canceled waiter
cannot invalidate an existing owner. Successful publication advances again and
checks the exact owner after releasing LOGIN_LOCK. A newer attempt always wins.
The worker performs no IPC under LOGIN_LOCK or the state mutex, rechecks the
current generation after writes, and emits only the latest generation heartbeat.
Cross-process invalidation takes delivery time; it is not instantaneous. Heartbeat
is 2 seconds; launcher idle expiry is 8 seconds. Every frame has a 5-second I/O
budget; connection plus challenge plus hello share a 10-second startup budget.
Overlapped cancellation drains completion before releasing buffers / OVERLAPPED.

The worker starts during normal login, outside DllMain. Public admission pins the
hooks module before creating its worker; once pinned it remains loaded until
process exit, including if a later thread / pipe step fails. This intentional
tradeoff prevents executing a worker from an unloaded DLL. Deinit only marks the
memory state closed and wakes the worker; it never joins under the loader lock.
Any pipe failure permanently closes native publication for that process; no
automatic reconnect replays a bearer. The managed receiver additionally supports
byte-identical same-generation reconnect, but native does not use it.

## Validation

- Actual Linux `cargo +nightly-2025-10-15 test -p api --lib --locked`: **54 passed,
  zero failed / ignored**, including 9 new production return-contract cases.
  They cover bounds, duplicate keys, redacted Debug, actual unchanged native
  fixture context, actual-response account bounds, owner generations, stale
  publication, invalidation, overflow and current-run / release / source /
  observer matching. Shape/context decoding does not authenticate a signature.
- Actual `hooks-config` i686-pc-windows-msvc cross-target `cargo check --lib
  --locked` passed. An isolated local type-check wrapper imports the exact
  production worker and all 8 Windows test functions; `cargo check --tests`
  passed. The wrapper's package-admission and run providers are stubs used only
  to type-check; this is **not** execution, package admission or whole-hooks
  compilation evidence. Logs are retained under `.local/evidence/cloud/ui06-native/`.
- Windows execution entry:
  `cargo test --locked -p hooks --lib --features patch-free,lan-goal-origin api::public_return::tests -- --test-threads=1`.
  Eight test functions include one synthetic child entry and seven substantive
  checks: real process birth / path / pipe anchors, owned wrong-server PID and
  owner death, overlapped timeout + drain + subsequent buffer reuse, oversized /
  partial / EOF frames, production challenge / actual game nonce / auth /
  invalidation / heartbeat / EOF, wrong challenge, and authentication guard
  cancellation / stale success. The child is the owned test executable, not a
  game; cleanup addresses only the returned child handle. The tests deliberately
  enter the admitted production worker seam; they do not pretend to validate a
  complete four-component package or the independent launcher admission factory.

Required next evidence: actual Windows compilation/execution, then a dedicated
cross-language owned-process probe if needed. The separate managed suite and the
Rust suite are complementary; neither alone proves their live interoperation.
Public deployment admission, private SAV/META resources and real-game acceptance
remain gated independently.

## Local-only return pipe correction (2026-10-27)

The review of `fc8ef510...fe6e3cd5` identified a P2 boundary omission that was
still present in the preserved `c42e0eb` source: `CurrentUserOnly` restricts the
ACL, but does not set `PIPE_REJECT_REMOTE_CLIENTS`. A peer PID must not be
interpreted as a local owned-game PID before the pipe excludes remote clients.
This is a source-confirmed omission, not a demonstrated SMB exploit; ordinary
Public admission remains closed.

`LocalOnlyPipeServer` now contains the existing native factory extracted from
`OwnedCoreReadChannel`. Both that channel and every `PublicReturnPipe` server
instance call it. It keeps the protected current-user-only DACL, non-inherited
handle, duplex/byte/overlapped operation, first-instance restriction and one
instance limit, and requests the kernel reject-remote bit at creation. The
process/creation-time, configuration ACK and nonce checks remain unchanged.

The managed return suite adds a Windows case with actual handles: the former
managed constructor is a negative control, the shared factory is queried and
its ACL inspected, successful same-user byte delivery and rejected duplicate
creation required, and a connection to the actual production receiver queried. The
probe uses `NtQueryInformationFile(FilePipeLocalInformation)`, whose NT creation
flag is `FILE_PIPE_REJECT_REMOTE_CLIENTS = 2`; Win32 `GetNamedPipeInfo` does not
expose that flag. The target Windows observation still needs execution: if the
OS does not expose the creation bit in this query, this probe must fail and its
method must be corrected, rather than claiming that a source constant proves
the live handle boundary. Sanitized receipts retain only fixed query labels.
No SMB service is enabled or reconfigured by the probe.

Focused Linux validation: PublicNativeReturn **14 passed, 14 Windows cases
skipped**; the CI adapter **10 passed** and diagnostic parser **24 passed**.
The adapter now requires **28 passed, zero skipped** on Windows; the prior
27-case result cannot satisfy the new lane. The four OwnedCore projects retain
an explicit link to the shared factory. Initial new-test platform-analyzer
errors and the outdated diagnostic-bound negative fixture were corrected
without disabling warnings or weakening rejection. Actual Windows kernel
observations, native interoperability and complete package evidence remain
separate gates. No game or capture was started.

## Native / managed interoperability seam

The test-only `public_return_interop.rs` reuses the existing synthetic child test
entry, so the eight-function native suite count does not change. A managed test
owns and starts a copy of that exact test executable under an allowed game
basename; it remains a test executable and loads no game. The child reads the
original configuration reference through production `hooks_config::get_or_load`,
which performs the real configuration-pipe read / ACK and retains its actual
server handle. The managed side uses the real LaunchPipe ACK and exact owned
Process.Start handle before its production PublicReturnPipe admits the child.

When a Public descriptor exists, the original configuration pipe now explicitly
opens with `SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION`, including when that
descriptor later fails validation. This prevents an unknown pipe server from
impersonating the client before owner admission. LAN open flags and all frame /
ACK bytes remain unchanged. The return pipe already used this restriction.

The fixture JSON `FF_SYNTHETIC_RETURN_INTEROP_V1` has exactly launcher_path,
launcher_sha256, release_id, compat_id and manifest_sha256. These are nonsecret
fixture identities, separate from the launch descriptor. The helper validates
the actual observed launcher path / SHA / creation identity, then supplies a
synthetic response with account `synthetic-account`, user `synthetic-client`,
source `10.66.0.10`, mod product and the actual native `current_game_instance()`
GUID to production `return_authentication`. Its synthetic signature is all zero
bytes; only context screening and transport are tested, not server authority.
The bearer exists only in child memory and the return pipe.

The test executable intentionally bypasses `prepare` and its loaded-DLL /
whole-package verification; fixture image consistency is **not** a package
admission proof. It then calls the exact production worker, queue and pipe code.
The child accepts only bounded stdin commands AUTH / INVALIDATE / EXIT, or EOF;
stdout emits fixed READY / AUTHENTICATED / INVALIDATED / STOPPED markers. Queue
markers are not remote-delivery acknowledgments. The managed receiver must wait
for its actual context / cancellation. Worker cleanup is bounded and a separate
45-second helper lifetime bound covers a broken test parent. No bearer, raw
frame, descriptor or diagnostic is printed.

This seam has passed i686 Windows **type-checking only** locally. Actual managed
invocation `--native-interop <exact hooks test executable>` and hosted evidence
are pending; the existing separate native and managed results cannot substitute
for that run.

### Shared configuration fixture correction

Pre-execution inspection found a deterministic interoperability input error:
the first probe supplied an empty password, while retained
`hooks-config::validate_user_identity` rejects empty passwords after the ACK.
The helper also incorrectly expected that empty value. Production validation
was not changed. The managed probe now embeds
`tests/PublicNativeReturn/native-interop.toml`, and the native helper checks its
explicit nonempty synthetic password. No credential is reused from an account,
and the configuration is delivered through the existing pipe.

Actual focused Linux regression
`cargo test -p hooks-config public_return_interop_fixture_uses_retained_configuration_contract --lib --locked`
passed **1/1, zero ignored**. It parses those exact shared bytes through the
retained production parser and also confirms that the original empty-password
variant remains rejected. This fixes a known precondition; actual Windows IPC
and cross-language execution are still required and are not inferred from it.

### Actual hosted compile failure and retained replay-resource gate

Windows candidate job `120288376951` at `568c3c5` reached
`ci-native-public-return` and failed while compiling all hooks test modules, with
official compiler code `E0425`. Managed suites, including PublicNativeReturn 25
and FinalLaunchPreparation 9, had already passed; native IPC and cross-language
interop had not run. The unavailable log archive was not treated as evidence of
a particular missing symbol.

A local diagnostic of all actual Rust test modules then reproduced the same
`E0425` failure from this missing input: `merc_mouse_sensitivity_tests.rs:2` requires
`SCBL_R58_C71_FIXTURE/native_fixture.rs` using compile-time `env!` and `include!`.
The missing input also leaves the `native_fixture()` call at line 24 undefined
(`E0425`). This is a retained C71 native-instruction replay dependency, unrelated
to the owned pipe tests. The source file is unchanged from the failed revision:
SHA256 `a0942f34386a90b7dfbfa95d634e98b0211e4248fc4c18d87ca98dce8552e679`.

The `native-replay-fixtures` Cargo feature now explicitly includes that test
module. The original tests, `include!`, assertions and real fixture requirement
are unchanged. Enabling the feature without the input still fails compilation;
there is no stub, placeholder native code or ignored passing test. The following
13 native replay tests are **held, not executed** in ordinary IPC/source suites:

```text
c71_same_input_fixture
c71_preserves_original_base_multiplier
c71_neutral_is_bit_exact
c71_gamepad_unchanged
c71_spy_campaign_and_non_first_person_unchanged
c71_no_bypass_and_positive_state_unchanged
c71_nan_or_inf_inputs_unchanged
c71_invalid_original_bit_exact
c71_owner_mismatch_and_remote_unchanged
c71_unrelated_camera_mode_unchanged
c71_invalid_pointers_fail_closed
c71_invalid_ranges_fail_closed
c71_no_cumulative_or_third_person_gain
```

The external C71 fixture, its native bytes/generator and provenance manifest
must be recovered before those replays can run. The unrelated authored
`tests/CrashDiagnostics/native_fixture.rs` is not a replacement. Missing native
header and retail-image closure for the separately retained VSS replays is
recorded in [vss-replay-closure.md](vss-replay-closure.md); this feature does not
supply those inputs or reopen accepted F3/EMF results.

Actual local diagnostics used root-locked Rust dependency versions and
`patch-free,lan-goal-origin` on the i686 MSVC target. The Linux host lacks the
MSVC C++/audio/resource build prerequisites, so a private diagnostic copy skipped
only those non-Rust build steps; production build scripts stayed untouched.
Before the fix it reproduced the missing environment input and `E0425`; after
the fix all Rust test modules type-checked, exit 0. Adding
`native-replay-fixtures` without the missing input still failed, exit 101, with
the same two errors. The original C71 source SHA was checked against the failed
hosted revision. Logs and the bounded receipt are retained under ignored
`.local/evidence/cloud/native-fixture-compile/`.

This is Rust type-check evidence, not a full Windows build or test execution.
The next hosted run must actually pass the exact IPC 8, source-transport 8 and
interop 3 suites. Those targeted counts never mean that all hooks or native
replay tests passed; this external-input dependency remains unresolved and its
13 native replay tests remain held.


### Actual Windows production-handle result

Run [40373151715](https://github.com/caox233/FifthFreedom/actions/runs/40373151715),
job `121748959278`, at source `6ecfdb474238614a9df5e22abbe91581b99f40e6`
passed PublicNativeReturn **28/28** and FinalLaunchPreparation **9/9**, zero skips.
The [independently verified official receipt](../evidence/hosted-local-pipe-6ecfdb.json)
records the actual NT observations:

- `LEGACY:SUCCESS:BYTE:SERVER`.
- `FACTORY:SUCCESS:LOCAL_BYTE:SERVER`.
- `PRODUCTION:SUCCESS:LOCAL_BYTE:CLIENT`.

The actual client handle connected to production PublicReturnPipe observes the
same kernel pipe creation mode, including FILE_PIPE_REJECT_REMOTE_CLIENTS. The
legacy current-user-only managed constructor is the real negative control.
Current-user ACL, noninheritance, first-instance collision and same-user read/write
also passed. The previously uncertain NT flag visibility is now observed on this
actual runner. IIS state was restored. This closes the reported missing local-only
creation boundary; it does not claim a demonstrated SMB exploit, full Public
admission, game execution or complete-package acceptance.
