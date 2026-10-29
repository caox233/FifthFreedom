# CI visual evidence — actual WPF

Status: ready-for-human; hosted root-DPI rendering and HWND input checks passed at
`dcb97d0`; physical-display, IME, GPU and live-network acceptance remain outside
this evidence.

The dedicated `visual-layout.yml` workflow builds the real launcher on a
GitHub-hosted Windows 2022 runner. It executes `--visual-probe` only, with the
existing isolated PreviewMode, synthetic identities and documentation-only
addresses. It does not start the game, capture a game/GPU frame, discover real
hosts, read an account database, publish a Release or deploy a service.

The matrix is eight states (`main`, `settings`, `settings-public`,
`settings-gameplay`, `discovery`, `join`, `create`, `cancel`), Chinese/English,
1120×680 and 900×580 DIPs, and root DPI 96/120/144/192 (100/125/150/200%).
Each phase therefore contains 128 primary screenshots. Modified settings pages
also receive a separate screenshot after scrolling to the bottom.

`VisualTreeHelper.SetRootDpi` changes the DPI of the production content before it
has a visual parent or HWND. Its logical Window owner is retained so implicit
styles/resources remain the production ones. `GetDpi`, bitmap dimensions and SHA256 are checked.
`RenderTargetBitmap` renders that WPF tree; this is not a resized 96-DPI picture.
It is **root-DPI WPF rendering, not a change to Windows display scaling or proof on
a physical monitor**. Separate focus checks show a real WPF Window on the runner
and exercise Tab/Shift+Tab traversal, programmatic focus containment and Escape.
Those keyboard checks use the runner's display DPI.

Checks cover the bounds and separation of the main panels, visible action areas,
32-DIP minimum primary action hit areas after the production Viewbox transform,
actual input hit testing, TextBlock clipping, settings save/cancel accessibility
after scrolling, preservation and horizontal scrolling of long synthetic server
addresses, gameplay draft restoration after Escape, and the real Save button's
PreviewMode in-memory commit followed by another edit/cancel. That Save check
does not claim to validate disk persistence. State rendering uses the
production `ApplyVisualProbeState` hook; it does not claim a real network join or
server creation succeeded.

The runner preserves four phases:

1. **Baseline:** archive commit `12d394086a22c04e7e1defc7678ceccfe02ee004` locally;
   inject only the same visual probe and isolated entry. Keep the exact entry
   patch, original/instrumented App hashes and probe hash. Use the old production
   settings/role UI and actual legacy host picker where no new state hook exists.
   Absent new controls are not counted as baseline layout failures.
2. **Modified:** compile the current clean checkout and capture every scenario.
3. **Rollback:** copy the retained baseline executable into a fresh owned
   directory and reproduce the old layout checks/screenshots.
4. **Reapplied:** repeat the current executable in another fresh directory.

Every executable copy owns its data directory, APPDATA/LOCALAPPDATA, temporary
directory and .NET bundle cache. The four-phase receipt requires matching
baseline/rollback and modified/reapplied executable hashes, actual baseline
layout failures, and passing modified/reapplied checks. It preserves image hashes
without assuming pixel identity across independent WPF renders. The source SHA
comes from the checkout; dirty tracked files are rejected.

Artifacts include all PNGs, complete per-frame layout/focus checks, publish logs,
instrumentation provenance and `comparison.json`. Failed runs retain their
evidence. Review the actual Chinese/English main, settings and discovery images
before claiming visual acceptance; automated geometry alone is insufficient.

Local preparation so far: the instrumented 12d baseline with the complete
128-scenario matrix and focus probe cross-compiled with zero warnings/errors.
Eight receipt-validator
tests passed, including missing/duplicate frames, tampered images, wrong DPI or
pixel dimensions, absent layout/hook checks and privacy boundaries. The official
SHA256-verified actionlint 1.7.12 accepted the new workflow. Hosted run URLs,
conclusions and manually inspected image findings follow below.

## First actual hosted execution and probe correction

