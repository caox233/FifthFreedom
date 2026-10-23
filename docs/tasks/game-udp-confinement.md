# Game transport confinement audit

Status: **needs-triage**. Read-only source audit; no transport behavior changed,
no game executed, and no new Public capability issued. Public production
admission remains closed. This is separate from the verified isolated Linux
server tests and the Windows API-only TCP tests.

## Actual retained paths

`src/native/client/hooks/src/hooks/storm.rs` installs six Winsock detours when
`Networking.IpAddress` is configured: `bind`, `connect`, `WSAConnect`,
`closesocket`, `sendto`, and `recvfrom`. Without that setting, only configured
packet diagnostics may install the last two. `hooks.rs::hook_with_name` logs
missing exports or installation errors; it does not return an all-installed
capability. The word “mandatory” in the existing log is not a verified boundary.

`force_bind_socket` attempts an IPv4 source bind and returns no success/failure
result. `sendto`, `connect` and `WSAConnect` still invoke their originals if that
bind fails. Explicit bind rewrites preserve loopback and the requested port.
Incomplete/non-IPv4 addresses retain the original Winsock handling. These are
retained LAN behaviors, not enforceable Public confinement, and must not be
changed under a general LAN cleanup.

`BOUND_SOCKETS` stores only numeric `SOCKET` values. Any successful hooked bind
can mark one, without live source/interface readback. Successful `closesocket`
removes the number after the original call. Neither the set nor that ordering
identifies a socket generation across concurrent close, handle reuse or
duplication. There is no owner epoch, interface index/LUID check,
`IP_UNICAST_IF` setting/readback, receive-interface check, or revocation for these
borrowed game handles.

The audited native client source does not intercept `WSASendTo`, connected
`send`/`WSASend`, `WSASendMsg` extension lookup/use, `socket`/`WSASocket`, socket
duplication, or overlapped-send completion/cancellation. This inventory proves
source coverage only. It does not prove that the closed retail executable uses
or does not use any particular missing path.

## Why API TCP does not close this gap

`api/source_transport.rs` owns its actual `TcpSocket`/`TcpStream` objects. It
checks a fixed API destination, live source/index/LUID before and after connect,
sets and reads back `IP_UNICAST_IF`, and shares a terminal lease epoch that closes
registered streams and cancels pending connections. Its production issuer is
closed; only tests construct synthetic leases. The Public-return descriptor
selects that closed policy, not a socket capability.

That ownership cannot be borrowed for an arbitrary retail `SOCKET`. A background
revoker must not close an old numeric handle that may now name another socket.
An overlapped `WSASendTo` returning `WSA_IO_PENDING` has submitted work, not
completed it; application buffers, completion callbacks and IOCP ownership need
their real lifetime rules preserved. Interface/address binding alone also does
not prove encrypted runtime ownership or authenticated peer freshness.

Adding denial to only `sendto` and `WSASendTo` would leave other real entry points
outside coverage. No such partial guard was implemented or advertised as game
transport protection. The production provider stays closed until one complete
enforceable boundary is demonstrated.

## Two bounded implementation candidates

**Kernel process/interface enforcement:** investigate standard Windows Filtering
Platform application/interface filters covering outbound and inbound IPv4 and
IPv6, TCP and UDP, connected/unconnected and overlapped operations, including
explicit loopback behavior. A file-path application ID must not be assumed to
identify one exact owned game process. Filter ownership, BFE/service failure,
owner crash, exact game termination, and filter cleanup order must fail closed;
automatic removal of a dynamic filtering session is not itself a revocation
guarantee. No availability, privilege, coverage or crash-order claim is made
until the exact Windows interfaces and real tests establish it. A driver/callout
or stronger isolation requirement must be reported if standard user-mode
filters cannot enforce the intended process boundary.

**Complete owned-socket integration:** capture creation/generation, reject
unknown/pre-existing/duplicated handles unless their ownership is proved, bind
the immutable source/index/LUID and destination policy, and verify actual OS
state at each relevant operation. Serialize same-socket operation/close against
reuse, and account for connected sends, extension APIs, async cancellation and
completion ownership. Preserve caller-visible return/error/callback behavior;
do not manufacture completion or treat registry removal as kernel cancellation.
Any unobserved retail path prevents a claim of whole-process coverage. This
candidate is larger than adding two detours and needs a supported inventory or
an independent kernel boundary before positive Public admission.

## Required Windows evidence before choosing or enabling a candidate

Use a deliberately owned x86 helper, not the game, with actual IPv4/IPv6 TCP and
UDP sockets: allowed and forbidden interfaces/destinations, wildcard and
explicit binds, loopback, connected sends, `sendto`, `WSASendTo` synchronous and
overlapped, pending completion, duplicate handles, concurrent close/reuse, owner
death and interface/address replacement. Verify packets or peer receipt, actual
kernel rejection, exact helper exit and cleanup; a mocked policy or absent
traffic without an allowed positive control is insufficient. Include a separate
sentinel process so process scope and cleanup cannot silently affect unrelated
instances. Successful helper tests still are not closed-retail runtime coverage.

Source hashes and the read-only input inventory are retained under
`.local/evidence/cloud/game-udp-audit/audit-inputs.json`. Candidate run
`39943233020` at `4322658` includes the later OPEN check and stack diagnostics,
but predates SERVER-16 observer policy. Run `39944703219` at `4b49da7` includes
all three. These are checked Git ancestry facts; their actual checkout and
execution outcomes require the new official receipts. Earlier b67 runtime
success does not validate subsequent source changes or game confinement.

Next: read-only verification of the exact WFP conditions/layers and lifetime
interfaces, then choose the smallest complete candidate with explicit gaps.
Do not alter system policy, install a driver, or enable a production lease as
part of this audit.
