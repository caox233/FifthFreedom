# Frozen-asset dependency closure

Status: frozen loader identified as Ultimate ASI Loader v9.7.0 Win32; the exact
FusionFix ASI source/build and capture-helper source rebuilding remain open. This record does not change
asset pins, repack archives, release software or independently verify the owner's
redistribution authorization.

## FusionFix: preserved input and confirmed upstream relationship

Both retained archives are byte-identical:

- `src/launcher/src/Assets/FusionFix.zip`
- `integration/scbl3/client/ScblPublicLauncher/EmbeddedFiles/FusionFix.zip`

Each is 1,348,213 bytes, SHA256
`b807d01b849ca7aead6c04a43eaa2924809d0741e7007b907e00da868f33a7ff`,
and contains 14 files. This matches `scripts/local-assets.json` and the production
`FusionFixDeploymentService.AssetSha256`. Neither archive contains a license,
notice or readme. The archived configuration references 5th Echelon's startup
integration; that reference alone does not identify the binary build version.

Official upstream evidence obtained using the system `gh` CLI with the
environment's unchanged network configuration. The asset metadata below came
from `gh api repos/ThirteenAG/WidescreenFixesPack/releases/assets/610110710`;
it was not inferred from a release-page label. This records that command's
successful response, not a claim that direct API access is generally available.
No proxy, host, DNS or TLS settings were changed for this investigation.

