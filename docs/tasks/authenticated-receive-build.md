# Authenticated receive: independent Linux candidate build

Status: **ready-for-agent** — clean-source preparation and orchestration checks
passed locally; the new hosted 44-contract/full Release build has not run yet.
Ordinary Public startup remains closed. This continues the owned transport
prerequisite within the full migration, without changing the currently validated
Linux guard3 or Windows source/runtime identities.

The independent entry is `python -B scripts/ci_easytier_authenticated_receive_build.py`.
The existing build-only workflow now has a separate `authenticated-receive-source`
job, target directory and three-receipt artifact. The old `ci_easytier_build.py`
entry still selects the original guard3 recipe and its original 35 checks. No
runtime matrix, Windows recipe, official archive/member pin or dependency
publication decision is changed.

The [new recipe](../../scripts/easytier-authenticated-receive-source.lock.json)
preserves the original six ordered guard/test patches, fixed upstream commit and
canonical archive, Cargo.lock, 14 explicit omissions, five materialized links,
notices, compiler/target and closed admission fields. It appends the two reviewed
[authenticated receive patches](../dependency-patches/easytier-2.6.4-authenticated-receive/README.md).
The entry validates that exact extension against the unchanged baseline and the
reviewed source evidence. It reuses the existing fixed Git-object archive fetch,
strict source preparer, locked Cargo runner, ELF-header checks and bounded receipt
writer. The common builder now accepts explicit check selections while retaining
its old default; it also verifies this recipe's whole-tree digest before and after
compilation. No alternative downloader or fallback to the old source is added.

| Identity | SHA-256 |
| --- | --- |
| New recipe | `4ee0affd0efe9fd8f9da394ef616787506870bcbeeff65873b771390e1385a2d` |
| All 699 prepared source files | `b0a7d3cf802d3de1f3741943ccd48db02cf883fe68899e9cd7f636627ec74e0f` |
| Eight ordered patches | `ef934c74ddadd714f681b1c5406b40272724b9940a070f7a6f1229111cfbafc1` |
| Unchanged Cargo.lock | `8251ad8841694cccdeece7e086b7750d66ec93500c7b9c9879e43791fd53dca9` |
| Restored guard3 source tree | `60c85f25267ce640bb27901b167047722478fd91d39693a1609e925e28440181` |

CI must execute these actual production-linked Rust filters with zero failures
and zero ignored tests; partial, duplicate or absent success summaries fail the
build. The seven new Python orchestration/recipe tests use inert compiler-output
fixtures and do **not** count as the Rust checks:

| Actual Rust filter | Required passing count |
| --- | ---: |
| `fifth_freedom_receive_` | 15 |
| `fifth_freedom_secure_profile_contract` | 4 |
| `fifth_freedom_ingress_contract` | 10 |
| `peers::secure_datagram::tests` | 9 |
| `peers::peer_session::tests` | 2 |
| `ff_explicit_pin_contract` | 3 |
| `peers::peer_conn::tests::peer_conn_secure_mode_data_roundtrip` (`--exact`) | 1 |

After these 15 new plus 29 retained checks pass, the job compiles core and CLI with
Rust 1.95.0, `--locked`, the explicit GNU target and unchanged default/ring features.
The job never starts either resulting runtime or uploads executables, dependency
archives/caches, raw compiler logs or private configuration. Only the explicitly
named run, source and build JSON receipts are uploaded. Build receipts bind exact
source/patch/lock identity, check counts/log hashes and static ELF output hashes;
`runtime_executed`, `runtime_binary_uploaded` and `public_startup_allowed` stay false.
A failed build preserves failure evidence rather than publishing a success receipt.

Local verification used the original pinned 13,066,240-byte canonical archive for
two fresh preparations. Both produced identical receipts and the whole inventory
matched the already tested source from the prior patch's actual 15+29 execution.
On the second fresh tree, reversing only the two new patches restored every
original guard3 file; reapplying them restored the new inventory. The new seven
orchestration tests, six existing build tests, eleven preparation tests and
workflow actionlint passed. These are source/orchestration results, not a new
full compiler or hosted run. The preserved local preparation receipt is under
`.local/evidence/cloud/authenticated-receive-build/local-preparation.json`.

## Next bounded active-challenge path (read-only finding; not implemented)

The existing production Data path already has the required transport route:
`NicCtx::do_forward_nic_to_peers_ipv4` calls `PeerManager::send_msg_by_ip`, which
marks the packet as `Data` and sends it through the actual peer/session filter.
On return, `PeerSessionTunnelFilter::after_received` validates the current peer,
local destination, AEAD tag and replay window before recording the new observation.
`PeerManager`'s `NicPacketProcessor` only hands fully decoded Data to the NIC.
An explicitly owned, source/interface-bound nonce exchange can use this path
without inventing an EasyTier plaintext control-frame exception.

However, the current observation is only a connection/session-bound receive age;
it has no payload/nonce correlation API. Combining any recent observation with an
unrelated successful socket reply cannot prove that the reply crossed that exact
session. The next source change must bind a one-use unpredictable challenge and
its expected peer/direction to the exact accepted Data packet and same session
installation/revision, with a bounded monotonic deadline and invalidation on
close/rekey/owner loss. A delayed reply, prior nonce, other connection, or any
replayed/tag-invalid packet must not complete the new exchange. The current local
sync/grace policy must also remain explicit: passive receipt does not independently
prove the remote endpoint has completed a newly synchronized key epoch.

Existing `PeerConnPinger` Ping/Pong bypasses session encryption; CLI auth/key
fields are handshake history. Neither is a substitute. The development network
probe's constant-payload TCP/UDP echo proves bounded reachability and negative
pin behavior, not a one-use active challenge. Its current worker only projects
CLI/session ownership separately. This slice changes none of those runtime tests,
creates no lease factory, and grants no launcher/native admission. The future
challenge still needs the actual owned-core/kernel-session chain and expected-peer
policy before it can contribute to a renewable lease.
