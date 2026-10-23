# UI-06 native owned authentication return

Status: ready-for-agent. Production-linked implementation and portable tests are
complete; actual hosted Windows pipe execution, full native build and managed /
Rust interoperation remain separate required evidence. No game was started.

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
