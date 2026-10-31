# MemoryModule source referenced by Ultimate ASI Loader v9.7.0

`upstream/` preserves the complete 38-file source tree of
[`fancycode/MemoryModule` at `5f83e41c3a3e7c6e8284a5c1afa5a38790809461`](https://github.com/fancycode/MemoryModule/tree/5f83e41c3a3e7c6e8284a5c1afa5a38790809461).
The files total 126,092 bytes. Original bytes and Git modes are preserved,
including the three executable test scripts. No upstream script was executed.

Ultimate ASI Loader tag `v9.7.0`, commit
[`f9f6ccf4723f42be4bf6e051a82d2ec44b29c9fc`](https://github.com/ThirteenAG/Ultimate-ASI-Loader/tree/f9f6ccf4723f42be4bf6e051a82d2ec44b29c9fc),
references this exact revision at `external/MemoryModule`. Its tagged
[`premake5.lua`](https://github.com/ThirteenAG/Ultimate-ASI-Loader/blob/f9f6ccf4723f42be4bf6e051a82d2ec44b29c9fc/premake5.lua)
compiles `external/MemoryModule/*.c` and `*.h` into the Win32 loader. Those globs
select the preserved `MemoryModule.c` and `MemoryModule.h`.

## Source availability and license

The MemoryModule source is governed by the **Mozilla Public License 2.0**.
The complete unmodified license is [upstream/LICENSE.txt](upstream/LICENSE.txt);
author and contributor notices remain in the original source. Recipients can
obtain the source directly from `upstream/` or the exact upstream revision linked
above. This source copy does not limit their rights under the MPL.

MPL section 3.2 requires executable recipients to be informed how to obtain the
covered source. This directory supplies an exact source copy and access notice
for inclusion with candidate distributions. The repository copy alone does not
prove that a particular downloaded candidate contains that notice or source;
packaging receipts must establish its actual inclusion.

## Verification and remaining boundaries

[SOURCE-MANIFEST.json](SOURCE-MANIFEST.json) records every original path, byte
count, SHA256, Git blob ID and executable mode. The complete Git tree was
reconstructed from the downloaded bytes and modes and matched the pinned
commit's tree. `LICENSE.txt` also matches the existing pinned MemoryModule
notice under `docs/third-party/` byte-for-byte.

The unchanged upstream `.gitattributes` requests CRLF checkouts for
`scripts/run-appveyor.bat`, while its canonical Git blob uses LF. The manifest
pins both complete representations of that one file. Packaging accepts only
those two exact hashes and emits the canonical LF bytes; mixed line endings or
any content change are rejected. The other 37 source files require exact bytes.
Canonical Git modes remain in the manifest on Windows, where NTFS does not
represent POSIX executable bits. POSIX source copies preserve those bits.

The retained FusionFix archive contains an Ultimate ASI Loader binary whose
SHA256 is `ad5d8d449ce305caf62cb17d8698ebf17e94f9120ee993d0dd85211dfe14f8dd`.
Its exact official release comparison is recorded in the repository's
`docs/dependency-closure.md`. The tag's source reference establishes which
MemoryModule revision the published build recipe selects. This copy does not
independently reproduce that loader or identify the source of the frozen
FusionFix ASI. No binary or pinned asset was replaced.

The embedded Bink, wndmode and vorbisfile binaries, DirectX SDK terms, and exact
ASI source/build evidence retain their separate unresolved requirements. Their
permissions are not supplied by MemoryModule's MPL or the loader's MIT notice.
The upstream build/test scripts are historical source, not commands to run
without reviewing their dependencies and effects.
