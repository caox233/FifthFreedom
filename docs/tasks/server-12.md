# SERVER-12 — explicit owned EasyTier profile

Status: ready-for-agent (profile/parser implemented; runtime composition pending).

`src/public/fifth_freedom_control/easytier_profile.py` is the shared production
recipe parser, private TOML emitter and bounded peer-observation parser. It does
not launch EasyTier or grant native admission. The supervisor and native owner
lease consume its canonical public recipe digest as `runtime_profile_sha256`;
the executable digest remains a separate identity. Source/license publication
holds and the complete-package private-input gate are unchanged.

## Recipe and APIs

`scripts/easytier-probe-profile.schema.json` describes
`ff-easytier-owned-probe-v1`. The Python parser additionally rejects ambiguous
encodings and enforces the cross-field constraints. The entire recipe contains
only these fields: schema, purpose, transport, initiator/responder role, UUIDv4
instance ID, explicit network name, interface name, RPC port, local/remote
endpoints and the existing `RuntimeCandidateIdentity`. Endpoints contain numeric
RFC1918 underlay IPv4, distinct usable addresses on one separate /30 overlay,
an explicit listener port and a canonical base64 X25519 public key. Secrets,
arbitrary flags, hostnames, proxy URLs, default routes and extra fields are not
recipe inputs.

The purpose is always `owned-isolated-development`. Transport is exactly `tcp`,
`quic` or `wss`; TCP is a development probe for the shared inner Noise/lifetime
boundary. QUIC/WSS emission and parsing do **not** constitute runtime acceptance
of the product's Fast/Compatible transports. Every future isolated test receipt
must identify its actual transport.

- `parse_profile(bytes) -> ProbeProfile`; at most 16 KiB, duplicate keys rejected.
- `.canonical_bytes`: sorted-key, compact ASCII JSON plus one LF. `.sha256` is
  SHA256 of exactly those bytes. Schema-version changes are required when the
  fixed emission/security policy changes. The digest contains no PSK/private key
  and is an identity, not approval or evidence that containment exists.
- `emit_private_toml(profile, network_secret=bytes, local_private_key=bytes)`
  requires explicit 32-byte secrets. The caller must obtain the PSK and private
  keys from a CSPRNG, share only the PSK between the two owned nodes, and supply
  the corresponding public keys in the public recipes. The function checks the
  local key pair and rejects low-order remote keys through audited X25519. It
  cannot establish randomness by inspecting supplied bytes.
- `write_private_toml(absolute_path, content)` creates a new Linux file using
  retained directory descriptors, `O_EXCL`, `O_NOFOLLOW`, mode 0600 and an owned
  0700 parent. It refuses replacement and unsafe ancestors. A failed write may
  leave an owner-only incomplete file; it is never reported as successful or
  automatically removed/replaced.
- `core_arguments(profile, absolute_config_path)` fixes `--disable-env-parsing`,
  `--rpc-portal 127.0.0.1:PORT` and `--rpc-portal-whitelist 127.0.0.1/32`.
  The owner must provide the existing clean environment and preserve the private
  config object for the entire child lifetime; arguments alone do not pin a file.
- `peer_query_arguments(profile)` fixes the same loopback address, explicit
  instance UUID and `--verbose peer list`.
- `parse_peer_observation(profile, bytes) -> PeerObservation` consumes at most
  64 KiB and returns only the profile digest, connection UUID and peer IDs.

Private generation lazily imports `cryptography==50.0.0`, pinned in
`scripts/requirements-profile.txt`. Missing cryptography fails generation with a
fixed error; public parsing and the zipapp import do not need it. CI must install
this exact binary wheel dependency before emitter tests; tests do not skip a
missing or wrong crypto provider. This requirement does not claim completed
runtime dependency/source redistribution closure.

## Exact pinned upstream mapping

These references are to EasyTier commit
`8428a89d2dabc94c97d370ec607c6ca142473626`, with the two separately reviewed
SERVER-07 session/ingress guards. The original release binary locks remain
unchanged; use the distinct candidate identity produced by the clean builder.

