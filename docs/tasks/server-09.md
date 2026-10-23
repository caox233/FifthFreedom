# SERVER09 — exact owned overlay/native process lifetime

Status: ready-for-agent (lifecycle implementation and synthetic tests complete;
production Public admission deliberately closed).

`src/public/fifth_freedom_control/owned_process.py` and
`overlay_supervisor.py` implement the process lifetime needed by the future Linux
Public owner. This slice does not start EasyTier, create a TUN, alter a route,
start a game, deploy a service, or change the native Public gate.

## Executable and child ownership

The owner accepts an absolute, component-by-component no-symlink ELF64 x86-64
image, fixed size and SHA-256. Its directories must be owned by root or the
service uid and not group/world writable (the root-owned sticky temporary
directory exception is for owned test fixtures). The image must be a regular,
single-link executable without group/world writes, setuid/setgid or Linux file
capabilities. Only `ENODATA` establishes absence of `security.capability`; an
inability to inspect capabilities fails closed. Capability-based EasyTier
installation is therefore not compatible with this supervisor. The future
owned namespace launcher must establish needed rights without a
credential-changing child exec.

The exact open image descriptor remains pinned; the isolated Python bootstrap
hashes it again, checks stable metadata before and after reading, and checks
metadata once more immediately before `os.execve(fd, ...)`. The parent confirms
the child's `/proc/.../exe` device/inode/size after exec. This assumes trusted,
immutable installation during execution: replacement uses a new inode and a
coordinated restart, never an in-place write. It does not defend against a
hostile same-uid writer racing the final hash/exec interval. A sealed memfd would
change native `current_exe()` and break the existing package-root verifier, so
that behavior was not silently substituted. A future stronger installation
boundary requires an explicitly reviewed read-only mount/package-root contract.
Dynamic loader/library integrity remains the trusted host runtime's boundary.

The bootstrap is `Python -I -S`, with no `preexec_fn`, shell, process-group
operation, inherited environment or child-output forwarding. Before release it
sets `PR_SET_NO_NEW_PRIVS` and `PR_SET_PDEATHSIG(SIGKILL)`, checks the expected
parent, then waits on a bounded internal pipe. The parent obtains a pidfd before
release and before polling/waiting/reaping. No runtime can start when pidfd
acquisition fails; pipe EOF makes the unreleased bootstrap exit and be reaped.
Only the image and two internal handshake descriptors cross that bootstrap;
keys, arbitrary descriptors and loader injection variables do not.

`OwnedChild` keeps the exact pidfd and `/proc` directory fd. Signals only target
that pidfd. Shutdown waits for the direct child and closes those handles; no PID
search, executable-prefix kill, process-group kill or adoption is implemented.
PDEATHSIG remains effective after the verified non-credential-changing exec.
Abrupt owner death kills each owned child directly; it cannot provide ordered
graceful shutdown. Normal shutdown does provide that order.

## Readiness and ordered revocation

`OwnedSupervisor` starts the overlay, observes ownership, then starts the native
child. `READY` is only a local lifecycle observation. It is never an encryption,
peer admission or release authorization claim. The monitor must remain valid;
an overlay/native exit, changed namespace/index/address, missing observation,
timeout or observer error permanently revokes readiness. Error results use
fixed reason codes and do not retain observer exceptions, paths or child output.

After revocation the owner sends native SIGTERM, escalates to SIGKILL if needed,
and awaits native death before doing the same for the overlay. This closes both
listeners and accepted connections. Cleanup never deletes an interface or route
by name. Default budgets are 10 seconds for startup, 0.5 seconds per observation,
0.1 seconds between observations, and 1 + 2 seconds per child's TERM/KILL waits.
Startup cancellation and late observations cannot publish readiness. A kernel
task still stuck after SIGKILL is reported as a failed stop; exact handles remain
held, overlay cleanup is withheld, and successful termination is not claimed.
Adapters are local, trusted and cancellation-cooperative; this is not a sandbox
for untrusted Python callbacks. Detached descendant ownership is not provided:
the admitted runtime/native profile must run in the foreground without daemon
forking. Future runtime evidence must verify that profile constraint.

`KernelTunMonitor` supplies a real inspection seam: expected interface absent
before launch, current/child namespace device+inode equality, assigned exact
IPv4 address, link-up state, stable ifindex, and an exact child descriptor whose
kernel fdinfo names that interface and whose character device is Linux TUN
10:200. Missing/changed observations fail closed. A same-name interface appearing
alone is insufficient. This monitor does not prove that the namespace is owned
or egress-isolated, that TUN traffic is encrypted, or that peers were admitted.
It does not create a namespace/TUN. **The real TUN/fdinfo path is untested here**:
this cloud workspace has no TUN, NET_ADMIN or usable unshare network namespace.

