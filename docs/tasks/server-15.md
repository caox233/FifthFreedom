# SERVER-15 — real owned overlay and native development composition

Latest tested `329f18a` composition job `121924250674` now passes both controls
with the complete same-run candidate. The [verified final receipt](../evidence/hosted-composition-329f18a.json)
records held OPEN before each trigger, five native listeners, lease-revoke
exit 92/port release/EOF-reset while the overlay stays alive, and peer-loss exact
cleanup/permanent revoke/zero listeners without a remote EOF claim. All four case
namespaces and the private stage are removed, with the source package unchanged.
The six-patch guard3 build binding is verified; runtime hashes are runner-reported,
not locally rehashed binaries. Ordinary Public admission remains closed.

Earlier checkpoints follow with their original scope.

Status: ready-for-agent for remaining Public integration. Earlier `9139c9e`
composition job `121846866679` fails before recorded controls because its
downloaded candidate lacks five declared hidden source files. The exact
[artifact/input reproduction](../evidence/hosted-final-9139c9e.json) and
[publication repair task](ci-artifact-source-closure.md) distinguish this from
runtime query failure. No new copied-package, runtime or cleanup success is
claimed; the complete same-run artifact must be revalidated and both controls run.

Retained history: both actual isolated
development controls passed at `c42e0eb`, run `40371709938`, with independently
verified official receipts below. Newer `7c614c1` run `40374533090` fails again
before READY with `QUERY_IMAGE_CHANGED` and unconfirmed exact child cleanup;
the earlier success does not establish stability. Earlier failed runs remain retained. Ordinary
Public admission remains closed; guard3 success does not verify the distinct
peer-map71 runtime or Windows owned-core/NIC chain.

The [new verified receipt](../evidence/hosted-failures-7c614c1.json) records
`CHILD_START_CLEANUP_UNCONFIRMED` and `STACK_WORKER_CLEANUP_FAILED`. Two namespaces
and the private staging tree were removed, but that does not prove the exact
child was drained. The peer-loss case did not run. Query/worker code was identical
to successful c42. The subsequent `aac024382` fix reproduces an actual short-lived
query exit transition, retains live mismatch rejection and exact ownership, and
passes 26 focused tests plus500 real query invocations. See
[the regression receipt](../evidence/query-exit-transition-regression.json).
A fresh hosted composition must still prove both controls and cleanup; local
query success cannot replace the missing historical drain evidence.

The9a candidate `40387245159` did not execute composition: its Linux LAN test
failed the new test's one-shot secure-close assumption, so composition job
`121811023064` was skipped. The [verified LAN failure](../evidence/hosted-lan-failure-9aede37.json)
and [test-only callback correction](../evidence/quick-match-close-callback-regression.json)
remain distinct from the runtime query fix. Local five-package519 success after
that correction is not an actual composition result.

## Problem and implementation

SERVER-13's guard3 EasyTier probe and SERVER-11's native lease lifecycle probe
exercise separate systems. SERVER-14 supplied the reusable owned observer.
This task connects `OwnedOverlayMonitor`, `OwnedSupervisor` and
`NativeOwnerLease` to the actual core/CLI and actual native executable in one
owned namespace. The first combined profile is explicitly TCP development;
it is not QUIC/Fast/WSS acceptance and does not replace the independent
correct-pin / wrong-pin / restore transport matrix.

The new `scripts/ci_owned_stack.py` reuses the reviewed namespace, complete nft
readback, build receipt, private image copy and native candidate validators.
It requires the native Windows/Linux component candidate and guard3 build to
match the current clean checkout, workflow run and run attempt. Both EasyTier
executables must match the same guard3 build receipt. The CI job must build
that pair itself: the receipt binds the two images and run coordinates, but
contains no independent job identity and does not by itself prove which job
produced it. No EasyTier binary is uploaded or downloaded between jobs.

The driver makes a fresh root-owned 0700 `/tmp` tree, copies the original
component candidate without changing it, and executes the copied real native
package verifier. Its result must equal the same-run candidate receipt. New
private configs, random 32-byte issuer keys, PSKs, X25519 private keys and native
state directories live outside that package. Native config binds the actual
responder recipe digest and actual guard3 core digest; these are not synthetic
runtime values in the hosted composition.

