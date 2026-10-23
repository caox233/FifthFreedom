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

Next: retain the comparison below while following the actual candidate that
contains SERVER-16 and OPEN. A complete transport boundary still needs a
separately verified per-launch identity or kernel enforcement and existing-flow
revocation tests. Do not alter system policy, install a driver, or enable a
production lease as part of this audit.

## Read-only WFP interface comparison

The subsequent comparison read Microsoft's official documentation and the
already locked `windows` 0.54 bindings. It did not call WFP on Windows, modify
filters, install software or run a socket experiment. The relevant APIs already
exist in those SDK bindings; actual host availability and access remain untested.

| Requirement | Documented interface | Remaining boundary |
| --- | --- | --- |
| System support/access | `Fwpuclnt.dll`, `FwpmEngineOpen0`, desktop Windows Vista onward | Opening the engine needs `FWPM_ACTRL_OPEN`; adding policy requires separate rights. An opened engine is not proof of permission to add filters. |
| IPv4/IPv6 TCP and UDP | Four `ALE_AUTH_CONNECT` / `ALE_AUTH_RECV_ACCEPT` layers | ALE authorizes flows, including the first UDP packet for a remote tuple. Existing flows require reauthorization tests, not just new-connect rejection. |
| Interface and loopback | Local/arrival/next-hop interface conditions, address/protocol conditions and loopback flags | Conditions differ by layer. Reauthorization may supply `FWP_EMPTY` for unavailable arrival fields; a proposed policy must define rejection rather than infer a known interface. |
| Exact owned process | `ALE_APP_ID` is an executable device path; `ALE_USER_ID` identifies the user; `ALE_PACKAGE_ID` identifies an AppContainer | These are not one PID/creation epoch. The documented condition catalog and locked bindings expose no direct generic PID filter condition. Kernel `FWPS_INCOMING_METADATA_VALUES0.processId` is callout metadata, valid only with its presence flag, not a user-mode filter key. |
| Owner loss | Dynamic-session objects are deleted when the session ends | Removing the only block can remove protection while a game process still exists. A job lifetime and WFP cleanup are not documented as one atomic operation. |

Consequently standard AppID filtering of the shared retail executable path is
not yet a substitute for the exact owned-process boundary. A per-launch security
identity or independently controlled unique executable identity would require
its own launch/compatibility evidence; neither is present here. A design with a
retained deny baseline and short-lived allowances also needs actual filter
arbitration, established-flow reauthorization and crash-order tests. This audit
does not select a new kernel callout driver as the automatic solution.

Official references consulted:

- [Filtering condition identifiers](https://learn.microsoft.com/en-us/windows/win32/fwp/filtering-condition-identifiers-)
  and [conditions at each layer](https://learn.microsoft.com/en-us/windows/win32/fwp/filtering-conditions-available-at-each-filtering-layer).
- [ALE layers](https://learn.microsoft.com/en-us/windows/win32/fwp/ale-layers)
  and [ALE reauthorization](https://learn.microsoft.com/en-us/windows/win32/fwp/ale-re-authorization).
- [Engine opening](https://learn.microsoft.com/en-us/windows/win32/api/fwpmu/nf-fwpmu-fwpmengineopen0),
  [access control](https://learn.microsoft.com/en-us/windows/win32/fwp/access-control)
  and [dynamic session lifetime](https://learn.microsoft.com/en-us/windows/win32/api/fwpmtypes/ns-fwpmtypes-fwpm_session0).
- [Kernel incoming metadata](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/fwpsk/ns-fwpsk-fwps_incoming_metadata_values0_)
  and [filter arbitration](https://learn.microsoft.com/en-us/windows/win32/fwp/filter-arbitration).

Exact downloaded document hashes are retained in the ignored audit directory's
`wfp-official/` indexes. These source references are not Windows execution receipts.
