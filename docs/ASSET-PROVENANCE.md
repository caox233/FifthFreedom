# Build assets and public-data boundary

The project owner states that redistribution authorization exists for the requested
game DLL, music, artwork and third-party build inputs. Source links and the precise
authorization conditions will be supplied by the owner. This is a recorded owner
statement, not an independent legal verification. The source license does not
relicense third-party binaries, media or SDK headers.

`scripts/local-assets.json` pins the existing build inputs. Reviewed non-save
resources are retained in this migration. Do not replace a pinned input merely to
make a missing-file check pass. Add matching upstream notices and source references
when available. The frozen FusionFix archive still lacks an internal upstream
license notice. A separately verified upstream source/license reference and
byte-level release comparison are now recorded in
[`dependency-closure.md`](dependency-closure.md). Eleven data/configuration files
match the compared official distribution, but the ASI, main INI and loader differ;
the exact frozen-binary version and additional notices remain unresolved. The
upstream license copy does not relicense every bundled archive member.

`docs/ASSET-PUBLICATION.json` lists default SAV/META inputs held locally pending
privacy verification. No actual secret was identified, but opaque SAV buffer I/O
and a SHA-256 manifest do not establish that player-specific fields are absent.
The public source checkout therefore does **not yet independently build a complete
game package**. Code, source tests, protocol work and UI integration can proceed in
the cloud. A full Windows CI package is gated on approved matching inputs or a
reviewed, reproducible default-data replacement. Do not upload personal saves,
fake an asset, weaken package checks, or call this gate a successful build.

The pinned capture helper binary is preserved, but its build project has not yet
been migrated. Record this source-rebuild gap instead of calling every binary
independently reproducible. The cloud independently verified the retained binary
SHA256 `d34d192bb97a379c4b063c24f7cea6789005b9e5a5584404b701dfd4e7533a11`.
The local handoff reports C++ source SHA256
`bed779d4302b68e4397a8e3fe9afddfa886c9b96c8667a4a9279def85e11a43b`,
but that source has not arrived and its digest has not been independently checked
in the cloud. Local migration builds used the retained inputs and are distinct
from a clean public checkout build.

The repeatable read-only `scripts/capture_helper_receipt.py` check verifies the
retained binary's hash, size, x86 PE header and independent manifest pin, then
checks an explicitly supplied source against the reported digest. Its current
result is `blocked_source_not_supplied` (exit 2). It does not execute a capture,
copy inputs, change the pin, authorize publication or establish source/binary
equivalence; those boundaries remain visible even after source identity matches.

Legacy live deployment host defaults in the SCBL3 donor are replaced by inert
`.invalid` examples. Explicit deployment configuration belongs to cloud
integration; no automatic connection to a previous operator's service is intended.
