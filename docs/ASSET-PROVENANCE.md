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
match the compared rolling FusionFix distribution, but the ASI, main INI and
loader differ. A separate exact release comparison now identifies the frozen
loader as Ultimate ASI Loader **v9.7.0 Win32**: its `version.dll` is identical to
that official release's `dinput8.dll`. The retained ASI's exact source/build
identity remains unresolved. [`fusionfix-provenance.json`](fusionfix-provenance.json)
binds the downloaded official ZIP, tagged source, PE resources and seven exact
notice texts. Three embedded DLLs also match the tagged binary inputs, but their
independent redistribution terms remain unverified. These source notices do not
relicense every archive member or establish an independent rebuild.

The Windows component collector and complete-package builder copy those seven
pinned texts plus the exact provenance JSON into `licenses/FusionFix`. They now
also carry the complete pinned MemoryModule source, access notice and manifest
under `corresponding-source/MemoryModule/`, plus the existing upstream FusionFix
MIT notice and an explicit attribution-scope statement (50 files in total).
The [source reference](development-reference/fusionfix-loader-memorymodule/README.md)
contains the exact 38-file, 126,092-byte tree selected by the v9.7.0 loader recipe.
The common copy operation validates the complete inventory and fixed hashes
before output; only the one explicitly pinned BAT checkout representation may
differ in line endings, and output uses its canonical Git bytes. It rejects
extra, altered or linked files. Component artifacts use a separate notice checksum list, keeping
the existing four-component checksum contract unchanged. The assembled server
candidate and complete-package manifests include the copied files as well.
The JSON retains all unresolved source and permission holds;
carrying notices is not approval of every embedded binary. The copy operation
and real component collector were exercised with inert packaging inputs only;
that check does not claim a complete Windows build. The source-copy addition
initially passed 34 focused local and independent tests. A subsequent clean
checkout exposed an overly strict permission comparison; the corrected Git-mode
check now passes 36 focused tests and the complete clean source lane (649 passed,
2 skipped at `ac89a7f`). The original failure remains in the
[source-copy receipt](evidence/memorymodule-source-packaging.json). Actual hosted
packaging of this addition remains to be verified. This narrows the MemoryModule source-availability
gap; all seven existing provenance holds and independent binary/permission
requirements remain recorded.

`docs/ASSET-PUBLICATION.json` lists default SAV/META inputs held locally pending
privacy verification. No actual secret was identified, but opaque SAV buffer I/O
and a SHA-256 manifest do not establish that player-specific fields are absent.
The public source checkout therefore does **not yet independently build a complete
game package**. Code, source tests, protocol work and UI integration can proceed in
the cloud. A full Windows CI package is gated on approved matching inputs or a
reviewed, reproducible default-data replacement. Do not upload personal saves,
fake an asset, weaken package checks, or call this gate a successful build.

The pinned capture helper binary is preserved. The historical single-file source
has now been received from reference commit `b7a4c871b39b527f3e9c12fa099ca6b7ea04c3cc`
and independently read and hash-verified in the cloud. Its original bytes are
retained under `docs/development-reference/capture-helper/`. This closes source
receipt, not the distinct rebuild/equivalence gap. The cloud verified the retained binary
SHA256 `d34d192bb97a379c4b063c24f7cea6789005b9e5a5584404b701dfd4e7533a11`.
The received 16,267-byte C++ source has SHA256
`bed779d4302b68e4397a8e3fe9afddfa886c9b96c8667a4a9279def85e11a43b`,
and exactly preserves the historical mixed CRLF/LF byte sequence. Local migration builds used the retained inputs and are distinct
from a clean public checkout build.

The repeatable read-only `scripts/capture_helper_receipt.py` check verifies the
retained binary's hash, size, x86 PE header and independent manifest pin, then
checks an explicitly supplied source against the reported digest. With the received
reference path its actual result is `reported_source_identity_verified` (exit 0);
the earlier `blocked_source_not_supplied` result remains historical. It does not execute a capture,
copy inputs, change the pin, authorize publication or establish source/binary
equivalence; those boundaries remain visible even after source identity matches.

The independent Windows reference build subsequently passed 19 bounded no-attach
checks at source `6ecfdb4`, run `40373151513`; see the
[exact receipt](evidence/hosted-capture-reference-6ecfdb.json). The downloaded
reference EXE is x86 PE32 and differs from the retained binary. Actual compiler
version also differs from the historical recipe. These results do not establish
reproducibility, debugger lifecycle parity or privacy of real capture output; the
production helper was not replaced.

Legacy live deployment host defaults in the SCBL3 donor are replaced by inert
`.invalid` examples. Explicit deployment configuration belongs to cloud
integration; no automatic connection to a previous operator's service is intended.
