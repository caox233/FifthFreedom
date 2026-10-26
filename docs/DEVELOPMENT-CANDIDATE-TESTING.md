# Unified development candidate testing

There is currently **no newly verified complete unified download**. Component
artifacts and their checksums are not an installable complete package. This page
records the acceptance steps for the next admitted candidate, not a request to
test incomplete artifacts. No formal Release or production deployment is enabled.

## Inputs still needed before complete packaging

The public checkout verifies 12 of 20 required build inputs. The missing eight
are `src/launcher/src/Assets/DefaultSaves/{MOD,Official}/0000000{1,2}.{sav,meta}`.
The twelve-file privacy hold also includes four donor files outside this Windows
package. A matching hash proves identity, not absence of private data. Complete
packaging requires genuinely reviewed default templates, or a reviewed,
reproducible default-data generator with appropriate manifest updates. Do not
upload personal saves, invent bytes or disable validation. The existing private
`--asset-dir` import option is not publication approval.

The separate C71 gate requires the real x86 `native_fixture.rs`, native bytes or
an auditable extraction/relocation generator, fixed source hashes and reviewed
provenance/publication scope. Its 13 retained replay checks have not run in this
public checkout. An unrelated diagnostic fixture does not satisfy that input.
The gate also needs reviewed manifest admission when those inputs arrive; file
presence alone is insufficient. Neither gap requires a local PC to remain on
while independent cloud code/build work continues.

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
