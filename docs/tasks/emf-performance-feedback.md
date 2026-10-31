# EMF frame-time feedback and separate zero-size overlay errors

Status: **needs-triage**; frame-time comparison input: **needs-info**.
Updated 2026-11-01 (Asia/Hong_Kong). Initial review used
`d207fc611be3a89351f8517fcbc37aeb18610dd4`; the follow-up below checks
`0e691519b878a8fbbb0695392eb2a2ba9616e841` and the delivered e4 candidate.

The user's local four-endpoint review reports EMF performance concerns and one
client with 4,039 zero-dimension drawing errors. Raw logs, player identifiers and
screenshots are not available as public fixtures and are not uploaded here.
That count establishes neither elapsed render time nor an EMF cause. Preserve the
accepted VSS EMF display result, F3 alignment and stable world camera. A new
performance complaint does not withdraw their existing, narrower acceptance.

The **2026-11-01 user-delegated local review**, clarified by the user, reports
that all three existing e4 traces contain **neither `0×0` drawing errors nor
`Render error` records**, not merely an absence of a flood. This task did not
receive or reread the private traces; the absence is the user's existing-log
summary, not an independent cloud log count or continuous viewport measurement.
The earlier 4,039 count and another machine's minimize errors belong to previous
reports and cannot explain this round's EMF slowdown. Absence of these errors
does not establish improved frame times or identify a performance fix; real
matched per-frame data remains unavailable.

## Source findings and limits

1. The locked overlay dependency, `hudhook 0.8.1`, rejects non-positive scaled
   display dimensions in `src/renderer/pipeline.rs::Pipeline::render`, before
   `ctx.frame()`, the application's `MyRenderLoop::render`, and its gameplay HUD.
   It records `Insufficient display size` and returns an error; its DX11 Present
   hook also records `Render error` and still calls the original Present.
   The application's gameplay HUD already rejects zero/non-finite dimensions.
   Adding another check there cannot intercept the earlier dependency rejection.
   This is a plausible **separate overlay/resize path**, not a demonstrated match
   to all 4,039 records or evidence that EMF caused zero dimensions. No new
   dependency patch, invented viewport size or suppressed error logging is used.
2. Production `scripts/build.py` explicitly sets
   `SCBL_EMF_CONSTANT_REUSE_BUILD=1`. The reuse module's older `default=false`
   diagnostic text does not mean that the complete candidate disabled it. Its
   environment/shader/ownership admission and native fallback remain intact.
   `view_us` and `tail_wall_us` in its sampled diagnostics measure bounded
   **CPU wall segments inside a view**, not a complete presented frame or GPU
   duration. They must not be pooled with frame times or used as p95/p99 GPU data.
3. No retained, reviewed per-frame before/after and EMF on/off input is available
   in this cloud task. Consequently there are **no measured game-frame p95/p99
   numbers and no performance-fix claim**. No offsets, material policies or
   default optimisation switches were changed based on the error count.

## Executable offline comparison

[`scripts/frame_timing_review.py`](../../scripts/frame_timing_review.py) consumes
an explicitly prepared, de-identified numeric JSON projection. It does not read
game logs, enumerate processes, launch a game, enable diagnostics or capture.
Keep inputs and output local, for example:

```text
python -B scripts/frame_timing_review.py .local/frame-input.json --output .local/frame-report.json
```

The file has exactly these fields:

| Level | Required fields and constraints |
| --- | --- |
| Document | `schema: 1`; `evidence: synthetic_fixture` or `reviewed_frame_timing`; one `metric`: `present_interval_ms`, `cpu_frame_ms` or `gpu_frame_ms`; `controls`; `runs`. The evidence label is an input declaration, not independently verified provenance. |
| Controls | Boolean `same_machine`, `same_scene`, `same_graphics_settings`, `same_capture_method`. Any false value blocks differences while retaining descriptive summaries. |
| Run | `phase: baseline` or `modified`; `emf: off` or `on`; integer `sample` 1–10; exact lower-case 40-character `build_sha`; `frames`. Each phase uses one build. Every sample requires all four distinct cells. Different repetitions are reported separately, never pooled. |
| Frame | Consecutive zero-based `index`, positive finite `frame_ms` up to 60,000, integer `width` and `height` from 0–16,384. No timestamps, accounts, names, paths, addresses or free text. |