Each case creates two fresh owned network namespaces and a direct veth pair,
with no default routes and exact default-drop nft rules. Their retained handles
are checked before entry. Workers cannot enter the namespace or start children
until the parent owns a worker pidfd and releases the START barrier. The
single-threaded worker enters that namespace before creating its asyncio loop
or children. Core, CLI and native use their existing verified ELF handles and
exact process ownership. `OwnedOverlayMonitor` observes the real owned TUN,
loopback RPC listener inode and bounded CLI peer result; its recent local
observation drives the development parent lease, with no independent renewal
timer. This is not authenticated-traffic freshness: peer auth level and Noise
keys report handshake history, and plaintext Ping/Pong may keep local connection
state alive. The current monitor has no current-session authenticated-receive or
challenge observation. Native READY and all
five kernel listener inodes must belong to the exact native child. A remote
TCP connection is then established across the overlay and sent an HTTP/2
preface; no account RPC or game runs, and no HTTP/2 handshake is claimed.

## Two finite controls

1. **Lease revoke with the overlay alive.** A development-only proxy requests a
   pause after a successful real monitor observation and before returning that
   observation to the supervisor. Only then does it acknowledge the barrier.
   One 1.8-second monotonic budget includes the observation and the pause. The
   proxy never returns old proof or renews the lease while paused. The owner
   synchronously revokes; the real native watchdog must exit 92, all five ports
   must actually rebind, and the exact core must still own the same live TUN.
   The remote held TCP stream must return actual EOF/reset, not a timeout. The
   server verifies its owned TUN/core again before releasing the proxy with a
   fixed failure, allowing the original supervisor to clean up. If the finite
   observation deadline expires, the original fail-closed path remains active
   and the test fails.
2. **Peer loss in a new epoch.** The second case has fresh namespace, profile,
   keys, core/native ownership and private data. After genuine READY/five
   listeners/held TCP, the driver stops and reaps the exact peer worker/core.
   The unwrapped owned monitor must permanently revoke before native
   cleanup awaits. The actual supervisor must reap native and core, and no
   native listening socket may remain. A remote connection timeout after its
   network disappears is never counted as EOF; this case makes no such claim.

The namespace owner independently checks the full nft rules and absence of
routes before and after each control. Any missing privilege, changed rule,
wrong source/run/image, failed cleanup or timeout fails the test. No successful
skip exists. Cleanup uses owned workers/children and retained namespace
identities; private state is deleted only within the fresh identity-checked
tree. A failed or forcibly killed worker cannot produce a successful child
cleanup claim.

These controls test explicit owner revocation and actual removal of the owned
peer. They do not test an adversary preserving handshake state with plaintext
keepalives, and a successful development receipt would not establish continuous
authenticated peer liveness. The ordinary production factory stays closed;
the new authenticated-receive work must be reviewed and integrated separately
before any production freshness claim. Existing A/B result fields retain their
original development meaning.

## Hosted invocation

The ordinary runner must create the public output parent before sudo. Its job
builds guard3 core/CLI from the exact checkout and obtains the same-run native
component candidate through the existing artifact dependency. The root command
must receive the reviewed hosted environment markers used by `preconditions`
(`GITHUB_ACTIONS`, `RUNNER_ENVIRONMENT`, `GITHUB_SHA`); no secrets are environment
inputs. Invoke the setup Python by its absolute path, because cryptography is
needed only for private X25519 generation:

```text
sudo <absolute-setup-python> -B -I scripts/ci_owned_stack.py \
  --package <same-run-component-candidate> \
  --candidate-receipt <candidate>/CANDIDATE-RECEIPT.json \
  --build-receipt <this-job-guard3-build-receipt.json> \
  --core <this-job-easytier-core> --cli <this-job-easytier-cli> \
  --commit <checkout-sha> --run-id <workflow-run> --run-attempt <attempt> \
  --output <runner-created-public-output-parent>/owned-stack.json
```

Use the job's setup interpreter with its installed cryptography dependency,
`-I -B` as in the existing EasyTier probe. Do not add arbitrary Python path or
environment overrides. Runtime workers also use `-I -B` and fixed clean
environment.

The only public output is schema
`ff-owned-overlay-native-stack-development-v1`, status
`owned_stack_development_verified` on success, public source/build/profile
identities, fixed A/B observations and cleanup counts. Exit code is 0 only for
both cases and all cleanup; otherwise 1 with fixed diagnostics. The report is
exclusive/no-follow 0644 for the ordinary uploader; private inputs remain
0600/0700. Raw runtime stdout, stderr, keys, configs, accounts, paths and packet
captures are not receipt contents. The copied verifier is bounded to ten
seconds and 16 KiB; each CLI observation retains SERVER-14's bounded reader.

