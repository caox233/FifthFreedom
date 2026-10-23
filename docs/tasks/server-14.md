# SERVER-14 — owned overlay observations for native lease composition

Status: ready-for-agent. Implementation, local process/IPC tests and independent
review complete; same-build hosted composition is still required. Ordinary
Public startup and `ClosedPublicAdmission` remain closed. No production admission
provider is added by this task.

`owned_overlay.py` supplies a concrete `OwnedOverlayMonitor` implementing the
existing `OwnedSupervisor` monitor interface. It consumes the real finite
`easytier_profile` recipe and peer parser. It also supplies `OwnedCliQuery`, the
bounded query implementation that can replace the development worker's manual
CLI wrapper. There is no background heartbeat and no encrypted/config-IP boolean.

## Exact observation boundary

The monitor borrows the owner's verified core and CLI ELF handles and duplicates
an already-open network namespace handle. Core bytes/size must match the parsed
recipe. The caller must independently pin **both core and CLI to the same reviewed
build receipt**, emit the matching private TOML and enforce the owned namespace's
egress/nft rules. A caller-supplied CLI hash proves integrity of that chosen file;
it cannot establish that its JSON describes guard3 or any encrypted runtime.
Likewise a namespace inode match does not establish ownership or containment of
the whole namespace. This monitor neither creates nor changes namespaces,
firewalls, routes or interfaces.

Before core startup, `prepare()` uses the retained `KernelTunMonitor` to refuse an
existing interface. Every observation then checks the exact owned child object,
pidfd liveness, `/proc` executable identity, current/child namespace identity and
the core's TUN fd/index/address. The fixed numeric loopback RPC must be the sole
LISTEN entry for that port across TCP and TCP6; its socket inode must belong to
the pinned core's fd table. Wildcard listeners, IPv6 duplicates and another
same-uid process's listener fail. No remote endpoint, name resolution or forwarded
header supplies identity.

Only the recipe's fixed verbose peer-list CLI arguments are used by the monitor.
The CLI is executed from the verified file descriptor using the existing hash,
parent-death/no-new-privileges, ready/release and pidfd bootstrap. The new explicit
`OwnedQueryChild` type permits a short-lived successful CLI to finish before a
post-exec `/proc/exe` observation: the parent already received the bootstrap's
verified-hash acknowledgement, released execution and observed the CLOEXEC status
pipe close. All bootstrap failure paths exit71..79, so an exact-child exit0 after
that boundary is the verified target's completion. Missing `/proc/exe` on its own
is never success. Ordinary `OwnedChild.spawn` still requires a live matching image
and discards stdout/stderr; a query cannot inherit a native lease socket.

CLI stdout remains bounded private memory (at most65,536 bytes). Its execution
and reading share one monotonic deadline, default1.5s and maximum2s. No reader
thread survives a timeout. Stderr is discarded, and raw output never enters
exceptions or receipts. On timeout, overflow, nonzero exit or cancellation the
native revoker runs synchronously before awaited exact-child cleanup. Cleanup
has its own bounded terminate/kill budgets. A failed stop retains the exact
handle and blocks replacement queries; `close()` can retry it. A second
cancellation during bootstrap cleanup cannot abandon the pinned child.
The query retains that exact handle before the first bootstrap await, so even
a failed bootstrap cleanup cannot discard ownership before `spawn` returns.
This transfer grants cleanup responsibility, not readiness or admission.

After the awaited query, the monitor checks core, namespace, TUN and the same RPC
inode again before parsing and publishing an observation. The parser verifies
the actual pinned peer/route shape, keys, authentication level, role, endpoints
and direct route. Before first readiness, an absent/unapproved peer is pending
under the supervisor's startup deadline. After acceptance, any lost or changed
peer/connection, interface, RPC or process permanently revokes this monitor.
Changing back to good JSON cannot revive it. RPC's route-joined peer view is not
a complete connection inventory or independent cryptographic proof.

The peer authentication level and Noise keys describe the completed handshake;
`is_closed=false` describes current local connection state. Neither proves that
authenticated peer data arrived recently. In the pinned runtime, Ping/Pong can
bypass encryption, so their continued receipt cannot fill that gap. The monitor
currently consumes no current-session authenticated-receive counter, timestamp
or challenge response. Repeating unchanged peer JSON can refresh the local
observation time indefinitely while adding no authenticated-traffic evidence.
This limits the monitor to the existing contained development recipe; it is
insufficient for production admission or production lease renewal.