- [Config and merge behavior](https://github.com/EasyTier/EasyTier/blob/8428a89d2dabc94c97d370ec607c6ca142473626/easytier/src/common/config.rs#L525):
  `network_identity`, `[secure_mode]`, explicit `listeners`, `[[peer]]` URI and
  public key. Only initiator emits a connector; responder has explicit `peer=[]`.
  Both have a numeric listener. Omitted listeners could enable default listeners,
  so omission is not used.
- [Flags schema](https://github.com/EasyTier/EasyTier/blob/8428a89d2dabc94c97d370ec607c6ca142473626/easytier/src/proto/common.proto#L7):
  secure mode, AES-256-GCM, IPv4 TUN and explicit interface are required. Proxy,
  exit-node, DNS, UDP broadcast, KCP/QUIC **proxy** input/relay, foreign relay,
  UPnP, hole punching and optional P2P discovery are disabled. QUIC-proxy flags
  do not disable the selected QUIC tunnel transport. Mapped listeners, proxy
  networks, advertised extra routes, exits, port forwards and UDP/v6 STUN lists
  are empty. There is no invented general `disable_advertise` flag: the intended
  overlay route still exists. `disable_relay_data` is not a strict ingress ACL.
- [RPC bind parser](https://github.com/EasyTier/EasyTier/blob/8428a89d2dabc94c97d370ec607c6ca142473626/easytier/src/rpc_service/api.rs#L178):
  omitted or port-only portal can bind broadly; both are prohibited here.
- [CLI verbose branch](https://github.com/EasyTier/EasyTier/blob/8428a89d2dabc94c97d370ec607c6ca142473626/easytier/src/easytier-cli.rs#L1428):
  ordinary JSON is a table projection without keys/authentication. Verbose emits
  `PeerRoutePair[]`, using the generated Rust serde format: key bytes are integer
  arrays, `secure_auth_level` is integer 3 for `NetworkSecretConfirmed`, UUIDs in
  default/direct connection fields are four big-endian u32 parts.
- [PeerConnInfo](https://github.com/EasyTier/EasyTier/blob/8428a89d2dabc94c97d370ec607c6ca142473626/easytier/src/proto/api_instance.proto#L33),
  [auth enum](https://github.com/EasyTier/EasyTier/blob/8428a89d2dabc94c97d370ec607c6ca142473626/easytier/src/proto/peer_rpc.proto#L324)
  and [route join](https://github.com/EasyTier/EasyTier/blob/8428a89d2dabc94c97d370ec607c6ca142473626/easytier/src/proto/api.rs#L175)
  establish the bounded observation schema. Require one returned route/peer and
  one open connection, nonzero distinct IDs, expected network, both expected
  static keys, auth level 3, expected direction, direct cost/next hop, remote
  overlay /30, no proxy CIDRs, matching default/direct connection UUID and exact
  numeric tunnel endpoints. Only the initiating end's source port may be
  ephemeral. The parser rejects extra returned peers or ambiguous instances.

The CLI joins **routes** to peers. Unrouted connections can be absent from this
projection; it is not an exhaustive connection inventory, key allowlist or proof
of packet encryption. A missing/default connection selection simply fails the
observation until the actual source reports the required state.

The upstream shared-secret proof currently returns `NetworkSecretConfirmed`
before optional `peer_public_key` checking. A configured key plus this observation
is not a handshake pin. The fixed two-node development probe additionally needs
its fresh dedicated PSK and exact peer-only kernel containment. Before product
clients send credentials, the expected server key must be authenticated during
handshake. The separate
`docs/dependency-patches/easytier-2.6.4-explicit-pin/` contract now records a real
in-memory before acceptance and a minimum source guard producing explicit
identity rejection. The earlier guard2 candidate is unchanged; adopting this
behavior requires a new candidate identity and the remaining owner integration.

## Owner/provider integration still required

The root provider must verify the distinct patched core **and matching CLI**,
retain their exact executable/child handles, own the namespace and RPC endpoint,
and limit query lifetime/output without shell execution. Run the CLI only in
the exact core namespace against that core's verified loopback listener. Parsing
arbitrary supplied JSON or connecting to an unowned loopback service is not
readiness. Never persist raw CLI output, config dumps or runtime logs.

Empty UDP/v6 STUN lists and hole-punch flags do not disable upstream TCP STUN
defaults. The owned namespace must have no default route or external DNS access
and kernel egress must permit only the explicit owned underlay peer/protocol/
listener flow, its established responses, loopback RPC and required overlay
traffic. Record blocked discovery attempts honestly; do not label them disabled.
No production endpoint, driver installation or network-policy bypass is needed.

Only the composed owner can combine current key/auth observation, exact child and
TUN ownership, verified containment, protected native socket/source policy and
the native lease contract. Loss of any observation must revoke readiness and
close accepted native connections before owned overlay teardown. This module
does not move that admission gate.

## Verification

`python3 -m unittest discover -s tests/server -p test_easytier_profile.py -v`
passes nine focused tests locally: all six transport/direction configurations,
RFC 7748 key-pair controls, malformed/ambiguous recipe rejection, canonical
identity, private file safety and negative auth/key/route/endpoint observations.
The Windows-only limitation is the explicitly Linux dirfd/owner writer check;
emitter/key tests run on Windows after the pinned dependency is installed.
Fixtures are synthetic and do not claim a live RPC observation.

`docs/dependency-patches/easytier-2.6.4-profile/profile-parser-check.rs` also
instantiates the real upstream `TomlConfigLoader`, compares every emitted flag
and required endpoint/secure field, and checks disabled/empty options. It uses
six ignored synthetic RFC 7748 configs and creates no GlobalCtx, core, TUN, RPC
or network socket. The actual GNU/default-feature run exited 0 with **6/6**
profiles accepted; the upstream parser file remained byte-for-byte unchanged.
The exact command, source/log digests and scope flags are in
`docs/dependency-patches/easytier-2.6.4-profile/receipt.json`. Raw logs and synthetic
private configs remain ignored. Rust was 1.95.0 with the same explicit toolchain
setup recorded for SERVER-07; the verifier is an example copied into the ignored
checkout, not a modified or executed official runtime binary.

## Pinned QUIC bind-address correction

The pinned producer describes a QUIC initiator's **UDP socket bind address** in
`TunnelInfo.local_addr`, which may be `0.0.0.0` or `::`; it does not necessarily
describe the selected packet source address. At exact upstream commit
`8428a89d2dabc94c97d370ec607c6ca142473626`,
[`quic.rs:354`](https://github.com/EasyTier/EasyTier/blob/8428a89d2dabc94c97d370ec607c6ca142473626/easytier/src/tunnel/quic.rs#L354)
creates wildcard client endpoints (and can reuse a configured server endpoint
from its pool). The connector at
[`quic.rs:584`](https://github.com/EasyTier/EasyTier/blob/8428a89d2dabc94c97d370ec607c6ca142473626/easytier/src/tunnel/quic.rs#L584)
serializes `endpoint.local_addr()` through `build_url_from_socket_addr`.

The observation parser now accepts exactly the configured local IPv4 address or
those two wildcard hosts **only for QUIC initiators**, with a valid nonzero port.
TCP/WSS initiators and every responder retain the exact local-address checks;
responder ports, both remote endpoints, secure auth level 3, both keys, network,
direct route and unique-connection requirements remain unchanged. IPv6 loopback,
mapped IPv4 forms, other local addresses, invalid ports, proxy query strings and
wildcard remote endpoints remain rejected. `PeerObservation.local_bind_kind`
explicitly distinguishes `wildcard` from `exact`. Neither kind is a substitute
for the independent owned namespace, route and full nft source/egress checks.

The new regression failed before the correction (9 passed, 1 error), then passed.
The current profile suite has 12 passing tests. A pure example uses the actual
pinned upstream URL helper, `PeerConnInfo`/`PeerInfo`/`Route` types and
`list_peer_route_pair` join, then the same generated serde used by the single-
instance verbose CLI. Its three full projections verify key byte arrays, auth
enum values, connection UUID fields, IPv4 route fields and all tunnel URL forms.
Inputs are synthetic public RFC 7748 vectors and fixed example addresses; no
socket, async runtime, core, RPC or TUN was created.
The example, resulting synthetic fixture, exact source hashes and command/log
receipts are under `docs/dependency-patches/easytier-2.6.4-profile/` as
`quic-bind-serde-*` and `quic-bind-receipt.json`. This corrects a confirmed
producer/consumer semantic mismatch; it does not by itself establish the cause
of any hosted readiness timeout or prove successful network operation.

## Exact mapped-IPv4 resolved endpoint

The next actual hosted run, [39924391718](https://github.com/caox233/FifthFreedom/actions/runs/39924391718),
reached an owned TUN and matching core image, but its last of 123 initiator
observations failed specifically with `PEER_RESOLVED_ENDPOINT_INVALID`. The
official sanitized receipt is recorded in
[hosted-owned-overlay-45e879b.json](../evidence/hosted-owned-overlay-45e879b.json).
It contains no raw endpoint, so it does not identify that original address.

A bounded local example then used the **actual pinned `QuicTunnelListener` and
`QuicTunnelConnector`**, an owned IPv4 loopback listener on an OS-assigned port,
and one synthetic echo. The connector reported the configured IPv4 remote URL,
but `connection.remote_address()` became `quic://[::ffff:7f00:1]:40960` for
`127.0.0.1:40960`. The IPv4 listener reported ordinary IPv4 remote addresses in
both fields. This matches the exact producer call chain at
[`quic.rs:592–600`](https://github.com/EasyTier/EasyTier/blob/8428a89d2dabc94c97d370ec607c6ca142473626/easytier/src/tunnel/quic.rs#L592):
configured `remote_addr`, connection-derived `resolved_remote_addr`, and the
production URL builder. The same builder emits `::ffff:a4d:2` for the public
synthetic profile's `10.77.0.2`. The old parser rejected that projection with
the same fixed reason; the corrected parser accepts it.

The correction allows one additional host **only for a QUIC initiator's
`resolved_remote_addr`**: the canonical hexadecimal IPv4-mapped form generated
from the configured remote IPv4. Its port must still equal the configured peer
listener. It does not normalize arbitrary incoming IPv6. Wrong mapped IPv4,
wrong/zero ports, IPv4-compatible addresses, other IPv6 prefixes, scope IDs,
mixed dotted notation, userinfo, proxy queries and other paths remain rejected.
No other role, transport or endpoint field gains this allowance. Authentication,
keys, route, namespace, nft containment and readiness deadlines are unchanged.

The retained example and unchanged output are `quic-loopback-endpoints.rs` and
`.json` in the profile evidence directory; `quic-resolved-receipt.json` binds
their source, logs, exact upstream files and before/after checks. The example
creates no core, TUN, RPC, PeerConn/Noise session, game or public-network traffic;
the STUN collector is never started. Its echo is outer-transport/address evidence,
not encrypted-overlay or peer-authentication proof. Current checks pass 14
profile tests; the full hosted three-transport matrix still needs a new run.
