# EMF frame-time feedback and separate zero-size overlay errors

Status: **needs-triage**; frame-time comparison input: **needs-info**.
Updated 2026-10-31 (Asia/Hong_Kong), against source `d207fc611be3a89351f8517fcbc37aeb18610dd4`.

The user's local four-endpoint review reports EMF performance concerns and one
client with 4,039 zero-dimension drawing errors. Raw logs, player identifiers and
screenshots are not available as public fixtures and are not uploaded here.
That count establishes neither elapsed render time nor an EMF cause. Preserve the
accepted VSS EMF display result, F3 alignment and stable world camera. A new
performance complaint does not withdraw their existing, narrower acceptance.

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
