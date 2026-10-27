# Historical Capture reference build

Status: **ready-for-agent** for the remaining equivalence/lifecycle work. The first
GitHub-hosted Windows x86 build and 19 bounded checks passed with independently
verified receipts below. Source receipt is closed; binary parity and live debugger
lifecycle acceptance remain open. No automatic capture is authorized.

The received reference from commit
`b7a4c871b39b527f3e9c12fa099ca6b7ea04c3cc` is 16,267 bytes with SHA256
`bed779d4302b68e4397a8e3fe9afddfa886c9b96c8667a4a9279def85e11a43b`.
Its exact mixed line endings are preserved (136 CRLF and 39 other LF). The
existing embedded binary remains
`d34d192bb97a379c4b063c24f7cea6789005b9e5a5584404b701dfd4e7533a11`.
The historical association alone proves neither reproducibility nor equivalent
behavior. The original source and binary are not overwritten by this task.

## Independent build and finite checks

`.github/workflows/capture-reference-build.yml` uses a fresh Windows 2022 hosted
runner with MSVC x86 and the recorded C++17/static CRT/debug compilation options.
`scripts/ci_capture_reference.py` verifies the source, retained binary and its
existing manifest before compilation, copies the source bytes into a new
temporary directory, and builds two independent PE32 executables. It records the
actual compiler version rather than assuming the runner matches historical
MSVC 14.44.35207 or Windows SDK 10.0.26100.

The reference executable is invoked only with missing arguments and a fixed
invalid pipe prefix. Both must return 2 with no output, before `OpenProcess`,
pipe access or debugger attachment. A separate fixture includes the exact
reference translation unit with its entrypoint renamed; it calls only the pure
exception-field classifier, module-name sanitizer and bounded module/RVA
formatter. All 17 cases and the exact summary are required. The fixture does
not call the renamed debugger entrypoint or any dump function. These 19 checks
do not exercise debugger attach/detach, exception interception, actual dumps,
or a game.

Only the independently rebuilt `CaptureReference.exe`, fixed JSON receipt and
`SHA256SUMS.txt` are uploaded as a development reference artifact. Raw compile
logs, PDBs and the fixture executable stay in the disposable runner. The receipt
includes compiler exit codes, diagnostic codes and log hashes. The original
asset is reverified after all checks. This reference must not be copied over the
production helper merely because the build succeeds.

## Next executable action and limits

The retained hosted evidence below covers the independent build and its
publication corrections. Do not repeat those accepted checks merely to clear
this task. The remaining work is to account for the recorded compiler/build
inputs and establish reproducibility or bounded behavioral equivalence before
proposing any production-helper replacement. Debugger and dump lifecycle
acceptance still requires separately authorized execution; these finite
non-attach fixtures cannot establish it. A changed build implementation needs
its own exact-SHA hosted result, retaining any new failure without substituting
Linux parser checks for Windows execution.

Source availability does not close the independent held SAV/META privacy gate,
C71 retail fixture closure, full package gate, source/binary parity or remaining
rights/provenance review. No diagnostic capture, production replacement, game
launch, formal Release or deployment is authorized by this lane.


## Actual first hosted build

Run [40373151513](https://github.com/caox233/FifthFreedom/actions/runs/40373151513),
job `121748958885`, at source `6ecfdb474238614a9df5e22abbe91581b99f40e6`
passed both real x86 compilations and all 19 checks with zero skips. The official
ZIP digest, internal sums and actual downloaded PE were independently verified
in the [receipt](../evidence/hosted-capture-reference-6ecfdb.json).
`CaptureReference.exe` has SHA256
`cfef61dc98ed01925a45da8aa56bdce6b39b94eec4c2e4602ec0fa7096de0501`.
The compiler version `194435229` differs from historical `194435207`; the rebuilt
809,984-byte EXE differs from the original 810,496-byte asset. The original source
and asset remain unchanged. No attach, capture, production replacement, binary
reproducibility or runtime equivalence is claimed. The later failed-output
publication correction has the separately bounded hosted evidence below.

## Failed-output publication correction: retained hosted evidence

The [later verified receipt](../evidence/hosted-capture-publication-7c614c1.json)
closes the stale requirement for a first hosted run containing the two correction
commits. Source `7c614c1`, run `40374521618`, job `121753823205`, includes both
`134b49b` and `78de05f`. The official artifact `12674082013` matches the retained
ZIP's SHA256 `218ad0b35d00f338f15f5584a44cd69d34f6a8df83c756fc028b7b833132e6e8`.
Its exact three files, internal sums, PE32 identity and 19 checks with zero skips
were independently reverified. The original source and embedded helper still
match their retained hashes; the workflow, build script and regression tests
are byte-identical at current source `329f18a`.

The official Windows log also records all nine portable regression checks,
including a post-copy failure that removes the failed output and preserves its
receipt. The successful hosted build exercised the corrected success upload.
The workflow's success-only executable-upload condition was reviewed; no separate
failed GitHub build/upload job was deliberately induced or observed. Therefore
this closes the observed Windows fixture and successful publication requirement,
not an unobserved end-to-end failed-upload scenario.

Binary reproducibility/equivalence and debugger attach/detach or dump lifecycle
remain open. This follow-up reused already verified hosted evidence; it did not
repeat capture, replace the production helper or admit a complete package.
