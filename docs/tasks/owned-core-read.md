# Instance-owned Windows NIC read channel

Status: implemented and independently reviewed; actual Windows pipe execution pending.
This is a historical observation channel. It does not enable Public, construct a
live overlay lease, authenticate a remote peer or run the full EasyTier core.

The [owned process primitive](owned-core-process.md) previously passed 17 actual
Windows x86 synthetic-child checks. This addition supplies one typed optional
`FF_OWNED_CORE_READ_V1` entry to that exact process creation path. Its absent case
keeps the existing fixed environment; ambient descriptors still do not opt in.
Image/configuration pins, atomic `JOB_LIST`, suspended creation and exact child
handles remain the same. No existing game/LAN configuration pipe is changed.

The launcher creates a random first-instance, overlapped byte pipe before
resuming the child. Native `CreateNamedPipeW` explicitly sets
`PIPE_REJECT_REMOTE_CLIENTS`; a protected DACL grants only the actual current user
full control. This local-only kernel contract is necessary before comparing
client PIDs to local process handles. The private security descriptor is freed
and handle ownership transfers to the managed stream only after construction
succeeds. Actual local pipe cases cannot be described as an SMB/remote test. It accepts only the kernel client PID matching its retained
created process handle and creation time. A non-secret descriptor binds a fresh
attempt, explicit Instance UUID, and the actual launcher PID/birth/image path and
hash. The parent image file and exact process handle remain held. This digest is
owner consistency, not publisher verification or a server authentication proof.

The actual Rust endpoint independently obtains its own kernel parent PID from
Toolhelp, opens and pins that process, checks its birth and actual image bytes
against the explicit descriptor, then requires the pipe's kernel server PID to
be that same live handle. There is no PID/name search for an arbitrary core and
no trust derived from HELLO. Identification SQOS prevents pipe-server
impersonation of the child. A single process can claim the explicit descriptor
only once, for the exact configured Instance ID; failures do not reconnect.

The production patch calls this endpoint in `EasyTierLauncher::easytier_routine`.
It verifies the parent/pipe and reads the challenge before constructing/running
the Instance. HELLO is deferred until `Instance.run` finishes. During run, parent
loss, pipe EOF or any unsolicited pre-HELLO byte aborts initialization and goes
through the existing `clear_resources`. The normal absent-descriptor path is
unchanged. After startup, the endpoint borrows this actual Instance and directly
calls `Instance::observe_windows_session(source)` for each request. Serve failure,
EOF, cancellation through the existing stop notifier and owner loss all leave
that borrowed scope before existing cleanup. No detached task keeps the Instance
or Wintun session alive.

Wire version 1 uses a four-byte little-endian length and at most 2 KiB strict JSON.
Unknown, duplicate, null, malformed and out-of-range fields are rejected. HELLO
binds the fresh attempt, challenge and Instance. Each read has its own random
nonce and increasing request number; replies must exactly echo attempt, nonce,
request, Instance and canonical unicast IPv4 source. A reply contains either a
bounded name and kernel GUID/LUID/index or the fixed `NIC_UNAVAILABLE` code.
There is no configured-IP or cached-tuple fallback, `ready` message, account
credential, bearer or serialized generation presented as a capability.

The launcher's ten-second startup budget covers resume through HELLO. Its
three-second read budget covers write and response validation, with one pending
request and no hidden queue. Rust starts its single three-second frame budget
after receiving an actual first prefix byte; a readiness notification alone
does not start it. The remaining prefix/body, the current NIC accessor's own
two-second outer/inner-lock budget and reply write share this deadline. Idle
alone grants no observation authority. Parent liveness is independently checked
while idle and before/after each observation. Kernel/file calls are synchronous
and not forcibly preempted by async deadlines; the owned process/job and CI job
provide outer containment.

Malformed/late replies, cancellation, EOF or owner exit permanently close the
managed attempt and cancel pending I/O. No replacement connection can reuse it.
Disposal drains the exact owned job before waiting for the reader/watch and
pending caller to unwind and releasing descriptor inputs. Token callbacks run
asynchronously outside the gate; a throwing or blocked callback cannot bypass
owned-process cleanup. Notification completion is bounded and failure is reported
with a fixed cleanup code after the job is drained. The returned tuple is explicitly historical,
not valid evidence of current routing or an ongoing session after return.

## Verification and exact source composition

- `tests/OwnedCoreRead` links the actual production classes. Eight portable cases
  pass locally. Eleven further cases are explicitly Windows-only and remain pending:
  actual owned pipe identity, unavailable response, nonce/replay rejection,
  pending cancellation, bounded timeout, EOF with a still-live child, process
  exit, foreign same-image descendant rejection disposal of pending I/O and a throwing cancellation callback after exact job
  cleanup.
- `tests/native/windows-owned-read` links the exact two new production Rust
  modules. Eight real parser/duplex/cancellation tests pass. All library/test/helper
  targets type-check for `x86_64-pc-windows-msvc`; this does not execute Windows.
- The separate test-only Rust helper uses those real parent/pipe/serve modules
  with explicitly synthetic observations. Managed `--native-helper <exact exe>`
  has four Windows-pending cases: positive exchange with a four-second idle and
  explicit unavailable result, wrong parent birth, wrong actual image digest,
  and an incomplete prefix that must terminate after the real first-byte budget.
  It does not import EasyTier, load a driver, create a NIC or open a network socket.
- The retained owned-process project still has 17 cases. Its ambient-environment
  control now also covers the new descriptor. WPF cross-compilation passed; the
  new optional branch has not yet received hosted execution evidence.

The [independent patch receipt](../dependency-patches/easytier-2.6.4-windows-owned-read/receipt.json)
binds the successful ten-patch input, four exact touched files and test inputs.
Application to a private copy of the exact 701-file source produced 703 files;
all other bytes and the lock remained identical. Original evidence is retained.
The old successful ten-patch recipe and the separate no-Packet/peer patches are
not changed. A full combined Windows build and actual core/runtime execution are
still separate requirements; the thin launcher hook has not yet been compiled
as part of that complete dependency tree.

Later authenticated peer/challenge work must retain the real Weak objects inside
this Instance-owned scope and recheck the same live NIC/peer owners before and
after waiting. Current JSON tuples, timestamps or numeric generations cannot be
combined into a Public admission lease. The no-Packet loader closure, reviewed
runtime assets and real route/UDP/peer proof remain gates.

Windows API contract reference:

- [CreateNamedPipeW and PIPE_REJECT_REMOTE_CLIENTS](https://learn.microsoft.com/windows/win32/api/winbase/nf-winbase-createnamedpipew)
