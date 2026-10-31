# CI — distinct inert Windows core/NIC experiment

Status: ready-for-agent. Offline implementation and focused checks are available;
the new Windows build and runtime have **not run**. GitHub API authentication
returned HTTP 401 and the normal development push failed before this batch.
The last verified remote revision remains
`fe6e3cd5992888273bada9c7810a264cf8d54630`. No credential retry, alternate identity,
release, or deployment is part of this task.

The independent recipe is
[`scripts/easytier-windows-inert-owned-read.lock.json`](../../scripts/easytier-windows-inert-owned-read.lock.json).
It retains the reviewed Windows owner-read/no-Packet twelve-patch inputs and
appends the single reviewed inert production-plus-contracts patch. The former
twelve-patch image lacks the TCP STUN setting and is never an accepted input to
this runtime experiment. The exact template disables the reviewed discovery,
peer, listener, relay and proxy paths; only a fresh instance and adapter name may
be inserted. Its fixed address is `10.254.253.1/24`, with conflict checks across
the whole `10.254.253.0/24`; the sole RPC argument is the nonzero loopback
`127.0.0.1:15998`.

The new [workflow](../../.github/workflows/windows-owned-core-runtime.yml)
prepares and compiles this source in one disposable Windows 2025 job. It requires
the thirteen inert contracts, eight owner-read contracts, the retained eight
interface and eight typed-session contracts, selected dependency graph, actual
core/CLI compilation and both normal/delay no-Packet import checks. The builder
does not run either output. The runtime adapter subsequently verifies the exact
checkout, run and attempt, the entire prepared tree, recipe/profile identities,
both compiled PE identities, and the official Wintun archive/member byte pins.
It copies the core to a fresh staging directory; the original build output is
preserved. No runtime binary, archive, private manifest, config, raw process
output or firewall plan is uploaded.

The fixed x86 managed probe embeds the new recipe and template and must pass its
22 portable input/inventory/cleanup-order contracts before the actual experiment.
The experiment requires real owned process startup, the correct source NIC's
kernel identity, a wrong-source rejection, and a fresh NIC-absence observation
after EndReads while the exact core is still alive. Final exact Job drain,
unchanged foreign kernel identities, released input pins and owned config removal
are separate required results. The runtime record is a single strict JSON object;
known failures remain failures, including a failure whose exact Job was drained.

The temporary firewall rules use the exact staging application path. They are
**not PID isolation, product WFP policy or packet-behavior proof**. All existing
firewall profiles must already be enabled and local rules available. Prepare
writes a durable exclusive plan before adding the two inbound/outbound Block
rules; ActiveStore readback must match the exact application and all filters,
with PrimaryStatus `OK` and EnforcementStatus `Full`. Arm writes a durable marker
before managed dispatch. Restore removes only the exact owned rules and only
after a matching runtime/source/profile/core identity reports successful exact
Job drain. A partial Prepare can be cleaned before Arm; once armed, a missing or
invalid drain record keeps the blocks and fails. Unknown modified rules are not
removed. The original policy snapshot must match after removal.

The workflow's `always()` recovery covers runtime failure/cancellation, but cannot
promise cleanup after a hard runner/controller loss. PersistentStore rules may
then remain until the disposable VM is discarded; VM disposal is not reported as
successful Restore. Driver packages/services may remain under the existing
no-uninstall boundary. Owned adapters must be absent and unrelated device
identities unchanged. No peer traffic, encrypted session freshness, game, full
package or production admission is certified.

Local verification:

- Eight builder unit controls passed, including real patch application, old
  identity rejection, retained full-build delegation, count mismatch, source
  mutation and evidence overwrite rejection.
- Independent source reconstruction produced 704 EasyTier plus 49 paired TUN
  files, tree `8cf92686e0cce91f6db26dccdb3ca36f4e776b21254da036939fc17e9992c584`
  and ordered patch manifest
  `bbad0f9c745a018fb833ad0ced41a1e79c3253cf947a98288d5763d03f27f3eb`.
  The original twelve-patch tree remained unchanged. The first local audit used
  a relative Git ceiling and correctly failed the whole-tree comparison; the
  wrapper now resolves the work directory before applying the patch. No local
  Windows compilation or core execution was inferred from reconstruction.
- Thirteen adapter controls passed for exact producer shape, wrong-run/old-source
  rejection, cleanup failure, bounded diagnostics, driver inventory and ordered
  restore dispatch. The new workflow passes actionlint.
- PowerShell is unavailable in the current Linux workspace. The firewall script
  has source review only; actionlint does not parse or execute PowerShell, and
  neither local Python tests nor the earlier separate Wintun session experiment
  establish this new Windows firewall/core/NIC result.

Next action: after official credentials are restored, push the reviewed batch
normally, retain the first run of this distinct workflow, and inspect its exact
sanitized build/runtime receipts. Do not reuse the earlier twelve-patch build or
separate IPC test as a successful inert core runtime result.
