# Official Wintun input and bounded retained-session probe

Status: official input and license retrieved; **two real retained-session cases
passed on a clean disposable hosted Windows 2025 VM**. Ordinary Public stays disabled.

## Actual input evidence

An ordinary HTTPS fetch of the [official Wintun 0.14.1 archive](https://www.wintun.net/builds/wintun-0.14.1.zip)
succeeded after the previously recorded proxy denial. The ZIP is 750540 bytes,
SHA256 `07c256185d6ee3652e09fa55c0b673e2624b565e02c4b9091c79ca7d2f24ef51`.
This is the observed official download digest, not an independently published API
digest. Its amd64 and x86 DLLs match both the length and SHA256 already pinned in
the EasyTier audit; those earlier DLL pins were not changed.

| Official member | Bytes | SHA256 |
| --- | ---: | --- |
| `wintun/bin/amd64/wintun.dll` | 427552 | `e5da8447dc2c320edc0fc52fa01885c103de8c118481f683643cacc3220dafce` |
| `wintun/bin/x86/wintun.dll` | 550928 | `d694fa46ab4cfebcb2632d094c7aa97278eef2f8052438621766d863ae98a931` |
| `wintun/LICENSE.txt` | 5431 | `183adac21e7d96c508c8fd34d394b7b6708bc81564ad1bad61ab66143a008cd2` |
| `wintun/include/wintun.h` | 10362 | `510a5984fbf73efd21a61ada60edfe05e1a38a77c8c47f6d62e0ab1cdbdd460f` |

The [receipt](wintun-official-binary-evidence.json) records all seven ZIP members
and the two previous DLL matches. The archive, original header/README and license
are retained in ignored `.local/evidence/cloud/ui09-runtime-inspection/`. Retrieval
executed no binary and added none to Git; the later authorized hosted run is
recorded separately below. The [published notice](../third-party/Wintun-0.14.1-Prebuilt-LICENSE.txt)
keeps the archive's original CRLF bytes, with an exact-path `-text` Git attribute.

The license defines the exact downloaded DLLs as the Software, grants lawful use
subject to its terms, and calls the accompanying `wintun.h` interfaces the
Permitted API. It restricts modification/reverse engineering and permits
redistribution alongside other software only under its stated API-use conditions.
Obtaining this document closes the missing official-origin/terms input. It does
not establish that a final package satisfies every condition, that a binary was
rebuilt from source, or that Windows accepted its signature/driver. Npcap's
separate permission hold and the overall publication gate are unchanged.

## Runner capability that is actually established

GitHub's [hosted-runner reference](https://docs.github.com/en/actions/reference/runners/github-hosted-runners#administrative-privileges)
states that Windows VMs run as administrators with UAC disabled. The official
Windows 2025 image inventory contains the MSVC/Windows SDK tools needed to build
a user-mode probe. Their exact read hashes are in the receipt. The project's
earlier disposable-runner IIS setup also exercised elevated configuration, but
that is not a Wintun load test.

Before the probe below, no receipt proved driver installation, session startup
or teardown on this runner. The first successful run now supplies that narrow
evidence. Missing privilege, unknown device inventory, rejected signature or
blocked driver load still produce a nonzero result, never a successful skip.
Linux checks cannot substitute for this Windows execution.

## Approved minimal next execution

Build a separate bounded Rust probe against the **actual patched tun backend**
from [UI-09](ui-09.md), with `async`, default DNS support disabled, and a pinned
Cargo lock. It will call `Configuration::platform_config` with
`request_new_session`, `skip_config`, a minimal ring and the absolute verified
amd64 DLL path, then `tun::create`, `session_identity_observer` and `AsyncDevice`.
This is an executable consumer of the existing implementation, not a new
admission factory. It needs neither the full EasyTier runtime nor Packet/Npcap.

Run only in a fresh GitHub-hosted `windows-2025` job whose process is elevated.
Preflight must reject any existing present **or nonpresent** Wintun device,
existing Wintun driver state, requested-name collision, or incomplete/unknown
inventory. Use a fresh nonzero GUID and exact `ff-<32 lowercase hex>` name. The
DLL is loaded only after byte verification from an owned directory and ordinary
Windows signature checking; do not weaken Secure Boot, code integrity,
test-signing or trust policy.

The first two cases are deliberately finite:

1. Create a real session, require its retained-handle LUID and kernel GUID/index/
   alias to match, then drop its owner. All observer clones must remain revoked,
   and the adapter must disappear within the bounded cleanup interval.
2. Create a separate real session and async receive worker. Invoke the actual
   async shutdown path, require sticky observer invalidation, then drop the
   owner and require adapter disappearance. Do not infer worker termination just
   from an invalid observer; require the concrete cleanup observations as well.

There is no harness IP/route/DNS configuration, packet send, peer connection,
game launch or production deployment. Windows may perform its normal device
initialization; its effects must be measured rather than claimed absent.
Capture unrelated adapter identities before and after and require equivalence.
Never adopt, rename or delete an unrelated adapter. Parent cleanup acts only on
the exact child handle; a fixed deadline and bounded, allowlisted results prevent
an indefinite worker from appearing successful.

## Official effects that the wrapper cannot remove

The reviewed official 0.14.1 source shows that `WintunCreateAdapter` can install
or update the driver, a name collision can rename the conflicting adapter, and
`WintunCloseAdapter` queues orphaned-device cleanup. A random name and preflight
reduce collision exposure; they do not change those API semantics or establish
atomic exclusive creation. Hence the clean disposable-VM requirement and the
before/after identity check. Do not globally uninstall the driver to make the
receipt look clean; dispose the owned sessions and let the job VM be destroyed.
Only sanitized receipts should be uploaded, not DLLs, drivers, executables or raw
system inventories.

Own-interface address loss, down/rename, source routing, secure-peer monitoring,
owner IPC and a production UI-08 lease issuer remain separate work. These two
session cases cannot establish a complete live overlay or make Public ready.

## Implemented entry and actual local verification

The separate [workflow](../../.github/workflows/wintun-session-probe.yml) invokes
[scripts/ci_wintun_session_probe.py](../../scripts/ci_wintun_session_probe.py).
It triggers only for the narrowly listed probe/paired-tun changes in a pull
request, or by explicit dispatch once GitHub has registered it. It fetches the
exact UI-09 tun commit/tree, verifies the canonical archive and
patch recipe, then builds only the [tiny consumer](../../tests/native/wintun-session-probe/src/main.rs)
with its own locked dependencies. Cargo resolution must identify the local
patched tun manifest with exactly the `async` feature and its three implied
features; default DNS support, EasyTier and pcap dependencies are rejected. The
49-file prepared backend has SHA256
`832192192b329f78407db9e21982108753efcce97ee0399af40c5932e6420b47`.
The binary is bound to the same-run build by SHA256 before and after execution.

The [read-only environment script](../../scripts/ci_wintun_session_environment.ps1)
requires the official `win25` or `win25-vs2026` hosted image and an elevated
process. Both exact aliases designate Windows Server 2025. It enumerates
all PnP devices without `-PresentOnly`, reads Net-device hardware/service
properties, the driver store, system drivers, the Wintun service registry key and
system driver file. The original official INF names HardwareID `Wintun`, service
`wintun`, driver `wintun.sys` and Net class; these checks also conservatively
reject matching names/paths. Any enumeration failure is fatal. A pre-existing
package/service/file blocks the initial load even without a live adapter.
Afterwards, an installed driver may remain, but all owned adapters must be gone
and the unrelated PnP identity digest/count must match. No uninstall is attempted.

The Rust consumer additionally snapshots real kernel interface GUID/LUID/index/
alias identities. It holds a read-sharing file handle over the pinned DLL for the
whole operation, then relies on the existing production backend signature and
signer checks. The shutdown case uses `AsyncWriteExt::shutdown` and a five-second
`AsyncReadExt::read` EOF wait: the actual receive worker must release its sender.
This is **not** a claim that its `JoinHandle` was explicitly joined. After owner
drop, successful kernel table enumeration must prove the GUID/LUID absent within
ten seconds; lookup errors are failures. Both weak observers must remain revoked.

The parent bounds the exact probe child to 90 seconds, keeps each output pipe
within a fixed memory limit, and terminates/reaps only its own `Popen` handle.
Reader startup failure and cleanup failure cannot become success. A runtime
failure remains the primary result if postflight also fails. Receipts contain
only fixed codes, booleans, counts and hashes; adapter names, device IDs, raw
exceptions and build output are not uploaded. The workflow publishes exactly one
sanitized JSON file, never runtime binaries or the official DLL. The disposable
VM is the containment boundary for driver-install/global orphan-cleanup effects,
including an interrupted or failed probe.

Actual local commands/results before hosted execution:

- Fresh fixed-commit Git fetch/archive + real recipe application: 49 backend
  source files verified; official DLL extracted and hash checked, never loaded.
- `cargo +1.95.0 check --locked --offline --target x86_64-pc-windows-msvc`
  against that backend and the tiny probe: passed. This is a Linux-hosted type
  check, not a Windows link, signature or driver result.
- `python3 -B -m unittest discover -s tests/ci -p test_ci_wintun_session_probe.py -v`:
  14 passed, no skips. These cover dirty/nonpresent input refusal, false readiness
  receipts, unrelated identity changes, bounded real child processes, injected
  second-reader startup failure, immutable destinations, and paired resolution.
  They do not simulate or claim an actual driver session.

The next action after those local checks was to run the reviewed workflow on the
exact pushed revision,
retain its run/job IDs and sanitized receipt, and inspect the actual first
nonzero stage if any. Driver success cannot be inferred from these local results.

### First hosted result: exact 2025 image alias

[Run 39929836621](https://github.com/caox233/FifthFreedom/actions/runs/39929836621),
job `120335090572`, passed all 14 portable harness tests, then failed **before
source/build/driver activity** with `stage=HOST code=HOST`. Its
[official receipt](../evidence/hosted-wintun-b613861.json) is preserved with the
matching artifact ZIP digest. The job reports Windows Server 2025 and image
`windows-2025-vs2026`. GitHub's official image generator explicitly assigns that
image the `win25-vs2026` ImageOS value, whereas the first probe admitted only
`win25`. The exact two-value allowlist corrects this documented image distinction;
other Windows versions, unknown suffixes, self-hosted runners and non-elevated
processes remain rejected. No driver result is inferred from this failure.

The follow-up portable suite has 15 tests including both accepted official
aliases and negative host boundaries. The receive buffer also holds a full
65536-byte maximum packet: unexpected nonempty input still fails, without a
one-byte-buffer panic obscuring the reason. At that point no runtime packet had been observed; the next action was the first
actual driver/session run.

### Actual hosted result: both retained-session cases passed

[Run 39930667783](https://github.com/caox233/FifthFreedom/actions/runs/39930667783),
job `120337826134`, completed **successfully** for source `319bf445` and PR merge
checkout `1723ec2ad34b69a18ca5f9260ed9594488bd977a`. The
[success receipt](../evidence/hosted-wintun-319bf44.json) preserves the full bounded
JSON. Its downloaded ZIP SHA256 matches GitHub's official artifact digest
`ec4547d5eeb018122309f346de54ef3b447db8655d9bbe6b24ef9b141f485fd1`.
The source recipe/Cargo lock hashes also match that exact source revision.

This run compiled and linked the tiny consumer for AMD64 MSVC, passed all 15
portable boundary tests, performed the clean-device/driver preflight, and then
executed **both real lifecycle cases** through the patched production backend.
The official DLL matched the retained pin; normal signature/signer checks and
actual session creation succeeded. Both owner-drop and async-shutdown cases
returned the required revocation/cleanup results; the async worker released its
sender and returned EOF. There is no claim of an explicit worker join.

Before execution there were no Wintun devices (including nonpresent devices),
packages, services, registry entry or system driver file. Afterwards there were
zero Wintun devices, and one installed driver package/service plus the normal
registry/file state. That state was preserved; no global uninstall ran. The
seven unrelated PnP identities had the same digest before and after, and the
Rust kernel GUID/LUID/index/alias set also matched exactly. Both owned adapters
had disappeared within the finite cleanup interval.

The harness made no IP/route/DNS configuration or packet send, and published only
its sanitized receipt. This does not prove there were no normal Windows device
initialization effects. The result remains specific to a clean disposable VM and
the two owned session lifetimes. No encrypted peer, live source/route admission,
owner IPC chain, Public launch, game, Npcap runtime or final redistribution closure
is established here. The next useful implementation is the separate retained
session address/route and current-process/secure-peer capability chain; it must
consume this real lifetime without treating this receipt as a Public lease.
