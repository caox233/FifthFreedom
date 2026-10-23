# SERVER-15 — real owned overlay and native development composition

Status: ready-for-agent; implementation, local checks and independent review
complete; the actual isolated hosted run remains pending. Ordinary Public admission
remains closed.

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
