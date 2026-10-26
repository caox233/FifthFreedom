# Historical Capture reference build

Status: **ready-for-agent** for the first GitHub-hosted Windows execution and
receipt review. Source receipt is closed; binary parity and live debugger
lifecycle acceptance remain open.

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

Run the workflow at the reviewed source SHA and verify its actual job, artifact
digest, PE32 identity, 19 passed checks, zero skips and original-input hashes.
If it fails, retain that receipt and fix the specific build or fixture defect.
Portable parser/admission checks and Linux rejection of the Windows-only runner
cannot stand in for this execution.

Source availability does not close the independent held SAV/META privacy gate,
C71 retail fixture closure, full package gate, source/binary parity or remaining
rights/provenance review. No diagnostic capture, production replacement, game
launch, formal Release or deployment is authorized by this lane.