## Direct owner composition

The existing supervisor runs this monitor before every native service observation.
`lease_binding()` supplies the native parent's expected binding from a successful
observation no older than0.5s, rechecking the core/namespace/RPC without yielding.
It binds the parsed profile digest, core executable digest and observed interface
to the separately verified native configuration, service package and admitted
Windows client identity. It derives none of those expectations from a native
HELLO. A stalled owner cannot use the old observation to renew indefinitely.
The 0.5s check measures the age of the local CLI/kernel observation only. It does
not bound the age of a remote authenticated event. A future production provider
must validate a reviewed current-session cryptographic freshness contract before
using this composition; a passive receive observation alone also does not create
an active challenge or idle-peer liveness policy. `ClosedPublicAdmission` stays
closed while that capability is absent.

The composition below assumes the independent same-build, private-config,
package and current containment checks have already succeeded. It is a concrete
integration interface, **not** a way to open the closed production factory:

```python
monitor = None
lease = NativeOwnerLease(
    lambda: monitor.lease_binding(
        config_sha256=native_config_digest,
        service_manifest_sha256=verified_service_manifest_digest,
        admitted_client_storage_key=verified_client_storage_key),
    lease_ms=5000, renew_seconds=1, hello_timeout=60)
monitor = OwnedOverlayMonitor(recipe_bytes, verified_core, verified_cli,
    owned_namespace_fd, revoke=lease.revoke)
supervisor = OwnedSupervisor(
    monitor.core_spec(private_overlay_config_path),
    ChildSpec(verified_native, lease.arguments(private_native_config_path),
              lease.child_socket),
    monitor, lease,
    LifecycleLimits(startup=60, observation=2, interval=.1))
try:
    await supervisor.start()
    await supervisor.wait()
finally:
    try:
        await supervisor.stop()
    finally:
        await monitor.close()
```

The owner retains the executable handles and all namespace/containment resources
until exact native/overlay cleanup is confirmed. `monitor.close()` revokes and
reaps only its query, and closes its duplicated namespace fd; the supervisor owns
native/core cleanup. No PID search, process-group signal or global route cleanup
is added. The recipe's current purpose remains `owned-isolated-development`.

## Local evidence and next executable action

The new suite exercises actual owned interpreter ELFs, pidfds, pipe output,
loopback listener inode ownership and Unix native-owner lease credentials.
It includes a concrete `OwnedSupervisor` + `OwnedOverlayMonitor` +
`NativeOwnerLease` composition reaching READY, then losing the peer observation,
revoking the endpoint and reaping both exact children. Only TUN observations and
the peer JSON service/native wire peer are explicitly synthetic. No EasyTier,
TUN, namespace, game or actual native Public main is started by these tests.

Focused tests cover fast exit, stdout suppression/bounds, timeout, early/late
cancellation, retained-handle cleanup retry, same-uid RPC impostor, wildcard/IPv6
table negatives, post-await core death, changed namespace/TUN/RPC, stale bindings
and irreversible peer loss. Final local results are18/18 new tests,16/16 retained
supervisor tests and10/10 parent-lease tests, all with zero Linux skips. Windows
has one portable budget test and explicitly skips the17 Linux process tests.
The initial cancellation test's expected single stop invocation became stale
after pre-await ownership transfer introduced safe idempotent cleanup at both
layers. That failed log is retained; the corrected assertion verifies every
cleanup targets the same reaped child. Results and source hashes remain in
`.local/evidence/cloud/server14/`.

A later scope regression repeats the same synthetic historical peer response
through actual owned core/CLI interpreter children. It confirms that local query
time advances and the development binding remains usable, while the production
factory still rejects before spawning a child. This deliberately demonstrates
the missing authenticated-traffic guarantee; it does not simulate encrypted
traffic or change the original 18-test evidence above.
The resulting owned-overlay suite passes 19/19 on Linux with zero skips.

