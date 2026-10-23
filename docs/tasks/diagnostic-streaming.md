# Retained diagnostic text streaming regression

Status: `ready-for-agent` for hosted Windows integration; the affected user's
full export acceptance remains `ready-for-human`. No chat or diagnostic-export
acceptance entry has been marked complete by this work.

The cloud can test the actual retained text reader without inventing Windows
file identity behavior. `DiagnosticTextStream.cs` now contains the existing
strict UTF-8/JSON/redaction implementation and the existing 16 KiB read/line
loop. `DiagnosticTextSanitizer` keeps its original `Line`/`Copy` signatures and
still applies the real context's input/output budgets, source counters and
projection before writing. `DiagnosticBundleService` still owns Windows file
identity checks, ZIP verification and final publication. No ZIP, source-discovery,
configuration or redaction policy was relaxed.

This is a shared implementation extraction with new coverage, not a claimed fix
for the user's historical `InvalidDataException`. The extracted `Line`, `Scrub`
and recursive JSON writer are mechanically identical after the recorded parameter
renames. The read loop preserves the order of checks, reads, input accounting,
line-limit rejection and synchronous emission; the line buffer remains borrowed
only until the callback returns.

## Actual executable checks

`tests/DiagnosticTextStreaming` targets .NET 10 and directly links the production
helper. It has no SDK stubs, alternate sanitizer or package dependencies. Run:

```sh
dotnet run --project tests/DiagnosticTextStreaming/DiagnosticTextStreaming.csproj --configuration Release --no-launch-profile
```

All **12 cases passed** in this Linux cloud using .NET SDK **10.0.302**:

- Chinese UTF-8 split across a real 16 KiB file read, including intact emoji;
  a synthetic JSON token split across reads; CR/LF split across reads and a final
  multibyte line without a newline; server hex dumps across the same boundary.
- Exact line-length acceptance and one-byte-over rejection, invalid UTF-8 across
  reads, JSON depth/node limits and a malformed final JSON line.
- A requested snapshot extending past actual EOF fails; an existing tail beyond
  the requested snapshot is not read. Cancellation before the read or after its
  accounting callback prevents output. A caller output failure stops further reads.

Every successful and rejected copy compares the original file bytes afterward.
The failing-stream cases explicitly permit previously emitted complete lines in
the test's memory sink. They do **not** assert full ZIP rollback or publication
behavior. Total export/file/ZIP budgets and source projection stay in the retained
wrapper and are not represented by a test reimplementation.

A separate cross-target build of the complete WPF launcher passed with **zero
warnings/errors** using `EnableWindowsTargeting=true` and isolated ignored
intermediate/output directories. This compiles the actual wrapper/shared helper
together; it is **not Windows execution**, a complete resource package or user
machine acceptance. The existing 24 `DiagnosticByteDumpRegression` full-export
cases were **not rerun** here: they require the Windows `ExportRetained` identity
and ZIP path. CI registration is owned by the CI task and is not implied by these
local results. Exact inputs/log hashes are in
[`diagnostic-streaming.json`](../evidence/diagnostic-streaming.json).

## Why this slice followed the chat investigation

The requested three synthetic chat clients cannot currently exercise the actual
retained chat protocol from the uploaded source alone. `chat_relay.rs` borrows
the retail `ChatString` and calls verified x86 descriptor/service/send addresses;
the ordinary receive hook remains in `goggle_feedback_native.rs`. The hosted
LAN/public service is not a replacement implementation of that chat serializer.

The historical `multiplayer/vss-agent/chat/probe.cpp.txt` already checks sender
and host exclusion, remote-only peer lists, the native team UI filter, and aborts
after session/role/namespace/match-key/peer-list mutation. Its `queue` callback
states that network/socket delivery is simulated, and it stops the retail policy
at the accepted serialization boundary. Its final receipt explicitly says
`transport=SIMULATED serialization_and_sockets=NOT_TESTED`. The historical **391**
checks therefore are not three live clients, actual wire serialization, or a
disconnect/reconnect network regression; none were rerun during this task.

The historical generator requires retail image SHA256
`c6b9f330fac1412f19f32a6fd86edb4c66291a6902611e9433b9b0ea659eb4bc`, an external
`native` extraction/relocation module and generated native-code/pin inputs. These
are absent from the uploaded tree, as also recorded in
[`vss-replay-closure.md`](vss-replay-closure.md). More build tools cannot substitute
their actual behavior. No game binary was run and no fabricated chat server,
retail image, native callback or source pass was introduced.

The next chat step requires the reviewed author-written extraction/build closure
and authorized exact retail input in a private location, followed by a bounded
actual serializer/transport harness. Preserve the original evidence and hashes.
Three synthetic protocol clients would still not replace the three-person
global/team/enemy/Chinese-display acceptance in
[`ACCEPTANCE.md`](../ACCEPTANCE.md).