## Evidence and limits

Local `python -m unittest discover -s tests/ci -p test_owned_stack.py -v`:
17 passed, zero skipped on this Linux environment; independent review reran
the same 17 with zero skips and found no remaining blocking issues. Reused EasyTier probe checks
20/20 and retained native lifecycle checks 12/12 also passed. Coverage includes actual
loopback TCP EOF and non-closure timeout, command-pipe cancellation, exact
run-coordinate rejection, private manifest modes/bindings, no-start containment
failure, pause ordering/deadlines, and real owned interpreter children plus the
production supervisor/SCM parent lease. That last case uses an explicitly
synthetic native wire peer and injected TUN observations; it demonstrates the
control ordering without running the real native Public server or EasyTier.
Two cleanup fault cases ensure a failed server stop still attempts independent
query/namespace/image cleanup and an image-close failure cannot skip other
descriptors. They do not bypass the supervisor's native-before-overlay order;
any failure forbids a successful child-reaped acknowledgement.

The current cloud environment lacks the required root-owned network namespace
capabilities. The actual local CLI exited 1 with `GITHUB_HOSTED_REQUIRED`,
`ROOT_NAMESPACE_PRIVILEGES_REQUIRED`, `NET_ADMIN_AND_SYS_ADMIN_REQUIRED`,
`EXISTING_TUN_REQUIRED`, and `NFT_REQUIRED`; it started zero cases or runtimes.
This is a negative prerequisite check, not a hosted runtime result. Actual guard3/core/CLI/native composition and the A/B positive
controls require the new hosted job after independent review. No production
provider, automatic Release, game, private SAV/META data or deployment is
introduced. The complete client package privacy gate remains unchanged.

## Executable-entry audit and bounded failure diagnostics

The SERVER-15 command already is the minimal executable development assembly.
It loads genuine component and guard3 identities, creates the isolated owned
namespace/profile, then invokes the existing owned monitor, supervisor
and parent lease. A second launcher or synthetic admission issuer would add no
missing capability. `create_public_supervisor` correctly remains closed: the
only implemented runtime recipe has purpose `owned-isolated-development`, the
actual hosted encrypted-runtime/native composition is still unverified, and no
reviewed production admission provider supplies the required ownership and
current runtime proof. Candidate integrity and a namespace name are not such
proof.

An execution audit found and reproduced a concrete diagnostic gap. The stack
worker discarded all exception details to its null stderr. Two actual worker
subprocesses (bad arguments and an absent private manifest) therefore yielded
only `WORKER_REPLY_EOF` in the parent. They failed before namespace entry or any
runtime startup. The retained pre-fix test log records 19 tests with two errors.

The stack now reuses SERVER-13's reviewed fixed failure schema, phase whitelist,
code whitelist and exact worker reaping. It forwards no raw exception, private
path or config. Unknown stack-specific errors remain the fixed unclassified
code; accepting arbitrary upper-case text is deliberately not part of this
change. Failed startup without a returned child handle does not independently
claim that child was reaped. A later cleanup failure cannot replace the original
phase/code or produce a successful acknowledgement. The enclosing case remains
failed and can retain this one bounded record for diagnosis.

Post-fix checks: 20/20 passed locally, including both real worker failures with
exact Popen wait/pidfd close and a failed spawn plus independently failing cleanup.
The failure path retained the original fixed code and suppressed a private
sentinel. This establishes the diagnostic repair, not the root cause of the
separate earlier full-overlay hosted EOF and not successful encrypted runtime
startup. That root cause awaits the new hosted phase/code evidence.

## Sample the held connection before either control

The combination now reuses SERVER-11's bounded observation of the already held
TCP socket before proceeding with its test control. Case A first receives the
real monitor's pause acknowledgement, then the client's ordered OPEN
acknowledgement, and only then revokes the native lease. This work consumes the
existing single pause budget; it does not create a new lease or extend the
1.8-second fault barrier. Case B checks OPEN immediately before stopping its
exact peer. The client checks its exact core image before and after the socket
sample, and refuses a second OPEN or a CLOSED command without OPEN. No fresh
connection is substituted for the held one.

