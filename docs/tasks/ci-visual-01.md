# CI visual evidence — actual WPF

Status: ready-for-agent; implementation and cross-compilation are not Windows rendering evidence.

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
