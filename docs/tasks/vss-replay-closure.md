# VSS late-geometry replay input closure

Status: **needs-info**, **ready-for-agent** once the listed native inputs are
available. Audit of commit `a689dd5759bdbebc78aadf940b2247b77b792c5e` on
2026-10-23 found no complete uploaded replay of the unresolved scope lifecycle.
All **13 authored historical listings matched their recorded SHA256 and byte
length**. The [machine-readable receipt](vss-replay-closure.json) records those
checks and pins the five inspected production sources. No historical script,
native replay, game or capture was executed; no gameplay source changed.

This refines the missing input in [the lifecycle audit](vss-lifecycle-audit.md).
It does not reopen the user's accepted VSS EMF or F3 results, establish a new
failure, or repeat the seven existing policy tests as native evidence.

## What the uploaded listings actually cover

The files remain unchanged under
[`historical-replays/`](../development-reference/historical-replays/README.md).
Their older pass counts describe historical runs, not runs made by this audit.

| Listing | Actual authored path | Gap for the requested replay |
| --- | --- | --- |
| `entry/.../entry.cpp.txt` | Calls native scope tick/entry and a relocated binder with controlled resource getters, queue scheduling and engine leaves. `setup()` prepopulates `pawn+0x1508` with the scope actor; `Binding::reset()` sets resource sections at `+0x64` to **6**. The delayed cases change controller, local-owner flag or viewport, then restore them. | There is no zero-to-ready geometry transition, actor create/destroy observation, or delayed bind against a replacement owner. Its binding-work queue is not the attachment factory's FIFO. |
| `entry/.../check.py.txt` and `fixture.template.rs.txt` | Copies the exact current `update_scope` body into an x86 wrapper and links the presentation/entry policies. | The extracted body stops before `prepare`; it does not link the production `ensure`, lease publication or `release` lifecycle. It runs an externally supplied `entry.exe` rather than building that harness from the listing. |
| `emf/.../source-probe.cpp.txt`, `fixture-stubs.rs.txt` and `check-source.py.txt` | Replays material admission, native material factory and draw setup/cleanup. The wrapper explicitly supplies `READY`/`LEASE`; stale pawn/actor cases test material ownership rejection. | This is the accepted EMF material path. Supplied lease state is not native actor creation, and the script explicitly excludes live resource timing. Reusing its stale-owner case would not establish delayed binding safety. |
| `multiplayer/vss-agent/chat/` | Chat relay ABI and ordinary/relayed message dispatch. Its Python listing imports an external `native` module to extract and relocate retail code. | The directory name does not make this a VSS actor replay. The imported helper and pinned image are absent; its native callbacks concern chat. |
| Remaining multiplayer export/crash listings | Authored diagnostic classification and fault-context tests. | They supply no native scope create/bind/tick implementation or history of the missing geometry lifecycle. |

Useful entry anchors are `entry.cpp.txt:98` (ready sections), `:137` (existing
actor), `:170–189` (delayed controller cases), and `check.py.txt:33–47` (body
extraction and external harness invocation). The EMF boundary is explicit in
`check-source.py.txt:160`: owner lease publication is a fixture leaf and live
resource timing is not tested.

## Exact missing inputs

The committed tree was inventoried by `git ls-tree`; none of the six historical
basenames below or either `Blacklist_DX11_game.exe`/`Blacklist_game.exe` was
present. This is a statement about the uploaded repository, not a search of the
user's private retained files.

| Missing input | Why it is needed |
| --- | --- |
| Exact authorized x86 retail image | Both entry and EMF scripts require SHA256 `c6b9f330fac1412f19f32a6fd86edb4c66291a6902611e9433b9b0ea659eb4bc`. This expected hash is present in source; the image itself has not been received or verified here. A different image cannot be substituted. |
| Entry `relocs.h` and its generation method | The harness relocates the retail image using this table before executing tick/entry callbacks. The listing does not define the table or enough extraction rules to validate its completeness. |
| Entry `retail.h` and its generation method | The binder and queued binding callback are loaded from this generated code. Replacing them with guessed C++ behavior would remove the native behavior being investigated. |
| Entry `PINS.json` and reproducible harness build recipe | `check.py.txt` requires this manifest and a prebuilt `entry.exe`. A portable replacement should build the harness from reviewed source and verify inputs; an opaque executable alone would not close the dependency. |
| Actual x86 DirectX helper identity | `entry.cpp.txt` loads a sanitized `<LOCAL_PATH_01>` for four quaternion functions. Its DLL name, version/hash and authorized source must be recovered from the retained manifest; the placeholder alone does not identify it. |
| Native create/destroy and attachment FIFO callback closure | Current production calls include actor create `D5F360`, destroy `D32E20`, pawn destruction `D99D80`, binder `AA6120`, tick `DA6EF0`, attachment factory `CC14C0` and reload `4D43E0`. The historical entry fixture does not provide a verified extraction/relocation/callback-boundary recipe covering this complete sequence. Production signature hashes identify some functions but cannot reconstruct their bytes or transitive dependencies. |
| EMF `full-code.h` and `NATIVE-PINS.json` | These are also absent, so even the older material replay is not independently runnable from the upload. They are not prerequisites to reaccept EMF and are not substitutes for the entry/lifecycle inputs above. |

The missing `native` Python extractor in the chat reference is another closure
gap, not an alternative source for these callbacks. Windows x86 MSVC/Rust tools
can be supplied on hosted CI; installing more tools cannot supply the missing
native behavior or its provenance.

## Next bounded implementation once the inputs exist

Recover the reviewed author-written generators, build recipe and manifests from
the retained migration materials first. Keep original files and their hashes;
any legal retail image or generated machine bytes stay in an explicitly supplied
private input location unless independently cleared for publication. Do not
upload personal saves, dumps or the local four-role evidence to fill this gap.

The portable runner can then build a fresh x86 harness into an owned output
directory, require the image/hash/signature/relocation checks, and directly link
the current production lifecycle path. Merely copying the old `update_scope`
wrapper would leave `ensure`/`release` outside the test. Use bounded ticks and
callback queues, a process deadline and exact owned-child cleanup; never start
the game executable or an old transaction/finalize script.

The first experiment should hold controller/viewport and actor identity valid,
observe zero geometry sections, then make that **same resource** ready and run
native ticks. Count actual actor creation/destruction, binding attempts and
attachment factory/FIFO events. Controls must cover never-ready geometry, a
replaced owner generation before a late callback, and no second FIFO enqueue.
If the unchanged production baseline already recovers, record that result rather
than manufacture a failure. A genuine failure is required before proposing a
product fix and baseline/modified/rollback/reapplied evidence.

This native callback experiment still would not establish GPU pixels or live
multiplayer acceptance. Those boundaries and the existing accepted results stay
in [remaining-bugs.md](remaining-bugs.md).
