# Public-network dependency identity and notice closure

Status: **ready-for-agent** — fixed inputs are identified and downloaded bytes
verified; license/packaging integration and real networking remain incomplete.
The launcher Public option remains unavailable. No driver was installed, no
EasyTier process was executed, and no game or previous deployment was contacted.

## Donor requirements and version differences

The retained `integration/scbl3/COMPONENT_VERSIONS.json` and launcher
`Services/PublicTunnelConfig.cs` both name EasyTier **2.6.4**. The Windows downloader
defaults to x86_64 and obtains the selected release's asset size/digest at download
time. Its Public tunnel requires `easytier-core.exe`, `easytier-cli.exe`,
`Packet.dll` and `wintun.dll`; the official package also carries a WinDivert driver.
The x86 game/launcher does not by itself select an i686 EasyTier process: these
are separate executable architectures. The i686 package was audited separately,
not silently substituted for the donor's x86_64 default.

The donor Linux manager's `external_easytier.py` calls
`https://api.github.com/repos/EasyTier/EasyTier/releases/latest` when installing a
missing managed runtime. It **does not pin Linux to 2.6.4**. Its integrity checks
validate the currently returned official digest; that is different from a stable
cross-platform version lock. New integration must explicitly consume a reviewed
fixed version before it can claim a matched client/server runtime.

Route Guard separately uses WinDivert **2.2.2**. Its old PowerShell downloader uses
the upstream `basil00/Divert` redirect and copies x64 DLL/driver files without a
size/digest check. None of these old donor download scripts was executed or edited
in this audit. Current `scripts/local-assets.json` and the Capture helper gap are
unchanged.

## Actual downloaded inputs

Official GitHub API release metadata and normal HTTPS downloads were inspected on
2026-10-23. Downloads remain in ignored
`.local/evidence/cloud/public-network-dependencies/`; no new third-party binary is
included in Git. API metadata identified these exact assets:

