# SERVER-08 — explicit native Public source-network admission

Status: **ready-for-agent**. The native service paths now accept an immutable
source policy. Ordinary Public startup remains `PUBLIC_PROFILE_NOT_READY` and
the static deployment preflight still reports `startup_allowed: false`.
This implements a necessary admission boundary, not an activated Public server.

## Threat boundary and existing identity

The retained API already read tonic's kernel TCP peer for login, registration
and signed-token checks, but its game-handler recheck hard-coded the LAN address
policy. Password recovery, capabilities and reflection did not apply a Public
network constraint. PRUDP parsed `recv_from` datagrams before any CIDR filter.
The existing ticket/run code and secure registration use `ClientInfo.address`,
which the transport establishes from that observed UDP endpoint; client-provided
StationURLs are not the authoritative source.

`quazal::source_policy::SourcePolicy` holds either the original LAN behavior or a
private, validated Public network. The Public constructor takes the declared
overlay host IPv4 and prefix. The complete `/8` through `/30` network must lie
within RFC1918 or `100.64.0.0/10`, and the declared host cannot be the network or
broadcast address. Client admission excludes those two endpoints, out-of-range
addresses and IPv6, including IPv4-mapped IPv6. SERVER-05 now calls that same
constructor rather than maintaining a second CIDR implementation. This remains
narrower than UI-04's general non-reserved CIDR parser.

This is an additional kernel-peer filter. An address inside the CIDR is not an
account, a valid game run, a package identity or proof of authenticated encryption.
UDP source spoofing must still be prevented by the owned encrypted overlay and
interface confinement. No forwarded header, request Boolean, account field or
reported StationURL configures or overrides this policy. The policy has no
environment fallback or mutable runtime setter.

## Production entrypoints

| Path | Enforcement before side effects |
| --- | --- |
| API TCP incoming stream | Actual accepted TCP `peer_addr` is filtered before tonic parses HTTP/2. This includes reflection and optional admin services. Rejected streams are dropped without an application response. |
| API login and registration | Actual tonic `remote_addr` is checked before credential lookup, token issuance or account/run writes. |
| Public password recovery and capabilities | The same source check runs before recovery work or response construction. The original LAN behavior is retained. |
| Authenticated API interceptor | Source policy, signed source-bound token, admitted Windows release identity, current account version and game-run identity must all pass before an internal `VerifiedSource` extension is created. |
| Game-handler recheck | After the generation lock, the handler consumes that private verified source/policy and confirms the current kernel peer still matches. It does not substitute the LAN policy. |
| P2P diagnostic | The destination remains that same verified TCP peer IP and the original fixed UDP port 13000; there is no request-controlled alternate host or port. SERVER-06 still governs socket creation. |
| HTTP configuration and content | The accepted socket's kernel peer is checked before reading HTTP bytes or opening a content file. Rejection writes no HTTP response. |
| Authentication and secure PRUDP | `recv_from` feeds the production datagram handler, which rejects before packet parsing, per-client diagnostics, SYN allocation, ACK, liveness/fragment state or callbacks. The packet dispatch boundary independently rejects an unadmitted peer. |
| Native Login/LoginEx/RequestTicket | The actual `ClientInfo.address` is checked before the retained credential/ticket handlers. Existing account, source-bound run, package, transport-owner and ticket checks continue unchanged. |

The existing PRUDP endpoint/session guards and secure registration's observed
URL behavior remain intact. Filtering a packet does not retire another client's
session, reset cleanup timing, or acknowledge it. TCP filtering preserves tonic's
TCP_NODELAY configuration and `Connected` peer metadata.

## LAN and activation

Main explicitly passes one `SourcePolicy::lan()` through the API, both PRUDP
servers and both HTTP services. LAN API validation remains the original IPv4
unicast rule, including loopback/provider addresses; LAN TCP/UDP admission does
not acquire a new subnet restriction. Existing constructor/test adapters select
LAN explicitly. Signed historical fixture bytes, role paths, release IDs,
password policy, wire messages and accepted gameplay repairs are unchanged.

The Public network type being constructible does not activate a listener.
`PUBLIC_SOURCE_POLICY_NOT_ACTIVATED` in the static preflight remains truthful:
ordinary Public startup still has no allowed plan. Future reviewed activation
must provide this policy from the same owner-controlled deployment document,
bind every socket to the verified owned interface and supervise the overlay's
lifetime. Current standalone observer and capability/readiness policy are not
promoted to Public readiness by this task.

## Evidence and limits

New focused tests use synthetic accounts, tickets, declared private peer
addresses and packets. They exercise real handler cores, SQLite transactions,
the production PRUDP parser/dispatch and actual loopback socket acceptance.
Out-of-network Users calls preserve credential, recovery, account, run and ticket
state; in-network requests still require the original token/run/source identity.
Native ticket handlers deny outside sources and retain the in-network exact-run
source binding. Malformed outside datagrams cannot allocate, log or ACK; admitted
synthetic User packets reach dispatch without transmitting to their declared
private address. LAN loopback SYN/ACK bytes and pending endpoint ownership remain
valid. HTTP rejection leaves the supplied request bytes unread, and the actual
API incoming filter closes a rejected loopback TCP connection before handing it
to tonic.

Positive private-CIDR API/PRUDP peers in these unit cases are explicitly injected
transport metadata, not a demonstrated private interface or encrypted overlay.
No Public service, game, runtime download, tunnel, namespace or deployment is
started. Hosted interface-isolation and overlay-lifetime results are separate.
Focused/full logs, commands, scoped patch and file hashes are retained in ignored
`.local/evidence/cloud/server08/`. An initial compile found eight legacy fixture
constructor calls missing the newly explicit policy; the LAN fixture adapters
were corrected without changing their payloads or historical vectors.

The final local native suite passed **348/348** (341 retained plus seven new
API/ticket/HTTP cases). Quazal passed **81/81**, including 69 library cases,
11 existing integration cases and one documentation test. Its six new policy/
datagram cases account for the increase from 75. The production Debug server
built without warnings. Four actual binary CLI checks retained fixed Public or
preflight refusal codes, empty stdout and an unchanged owned temporary directory;
the rebuilt binary still contained exactly one ASCII server marker and no
UTF-16LE copy. These are local source/unit/socket/build results, not hosted or
encrypted-overlay acceptance.
The seven portable protocol/startup-wiring checks also passed; the original
package/release declarations and ordering of the closed Public startup gate
remain intact.

Next: use this same policy in the reviewed owned runtime activation path, after
the encrypted-overlay and process-lifetime prerequisites pass. Do not lift the
Public gate based on CIDR admission alone.