- Project: [ThirteenAG/WidescreenFixesPack](https://github.com/ThirteenAG/WidescreenFixesPack).
- Release: [Splinter Cell Blacklist Fusion Fix, tag `scb`](https://github.com/ThirteenAG/WidescreenFixesPack/releases/tag/scb).
- The observed `master` and `scb` refs resolve to
  `708f7528fb8b54c79d4ac3c6071ea7ccc01ae162`. This is the observed upstream
  revision, **not a proven source revision for the frozen binary**.
- Current [release asset metadata, ID 610110710](https://api.github.com/repos/ThirteenAG/WidescreenFixesPack/releases/assets/610110710)
  identifies `SplinterCellBlacklist.FusionFix.zip`, 8,266,614 bytes, SHA256
  `e7ab5a39f4861e90f1ea56c6f63321823f4d20b154977f563af3a4e34f5feb44`.
  The asset was downloaded from the official release URL and its digest matched
  the GitHub metadata before member comparison. It was inspected only, not run
  or copied over the retained input. The rolling `scb` release can change; the
  asset ID and digest identify what was actually compared.

**Eleven of the fourteen files match the official current release byte-for-byte**:
all ten XML files under `src/SYSTEM/update/Data/ExtractionWaveConfigs/` (including
its `Random/` subdirectory), plus `src/SYSTEM/update/difficultyconfiguration.ini`.
This establishes a concrete relationship to the upstream distribution. The three
remaining files differ:

| Archive member | Frozen SHA256 | Compared upstream SHA256 |
| --- | --- | --- |
| `src/SYSTEM/scripts/SplinterCellBlacklist.FusionFix.asi` | `d5e63ea3ee00449ac6afcffd82a08d673a0bebc855a6d42b3b889ded9b8677c9` | `924fb076866ff8245f5e64f426782eaccaeaa2fb4791e8dc428f0184208be73c` |
| `src/SYSTEM/scripts/SplinterCellBlacklist.FusionFix.ini` | `7afff9f1ca632991fb4cbf965e4810e3480935436e386bb782c41f93c4c32252` | `e8fd345ac9c4e584cf0c230798496b205750dd63ffd1c03fa10fe37bd70a9c49` |
| `src/SYSTEM/version.dll` | `ad5d8d449ce305caf62cb17d8698ebf17e94f9120ee993d0dd85211dfe14f8dd` | `d5a059aa467a7a7127c8f6169f79fa63ff0f55986ee9eb2fd9a281bebf2aa2e6` |

That rolling-release comparison did not identify the frozen binaries. The loader
has since been matched to a separate exact official release below; the ASI's
source/build identity remains unassigned. Replacing either binary with the
current upstream release would be a behavior change requiring separate review
and regression, not a way to close this documentation gap.

## Upstream source and license notice

The observed upstream source lives under
[`source/SplinterCellBlacklist.FusionFix`](https://github.com/ThirteenAG/WidescreenFixesPack/tree/708f7528fb8b54c79d4ac3c6071ea7ccc01ae162/source/SplinterCellBlacklist.FusionFix),
with distribution data under
[`data/SplinterCellBlacklist.FusionFix`](https://github.com/ThirteenAG/WidescreenFixesPack/tree/708f7528fb8b54c79d4ac3c6071ea7ccc01ae162/data/SplinterCellBlacklist.FusionFix).

The exact upstream [`license`](https://github.com/ThirteenAG/WidescreenFixesPack/blob/708f7528fb8b54c79d4ac3c6071ea7ccc01ae162/license)
at that revision states **MIT License, Copyright (c) 2018 ThirteenAG**. Its
unmodified text is retained separately in
[WidescreenFixesPack-upstream-MIT.txt](third-party/WidescreenFixesPack-upstream-MIT.txt),
SHA256 `389eef0fb81f8b3bc7aca2025da43cf007acfb390a70f49439d269e51de0b8dc`.

This is a verified notice/source reference for the cited upstream project. It is
not a declaration that every file in the frozen archive, embedded loader,
game-derived data, SDK input or other bundled third-party material is covered by
that license. The ASI attribution and additional binary terms remain open; the
following loader evidence narrows the gap. No notice was inserted into the pinned archive, so its integrity check
continues to refer to the original bytes.

## Exact loader release and embedded inputs

The retained `src/SYSTEM/version.dll` is **2,376,192 bytes** with SHA256
`ad5d8d449ce305caf62cb17d8698ebf17e94f9120ee993d0dd85211dfe14f8dd`.
It is byte-for-byte identical to the sole `dinput8.dll` member of the official
[Ultimate ASI Loader v9.7.0 Win32 release](https://github.com/ThirteenAG/Ultimate-ASI-Loader/releases/tag/v9.7.0).
This is a renamed identical member, not a version-string inference. Release ID
277335045 / asset ID 341394264 identifies `Ultimate-ASI-Loader.zip`, 912,116 bytes,
SHA256 `768d3ad612b8bbf16189c8dddd9626bb8d26eb7626a90339703ae0adb2c71390`.
The download matched the official API digest before member comparison. Its tag
resolves to `f9f6ccf4723f42be4bf6e051a82d2ec44b29c9fc`.

The root PE `RT_VERSION` resource at `[16,1,1024]` reports file/product 9.7.0 and
`GitHash=f9f6ccf`, consistent with that tag. The checker follows the PE resource
directory; it does not search arbitrary UTF-16 strings. That distinction matters:
the loader contains other DLLs with their own version/company strings. The ASI's
root resource instead has fixed versions 1.2.0.0/3.0.0.0 and string versions
1.0.0.0, without the loader's GitHash. Those generic/conflicting fields do not
identify an exact ASI source revision or authorize assigning it a semantic release.

The exact tagged Win32 resource script embeds three DLLs. Each was compared to
the corresponding root loader RCDATA bytes, and all three match:

| Tagged input | RCDATA path | Bytes | SHA256 |
| --- | --- | ---: | --- |
| `vorbisfile.dll` | `[10,101,1033]` | 37,376 | `4179f04a7b9134761f948fc81ae9b499fd7e97d69aac8ccd926354c1fb8df876` |
| `wndmode.dll` | `[10,103,1033]` | 778,752 | `a3a4ca76a515801087d92ad9fc6c988bc0258691bf8f224c6ab82b941adfa1b0` |
| `binkw32.dll` | `[10,105,1033]` | 215,552 | `9a3ab8f5f346ce36e468120194d467780bb9bf0233dda511966998b6f514f823` |

This establishes their exact byte provenance. **Independent redistribution terms
for those three binary inputs have not been verified.** In particular, a DLL
filename or the loader's MIT notice does not establish that embedded binary's
license. The owner's existing authorization statement remains separately recorded.

The tagged build recipe and exact submodule commits identify seven available
notices, copied without modification into `docs/third-party/`:

| Component | Exact notice |
| --- | --- |
| Ultimate ASI Loader | MIT, copyright 2023 ThirteenAG |
| miniz | MIT, RAD Game Tools/Valve and Rich Geldreich/Tenacious Software notices |
| MemoryModule | MPL 2.0; source header credits Joachim Bauch |
| d3d8to9 | BSD 2-Clause, Patrick Mours |
| injector | zlib, LINK/2012 |
| injector utility | MIT, praydog |
| MinHook | BSD 2-Clause, including the HDE32/HDE64 notices |

[`fusionfix-provenance.json`](fusionfix-provenance.json) records each exact source
commit/path, original Git blob, notice length/hash, tagged build/resource script,
release asset and embedded input. The tagged `xliveless.cpp` separately declares
its public-domain donation. The minidx9 README defers to Microsoft's DirectX SDK
terms; it is not itself an SDK redistribution grant. MemoryModule's MPL source
availability/distribution obligations still need an explicit package plan. No
independent loader rebuild or complete bundled-material permission closure is
claimed by these notice copies.

Run `python -B scripts/fusionfix_provenance.py` to verify both original archives,
the independent existing asset pin, exact root PE metadata, three embedded
binary digests and seven notice texts. The checker reads bounded regular-file
snapshots, checks archive hashes before parsing, rejects cyclic/out-of-bounds
resource trees, and never extracts, loads or executes a DLL. With an explicit
`--upstream-loader-zip PATH` it also rechecks the pinned official ZIP and identical
member. Without that input it reports `not_rechecked_in_this_run`; it performs no
automatic download. Its identity success leaves permission/rebuild flags false
and lists all unresolved holds. The actual official-ZIP check and six focused
tests passed; original downloads/raw evidence stay under ignored
`.local/evidence/cloud/fusionfix-exact/`.

## ASI historical source candidate: available, not identified as the frozen build

A further static investigation recovered a concrete historical source candidate:
[`2547fdeed707359e67bd4ac0d608de936d78186f`](https://github.com/ThirteenAG/WidescreenFixesPack/commit/2547fdeed707359e67bd4ac0d608de936d78186f).
Its official `dllmain.cpp` is 44,106 bytes, Git blob
`4e0c92155562f308b72de5a852b6a537af36bd02`. Ten official source/build/notice files
were downloaded and checked against their Git blob identities. Its MIT notice
matches the already retained upstream notice exactly. These are available
historical sources, **not an established source identity for the frozen ASI**.

The ASI's COFF timestamp, interpreted as UTC, is 2026-03-22 13:08:52. It falls
within official [release run 23403476638](https://github.com/ThirteenAG/WidescreenFixesPack/actions/runs/23403476638),
whose build job ran 12:53:18–13:22:02 at that commit. This is only a time
correlation: a PE timestamp can be edited and cannot establish binary provenance.
The candidate build recipe uses `windows-latest`, Premake and MSBuild; the exact
historical compiler image/tool versions have not been recovered. The rolling
release recipe permits updates to its release assets.

The retained ASI additionally has a static CodeView RSDS identity:
GUID `19f94904-0580-49cb-b7a1-ab6bcf5ba43b`, age 1, PDB basename
`SplinterCellBlacklist.FusionFix.pdb`. No matching PDB was obtained. Its debug
directory has no reproducible-build entry; that absence is not proof of a
particular compiler invocation. The original DLL was never loaded or executed.

The official historical job-log endpoint now returns HTTP 410. The release run
and related build run 23402508840 both currently list zero retained artifacts.
Thus neither an exact historical archive digest nor a matching binary/PDB has
been recovered through those endpoints. A build made today from the candidate
would be a new, separately identified candidate, not proof that it produced the
frozen ASI. Closing this identity gap requires a trustworthy historical artifact
containing the exact ASI digest, or a matching PDB plus an authenticated
source/build record. The current archive, asset pins and exact-source hold remain
unchanged; no broader license or redistribution conclusion follows from this
candidate source's MIT notice.

The bounded evidence, exact source links and availability results are recorded
in [fusionfix-asi-source-candidate.json](evidence/fusionfix-asi-source-candidate.json).
Raw downloads remain under ignored `.local/evidence/cloud/fusionfix-asi-followup/`.

## Capture helper: binary verified, reported source not received

The existing `src/launcher/src/Assets/SCBL.LAN.Capture.exe` was independently
hashed in the cloud: 810,496 bytes, SHA256
`d34d192bb97a379c4b063c24f7cea6789005b9e5a5584404b701dfd4e7533a11`,
matching the pinned build-input manifest. The executable was not run.

The local handoff reports a single C++ source file with SHA256
`bed779d4302b68e4397a8e3fe9afddfa886c9b96c8667a4a9279def85e11a43b`
and reports agreement with its historical manifest. **That source is not present
in the cloud handoff at this checkpoint; its digest and historical agreement are
reported, not independently verified here.** No rebuild, source/binary matching
or capture execution is claimed. Receive the reviewed source and build
instructions, verify the reported digest, then test rebuilding separately.

### Repeatable source-receipt check

On 2026-10-23, `git ls-remote --heads origin` still listed only `main`
(`fc8ef510b51be83c31f9f8233997f758b0477acc`) and `codex/cloud-integration`
(`814c5c1ae930afc9887c5a6b08ec4a95e3c74597`). No supplemental source branch was
available. This is a remote-ref observation, not evidence that the source was
lost or that no local original exists.

`scripts/capture_helper_receipt.py` now performs a read-only identity check:

```sh
python3 -B scripts/capture_helper_receipt.py
# After the reviewed single-file source is received, supply its explicit path:
python3 -B scripts/capture_helper_receipt.py --source /path/to/received/source.cpp
python3 -B -m unittest discover -s tests/build -p test_capture_helper_receipt.py -v
```

The actual first command exited **2**, `blocked_source_not_supplied`; the existing
binary's SHA256, size, PE32 x86 executable header and independent manifest pin all
passed. The focused regression suite passed **7/7** on Linux. It covers replaced
binary/manifest pairs, absent and mismatching source, symlink/non-file/oversize
inputs, malformed or non-x86 PE headers, and the boundary between input identity
and a verified rebuild. The normal source-test discovery includes this suite;
the missing source itself is not recategorized as a successful rebuild.

The tool never executes/copies the helper, searches private directories, includes
source contents or the supplied absolute source path in its JSON, or changes
asset pins. It exits **1** for an identity failure and **0** only when the explicitly
supplied source matches the reported digest and the retained binary/manifest are
still intact. Even that successful input check reports
`rebuilt_binary_verified=false`, `source_binary_equivalence_verified=false` and
`publication_approved=false`. Source review, the actual build recipe, a recorded
MSVC x86 build and any binary comparison remain separate evidence. No guessed
compiler/linker command has been introduced before examining the received source.

## Remaining closure gates

- Preserve the frozen FusionFix archive while obtaining exact ASI source/build
  provenance, the remaining embedded-DLL/SDK terms and a corresponding-source
  distribution plan; the exact loader release identity is now established.
- Receive and independently hash the capture-helper source; reproduce its build
  without running a game or collecting a capture.
- Keep the 12 opaque SAV/META privacy holds in `ASSET-PUBLICATION.json`. This
  evidence does not authorize publishing them or producing a fake complete pack.
- Keep original sources and unique local evidence until source/dependency closure
  and rollback receipts support safe cleanup. These cloud references do not
  replace the private cumulative four-role evidence.
