# CI artifact source closure

Status: **ready-for-agent** for the downstream composition result; the
source-transfer defect itself is verified fixed at `329f18a`. Continue the existing candidate-delivery and
dependency-closure goals; this is not a new product or a replacement goal.

Exact source `9139c9ed3fe7dd90db6dfbd79666fe28f8474c3d`, candidate run
`40397583631`, built the Windows components and assembled the service/client
candidate successfully on the runner. The downloaded artifacts are incomplete:
the Windows receipt declares 50 source/notice files but contains 45; the assembled
receipt declares 62 input files but contains 57. All remaining declared files
match their exact sizes and hashes. See the [official artifact evidence](../evidence/hosted-final-9139c9e.json).

Both archives omit these five reviewed MemoryModule source files beneath
`licenses/FusionFix/corresponding-source/MemoryModule/upstream/`:

- `.gitattributes`
- `.gitignore`
- `.travis.yml`
- `example/DllLoader/.gitignore`
- `example/SampleDLL/.gitignore`

The upload action excludes hidden files by default. Read-only execution of the
production input validator against the downloaded official assembly archive
reproduces `FileNotFoundError` at the first missing path. It executes no downloaded
binary and creates no process or namespace. The two runtime harnesses translate
this into their generic failure codes before private staging. The official
composition receipt contains zero cases and no verified copied package; it
does not establish runtime success or cleanup. This is distinct from the older
`QUERY_IMAGE_CHANGED` failures.

## Required repair and verification

Publish a bounded, independently validated payload that preserves the existing
download layout and all declared source/notice bytes. Hidden-file upload must
apply only to that explicit public payload, not broaden raw-log, private-input
or workspace publication. Reject undeclared hidden files, links, missing files,
changed digests and wrong run/source coordinates. Keep diagnostic logs on their
existing separate publication boundary.

Exercise an actual archive round trip and production input validation; a YAML
string assertion alone cannot prove source inclusion. Preserve a negative
control for the five omitted files, then verify new official artifact digests
and every declared downloaded entry. Re-run the native lifecycle and composition
against the complete same-run candidate. Missing-input diagnostics must remain
fixed codes without raw paths or private content, and must not claim a process
or private stage was cleaned when none was created.

The eight SAV/META input holds, thirteen separate C71 inputs, Windows firewall
admission and live-game limits remain independent. Fixing artifact completeness
does not admit the full Windows package or ordinary Public startup.

## Diagnostic correction and local proof

Commit `5aa8a50319dbaf5322f496df53acc44238a24856` maps missing candidate
inputs to `CANDIDATE_INPUT_MISSING` at both harness boundaries. It preserves
the complete admission body, hash/path/inventory checks and runtime ordering.
The official incomplete artifact remains rejected. Local and independent
native-lifecycle/stack checks pass 56 with zero skips; malformed, changed,
extra and linked inputs remain rejected before staging or execution.

The [regression record](../evidence/artifact-hidden-source-regression.json)
also distinguishes a local publication reconstruction from an official upload:
only the five exact pinned public source files were added to temporary copies
of the two verified artifacts. New bounded staging and actual ZIP round trips
preserve 61 Windows payload files and 65 assembly payload files; the latter's
real read-only native input validator admits its 62 declared files plus two
metadata files. The extra lane receipt remains outside the candidate root.
Original downloads stay unchanged, no binary executes, and a new hosted
upload/download plus runtime result is still required.

Reviewed publication commit `37ab1e1a8ea25372ea0e8db7419bb2cfe0d354e1`
creates a fresh validated public payload with the existing consumer layout.
Only those two successful payloads enable hidden-file transfer; diagnostic/log
artifacts remain separate with their normal hidden exclusion. Eight focused
checks pass locally and independently, including malformed receipt rejection
without a traceback. Independent source-change and staged-extra-file controls
both reject and remove only the invocation-owned staging tree. Native candidate
admission and its 64-file limit are unchanged. These checks still require the
new official upload/download and downstream runtime results.

The first integrated clean checkout at `37ab1e1` retained one failing method
with two assertion events: the outer CI annotation allowlist did not yet include
`CANDIDATE_INPUT_MISSING` and `CANDIDATE_INPUT_UNREADABLE`. The production-code
contract test correctly detected their conversion to an unclassified failure.
Single-file correction `dc55a9337bf13b8bc53b22f734d0ba6151e7379f` adds only
those exact literals. The test is unchanged; unknown values and suffixed codes
still reject. Related 77 checks and independent classifier 21 checks pass with
zero skips. The original failed clean report remains in the regression record.

The corrected exact `dc55a933` clean checkout passes 665 with 2 platform skips
(667 total), exit 0, unchanged tracked files and complete-package readiness false.
Report SHA256 is
`b06018fa707f336e2d284bd1e7ad031f9cb04830dc2401bea72e1be00b1e902f`.
The supplemental source patch applies/reverses exact canonical bytes, paths and
modes; original private four-role evidence is unchanged. These results permit a
new hosted verification run, not a successful artifact or runtime claim yet.

## Windows metadata portability

The next published source `2429db8` exposed a Windows metadata portability
failure before artifact publication. Source run `40419346353` passed Linux
665 with 2 skips, but Windows had 546 passes, 116 skips and five failed methods
(two failure plus five error events). Those failures converge on the publication
inventory's regular-file/link-count predicate. Candidate Windows job
`121909579406` also stopped at source checks and published no component payload.
See the [bounded failure and correction](../evidence/artifact-windows-metadata-regression.json).

Reviewed correction `c3ce3a6` uses fresh `Path.lstat()` metadata instead of
`DirEntry.stat()`. The documented Windows directory-enumeration metadata can omit
fields needed by the link-count check; the failed hosted log did not record their
actual values. All link, reparse-point, file-type and exact-inventory predicates
remain unchanged. Eleven focused checks pass locally and independently, including
an actual hard link, a simulated incomplete directory entry and fresh reparse
rejection. The old helper fails the incomplete-entry negative control.

Its clean checkout passes 668 with 2 platform skips (670 total), tracked source
unchanged. The supplemental patch applies/reverses exact canonical bytes, modes
and paths. These are local proofs; the next actual hosted Windows run and
subsequent official artifact round trip remain required. The original failed
source/run receipts are preserved. Full-package and separate C71 holds are unchanged.

## Actual repaired hosted publication

Source `329f18a`, candidate `40420957576`, now closes the previously observed
transfer defect. Official ZIP digests were verified before strict read-only
extraction: all 61 Windows and 65 assembly files, all 50 notices/sources and all
five hidden source files match. Six Windows/Linux component identities bind to
this same run. The production candidate validator admits its exact 64-file root,
without running downloaded binaries during review. The [bounded receipt](../evidence/hosted-artifact-native-329f18a.json)
also records all five actual native lifecycle controls and cleanup. The new
Windows source job passed and measured the platform metadata difference directly;
see its [source evidence](../evidence/hosted-source-329f18a.json).

The original incomplete artifacts and failed Windows metadata run remain historical
failures. Composition was still compiling at the observation, so its runtime
controls remain the next result to read. This transfer repair does not close
permission review, privacy-held saves, C71 input closure, Public admission or
live multiplayer acceptance.
