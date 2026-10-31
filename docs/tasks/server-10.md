# SERVER-10 — inherited owner lease and controlled native startup

Status: **ready-for-agent**. The Linux executable now connects the previously
verified deployment plan, interface-bound sockets and source admission to an
explicit inherited owner lease. Ordinary `--deployment-profile public` still
fails with `PUBLIC_PROFILE_NOT_READY`. The production Python admission provider
remains closed until the actual patched runtime and owned secure profile pass
integration. This task does not declare Public deployment or live game acceptance.

## Trust and executable path

The new invocation is:

```
FifthFreedom.Server --deployment-profile public \
  --public-deployment-config /absolute/private/deployment.json \
  --owned-public-lease-fd INHERITED_DESCRIPTOR
```

Both arguments are required. LAN initialization, alternate config/data/key
sources, smoke ports, launcher mode and static inspection cannot be combined
with this path. It is Linux-only. No environment Boolean, network name, PID file,
Unix pathname socket or forwarded network header grants activation.

The caller is the trusted local deployment-owner parent running as the same
UID. Linux peer credentials identify that process; they do not attest its Python
code, prove encrypted overlay health, or protect against the owner/root issuing
false declarations. The reviewed supervisor provider must supply that independent
runtime/profile proof before granting a lease. The native protocol is a bounded
local authorization capability under this explicit trust model.

SERVER-05 now produces one typed deployment plan for inspection and activation:
real SERVER-04 dual-package verification, exact admitted Windows client identity,
private shared issuer key, existing owner-private data directory, and actual
retained native `Config`. Static output remains unchanged with
`startup_allowed: false`. The deployment JSON may additionally contain
`runtime_profile_sha256` and `runtime_executable_sha256`; activation requires both
as 64 lowercase hex digits. The first binds the reviewed canonical public recipe
(no secrets); the second binds the actual runtime executable bytes. Supplying
these fields is not proof that such a recipe/runtime has been approved.

Before the first persistent write, startup validates the inherited descriptor
and pins the parent pidfd, verifies the plan, constructs the actual immutable
interface/source policies, completes the owner handshake, rechecks the kernel
boundary, arms the independent watchdog, installs the readiness callback, enters
the held private directory FD, and acquires its existing Linux instance lock.
The lock file is the first possible persistent creation. Only then are SQLite,
retained content and services initialized. Existing fixed database/WAL/SHM/journal,
lock and content paths are inspected without following symlinks and must be
owner-controlled regular single-link files or owned directories. No other files
are traversed or removed. This is a trusted-owner tree, not an adversarial
concurrent-filesystem mutation guarantee.

Native auth/storage always uses the admitted Windows client `ReleaseIdentity`;
the Linux service digest is not substituted. The controlled path starts neither
the LAN stdin observer controller nor admin services. Observer remains false.
All five services and temporary P2P sockets receive SERVER-06's exact interface
policy; SERVER-08's immutable CIDR policy reaches TCP incoming, HTTP before read,
API authentication, PRUDP before parsing and ticket admission. Config port 80,
capability fields and retained package/run protocol semantics are unchanged.

## Exact private IPC contract

The parent creates `AF_UNIX/SOCK_SEQPACKET`, enables `SO_PASSCRED` before spawning,
and passes only the child endpoint to native. Native verifies the descriptor's
family/type, `SO_PEERCRED` creator PID equals its actual parent and UID equals its
effective UID, and pins that parent with `pidfd_open`. It checks PPID again across
the handshake. Every packet must contain exactly one `SCM_CREDENTIALS` with that
parent's PID/UID/GID. The parent must similarly check actual per-packet native
credentials; socketpair `SO_PEERCRED` still identifies the original creator.
Truncated payload/control, extra ancillary data, duplicate/unknown JSON fields
and received descriptors are rejected. Received forbidden FDs are closed.

Each JSON packet is at most 4096 bytes and contains:

| Field | Value |
| --- | --- |
| `version` | `ff-public-owner-lease-v1` |
| `kind` | `hello`, `admit`, `renew` or `ready` |
| `nonce` | Native-generated 32 random bytes as 64 lowercase hex digits |
| `sequence` | HELLO 0; parent ADMIT 1; parent RENEW exactly 2, 3, ... |
| `binding` | Exact object described below, repeated unchanged |
| `lease_ms` | Required for ADMIT/RENEW, 100–5000 ms; absent from HELLO/READY |

