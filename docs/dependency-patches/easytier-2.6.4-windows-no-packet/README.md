# Independent Windows candidate without Packet imports

Status: ready-for-agent; source preparation and finite contracts passed. Full Windows
core/CLI compilation and their actual import inventories remain pending the new
`independent-windows-no-packet` build-only job. No core, driver or game was executed.

The successful ten-patch Windows candidate still imports `Packet.dll` from its
core. Merely omitting `faketcp` does not remove that dependency: the unchanged
Windows interface enumeration returns `pnet::datalink::NetworkInterface` and
falls back to `pnet::datalink::interfaces()` when `GetAdaptersAddresses` fails.
The real baseline selected Cargo graph still contained the capture backend under
the new candidate's explicit feature list; its check failed with
`NO_PACKET_CAPTURE_DEPENDENCY`.

This separate recipe applies one additional patch to the exact previously
verified paired source, then removes only the verified derived `Packet.lib`
copy. The original tar archives, ten-patch tree, recipe and successful binary
identities remain unchanged. The full upstream workspace and unused source files
remain present. Cargo.lock changes by two dependency entries; package versions,
registry checksums and the paired TUN override remain unchanged.

The selected feature list retains wireguard, websocket, smoltcp, tun, socks5,
kcp, quic, magic-dns, zstd and the existing session observer. It excludes fakeTCP
and disables default features explicitly for both graph inspection and build.
TCP, UDP, QUIC, WSS and TUN source paths remain included; this source fact is not
a new runtime acceptance result. Non-Windows pnet behavior and the old Windows
fakeTCP branch remain available in their original feature configurations.

On Windows without fakeTCP, pnet supplies packet codecs and MAC types without
its `std` capture aggregate. A direct `pnet_base/std` dependency preserves
`std::net` address types through `no-std-net/std` without enabling capture
backends. A real packet-codec compile initially failed when this narrow
dependency was missing; that failed log is retained. With the correction, all
eight linked production contracts pass. Direct `ipnetwork` preserves the
existing WebSocket CIDR logic independently of pnet's capture aggregate.

Interface discovery still calls the native `network-interface` API. Its pure
conversion preserves successful-path IP/mask/MAC handling and the old Windows
`flags=0` filter semantics. It does not manufacture UP status or ownership.
Enumeration failure now yields an empty list and a fixed warning, with no
capture fallback. Empty discovery is not readiness; downstream local-address
and configured-peer behavior still requires actual integration validation.
Link-local filtering later in `IPCollector` is unchanged. The additional module
is byte-identical to the module linked by the eight conversion/codec tests.

The build script's capture import-library search path is conditional on fakeTCP.
The new recipe never restores the held Packet import library into its final
tree. Static inventories reject all compiled inputs there. This does not remove
or validate every Windows dependency: Wintun, WinDivert and other dynamic or
embedded components retain their existing source, runtime and licensing gates.
The prior notices and source files are preserved; no Npcap package was downloaded,
installed or assigned a new redistribution authorization.

Cargo metadata reports a workspace-wide feature union, including features from
nonselected workspace members and target dependencies. It is retained unchanged
and used to check the exact local paired TUN identity, not to assert effective
core features. The separate `cargo tree -p easytier --target
x86_64-pc-windows-msvc --no-default-features --features ... -e normal,build`
checks the actual selected build graph. The Linux-hosted analysis found 489
package/feature contexts with pnet `serde`, pnet_base `serde,std`, and no
pnet_datalink, pnet_sys or pnet_transport. Host build-dependency contexts can
differ on Windows; the job repeats the same strict feature/package checks there.
No metadata nodes are deleted or rewritten to obtain this result.

Local evidence comprises clean preparation from both exact archives, eight
production-linked Rust contracts, eight Python source/graph/PE gates and an
x86_64-pc-windows-msvc compile check of the small linked test target. The latter
does not link or build the full core. The initial packet-type failure and the
baseline capture-dependency rejection remain distinct from these passing checks.

The new Windows job is independent of the existing paired job. It runs the eight
interface/codec contracts plus the existing eight typed-session contracts, checks
metadata and the selected graph, builds core/CLI, then statically rejects
`packet.dll` in both normal and delay import tables for each image. Only bounded
source/build receipts are uploaded. Even a successful result proves those static
imports only; it does not prove arbitrary dynamic loads absent, driver readiness,
Public authentication, full dependency licensing, Win7 support or release fitness.

Exact inputs and local receipts are in `scripts/easytier-windows-no-packet.lock.json`
and [receipt.json](receipt.json). The old ten-patch recipe is an immutable pinned
input, not silently updated by this candidate.
