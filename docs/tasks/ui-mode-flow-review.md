# Launcher modes, settings and local connection flow

Status: ready-for-agent for the 2026-10-31 direct-controls correction and its new hosted checks; the previously delivered UI/Local candidate and independent Public execution gap remain recorded below.

The user tested the complete da180 Windows LAN candidate and requested these
changes on the original integration branch. The fixed review baseline is
`12d394086a22c04e7e1defc7678ceccfe02ee004`; the six migration milestones and accepted
F3/MAC-11/Chinese/world-camera/VSS results remain in force.

- Remove the renderer selector entirely. Normalize old DX9 settings to DX11 and
  keep the actual launch executable restricted to DX11.
- Offer one Public / Local mode choice, defaulting to Public for absent/invalid
  preferences. Persist explicit choices without silent environment fallback.
  Selection and editable server configuration are separate from runtime Ready.
- Entering Local starts one bounded discovery. Normal empty completion, network
  error and timeout have different explanations. One valid response pre-fills a
  confirmation; multiple responses require selection. Retry, manual input,
  create confirmation and cancellation remain available. No automatic host/game
  launch; mode/bind/close changes invalidate late results. Existing owned lease
  cleanup and active-game locks are retained.
- Use one settings entry with donor stable/test endpoints, update ports and
  Fast/Compatible transport semantics, plus retained local/OB, recovery, default
  saves, players and diagnostics. Public readiness must never borrow LAN status,
  credentials, players or latency. Configuration import verifies declared profile
  and local component pins; it cannot grant Windows network/session authority.
- MOD/FOV edits are drafts. Save commits; Cancel/Escape or changing settings page
  discards the draft. Reset changes only the draft. Preserve range 1.00–1.30 and
  default 1.20 and existing game behavior.
- Save descriptions use the embedded manifest date and the approved public
  default-template source. Retain exact template bytes, backup/confirmation,
  separate Official/MOD locations and pending live unlock-progress verification.
- Source tests explicitly decode UTF-8. Local review reported Python **3.14.7**,
  cp936, UTF-8 mode 0: the old implicit reader failed (12 tests, 1 error), while
  `-X utf8` passed. The cloud regression injects that non-UTF8 default at the real
  reader boundary; it does not require changing the user's regional settings.

## Current evidence and required delivery

Baseline source checks fail the requested single-menu/default-Public/no-DX9/
bounded-flow contracts. Independent production settings Load harness: baseline
11/15 versus modified15/15. Original controller contracts passed34/34. Bounded
protocol discovery passed46 cases, including actual loopback UDP, with isolated
baseline/modified/rollback/reapply evidence retained locally. The old version has
no bounded-flow API: its baseline/rollback capability failure executed zero flow
cases, rather than being presented as a live-network reproduction. UTF-8 reader
regression fails before explicit decoding and passes afterwards.

The Windows visual workflow must produce actual WPF renders at 100/125/150/200%
root DPI, Chinese/English and 1120x680/900x580 DIP windows. It records main,
settings including Public/gameplay, discovery/join/create/cancel, scroll reach,
text/layout/hit targets, and actual window keyboard focus/Tab/ShiftTab/Escape.
Root DPI render checks and host-display keyboard checks are distinct from physical
monitor, IME and game/GPU acceptance. Screenshots contain synthetic fixtures only;
the user's screenshot is not uploaded. Inspect actual images before accepting.

After reviewed source push, require exact-SHA source checks, Windows component
and complete-package build, packaged no-game UI/save smoke, UTF-8 manifest and
component validation, then download and hash the actual complete program ZIP.
Do not relabel the earlier da180 package as this UI revision. Record retained
baseline failures, any new CI failures, and corrected run identities.

Ordinary Public remains blocked until the independent owned startup/NIC/WFP and
native-session chain is admitted. Hosted da180 run40594743222 still failed at
FIREWALL_POLICY_UNAVAILABLE (InactiveProfile + Full; coverage UNKNOWN). This task
does not weaken that check, modify the user's firewall, deploy a service, execute
a game/capture, or create a formal Release. Quick matching, three-client chat,
C71 replay inputs, historical crashes and VSS lifecycle remain separate open work.

## Hosted follow-up checkpoint