Use the same metric and measurement method for all four cells. Preserve frame
order and stalls; do not remove unfavourable samples or use per-view timings as
frames. Contiguous indices check the supplied projection, not whether its
upstream acquisition dropped data. The caller must retain the private acquisition
receipt and verify that boundary separately. A document is limited to 8 MiB,
40 runs and 100,000 frames; duplicate JSON keys, unknown fields, duplicate runs,
unpaired cells, mixed phase builds and invalid values are rejected.

The report uses nearest-rank p50/p95/p99, with counts and maximum. It reports
**all frames**, positive-dimension frames and zero-dimension frames separately;
zero-size periods stay in the overall tail. Descriptive before/after and on/off
quantile differences are blocked unless each comparison cell contains at least
100 frames and the positive resolution is single and identical across the
quartet. The 100-frame threshold only gives p99 a minimum rank resolution; it is
not a sufficiency claim for repeatable performance acceptance. Zero-dimension
counts are shown beside both populations, including visible-only comparisons.

Differences of percentiles are neither paired individual-frame deltas nor causal
estimates. The report always says `performance_fix_verified: false`, including
when input declares reviewed real frames. Negative controls, repeated matched
runs and interpretation are still required. Synthetic test values are not game
measurements and must not appear in release notes as a speedup.

## Actual checks this turn

- Twelve analyzer tests pass, including hand-calculated ranks, four-cell
  differences, strict pairing/build identity, two unpooled repetitions, resolution
  and sample-count refusal, non-finite/invalid inputs, gaps, zero-dimension tails,
  UTF-8 CLI execution and output non-overwrite/privacy checks.
- The actual CLI produced a local receipt from a labeled **synthetic** four-cell
  fixture; it launched no game or capture.
- The **unchanged production** `vss_emf_glass_policy.rs` was compiled directly
  with Rust 1.95.0 and its two tests passed: exact-glass admission with every
  missing-link fallback, and closing-identity transition refusal. This is a
  narrow source-policy preservation check, not native draw, GPU or live acceptance.
- No runtime change was justified here. There is no baseline/modified performance
  result to report, and no invented rollback performance receipt.

See [the scoped receipt](../evidence/emf-frame-review-20261031.json).

## 2026-11-01 valid-viewport hot-path review

The current review distinguishes a native vision **view**, a classifier/material
invocation, an upload batch/item, an overlay draw command and a swap-chain
**Present**. They are not interchangeable frame counters. No live frequency,
CPU duration or GPU duration for these paths is available here.

| Production boundary | Source-established work | Interpretation and measurement boundary |
| --- | --- | --- |
| `vision_native_view::draw` | One shared owner invokes its original native view once through the existing middleware. | Views can be nested or occur several times per presentation; no one-view-per-frame assumption. |
| `vision_transparency::classify/reroute` | Preserves native classifier first. Large queues (`primary.len + mask.len >= 256`) build two local `HashSet` indexes; a capacity-shortage preflight can build another two before a native grow callback. Small queues use existing scans. | Repeated CPU allocation sites are confirmed by source, but their runtime count/cost is not measured. These indexes prevent repeated linear membership walks; removing them or retaining native-pointer facts across views/callbacks is not a justified optimisation. |
| `vision_transparency::material` | Existing exact glass exclusion or one original material call, then guarded constant adjustments. | No D3D resource creation or new draw submission appears in this hook. Per-material pointer checks and native material work need separate counts, not attribution from overlay errors. |
| `vision_constant_reuse` | Fixed per-thread 32-slot payload cache; ordinary clear changes its epoch. Item snapshots use stack scratch. A hit reuses the admitted resource; misses/fallback call native upload. Six queue admissions and existing backoff bound work. | There is no per-item heap cache allocation in this adapter. Native driver/resource creation behind a miss is not inspected by the pure cache test. |
| Reuse environment/shader proof | Addon files are read/hashed once on first admitted EMF activation; shader snapshots, disassembly and registry allocation occur at shader construction. | First-use latency and later shader creation remain separate from warmed steady-state view work. A constructor may still occur during play; it is not assumed free. |
| Locked `hudhook 0.8.1` DX11 Present path | One overlay render attempt precedes one original Present call. With valid dimensions, the backend creates a render-target view, uploads vertex/index/projection buffers and emits `DrawIndexed` for nonempty clipped element commands. | Common overlay cost is not EMF-specific. There is no observed calls-per-second value or proof that this explains EMF on/off differences. Multiple swap chains must not be pooled as one frame stream. |
| Locked DX11 overlay buffers | CPU vectors are cleared with capacity retained; D3D vertex/index/projection buffers are recreated only when capacity grows. Upload uses three Map/Unmap pairs on a successful backend pass, even when geometry is empty; projection remains populated. | Distinguish repeated RTV creation/upload from buffer reallocation. Caching the RTV without a verified resize/device-loss lifetime would risk retained back-buffer references, so no speculative dependency patch is made. |