`binding` contains `config_sha256` (raw private JSON bytes),
`service_manifest_sha256`, `admitted_client_storage_key`,
`runtime_profile_sha256`, `runtime_executable_sha256`, `netns_device`,
`netns_inode`, `interface_index`, `overlay_ipv4` and `overlay_prefix`. Native
computes the actual package/config/kernel values. Parent ADMIT must echo the
complete binding and nonce exactly. These private IPC values are not log output;
receipts must not copy private paths, keys, config bodies or raw config hashes.

Native READY uses **sequence 1 in the native-to-parent direction**, regardless
of how far the independent parent renewal sequence has advanced. It is emitted
only after the four existing HTTP/PRUDP bind callbacks and the real API incoming
listener have all bound. LAN's previous four-bit capability readiness remains
unchanged; the controlled path additionally requires the API bit.

Handshake is bounded to 3 seconds. READY must occur within 10 seconds after
watchdog activation and cannot be postponed by renewals. A parent should renew
at most one second apart, with a lease no longer than 5 seconds. Expired grants
cannot arm the watchdog; packets received/parsed after their deadline cannot
revive a lease. A fifth bind completing while poll waits on the earlier readiness
deadline clears only that deadline, never expiry or invalid-frame rejection.

EOF, pidfd parent death, changed namespace/interface/address, malformed/replayed
renewals, lease expiry or missing READY make the watchdog terminate the native
process with exit code 92, closing accepted sockets even if workers block in
SQLite/accept/read. Closing the parent endpoint is the revoke operation; an
unrecognized packet kind also revokes. The watchdog never signals unrelated
processes or removes interfaces/routes. Kernel address/index/namespace checks
are repeated on renewals; actual encrypted runtime health remains the parent's
responsibility. The original required-service supervisor also terminates startup
if any service exits.

## Evidence and remaining work

The local suite uses real credential-bearing Unix sockets and owned subprocesses
with explicitly synthetic metadata. It covers wrong nonce/config/profile/runtime
and package identities, namespace/interface mismatch, sequence replay/skip,
unknown/duplicate/oversized JSON, missing/wrong credentials, forbidden FD passing
including ancillary truncation, invalid descriptor/creator, expired admission,
handshake timeout, renewal expiry/EOF, real pidfd death while the socket peer
stays open, and both sides of the readiness deadline. The pidfd test exercises
the production receive/poll function with a real exited owned child; it is not a
claim of an independently deployed supervisor.

One subprocess runs all five actual retained services on ephemeral loopback
ports and synthetic SQLite state, receives READY from their production bind
callbacks, then proves revocation closes a blocked accepted TCP connection,
all three TCP listeners and both UDP listeners. This is an isolated lifecycle
test using the LAN socket policy, not an encrypted Public activation claim.
The invalid-main probe uses a freshly built real Linux executable with explicitly
inert synthetic Windows role files. It sends only a wrong-nonce ADMIT over real
inherited IPC and verifies no database, lock, content or listener is created.
It never grants valid authorization to an ordinary network interface.

Local final validation: **367/367 dedicated-server tests**, including 15 lease
cases and 14 shared preflight cases; **7/7** protocol/startup guards. Actual Debug
and Release main probes both preserved every fixture byte and directory entry,
refused wrong-nonce inherited admission and ordinary Public startup, and retained
exactly one ASCII server marker with no UTF-16 duplicate. Debug preparation to
HELLO took 50.34 seconds; optimized Release took 0.70 seconds on this cloud host.
The parent needs a separate bounded preparation window (the native three-second
handshake begins at HELLO). An initial 30-second Debug probe timeout is retained
as evidence, not reported as successful execution.

Final local results and byte/hash receipts are kept outside Git under
`.local/evidence/cloud/server10/`; the reviewed source does not include private
keys, configuration or test account databases. Historical fixtures are unchanged.
Next: compose the reviewed parent adapter with actual owned secure-profile
readiness and runtime provenance, exercise the complete controlled startup in an
owned namespace, and retain independent hosted/lifecycle evidence before opening
the production admission provider. Complete client packages still require the
held SAV/META privacy inputs; no synthetic input satisfies that gate.