The exact `7e9fff371bb4e0654bb73e5878d292378dcf36c7` complete Windows job
passed with packaged UI182 and save27 checks. Its downloaded archive is an
intermediate checkpoint, not the final revised delivery. See
[the retained results and follow-up findings](../evidence/ui-mode-followup-7e9.json).
Actual image review found a stale Local footer and an unapplied scrollbar style;
shown-window input checks also exposed a probe viewport-restoration defect.
The follow-up fixes retain the checks and require a new exact-SHA hosted run.

Independent review additionally requires local-only deployment-file/runtime
paths before filesystem access: reject UNC/device/ADS/network mappings and
inspect ancestors root-to-leaf before any descendant. Runtime pins and Public
readiness gates stay unchanged; sequential checks are not claimed to eliminate
concurrent filesystem mutation races.

## Historical verified revised delivery (before direct-controls feedback)

Source `dcb97d0295c9c8700fd390249f2d617430d89dcf` now has a complete Windows
package from manual run40816624490/job123259140125: actual packaged UI182 and
save27 checks passed. The downloaded ZIP was independently checked for exact
65-file contents, UTF-8 manifests/checksums, four component identities and
50 notice/source files. [Download and focused tests](../DEVELOPMENT-CANDIDATE-TESTING.md)
contain the exact artifact link, hash and retention date.

[The final actual WPF run](../evidence/hosted-ui-visual-dcb97d0.json) verified608
PNGs; modified/reapplied phases each passed128 scenarios/10,160 assertions.
Representative current images were inspected. Root DPI and hosted HWND focus
checks do not establish physical-monitor/IME/game/network acceptance. The
original and intermediate failures remain linked above.

Public path checks and MOD/FOV draft regression preserve hash and persistence
boundaries. The separate [current firewall prerequisite](../evidence/hosted-public-firewall-dcb97d0.json)
still refuses FIREWALL_POLICY_UNAVAILABLE with InactiveProfile/Full and UNKNOWN
WFP coverage. Its temporary owned rules were removed; original policy was not
enabled or changed. Public configuration can be selected and saved, while
actual Public game startup remains blocked. C71, multiplayer matching/chat and
the original live/gameplay boundaries are not closed by this delivery.

## 2026-10-31 direct-controls correction

Status: **hosted UI verification passed** for source `d40bfc3`; unified-package
verification is tracked separately. The previous dcb package remains historical
evidence, not validation of this change.
The user clarified that general UI feedback did not authorize a redesigned main
screen. The comparison baseline for this correction is the delivered source tree
at `d207fc611be3a89351f8517fcbc37aeb18610dd4`.

The original SCBL main-screen arrangement is restored in the existing shell:
left launch/status/options and right sign-in remain in place, with Public/Local
beside direct MOD/Official choices and the FOV slider below. The extra main-screen
explanation, gameplay summary and “Game options / 调整玩法” entry are removed.
There is no separate gameplay settings page. DX11-only launch/migration, the
single settings entry, default Public, accurate unavailable Public status,
server configuration and confirmed Local discovery/join/create are preserved.
No backend implementation is rolled back.

The directly visible controls retain the previously requested draft behavior.
Save/Cancel appears inline after an edit, in a reserved row so the launch control
does not move. Cancel or Escape restores committed values; Reset changes only
the draft, and Save uses the existing atomic persistence service. An unresolved
dirty draft disables launch instead of silently using or saving it. Owned busy,
network transition and active-game boundaries disable the direct controls and
actions. Independent source review identified a transient draft/visible-value
mismatch during a mode transition; rejected input now preserves the existing
draft and resumes it after the transition, without changing saved values.

[The focused regression receipt](../evidence/ui-direct-controls-regression.json)
records two new portable source/layout contract failures on the old UI and
16/16 passing checks after the change. Isolated patch apply/reverse/reapply
repeats the real source checks with exact restored bytes, preserving the original
private four-role evidence. The unchanged draft service passes 21 real temporary
filesystem/persistence checks. These checks do not establish WPF rendering.

