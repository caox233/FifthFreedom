# Explicit STUN configuration for an inert owned TUN probe

Status: source and local GNU contracts independently reviewed and passed. The
new Windows build/runtime remain separate gates. No core, driver, TUN or STUN
network probe was started for this change.

The retained twelve-patch source creates a collector with default TCP STUN
servers, then applies only the configured UDP and IPv6 lists. Consequently,
setting those two lists to `[]` does not remove default TCP discovery. Its
all-empty collector also queues three background routines when observed.
This independent change adds optional `tcp_stun_servers` configuration, uses one
production factory to select all three lists before collector startup, and
returns before setting `started` or queuing tasks when all three lists are empty.

Omission still calls the unchanged default-list functions. Explicit `[]`
survives parsing, setters and serialization. The guard applies to an initially
unstarted, all-empty collector; it does not stop an already running collector or
redesign selective nonempty discovery. A later explicit setter can still enable
the original routines. No default lists, ordinary LAN defaults, Cargo versions,
paired TUN source or previous recipe identities are changed.

The production delta touches only `common/config.rs`, `common/global_ctx.rs` and
`common/stun.rs`. The final contracts additionally append test modules to config,
STUN and RPC source and add the single TOML fixture. The candidate applies
[inert-profile-combined.patch](inert-profile-combined.patch) once as its thirteenth
ordered patch. [inert-stun.patch](inert-stun.patch) and
[contracts.patch](contracts.patch) are the equivalent split audit inputs;
applying both them and the combined patch would be incorrect.

The base tree is `980bb96687a83f7da040d8aa4c4f7134c1c07183637510f2f39a805eaa08a294`.
The resulting 704 EasyTier plus 49 paired TUN files hash to
`8cf92686e0cce91f6db26dccdb3ca36f4e776b21254da036939fc17e9992c584`.
Cargo.lock remains `37c7e066bef9d801b7b67942a18bd7365485e613c18d14f366aed178bb38a696`.
Independent forward application matched the complete final tree; reverse
application restored the complete original tree. Split and combined application
also produced identical files.

## Executed contracts

The tests call actual `TomlConfigLoader`, the production collector factory,
`get_stun_info` and the actual RPC address parser. They do not implement a second
configuration parser or discovery loop.

- The untouched production baseline with seven tests returned exit 101:
  four passed and three failed. TCP configuration disappeared on dump, explicit
  empty lists were not retained, and an empty observation queued three tasks.
- The first thirteen-test after run passed twelve. Its only failure exposed an
  existing getter rule: an IPv4 `/32` is converted to `/24`. The fixture and
  expected value now explicitly use `/24`; that production getter is unchanged.
- The final thirteen contracts passed with zero failures or ignored tests.
  The existing twenty-one config tests and one GlobalCtx construction/event
  test also passed using the same preserved final executable.

Upstream `cfg(test)` makes the default STUN lists empty. The UDP/IPv6 dump failure
in the before run therefore includes that test-only normalization case; it is
not evidence that production's nonempty UDP/IPv6 defaults previously erased
explicit empty lists. Custom nonempty TCP/UDP/IPv6 values exercise the real
selection path, and omission is checked against the unchanged default functions.
The original default constants remain present and unchanged.

The before empty observation and later-setter control use a current-thread
Tokio runtime and do not yield after queuing. Source inspection confirms that
startup only queues the futures; DNS/socket work lies inside them. Dropping the
collector aborts them before they can be polled. No nonempty STUN network test
was run. These are finite source/runtime contracts, not packet-capture evidence.

The reproducible focused command is `cargo +1.95.0 test --offline --locked
--target x86_64-unknown-linux-gnu -p easytier --lib fifth_freedom_stun_ --
--test-threads=1`, with the existing pinned Cargo/protoc environment recorded in
[receipt.json](receipt.json). This local build uses GNU/default features; it does
not substitute for the selected no-Packet Windows build.

## Fixed future probe input

[inert-profile.toml](inert-profile.toml) is byte-identical to the fixture consumed
by the actual configuration contracts. It is synthetic input for owned Windows
TUN observation, with no peer or authentication acceptance claim. The runtime
may supply only the separately validated random instance identity and owned
`ff-` interface name; it must preserve the disabled networking fields.

| Path | Required input and production boundary |
| --- | --- |
| Tunnel listeners and peers | Explicit empty `listeners`, `peer` and `mapped_listeners`; `core.rs` inserts default listeners only when the field is absent. |
| STUN | All three explicit empty lists reach the new production factory; the all-empty startup guard queues no discovery work. An old binary silently ignores the new TCP field and must not run this probe. |
| Hole punching and UPnP | TCP, UDP and symmetric punching plus UPnP disabled; respective run paths return on these flags. P2P disabled and `need_p2p=false`. |
| Proxy and relay | Empty proxy networks/port forwards/exit nodes/routes; KCP/QUIC proxy and input disabled; relay flags disabled. Empty `IpProxy::start` returns when exit is false and `no_tun=false`; its constructors only allocate state. |
| DNS, IPv6 and DHCP | DNS acceptance, broadcast relay, IPv6, public IPv6 provider/auto and DHCP disabled; SOCKS/VPN portal omitted. |
| Owned TUN | `no_tun=false`, synthetic `10.254.253.1/24`. A future harness must reject address and non-default-route overlap with the entire `10.254.253.0/24`, not only a single address. |
| RPC | Separate exact arguments `--rpc-portal 127.0.0.1:15998`. This is an allowed local listener, not a no-socket profile. Port conflict must fail startup. |

The actual RPC parser treats a bare port as wildcard. Even `127.0.0.1:0` invokes
a helper that temporarily probes ports on `0.0.0.0`; the nonzero fixed loopback
argument bypasses that path. The parser contracts never call port selection or
bind a socket. No new zero-RPC feature is introduced.

Source inspection of these selected branches is not evidence of a successful
Windows core startup. The future run still requires the newly built exact image,
dependency-byte pins, protected private inputs, exact process ownership,
temporary application-path firewall containment and restoration, kernel NIC
and route observations, and owned cleanup. Existing default/public profiles,
runtime admission and third-party redistribution holds remain unchanged.