| Artifact | Asset ID | Bytes | SHA256 |
| --- | --- | ---: | --- |
| [EasyTier Windows x86_64 2.6.4](https://github.com/EasyTier/EasyTier/releases/download/v2.6.4/easytier-windows-x86_64-v2.6.4.zip) | 418406680 | 32,657,720 | `27af91e270e554709b048bd32327fefd2dfce5062ae1e8701af7550c6f525f84` |
| [EasyTier Windows i686 2.6.4](https://github.com/EasyTier/EasyTier/releases/download/v2.6.4/easytier-windows-i686-v2.6.4.zip) | 418406679 | 30,881,261 | `bf557daeccc5525d95b8a230c339d75554fb52d82d1e050e9a5202c92c02e09e` |
| [EasyTier Linux x86_64 2.6.4](https://github.com/EasyTier/EasyTier/releases/download/v2.6.4/easytier-linux-x86_64-v2.6.4.zip) | 418406688 | 25,506,523 | `61b659eaedba658fa66fe47d17e1426cdd77e5d02fa15fed447bb4357c09dfd6` |
| [WinDivert 2.2.2-A](https://github.com/basil00/WinDivert/releases/download/v2.2.2/WinDivert-2.2.2-A.zip) | 78510020 | 405,137 | `63cb41763bb4b20f600b6de04e991a9c2be73279e317d4d82f237b150c5f3f15` |

All three EasyTier downloads matched both the official API SHA256 and size.
WinDivert's older asset has **`digest: null` in the official API**: its size matched
metadata, while the listed SHA256 is a new observation of the official HTTPS
download, not a match against a previously supplied upstream digest.

EasyTier tag `v2.6.4` resolved to
`8428a89d2dabc94c97d370ec607c6ca142473626`; WinDivert `v2.2.2` resolved to
`1789526ecfb9ff5397c94f9f54c1a3dc2fb60440`. These are source references observed from
the official refs, not independently reproduced binary builds. The machine
record [public-network-dependencies.lock.json](../../scripts/public-network-dependencies.lock.json)
also pins the archive's required members and retains the distinction between
official digest comparison and first-observed digest.

## Drivers and notices actually inspected

**EasyTier:** the exact tagged [LGPLv3 license](https://github.com/EasyTier/EasyTier/blob/8428a89d2dabc94c97d370ec607c6ca142473626/LICENSE)
was downloaded and read. SHA256
`e3a994d82e644b03a792a930f574002658412f62407f5fee083f2555c5f23118`
matches the retained donor `THIRD_PARTY_LICENSES/EasyTier-LGPL-3.0.txt` byte for
byte. The tag's `easytier/LICENSE` points to that root license. The inspected
runtime ZIPs contain no license files; a future package must retain appropriate
notices and corresponding-source information. EasyTier's license does not
automatically license the bundled third-party DLLs.

**WinDivert:** the exact tagged [LICENSE](https://github.com/basil00/WinDivert/blob/1789526ecfb9ff5397c94f9f54c1a3dc2fb60440/LICENSE)
states the choice of **LGPLv3 or GPLv2** and includes the full LGPLv3, GPLv3 and
GPLv2 texts. It matches the downloaded ZIP's license exactly and is retained as
[WinDivert-2.2.2-LICENSE.txt](../third-party/WinDivert-2.2.2-LICENSE.txt), SHA256
`14a0cb5214d536e4fdae6aa3f5696f981eeda106cd026e9794bba489ee79d628`.
EasyTier's `WinDivert64.sys` and `WinDivert32.sys` are byte-identical to the
corresponding official 2.2.2-A drivers. This was a byte comparison, not Windows
Authenticode validation or driver/runtime execution.

**Wintun:** both inspected EasyTier Windows packages identify `wintun.dll` as
**0.14.1** in their PE version resources. The x64/i686 DLLs match the third-party
blobs in EasyTier's fixed source revision. The official Wintun [0.14.1 source
README](https://github.com/WireGuard/wintun/blob/bfef136abfa1665c2592be09a7e383d646cdbe6e/README.md)
and [COPYING](https://github.com/WireGuard/wintun/blob/bfef136abfa1665c2592be09a7e383d646cdbe6e/COPYING)
were read. They explicitly distinguish GPLv2 source from a more permissive
prebuilt-binary license contained in the official ZIP. The ordinary HTTPS request
to `https://www.wintun.net/builds/wintun-0.14.1.zip` was denied by the environment's
proxy (`CONNECT 403`). Thus the exact original binary ZIP and its terms are still
unverified; the source GPL is not substituted for the missing binary license.

**Packet.dll / Npcap:** EasyTier's x86_64 package contains Npcap **1.79**, while its
i686 package contains **1.81**, according to the PE version/copyright resources.
Both DLLs match EasyTier's fixed third-party source blobs. The exact upstream
[1.79 license](https://github.com/nmap/npcap/blob/a3d510b8f18c465ce6719df2546c71c083cd7149/LICENSE)
and [1.81 license](https://github.com/nmap/npcap/blob/2e7fa35aa7fdfebcad5795bbca4b35853779d0c5/LICENSE)
were read. They state that Npcap is not open source and “may not be redistributed
or used in other software without special permission from the Nmap Project”; they
also recognize additional granted rights. The exact hashes and source refs are
in the lock. The additional official OEM page at `https://npcap.com/oem/redist.html`
was denied by the proxy. The project owner's earlier authorization statement
does not establish independently verified redistribution rights for these newly
inspected Npcap files. They remain outside Git and candidate packaging until the
specific rights or an independently reviewed replacement/distribution path is
established.

The two denied official hosts have been added through the formal environment
configuration draft. The draft requires publication; the current network was not
changed. No proxy, DNS, credentials or TLS checks were bypassed. Independent
official GitHub source-license reads do not claim to retrieve the denied binary
license or OEM page.

## Repeatable offline verification

```sh
python3 -B scripts/public_network_dependencies.py \
  --cache .local/evidence/cloud/public-network-dependencies
python3 -B -m unittest discover -s tests/build \
  -p test_public_network_dependencies.py -v
```

The verifier reads the fixed lock and cached ZIPs, checks sizes/SHA256, required
member identities, safe bounded archive layouts and retained notice hashes. It
does not download, extract, run, install or modify a network runtime. Exit 0 means
the locked bytes were verified; `closure_complete`, `publication_approved`,
`runtime_executed` and `installed` remain false. Missing cached inputs exit 2;
identity/layout/notice failures exit 1. The source tests use synthetic ZIPs and do
not download third-party files in CI.

Executed results: **4/4 downloaded archives and both retained notices verified**,
exit 0; **8/8 focused tests passed** on Linux. The initial command rejected the
valid `x86_64` identifier because its new validation expression omitted `_`;
that implementation defect was corrected and a clean-checkout test now exercises
the complete actual lock with an empty cache. It reports the four missing inputs
without downloading them. Raw download metadata, member hashes, license copies
and check receipts remain in the ignored audit directory.

Review also tightened the verifier to reject nonregular cache entries before
opening them and inspect ZIP members from the same bounded in-memory bytes whose
archive digest was checked. A regression replaces the on-disk file after the
checksum read and confirms the member checks still inspect the original verified
snapshot, with no second read of a potentially changed archive.

Next work is to integrate the fixed-version selection and explicit operator
configuration into a reviewed Public adapter and Linux service, satisfy the
remaining third-party notice/rights gates, then perform the separately authorized
Windows driver and real network acceptance. A pinned download alone does not
establish tunnel compatibility, game behavior or a completed public server.