The actual WPF probe now checks direct-control visibility on the main screen,
absence of the removed entry, real shown-window input/focus traversal and direct
Save/Cancel/Escape behavior. The historical scenario token `settings-gameplay`
is kept for four-phase comparison, but now depicts a dirty draft on the main
screen. The existing DPI/language/window-size matrix and independent Public/
Local dialog checks remain. Packaged self-tests also exercise busy/transition
refusal without dropping a pending draft. Hosted execution and inspection of its
actual rendered images are required before the unified candidate is delivered;
Linux cross-compilation is not a screenshot or live gameplay result.

The first actual direct-controls visual run, **41191825904**, is retained as a
failure in [its receipt](../evidence/hosted-ui-direct-failure-72e92af.json). All
672 downloaded PNG hashes/dimensions and the official outer ZIP digest match.
Baseline/rollback each reproduce the two requested state failures 48 times, with
zero layout failures. Modified/reapplied each complete 128 scenarios but fail144
checks: 96 shared-row-edge overlap reports, 32 tight status-text measurements and
16 keyboard round-trip assumptions. None is silently marked passed.

Recomputation from the actual WPF rectangles proves the 96 overlap reports are
shared edges (maximum numerical height `5.7e-14` DIP), not covering area. The
observer now rejects positive-area intersections and includes a tiny-positive-
area negative control, retaining pairwise and real input-hit checks. The existing
status column receives a fixed350 DIP width, with wrapping and clipping checks
preserved. Keyboard validation now records actual bounded forward/backward focus
routes and requires every direct editor/action to be reachable in both directions;
the old undetailed failure does not establish which transition differed.

Representative actual PNGs also show missing chrome/footer glyphs in some96DPI
captures while the corresponding192DPI image is complete. Render work is now
flushed before capture, without modifying the bitmap afterwards. These follow-up
changes cross-compile and pass16 portable UI contracts; new hosted renders and
inspection are still required. No overall visual or complete-candidate pass is
claimed for72e92af.


The follow-up actual WPF run **41193432884** passed for source `d40bfc3`
(PR merge `4778c504`, identical tree). [Its verified receipt](../evidence/hosted-ui-direct-d40bfc3.json)
records the downloaded official ZIP, digest/CRC and all **672** PNG hashes and
dimensions. Baseline/rollback each reproduce exactly96 requested state failures
(48 per new direct-control requirement), with no unrelated failures. Modified/
reapplied each pass128 scenarios and **12,944** checks, including all five direct
editors/actions and both entry-focus checks on actual forward/backward HWND focus
routes. Original 72e failures remain intact.

The earlier missing-glyph observation above is **explicitly corrected**: it was
a display-preview anomaly, not a confirmed product or PNG defect. All160
modified/reapplied PNG pairs are byte-identical and each extracted PNG matches
the official ZIP. Reopening the same bytes shows the complete title, top-right
and footer. The cited Chinese96DPI gameplay image also has the exact same SHA
`5df2044669205c1c57f41c2db812890fa3dc0cdcfda8586c0e54e0986527f99c`
in the old72e run. Therefore the added Render flush is not claimed to have fixed
missing glyphs. No image was edited and no further product/probe change was made
for that viewing impression.

Nineteen original representative images were inspected across all eight states,
both languages, both sizes and96/120/144/192 root DPI, including dirty direct
controls, compact settings, long Public addresses, discovery, multiple-host join,
create confirmation and cancellation. This is representative manual review plus
complete hash/dimension verification, not a claim that every image received
manual audit. The small-window96DPI text remains smaller under the retained
Viewbox. Physical-monitor/IME/game acceptance, Public readiness, camera and
multiplayer/performance issues remain separate; this visual artifact is not the
complete development candidate.


The next source `e4f8990` keeps the entire launcher and visual runner unchanged.
Its new actual WPF run **41195418280** also passed. [The new artifact receipt](../evidence/hosted-ui-direct-e4f8990.json)
binds the PR merge `abdc1c2b` to the exact source tree and independently checks the
new downloaded ZIP digest/CRC plus all672 PNG hashes/dimensions. All672 images
and all512 scenario frame records (geometry, focus and checks) are identical to
the reviewed d40 artifact. Therefore the prior representative image review
applies to these same pixels; no repeated manual review of unchanged images is
claimed. B/R still have96 expected state failures each; M/RP each pass12,944
checks with no frame errors. No UI source was changed for this follow-up.