## Candidate identity and the closed production provider

`RuntimeCandidateIdentity` distinguishes a GNU source-built `easytier-core` from
the unchanged official release pins. It carries upstream commit, source-tree
manifest SHA, ordered patch-manifest SHA (production/test roles stay explicit),
Cargo.lock SHA, build-profile SHA, executable SHA+size and target
`x86_64-unknown-linux-gnu`. Format/integrity validation is not an allowlist or a
reviewed secure-session policy. The CLI companion has a separate candidate
identity and is not accepted as the core role.

`PublicAdmissionProvider` is a future integration contract.
`create_public_supervisor()` currently uses only `ClosedPublicAdmission`, which
always raises `PUBLIC_PROFILE_NOT_READY`. No environment variable, JSON boolean,
monitor callback, candidate hash or command-line switch opens it. Before a real
provider can exist, it must bind:

- The exact patched core executable and complete reviewed source/build/patch
  identity, plus a fixed secure profile and versioned real readiness adapter.
- Verified private configuration input digests and exact peer-only/IPv4/no-egress
  constraints. Current upstream default STUN behavior makes an owned isolated
  namespace and explicit egress rules necessary; this slice does not supply them.
- SERVER05 same-run native package/key/data/config admission, SERVER06 interface
  confinement for every socket, and SERVER08 source admission.
- Actual owned namespace/interface/TUN lifetime, secure-session negative tests,
  and the reviewed bounded native activation/readiness lease. The internal
  Python process-release pipe is not that activation lease.

## Actual verification

`python -m unittest discover -s tests/server -p test_overlay_supervisor.py -v`
ran **16 tests, 16 passed, 0 skipped** on Linux (3.559 seconds for the final
focused run). The tests execute owned synthetic Python ELF children and loopback
TCP only. They verify:

- Fixed executable digest/type/mode/symlink/FIFO gates; explicit capability inspection
  rejection using injected xattr outcomes; changed image metadata rejection.
- Candidate identity cannot open Public admission.
- Overlay death revokes readiness, kills native, causes EOF on a previously
  established TCP connection, rejects a new connection, and leaves an unrelated
  owned synthetic child alive.
- Injected interface index/address/namespace/missing observation revocation;
  timeout and native exit; error sanitization; readiness revoked before native
  stop, native awaited before overlay, and real SIGKILL escalation after ignored
  SIGTERM.
- Concurrent starts reserve one runner/child pair; a failed bounded native stop
  retains exact handles and a subsequent serialized retry closes native before
  overlay without restoring readiness.
- Late monitor/service results cannot publish readiness after an owned overlay
  has died; exact child liveness is checked again after awaited observations.
- Startup timeout/cancellation, withheld-release pidfd failure, proc-open/signal
  exit-race pidfd cleanup, bootstrap/exec
  failure cleanup, clean environment, and actual post-exec PDEATHSIG/NO_NEW_PRIVS.
- Killing a synthetic owner process with SIGKILL makes its exact synthetic child
  die and closes its already established TCP connection. pidfds establish death;
  reaping an orphan is the host's init responsibility.
- An absent interface is not adopted. No successful kernel TUN ownership,
  encrypted overlay, game connectivity, native Public launch or live deployment
  is represented by these tests.

The subsequent hosted Linux source run `39905205058` and candidate control job
reported 14 lifecycle setup errors. Those fixtures initially tried to admit the
hosted system Python installation directly; a shared tool-cache installation is
not an owned private runtime fixture. The tests now copy the actual interpreter
ELF into their own private temporary directory, set mode 0700, and verify its
exact hash and size through the unchanged production reader. The parent-death
fixture uses that same owned image. A private `pyvenv.cfg` points the relocated
CPython to the existing host standard library; no Python loader environment is
inherited. The first local copy-only attempt exposed that required runtime
dependency and failed before this configuration was added. The corrected focused
suite passed 16/16, zero skipped, locally in 4.005 seconds. No production
permission, symlink, hardlink or capability check is relaxed. The original hosted
failure is retained; its exact setup rejection still needs the full hosted log,
and this fixture correction still requires a new hosted run.

Next: review this implementation, bind the patched runtime/profile evidence and
native activation contract, then run owned isolated-namespace integration only
where authorized NET_ADMIN/TUN capabilities are actually available. Preserve all
private asset gates and existing LAN behavior.
