# Windows retained-session observation prerequisite

Status: **six local lifetime tests, backend Windows typecheck and actual hosted
paired core/CLI MSVC Release build; not an admitted or executed Windows runtime**.
See [UI-09](../../tasks/ui-09.md).

The two patches apply to separate pinned repositories:

| Patch | Upstream input |
| --- | --- |
| [tun-session-observer.patch](tun-session-observer.patch) | EasyTier/rust-tun `12378839e7985283df0e4fb536b7137230356db5`, package tun-easytier 1.1.1 |
| [easytier-session-observer.patch](easytier-session-observer.patch) | EasyTier/EasyTier `8428a89d2dabc94c97d370ec607c6ca142473626`, v2.6.4 |

The backend's explicit `request_new_session` mode calls `Adapter::create` without
trying `Adapter::open`. It requires a nonzero GUID, exact `ff-<32 lowercase hex>`
name, `skip_config`, and an absolute Wintun DLL path. It verifies the DLL signature
before loading that absolute path. The default backend keeps its existing behavior.
This relies on a trusted installation directory; it does not defeat same-owner
file replacement between signature verification and loading.

The resulting observer reads the retained Session's Adapter handle, obtains its
actual LUID, then queries the kernel GUID, index and alias. It never uses the
binding's cached requested GUID as proof. `identity()` checks that snapshot;
`observe_ipv4()` additionally checks current interface UP state and the requested
preferred, valid IPv4 address. A mismatch or kernel error permanently invalidates
all clones. Weak references do not keep the Session alive. Owner drop revokes
before shutdown, receive-worker exit/unwind revokes, and async send errors and
explicit async shutdown revoke. These are observations when called, not a
background interface/secure-peer monitor.

The EasyTier patch adds the distinct `ff-windows-session-observer` feature. Its real
`VirtualNic::create_tun` path explicitly requests this mode and loads `wintun.dll`
beside the current executable. `create_dev` captures the resulting observer;
`NicCtx::observe_windows_session` exposes only local read-only evidence. The feature
suppresses this file's automatic self/interface firewall rules, firewall removal,
obsolete-registry cleanup, DHCP/NetBIOS registry changes and profile-category edit.
It does not disable every EasyTier system change: interface configuration, routes,
other DNS/registry paths and the official driver's own effects remain separate.

**Requests creation does not mean exclusive creation.** Official Wintun 0.14.1
can rename another adapter on a name collision, install/update its driver, and
queue global orphaned-device cleanup when an Adapter is closed. No pre-check or
random GUID changes these API semantics. A future actual development run requires
an explicitly disposable Windows environment and reviewed configuration. These
patches do not grant ordinary Public admission, implement local owner IPC, attest
a secure peer, or issue the UI-08 live transport lease.

## Reproduce the source and lifetime check without a driver

Obtain the two exact source checkouts using the project's normal dependency
policy. The verification entry reads only pinned files, copies them to a new
directory, applies both patches, checks resulting SHA256 values, runs the six
production-linked pure Rust lifetime tests, and reverses both patches to their
original hashes. It does not modify the input checkouts or download anything.

```sh
python3 verify.py \
  --tun-source /absolute/path/to/pinned-rust-tun \
  --easytier-source /absolute/path/to/pinned-easytier \
  --work-dir /absolute/path/to/new-local-receipt-directory \
  --rustc /absolute/path/to/existing/rustc
```

For backend typechecking, apply the tun patch in a separate complete source
checkout, resolve a separate Cargo lock against the pinned EasyTier dependency
versions, then retain that lock and run:

```sh
cargo check --locked --features async --target i686-pc-windows-msvc
```

This command was actually successful locally; it does not link or execute a
Windows program. The initial full-caller check was syntax-only; the separate
hosted entry below has since compiled and linked the real Windows feature.
Full builds must explicitly resolve the patched `tun-easytier` source in
Cargo (for example a reviewed local `[patch]`), record the resulting lock and new
binary identity, and combine the separately reviewed secure-peer patches. The
existing Linux guard3 recipe, official archive/member hashes and runtime admission
remain unchanged. No dependency binary or Wintun DLL is included here.

The [receipt](receipt.json) binds patch/source/log hashes and evidence boundaries.
Retain the tun-easytier WTFPL notice and EasyTier LGPL-3.0 corresponding-source
obligations. These source patches do not resolve Wintun binary redistribution
provenance or substitute a source license for the bundled DLL's terms.

## Separate full Windows build entry

[ci_easytier_windows_build.py](../../../scripts/ci_easytier_windows_build.py) and
its [independent workflow](../../../.github/workflows/easytier-windows-build-only.yml)
now assemble both source trees with the six existing secure-session/ingress/pin
patches, verify Cargo's actual local tun resolution and feature activation, and
build both real core/CLI targets. The
[Windows source lock](../../../scripts/easytier-windows-source.lock.json) records
all additional identities without altering the Linux lock or the earlier
two-patch receipt above.

[modern-windows-build.patch](modern-windows-build.patch) is a distinct additional
build-profile patch. Under `ff-windows-session-observer` only, it skips upstream's
Win7 thunk call, whose pinned implementation downloads compatibility archives
using `curl -k` without a digest. It does not alter normal upstream builds or
claim all build dependencies are source-closed. The candidate uses modern MSVC
and the installed Windows SDK; Win7 compatibility is outside this profile.

The builder restores only the hash-pinned x64 `Packet.lib` as an internal import
library retained only in the private build workspace. It verifies that the only
Cargo.lock edit is the tun path-source conversion, checks all prepared bytes before and after compilation, and inspects PE output
without executing it. Held-input provenance remains open. Its artifact allowlist
contains JSON receipts only, with no executable, DLL, driver, raw compiler log,
source archive or dependency cache.

Actual hosted run [39927010635](https://github.com/caox233/FifthFreedom/actions/runs/39927010635)
passed all 14 source/receipt regressions, locked paired metadata and the full
MSVC Release core/CLI build. The
[verified receipt](../../evidence/hosted-windows-easytier-41fe262.json) binds
branch source and PR merge checkout separately, all nine ordered patches,
source/lock identities, the three-member artifact digest and both runner-reported
AMD64 PE32+ identities. No runtime executable was uploaded or started, and no
driver/session, secure-peer or production admission claim follows from compilation.
