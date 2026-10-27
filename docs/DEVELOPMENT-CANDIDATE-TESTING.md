# Unified development candidate testing

There is currently **no newly verified complete unified download**. Component
artifacts and their checksums are not an installable complete package. This page
records the acceptance steps for the next admitted candidate, not a request to
test incomplete artifacts. No formal Release or production deployment is enabled.

## Input admission and remaining independent gates

All 20 required Windows build inputs are present, including the eight original
default SAV/META paths admitted from owner-confirmed public templates on
2026-10-28. The [handoff receipt](development-reference/default-save-inputs-20261028/receipt.json)
records unchanged sizes/hashes and the review limits. Default-save restoration
remains included. Four donor-only privacy holds remain; no personal saves were
admitted. Actual input-gate and full-build results must be recorded separately.

The separate C71 gate still requires thirteen reviewed fixture/native/provenance
inputs and has executed zero replay checks. It is not a dependency of the
Windows complete-package job. A full Windows candidate can be produced while
this distinct replay gap and ordinary Public service admission remain open.

## Delivery requirements

The delivery entry must identify the source commit, actual successful hosted
package run, complete candidate ZIP, `SHA256SUMS.txt`, `BUILD-RESULT.json` and
download location. Verify the ZIP digest and package admission from the same run.
Windows packaging uses `python -B scripts/build.py all` followed by
`python -B scripts/ci_candidate.py windows-package-smoke`; this is distinct from
the compile-only component lane. The Windows package does not by itself certify
Linux Public service readiness. Public remains unavailable until its independent
ownership, network and runtime acceptance is satisfied.

## Focused user checks after delivery

- Confirm Fifth Freedom branding, SCBL3 presentation and retained LAN create/join,
  save separation/restoration, diagnostics and observer entry points are usable.
- For [quick matching](tasks/quick-match-same-session.md), use at least two clients
  on the same server and mode; confirm independent queue entry leads to the same
  room/game. One successful client or two separate lobbies does not pass.
- For chat relay, retain the pending three-client all/team/opponent checks. A
  sender and host alone cannot prove delivery to another joining client.
- Check only the still-open or newly affected issues in
  [remaining bugs](tasks/remaining-bugs.md). Preserve accepted F3 alignment,
  MAC-11 HUD, Chinese input/world camera and VSS EMF results; no blanket repeat is
  requested. No game or capture is launched automatically by cloud delivery.

Record unverified Windows/GPU/real multiplayer behavior as pending. Offline
protocol, unit, build and isolated hosted probes are separate evidence levels.
