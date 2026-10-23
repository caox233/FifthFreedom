# SERVER06 — Native socket ingress isolation

Status: ready-for-agent

The launcher Public gate remains closed. This task verifies one necessary native
server boundary: a listener bound to the protected interface must reject traffic
arriving through another interface, even when that traffic targets the protected
IP. Binding an IP alone does not establish that boundary on Linux.

## Implemented test and ownership boundary

`scripts/ci_socket_isolation.py` runs the actual Rust `socket_policy_probe`
example linked to production `quazal::socket_policy::SocketPolicy`. It checks
TCP, explicitly addressed UDP and the production temporary/ephemeral UDP path.
The executable must be the trusted same-run build output; this harness is not an
executor for untrusted packages. Its resolved executable SHA256 is included in
the receipt. Building the probe does not start the native game server.

Each run uses three randomly named owned namespaces. All interface creation and
address/route changes occur inside those namespaces:

| Namespace | Interface | Synthetic address |
| --- | --- | --- |
| Server | `ff-protected` | `10.203.N.1/30` |
| Protected client | `ff-client` | `10.203.N.2/30` |
| Server | `ff-outside` | `10.204.N.1/30` |
| Wrong-interface client | `ff-client` | `10.204.N.2/30` |

`N` is chosen per run in 0–255. The wrong-interface client has only an explicit
`10.203.N.1/32` route via `10.204.N.1`. The destination is local to the server;
no forwarding, host route, host interface, sysctl, firewall, NAT, external
connection, TUN device, kernel module or driver installation is needed. Both
veth pairs originate inside the server namespace and move their peers directly
to the owned client namespaces. Namespace creation itself uses the standard
`ip netns` namespace-handle mount mechanism and requires `CAP_SYS_ADMIN`.

A successful receipt requires all 18 exchanges, in this order:

1. The production LAN/IP-only policy echoes on all three sockets from the
   protected client, then from the wrong-interface client, then again from the
   protected client. If the wrong path cannot reach the baseline, the harness
   fails before attempting the interface-bound policy.
2. The production interface-bound policy echoes on all three sockets from the
   protected client, rejects the wrong-interface exchanges, and then echoes on
   all three from the protected client again. The probe must remain alive before
   and after every exchange. Routing errors, malformed replies and closed
   connections do not count as an isolated ingress path.
3. Both owned probe processes stop and all three namespaces are removed. Any
   process/namespace cleanup error prevents a verified result.

Each client exchange is bounded to one second. READY output is limited to 4096
bytes and eight seconds, uses an exact schema and validates ephemeral ports.
Commands have eight-second deadlines. Probe shutdown first requests `STOP`,
then uses only its owned `Popen` handle for bounded termination/kill if needed;
forced shutdown fails the run. No process name/global kill is used. The probe
also has its own 120-second lifetime cap. SIGTERM and Ctrl-C enter owned cleanup;
SIGKILL or loss of the runner cannot be handled by userspace cleanup.

Only a successful `ip netns add` becomes owned. The harness holds an open
namespace handle and compares device/inode identity before using or deleting
its name. Colliding, unverified or replaced names are never deleted. It does not
enumerate names by prefix. An identity acquisition/cleanup failure is reported
as failure rather than claiming cleanup succeeded.

The ordinary LAN temporary UDP socket binds wildcard and may reply from the
server's external IP. The synthetic client therefore uses unconnected UDP,
while requiring the exact payload, destination port and one of the two owned
server addresses. A connected UDP client would incorrectly make the baseline
wrong-path test fail solely because of the reply source address.

## Execution

Use an isolated GitHub-hosted Linux development job. Build with the repository's
pinned Rust toolchain and existing verified protoc setup:

```sh
cargo build --locked --manifest-path src/native/Cargo.toml -p quazal --example socket_policy_probe
python3 -m unittest discover -s tests/ci -p test_socket_isolation.py -v
sudo --non-interactive python3 scripts/ci_socket_isolation.py \
  --probe "$PWD/src/native/target/debug/examples/socket_policy_probe" \
  --output "$PWD/.local/ci/socket-isolation/receipt.json"
```

The runner needs Python 3.11+, Linux, root with `CAP_NET_ADMIN` and
`CAP_SYS_ADMIN`, and existing `iproute2`. No dependency/driver installation is
performed by the harness. The output receipt must not already exist. Missing
privileges/tools, failed setup, negative-control failure or cleanup failure
returns a nonzero exit; there is no passing skip. CI workflow/runner integration
is maintained separately by CI03 and must run this exact privileged CLI, not
only the unit tests.

## Evidence and remaining acceptance

The initial focused suite passed **26 methods** on the cloud Linux workspace,
including real OS loopback TCP/UDP client exchanges and wrong-source-port
rejection. Seven namespace ownership/cleanup regression methods use Linux file
handle semantics and explicitly skip on other platforms; all remaining methods
are portable. Unit fixtures simulate namespace commands and cannot establish
live ingress isolation.

The real harness command was attempted in this cloud workspace and returned
exit **1**, `ROOT_NAMESPACE_PRIVILEGES_REQUIRED`, with zero namespaces created
and `interface_isolation_verified=false`. Its local receipt is
`.local/evidence/socket-isolation/cloud-capability-receipt.json`. An explicit
`sudo --non-interactive` invocation returned exit **127** because this workspace
has no `sudo`. No privilege/network restriction was bypassed. A successful
privileged three-namespace run is still required from GitHub-hosted Linux.

Even a future successful harness receipt means only
`interface_isolation_verified=true`. It explicitly leaves
`encrypted_overlay_verified`, `owned_overlay_liveness_verified`,
`game_executed`, `deployed` and `public_enabled` false. This test neither starts
EasyTier nor proves encryption, owned overlay liveness, full server/package
integration, public deployment, driver redistribution rights or game acceptance.
The public runtime gate must retain those separate requirements.
