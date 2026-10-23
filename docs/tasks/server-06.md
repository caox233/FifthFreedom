# SERVER-06: one explicit native socket policy

Status: **ready-for-agent** — production LAN callers use the common socket
creation boundary; Linux interface confinement and owned loopback probes pass.
Privileged namespace isolation evidence is a separate hosted check. Encrypted
overlay ownership/liveness and coordinated public startup remain prerequisites;
`PUBLIC_PROFILE_NOT_READY` is unchanged.

## Socket inventory and production wiring

All retained native production socket creation sites were inspected and routed
through `quazal::socket_policy::SocketPolicy`:

| Caller | Socket | Policy propagation |
| --- | --- | --- |
| Quazal authentication/secure PRUDP | UDP listener | `main::start_server` → `Server::bind_with_policy` |
| `simple_http::serve` | Configuration TCP listener | Explicit borrowed policy from main |
| `simple_http::serve_many` | Content TCP listener | Explicit borrowed policy from main |
| `api::start_server` | Tonic TCP listener | Explicit owned policy → `api_incoming` |
| `MyMisc::test_p2p` | Temporary outbound UDP socket | Service-held clone → `bind_ephemeral_udp` |

Native NAT traversal uses the existing PRUDP socket for its replies/probes; it
does not create a separate production socket. Its additional binds are test-only.
No protobuf field, PRUDP byte format, service port, game identity, package check,
named-pipe path or accepted gameplay repair is changed.

Main creates `SocketPolicy::lan()` explicitly once and clones it into each
service. The policy is immutable and private-field construction prevents callers
from relabeling a normal socket as protected. There is no environment switch,
global mutable selector, route manipulation or fallback to another interface.
The old generic PRUDP `bind` API remains an explicitly LAN-only compatibility
method; production startup uses the concrete-address policy method.

LAN delegates to the same standard-library TCP/UDP bind operations and retains
the original `0.0.0.0:0` temporary P2P bind. Tonic now consumes the policy-created
listener through its own `TcpIncoming`, preserving nonblocking operation,
`TCP_NODELAY=true`, default keepalive behavior and kernel connection metadata.
Using a raw listener stream would have lost Tonic's existing nodelay default;
the production helper and its real accepted-socket test prevent that regression.

## Linux interface-bound policy

`SocketPolicy::interface_bound(ipv4, device)` captures one concrete interface
name/index and assigned IPv4 address. It rejects wildcard/multicast/reserved
destinations, invalid/absent interfaces, unassigned addresses and down interfaces.
Every bind must use exactly that IPv4; a second address, IPv6 or wildcard is
rejected before creation. The temporary UDP method also uses that same source.

Each socket sets **`SO_BINDTODEVICE` before bind/listen**, verifies the socket
option and the actual socket's `SO_BINDTOIFINDEX` against its captured generation,
and checks the captured index/name/address/up state before and after
creation. A missing privilege, unsupported option or changed device fails closed.
The stored socket index check closes a name-swap race that separate before/after
name lookups alone cannot establish; Linux must support this verification option.
Owned descriptors close on all failures. This avoids Linux weak-host behavior
where binding an overlay IP alone can still admit packets arriving on a physical
interface. Windows interface-bound construction is explicitly unsupported; the
Windows LAN path continues to work normally.

This policy proves a socket's device constraint, not the device's provenance or
encryption. A renamed device can remain the same kernel interface for sockets
already created; a removed/recreated device must not be treated as a live owned
overlay merely because its name/index later matches. Existing-socket supervision,
encrypted runtime process/peer health, address ownership and shutdown on owner
loss still belong to the public lifecycle and remain blockers. No Public CLI
path can select this policy and start native services yet.

The separate TLS control-plane component is not a plaintext native socket. Its
explicit TLS/source-identity boundary remains documented in
[PUBLIC-03](public-transport.md); source identity still must agree with native
admission when those components are integrated.

## Executed evidence

With the documented Rust nightly/protoc toolchain, from `src/native`:

```sh
cargo +nightly-2025-10-15 test --locked -p quazal socket_policy -- --nocapture
cargo +nightly-2025-10-15 test --locked -p dedicated_server socket_policy_api_tests -- --nocapture
cargo +nightly-2025-10-15 test --locked -p quazal
cargo +nightly-2025-10-15 build --locked -p quazal --example socket_policy_probe
```

Focused socket policy **3/3 passed**, actual API incoming **1/1 passed**; full
Quazal **75 passed** (63 library, 11 integration, 1 documentation), zero failures.
The coordinated SERVER-05/06 dedicated-server run passed **341/341** and the
production main build completed successfully. The first dedicated focused command
was mistakenly run at the repository root and failed because no Cargo manifest
exists there; the documented corrected working-directory command passed.

The final socket-index verification addition was followed by another **3/3**
focused policy pass, a rebuilt probe and all six real loopback exchanges below.
The broader 75/341 runs preceded that addition; hosted validation records its own
exact source revision.

The compiled `socket_policy_probe` is a synthetic example using the production
policy. It serves only the fixed `ff-socket-probe-v1` echo payload over TCP, UDP
and a separately allocated ephemeral UDP socket. It accepts owned `STOP` on stdin,
joins its socket workers, and has a 120-second maximum lifecycle. Its readiness
record contains only protocol version, mode and selected ports. No game, account,
key, public deployment or old external endpoint is involved.

Both `ip-only` and `interface-bound` modes were actually started on owned
loopback sockets in this cloud executor. All six TCP/UDP/ephemeral exchanges
returned the exact bytes/source ports and both processes stopped cleanly. This
kernel accepted the initial `SO_BINDTODEVICE` operation despite absent NET_RAW
and NET_ADMIN capability bits; success was observed from the syscall and real
socket traffic, not inferred from permissions. Receipt:
`.local/cloud-receipts/socket-policy-loopback-index.json` (the earlier pre-index
receipt remains preserved as `socket-policy-loopback.json`).

The unit test also supports an explicit kernel permission-denial result without
falling back to LAN. Such a result is only fail-closed evidence. The standalone
probe requires actual interface-bound success and cannot convert a denied bind
into an isolation pass.

## Separate ingress-isolation check and next action

[Socket isolation](socket-isolation.md) defines three owned network namespaces:
the server has protected and unprotected interfaces; two clients approach the
same protected destination IP through different interfaces. First, ordinary
IP-only listeners must actually accept both routes, establishing the weak-host
positive control. Then interface-bound listeners must admit the protected route
and reject the other route for TCP and both UDP sockets. Without the positive
control, a routing/firewall failure would be inconclusive.

This cloud executor cannot create network namespaces or TUN devices, so that
topology requires the explicit GitHub-hosted Linux lane and its capability checks.
Its result must be recorded separately; loopback checks do not establish physical
interface isolation or real encrypted-overlay operation. Next, verify the hosted
topology receipt, then implement owned encrypted-overlay lifetime monitoring and
coordinated listener teardown before considering native Public startup.