Next: independently review this module and feed the already-reviewed hosted
worker's genuine same-build core/CLI, owned namespace/TUN and peer observations
through it. Then compose with the real component-only native package and lease
entrypoint in that contained development namespace. The existing native-only
and EasyTier-only hosted results cannot be relabelled as that combined result.
Keep the original wrong-pin/restore probe matrix unchanged: it intentionally
retains the responder through peer loss, whereas a native-serving monitor must
permanently revoke on that loss. A separate bounded combined worker must own the
core from initial spawn and bind the native configuration to the actual recipe
and core digest. SERVER-15 separates actual five-listener readiness and TCP
closure after lease revocation from peer-loss process/listener cleanup; remote
timeouts after peer loss are not EOF evidence. Containment checks apply before
and after both controls. An already running core cannot be adopted into the
supervisor.

## Initial IPv4 assignment window

The exact guard3 `VirtualNic.create_tun` creates an UP interface before
`NicCtx.run` awaits IPv4 assignment. That later path performs `link_up`, removes
old IPv4 addresses and adds the configured address. A first monitor query can
therefore see an interface before its IPv4 address exists.

`KernelTunMonitor` now returns pending only when no successful observation has
committed an interface index, the actual link flags contain UP, and the address
ioctl returns exactly `EADDRNOTAVAIL`. It returns no ownership observation and
commits no index, readiness or native lease. The owner's existing startup
deadline still bounds this wait. Any other errno, a down link, a configured wrong
address, missing TUN ownership, or the same missing-address error after successful
observation remains a failure. No retry after admitted address loss is added.

A read-only Linux control queries an absent IPv4 label on loopback: flags
succeed and `SIOCGIFADDR` returns `EADDRNOTAVAIL`. The regression passes this real
kernel result through a monitor with explicitly synthetic discovery/lookup
targets and a real owned child; it creates no interface, address or namespace.
The retained pre-fix log fails at `OVERLAY_ADDRESS_QUERY_UNAVAILABLE`. The fix
passes that pending case, rejects the same error after an index was accepted,
and rejects permission/I/O/no-device errors, wrong addresses and a process with
no TUN. The full lifecycle suite passes 20/20 locally with zero skips.

This fixes the independently demonstrated initial-observation defect. It does
not establish the exact errno or worker role in hosted run 39933668757. That
run's receipt already records `correct-pin-before` TCP, fixed UDP and ephemeral
UDP echoes; a later observation failed in the address-query stage, and both
namespaces plus private state were cleaned. Its successful phase and later
failure remain intact. A new hosted matrix must establish the complete result.

## Kernel query failure details

