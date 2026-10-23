# Frozen-asset dependency closure

Status: partial source/provenance evidence; exact frozen FusionFix binary version
and capture-helper source rebuilding remain open. This record does not change
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

No exact tag, commit, semantic version or independently reproducible build has
therefore been assigned to the frozen ASI or loader. Replacing them with the
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
that license. Exact frozen-binary attribution and any additional notices remain
open. No notice was inserted into the pinned archive, so its integrity check
continues to refer to the original bytes.

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

- Preserve the frozen FusionFix archive while obtaining its exact source/build
  provenance and any additional loader/data notices.
- Receive and independently hash the capture-helper source; reproduce its build
  without running a game or collecting a capture.
- Keep the 12 opaque SAV/META privacy holds in `ASSET-PUBLICATION.json`. This
  evidence does not authorize publishing them or producing a fake complete pack.
- Keep original sources and unique local evidence until source/dependency closure
  and rollback receipts support safe cleanup. These cloud references do not
  replace the private cumulative four-role evidence.