Run [40811466898](https://github.com/caox233/FifthFreedom/actions/runs/40811466898)
tested head `4e8eb0b96481772d724022faa1453907b8681bda` using GitHub's PR merge checkout
`2db77356b60b33c6e8b5477b61fee7125e21edcd`. All four phases produced 128 primary
PNGs each; the modified/reapplied settings pages also produced bottom-scroll
screenshots. All original artifacts were downloaded to the ignored local evidence
directory for this run and kept unchanged.

The run failed. Each modified phase had 400 failed detached `InputHitTest`
assertions; its geometric, text and actual-window keyboard/draft/long-address
checks otherwise passed. Manual image inspection also found black title text
caused by the probe clearing `Window.Content` and thereby losing implicit Window
resources. Those images are evidence of the probe defect, not proof of a product
foreground-color defect. The old baseline additionally has 40 minimum-hit-area
failures at small size; its absent `toolsCard` name and detached hit-test failures
are not counted as proof of the intended product regression.

The correction keeps the logical resource owner, adds explicit title-resource
checks, uses visual-layer geometric hit testing on the root-DPI render, and runs
separate real `InputHitTest` assertions after showing each modified/reapplied
window. No hit assertion was waived. A new validator regression rejects a current
receipt that substitutes geometric hit testing for shown-window input testing.
The corrected core cross-compiled without warnings/errors; nine validator tests
passed. Corrected Windows rendering and manual visual acceptance remain pending.

## Second hosted execution

Run [40813823778](https://github.com/caox233/FifthFreedom/actions/runs/40813823778)
tested `7e9fff371bb4e0654bb73e5878d292378dcf36c7` through PR merge checkout
`fca8d38c3e10113177b1ffd34858b8a2ee78e4bc`. All 608 downloaded PNG hashes matched
their receipts. Production title resources are now retained, and manual inspection
of the Chinese/English 200% root-DPI small-window main, public settings and gameplay
settings images confirmed the prior black-text probe artifact was gone.

Each modified phase had eight remaining failures: the shown-window hit test for
`btnToolsClose` in English public settings, at both sizes and all four DPI values.
The probe had left the page scrolled to its bottom after the supplementary image;
that same viewport was reused for the initial header-input check. The bottom PNG
shows the header outside the viewport while the Save/Defaults actions are visible.
The correction restores the exact original scroll offsets after the supplementary
capture and explicitly checks restoration. It retains both the header input test
and bottom-action accessibility/input tests. Other geometry, clipping, visual
hit tests, keyboard, draft and long-address checks passed on this run.

With the correct resource context, baseline/rollback each reproduced 82 layout
failures: 40 small action hit areas and 42 clipped status texts. Their 48 missing
`toolsCard` state checks are recorded separately. Original first/second-run
evidence remains unchanged; the viewport correction still needs a hosted rerun.

Manual review also found a stale Public presence caption behind the Local
join/cancel fixture. Product inspection confirmed the real mode renderer refreshed
presence only when switching to Public; the product correction refreshes both
directions. The next modified/reapplied probe asserts the selected mode and the
actual neutral Local or unavailable Public footer caption without assigning that
caption itself. The viewport and footer-check changes cross-compiled with zero
warnings/errors; this is compilation evidence, not a replacement for the next
Windows render and input run.

## Third hosted execution: passed

Run [40815530494](https://github.com/caox233/FifthFreedom/actions/runs/40815530494)
passed on Windows at head `9c029cd021d829c8c06ad8d32fd244dee64e8b4f`.
Its PR merge checkout `2873f6f84700f5d98175ba7b071d4f835da93f52` has the same
tree, `fdd504d59bfd1ca1c01d151c2acd4e6e2807216c`. The downloaded 608 PNG hashes
and dimensions were independently verified. The source, artifact identity,
phase counts, executable hashes and 18 visually inspected sample hashes are in
[`hosted-ui-visual-9c029cd.json`](../evidence/hosted-ui-visual-9c029cd.json).

Baseline and rollback each reproduced the same 82 measured layout failures
(40 small action areas and 42 clipped status-text measurements), plus 48 absent
new-state checks recorded separately. Modified and reapplied each passed all
128 scenarios and 10,160 assertions, including 384 actual shown-window input
checks, 784 keyboard/draft checks, 48 scroll-restoration checks and 256 mode/footer
checks. The retained baseline/rollback and modified/reapplied single-file
executable hashes match within each pair.

Image inspection covered Chinese/English main, settings, Public settings,
gameplay, discovery, join, create and cancel states, including the small window
at 200% root DPI and representative 100/125/150% images. The dark scrollbar is
now rendered; the Public page's header close button and its bottom Save/Defaults
actions are reachable. Local join/cancel show their correct presence caption.
The long synthetic host name is fully visible, although it fits one line and
does not establish wrapping behavior. Main panels and sampled action groups have
no visible overlap. Small-window secondary text remains small after Viewbox
scaling; automated geometry does not establish every user's readability preference.

This is real WPF rendering plus hosted HWND input evidence. It does not claim
physical-monitor DPI, IME, GPU/game, disk-persistence or live-network acceptance.
Original failed runs and their screenshots remain preserved.

## Final source-bound follow-up

Run [40816467332](https://github.com/caox233/FifthFreedom/actions/runs/40816467332)
independently passed at head `dcb97d0295c9c8700fd390249f2d617430d89dcf`, using PR
merge checkout `b14d145c590cfbe2338283adc78d2b869d19f4f9`. These two revisions share
tree `ba87ba223d22700ff2a28decc9247984ffd52da8`. That full tree differs from
`9c029cd`; the entire `src/launcher` subtree, visual runner and visual workflow
are unchanged. The changes comprise the server TUN test fixture and documentation.

All 608 new PNG hashes and dimensions were independently verified. Modified and
reapplied each passed 128 scenarios and 10,160 checks with zero failures; baseline
and rollback each reproduced the same 82 measured layout failures and 48 absent
new-state checks. Three newly downloaded images were visually inspected: English
Public settings, English join and Chinese cancel at 192 root DPI in the small
window. The dark scrollbar, complete bounded host name, settings close control
and correct Local footer remained visible without a new observed regression.

[`hosted-ui-visual-dcb97d0.json`](../evidence/hosted-ui-visual-dcb97d0.json)
records exact source/artifact identities, phase results and sampled image hashes,
and references the preceding fixed matrix. This follow-up retains the same
physical-display, IME, GPU/game, disk-persistence and live-network boundaries.
All earlier failed and successful evidence remains preserved.