The later independent run [39944703157](https://github.com/caox233/FifthFreedom/actions/runs/39944703157),
job 120384530669, already included the initial-address correction. Its actual
checkout was `b46c8fe56e1b160cbe7d1098843c7ac66eeb9249`. QUIC and WSS each completed
all three phases; TCP completed correct-pin-before and wrong-pin, then failed
at `observe / OVERLAY_ADDRESS_QUERY_UNAVAILABLE` before the restored phase was
confirmed. The worker reported cleanup complete, all six namespaces were removed
and private staging was removed. The receipt does not identify the worker role,
errno or whether that monitor had previously succeeded. The initiator is
recreated during restoration while the responder persists; neither can be
selected as the cause from that record. Earlier 05c success remains evidence
for its own run, not a repeatability guarantee.

The monitor now attaches immutable, bounded diagnostic metadata to the original
`LifecycleError`, retaining its exact type and fixed code. An exact-type extractor
returns only the syscall stage, one of twelve named errno classes or `OTHER`, a
boolean stating whether an index was committed before this call, and the UP flag
from this call's successful flags query (otherwise null). There are no addresses,
paths, numeric errno, exception text or raw kernel output. The worker can combine
this with its validated recipe role in an optional strictly checked failure
object. Historical frames remain valid. These fields are not readiness evidence.

The initial UP/`EADDRNOTAVAIL` pending condition and every subsequent rejection
are unchanged. Real read-only ioctl controls retain the old missing-diagnostic
failure, then verify the new address classification and a subsequent earlier
flags failure without stale metadata. Additional tests cover all nine stages,
finite/unknown errno classes, strict field/type validation and the original
pending boundary. The local suites pass 23 lifecycle, 19 owned-overlay and 27
combined-stack tests, with zero skips. They do not reproduce the unspecified
4b49 errno or identify its cause. The next actual hosted failure must supply the
new details before any broader runtime change is justified. Evidence remains in
`.local/evidence/cloud/kernel-query-details/`.

## Short-lived query completion and image identity

The first combined run containing OPEN, [39943233020](https://github.com/caox233/FifthFreedom/actions/runs/39943233020),
job 120389777286, completed case A: actual five-listener READY, the held TCP
connection, lease revocation, exit 92, port release and EOF/reset while the
overlay remained alive. Case B then failed at `observe / QUERY_IMAGE_CHANGED`
before combined readiness was confirmed. Its worker reported reaped children;
both namespace pairs and private staging were removed, with unchanged source
inputs. That code does not distinguish a changed executable from an exit between
the old guard's separate liveness and `/proc/exe` observations. It does not
identify the cause of the earlier 528 unclassified failure either.

A deterministic control now releases an actual owned interpreter child to exit
normally immediately after the kernel returns the expected executable identity.
The exact pidfd confirms its exit before the old guard rechecks liveness. The
old reader rejects this case; the revised query reader drains the complete,
bounded stdout and requires the actual exit status to be zero.

Only `OwnedQueryChild` uses the new MATCH/MISMATCH/EXITED/UNAVAILABLE distinction.
It is available after the existing hash acknowledgment, owner release and
CLOEXEC barrier. An observed different executable or live procfs failure is
permanent for that child, including when it subsequently exits with zero.
An absent `exe` link is treated as exit only when that same pinned pidfd already
signals termination. Other procfs errors remain failures even if termination
then occurs. Spawn and output reading use the same distinction; the ordinary
long-lived core/native image rule, deadlines, output bounds, callbacks and exact
cleanup remain unchanged. EXITED alone is never successful query output.

The regressions use real executable transitions to a different interpreter inode
and real exit barriers, plus explicitly injected procfs errors. They reject a
different-image/exit-zero sequence both at spawn and during output reading,
preserve failure after later exit, and retain the failed-exec/nonzero/overflow/
timeout/cancellation controls. Local results pass 23 owned-overlay, 23 supervisor,
10 native-lease and 27 combined-stack tests, with zero skips. No EasyTier, game,
driver or privileged namespace is started. Evidence is retained in
`.local/evidence/cloud/query-exit-observation/`. This fixes the demonstrated
query-exit race; another actual hosted composition must determine its runtime
result, without assigning it as the uniquely established cause of 432.

## Deterministic syscall-stage test inputs

The clean source check at `507e93f0612495b36508f79fd01956287c1e9e93`
ran 604 methods: 601 passed, two were skipped and one failed. In the 139-method
server suite, the synthetic `INTERFACE_RECHECK` fault test instead reached
`OVERLAY_FDINFO_ENTRY_UNAVAILABLE`. The original aggregate and server failure
logs remain retained. They do not identify the descriptor that disappeared.

The fixture previously enumerated every descriptor of a real interpreter child
before injecting a later fault. The spawn barrier proves execution of the
verified image; it does not promise that interpreter startup has finished or
that all enumerated descriptors will remain open. The production monitor
correctly rejects an entry that disappears before its procfs open.

Only the tests change. Synthetic read, TUN-descriptor and final-interface faults
now select the actual child stdin descriptor, whose `/dev/null` identity is
checked and which this fixture never closes. A separate owned-child control
enumerates the real stdin and fd200, explicitly closes fd200 after enumeration,
and records its actual procfs open returning `ENOENT`. Including both entries
must still fail at `FDINFO_ENTRY`; selecting only the persistent stdin under
the same closure must reach the injected `INTERFACE_RECHECK` error. Real child
image checks, descriptor-leak checks and exact cleanup remain in both cases.
The interface and TUN metadata are explicitly synthetic; no TUN or namespace
is created, and no production descriptor is ignored.

The new controlled test and original stage test each pass. The complete local
server suite passes 140/140, with zero skips. Production monitor bytes remain
unchanged. Evidence is retained in `.local/evidence/cloud/kernel-stage-fixture/`.
This demonstrates and removes the fixture's uncontrolled descriptor lifetime;
it does not assign fd200 to the earlier clean-checkout failure or explain any
historical hosted runtime failure. A new clean aggregate is still required.