Queued FIN/reset, any other read error, more than16KiB of protocol data, or a
100ms observation budget expiry rejects before the control. Early-close errors
use the fixed `STACK_HELD_CONNECTION_NOT_OPEN` reason without raw socket output.
Two additional worker tests use actual TCP and the production command handler,
with explicitly injected core and monitor objects: early FIN with or without
protocol bytes cannot ACK open, cleanup still runs, and a live socket can ACK
open before a later real FIN ACKs closed. The local stack suite passes22/22.

This closes a test evidence gap demonstrated by a separate real synthetic peer
that closed before any revocation. It does not identify a hosted failure's
cause, establish authenticated traffic freshness, or prove that a close between
the two observation points was caused exclusively by revocation. The existing
native process exit, port-release and overlay-survival evidence remains required.

## Preserve fixed failure reasons across the supervisor boundary

The official528 run39932526946/job120355233682 completed case A's native READY,
five listeners, exit92, released ports and held-TCP close checks. Case B failed
at `observe:WORKER_UNCLASSIFIED_ERROR` before combined readiness was confirmed.
That does not prove the background native process never reached READY. Both
cases removed their two namespaces; source-package and private-stage cleanup
checks passed. This run predates the LISTEN correction and OPEN barrier. Its
unknown reason cannot be reconstructed from a later source fix, and the cases
use separate fresh namespaces, so case A's TIME_WAIT rows do not carry into B.

Two diagnostic losses were confirmed in source and controlled execution. The
shared worker did not recognize the native checker's separate exception type
or many fixed observer reasons. Also, the production supervisor intentionally
keeps only its fixed failure reason, discarding an exception context before
`start()` reports failure. Adding outer reason names alone cannot recover that
discarded detail.

The stack now adapts only exact known exception classes with a single explicit
allowlisted string argument. The native checker's type accepts only its three
listener/image/table reasons. At most four context nodes are examined, only
through named lifecycle wrappers, with cycle protection. Unknown exception
strings, subclasses, context chains and arbitrary uppercase text are not
published. A lightweight stack-local wrapper around the actual monitor and
readiness provider records only a fixed code and existing phase when the current
call fails, then rethrows the original exception. It stores no exception or
runtime output. Successful calls and new attempts clear the corresponding role's
record. Only a matching final supervisor wrapper may use that same role's
capture; other failures retain their own code. Normal stop/control completion
does not emit a new failure record.

A real verified Python child with no listening sockets triggers the actual
native ownership checker, then the actual supervisor's monitor-failure and
reason-only path. Without the adapter, the retained regression reports only
`OVERLAY_MONITOR_FAILED`; with it, the frame preserves
`NATIVE_LISTENER_OWNERSHIP_INVALID`, and the exact child is reaped. The positive
owned-child/SCM lease composition also runs through the new wrappers. Four
additional portable checks cover unknown text, foreign `str()` hooks, exact
types, bounded/cyclic contexts, phase/cleanup retention, role mismatch, and
clearing stale captures. The complete stack suite passes27/27 locally.

The wire schema and fail-closed runtime behavior are unchanged. Missing captures,
unknown causes, contexts beyond the four-node limit, and failures outside these
wrappers can still be reported only at wrapper or unclassified level. These
controls start no EasyTier/native artifact, namespace, TUN or game. Only a new
hosted run can establish the actual reason for a future failure.


## Verified follow-up at c42e0eb

Run `40371709938`, composition job `121755096661`, passed both actual controls at
source `c42e0ebbc8084e943076aab7effc6abd4e8662c7`, which includes the query-exit
fix. The [independently verified receipt](../evidence/hosted-owned-stack-c42e0eb.json)
binds the official artifact digest and merge/source tree. Each successful branch
requires the exact held socket's OPEN acknowledgement and live worker pidfd before
control; the receipt has no separate OPEN field, so this observation combines the
mandatory exact harness path with its successful result. Lease revoke observed
native exit92, five rebound ports and EOF/reset while overlay remained alive;
peer loss observed permanent revocation, exact cleanup and zero native listeners.
All four namespaces and private stage were removed; source package was unchanged.

No QUERY_IMAGE_CHANGED occurred in this run. The older fe6 failure remains real;
this does not establish its unique cause or general stability. The runtime uses
guard3, not the newly built peer-map71 source. Ordinary Public admission remains
closed. Overall candidate result was eight jobs successful, two strict input
failures (eight held SAV/META and C71's thirteen unexecuted replays), and complete
Windows packaging skipped. No component or finite runtime proof is a full package.