The new locked-backend inspection was compared byte-for-byte to the Cargo-pinned
crate. It identifies work sites, not an executed D3D trace. A Linux allocator
probe used the **unchanged production `constant_reuse_policy.rs`** with a positive
allocation control: 10,000 synthetic cache cycles / 40,000 value checks observed
zero allocations, reallocations and frees inside construction/clear/insert/lookup.
This excludes repeated heap allocation in that pure cache workload only; it is
not the Windows adapter, shader registry, classifier, driver or game. No timing
numbers were produced. See [the follow-up receipt](../evidence/emf-valid-viewport-review-20261101.json).

The existing causal diagnostic build is **not a transparent measurement of the
shipping reuse path**: `SCBL_VISION_CAUSAL_BUILD=1` makes reuse's `built()` return
false. Production `scripts/build.py` clears that flag and enables reuse. Its
sampled native-call CPU segments can aid a separate diagnostic investigation,
but must not be presented as unchanged-production before/after frame timing.
The material/final-pixel-shader observers are likewise opt-in build paths.

### Isolated comparison when an already-reviewed input exists

No new user capture or repeated display acceptance is requested. The existing
offline analyzer remains the entry point; no game, observer or capture is armed.

1. First use an A/A quartet with the **same exact package build** in baseline and
   modified positions, EMF off/on in each. Keep machine, scene/camera, resolution,
   graphics/present settings, addon set and acquisition method identical. This
   tests measurement noise; it is not a performance fix. Repeated samples stay
   separate. Record order and any warm-up rule in the private acquisition receipt.
2. For a later supported implementation change, use the same four cells with
   exact baseline/modified builds and only that change. Each analyzed steady-state
   window must have the same finite positive viewport and at least 100 frames per
   cell (minimum percentile rank resolution, not sufficient statistical power).
   Do not merge machines, scenes or the three trace summaries into these cells.
3. Preserve whole-run frames, first EMF activation, shader creation and resize/
   zero-size intervals. Analyze predeclared warm-state positive-viewport windows
   separately, while retaining the whole-run report and excluded interval counts;
   never silently drop a stall. No zero-size error message is not itself a viewport
   sample. Any absent viewport/timing field remains an input gap.
4. Use separate documents for `present_interval_ms`, `cpu_frame_ms` and genuine
   `gpu_frame_ms`. Present intervals can include pacing/wait and are not GPU
   execution time. `view_us`, `tail_wall_us` and 1/128 causal-call samples are not
   whole frames and cannot populate those fields. Compare p95/p99 only after the
   analyzer's exact quartet, build, resolution and sample-count gates pass.
5. If reviewed low-level counters become available, associate them with that same
   presentation stream and interval: native views/classifier/material calls;
   primary/colour/mask sizes and temporary allocation counts; reuse eligibility,
   hits/misses/fallbacks; shader creations; overlay RTV creation, buffer capacity
   growth, uploads and element draw calls. Keep constructor/cold work separate.
   Such counters explain candidate mechanisms only after correlation; the frame
   analyzer deliberately does not accept arbitrary counters as frame durations.

Current outcome: **no reproducible timing defect or live performance improvement
established; no runtime/UI/camera/viewport policy changed**. The two repeat-work
sites above remain profiling candidates, not confirmed causes. Accepted VSS EMF
visuals and all prior gameplay acceptance remain intact.

## Lower-priority launcher music

One endpoint reportedly failed WMP/music initialization while the game remained
usable. "WMP" is the feedback label; it does not identify the emitting module or
bind the record to this launcher version. Current launcher uses WPF `MediaPlayer`;
`PlayStartupMusicIfEnabled` catches failures;
`MusicPlayer_MediaFailed` stops/closes playback and logs the exception type.
Music errors do not change game launch readiness. Classify this as **low priority,
needs-triage**, separate from EMF and the multiplayer crash. No unsupported codec
installation or player-system change is performed, and successful game use does
not prove the music subsystem healthy.

Next: use already available, locally reviewed per-frame inputs if they exist;
otherwise retain the exact missing four-cell measurement scope for a future
concentrated candidate session. Do not request repeated VSS display acceptance or
automatically collect new gameplay. A zero-size runtime fix requires a reproducible
minimise/restore/resize transition and verified restoration of real overlay input,
notifications and Present behaviour; an error counter alone is insufficient.
