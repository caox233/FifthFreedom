# Official Wintun input and bounded retained-session probe

Status: official input and license retrieved; finite development probe authorized
for implementation, **not yet executed**. Ordinary Public stays disabled.

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
are retained in ignored `.local/evidence/cloud/ui09-runtime-inspection/`; no
binary is added to Git or executed. The [published notice](../third-party/Wintun-0.14.1-Prebuilt-LICENSE.txt)
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

No current receipt proves Wintun driver installation, kernel policy acceptance,
session startup or teardown on this runner. Missing privilege, unknown device
inventory, rejected signature or blocked driver load must produce a nonzero
result, not a successful skip. This Linux cloud workspace cannot substitute for
that Windows execution.

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
