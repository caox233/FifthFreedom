# Finite owned core / Windows NIC experiment

Status: implemented and independently reviewed; actual hosted execution pending.
The entry point is `tests/OwnedCoreRuntime`, linked to the production
`OwnedCoreLaunchInputs`, dependency pins, atomic job/process owner, descriptor,
read protocol and read channel. It executes the actual core when its runtime
inputs and disposable hosted environment pass. It is not a Public provider.

The separate workflow/adapter builds the new inert thirteen-patch source in the
same Windows job, checks the actual core and CLI import tables, obtains the
reviewed official Wintun bytes, and stages only the exact core image and DLL.
The helper embeds both `scripts/easytier-windows-inert-owned-read.lock.json` and
the single reviewed `inert-profile.toml`. A manifest binds their byte hashes,
the exact prepared source tree, the actual core length/hash and one random probe
ID. The older twelve-patch image cannot satisfy this recipe identity. Image,
configuration and exact adjacent official DLL pins remain held by the production
owner until its whole job has drained. The helper neither installs unrelated
dependencies nor copies a donor configuration.

The only template changes are a fresh canonical `instance_id` and a fresh
`flags.dev_name` of `ff-` plus a nonzero lowercase GUID. Configuration creation is
exclusive; an existing file is rejected. The fixed native source is
`10.254.253.1/24`: the retained configuration getter converts `/32` to `/24`, so
the fixture explicitly describes the actual behavior. Preflight rejects overlap
with the entire `10.254.253.0/24` from existing IPv4 addresses or nondefault
routes. Existing default routes are retained and compared, not deleted.

The inert source/configuration disables peer listeners, peers, discovery,
TCP/UDP/IPv6 STUN, hole punching, proxy/DNS and forwarding paths. The actual TUN
remains enabled. The production launch primitive adds only the fixed
`--rpc-portal 127.0.0.1:15998`; a port conflict fails. Port zero would cause an
upstream wildcard availability scan and is not used. This is not a zero-socket
claim. The wrapper also requires reviewed, exact staging-path firewall blocks
on the disposable VM. Those blocks are development containment, not production
PID-specific network admission. Their removal requires the matching successful
exact-job-drain receipt; an unknown drain keeps the blocks for VM disposal.

## Actual execution and cleanup order

1. Require x86 execution on an elevated GitHub-hosted Windows Server 2025 image.
   The wrapper separately checks no existing present/nonpresent Wintun devices,
   package/service/registry/file state, and preserves unrelated PnP identities.
2. Read bounded IPv4 interface/address/route tables from Windows. Verify interface
   GUID/LUID/index/alias again using independent scalar `ConvertInterface*` APIs.
   Reject unknown data, conflicts and a preexisting requested identity.
3. Use the real `OwnedCoreReadChannel.StartAsync` and its exact child handles,
   parent anchor and one-attempt pipe. The actual Instance creates its NIC.
   Check the returned GUID/LUID/index/alias and preferred, non-skipped `/24`
   source against a fresh kernel sample. Require all foreign interface/address/
   route identities unchanged. Only the owned subnet, own-address/broadcast
   routes and exact on-link `224.0.0.0/4` and `255.255.255.255/32` kernel routes
   are allowed for that owned LUID/index. No new default route is allowed.
4. Query `10.254.253.2` through the same real channel and require the explicit
   unavailable response. This is a source-observation negative control; it is
   neither ingress isolation nor peer authentication.
5. Sample that the exact NIC is still present while the exact core is alive,
   then invoke `EndReads`. Only a new sample after that trigger can establish
   absence while the same retained core process is still alive. A pre-trigger
   absence fails. The shared observation budget is eight seconds. Queries are
   non-atomic; API failure or identity inconsistency cannot mean "absent".
6. In every return path, end reads and dispose the production channel, draining
   its exact job and descendants. Only successful disposal sets
   `exact_owned_job_drained=true`. A failed startup does not manufacture drain
   proof. Then compare the entire original kernel inventory, verify ordinary
   exclusive file access is restored, and remove only this attempt's created
   configuration. No process-name kill, ACL change or driver uninstall occurs.

The retained CLI may naturally exit after its Instance finishes. If it exits
before a post-trigger NIC-absent/core-alive sample, the strict observation fails
with `CORE_EXITED_BEFORE_NIC_ABSENT`. That result does not by itself prove a NIC
leak. Final kernel restoration and exact-job drain remain separate fields. The
probe does not modify the production lifecycle to manufacture an observation
window, and process termination never substitutes for the normal cleanup proof.

The official Wintun API may install its driver and perform its documented adapter
collision/orphan cleanup operations. The previously reviewed clean-VM preflight
and unrelated-device comparison bound this experiment; they do not erase those
side effects or prove permission for deployment. Driver package/service remnants
are recorded rather than globally uninstalled. All runtime activity remains
pending; Public, game launch, remote peers, route admission and full package
publication remain closed.

## Interface and evidence

`OwnedCoreRuntime.exe --manifest <owned staging/runtime-input.json>` emits exactly
one fixed-schema JSON object and no raw exception text. The manifest has exactly
`schema`, `probe_id`, `core_path`, `core_bytes`, `core_sha256`,
`source_tree_sha256`, `build_recipe_sha256` and `inert_profile_sha256`. The image
must be `ff-core-runtime-<probe_id>/easytier-core.exe`. The report uses schema
`ff-owned-core-runtime-v1`, binds the probe, actual core digest, source/recipe,
profile and generated Instance, and separates start, HELLO, observations, read
closure, live-core NIC absence, job drain and final inventory restoration. Failed
runs retain fixed original and cleanup error codes. No live profile, private
path, adapter name, PID, token, raw child output or binary is published.

`--contracts` runs 22 portable input/table/ordering cases. The last three cleanup
ordering cases directly exercise the function used by the actual runtime:
absence before the trigger is rejected, success needs a fresh post-trigger
sample, and process death cannot become live cleanup evidence. A controlled-clock
negative also rejects a synchronous query returning after the shared budget,
even when its late sample says absent. Synthetic table
rows test bounded decoding and classification only, not a Windows kernel or NIC.
`KernelAbi.rs` independently checks the consumed offsets/strides against actual
`windows-sys` declarations by MSVC-target metadata compilation. The local AMD64
and i686 checks and C# x86 compilation are not execution of Windows APIs.
Actual hosted x86 table conversion checks, full inert core build, driver/runtime
execution and exact cleanup receipts remain necessary.

API references: [GetIfTable2](https://learn.microsoft.com/windows/win32/api/netioapi/nf-netioapi-getiftable2),
[GetUnicastIpAddressTable](https://learn.microsoft.com/windows/win32/api/netioapi/nf-netioapi-getunicastipaddresstable),
[GetIpForwardTable2](https://learn.microsoft.com/windows/win32/api/netioapi/nf-netioapi-getipforwardtable2),
[FreeMibTable](https://learn.microsoft.com/windows/win32/api/netioapi/nf-netioapi-freemibtable).
