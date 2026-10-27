# Unified development candidate testing

The complete **Windows LAN development candidate** is now available from
[GitHub Actions](https://github.com/caox233/FifthFreedom/actions/runs/40594742870/artifacts/12777311164). It includes the launcher, both native DLLs, Windows LAN
server and the admitted default templates; default-save restoration is retained.
It does not include the retail game or enable ordinary Public service. All six
migration goals remain in progress; this delivery is not a formal Release.

## Verified download — 2026-10-28 (Asia/Hong_Kong)

- Built source: `da1805e6764ac202bf6b8c1a9548fa2c44d4c085`; actual PR checkout merge
  `4c6e345ca195dfaea1a37662d62e8338090f647a` has the same source tree.
- Hosted run `40594742870`, full Windows job `122508092763`: full build,
  packaged no-game smoke and upload all succeeded.
- Sign in to GitHub to download the artifact. Extract its inner
  `fifth-freedom-1.1.0-migration1-program.zip` (73,276,517 bytes), then unpack
  that program ZIP completely into a new writable directory. Artifact retention
  currently expires **2026-11-11 06:41 (Asia/Hong_Kong)**.
- Program ZIP SHA256 (not the outer artifact ZIP):
  `b33abbb35f76f387e42a53d2186024337de28e1ec6acf167d6e5ac2374f855f4`
- Outer artifact SHA256:
  `12da5a82e4f3580b9a530a047cb3fb140eb5474bc178ddcc4d418b401dcaf358`.

On Windows, `Get-FileHash .\fifth-freedom-1.1.0-migration1-program.zip -Algorithm SHA256`
checks the inner archive. Keep all `runtime`, `package` and `licenses` files
alongside `FifthFreedom.exe`; do not mix DLLs or overwrite previous player data.
The outer artifact also carries `BUILD-RESULT.json`, checksum lists and actual
smoke/build evidence. Read the program's `README.txt` for the verified LAN setup.

Actual packaged checks passed **166 UI/protocol + 26 LAN-tools/save checks**,
with zero failures and no game execution. Official/MOD restoration used isolated
temporary fixtures and exact authorized template bytes. Independent read-only
download checks verified all **65 files**, 63 manifest entries, 64 checksum rows,
four embedded component identities/PE architectures and all 50 FusionFix notice
and corresponding-source files. See [hosted proof](evidence/hosted-complete-package-da1805e.json)
and [download verification](evidence/complete-package-download-da1805e.json).

The same source passed Linux 672 + 2 skips and Windows 558 + 116 skips; its Linux LAN
suite passed 523 with 0 failures/ignored, including the four new empty-lobby protocol
cases. Those cases do not establish retail matchmaking order or live same-game
entry. The first full build at `6f720` failed at the update-note checksum path and
remains recorded; strict UTF-8 reader correction `45b5c6b` is verified by this new
complete Windows build. No old failure artifact or compile-only payload is
presented as this complete program ZIP.

## Brief LAN test

1. All participants use this same complete candidate and select LAN, their actual
   IPv4 adapter, distinct accounts, and the same Official/MOD/gameplay mode.
2. One participant creates the host; others join that host's real IPv4:50051.
   Follow `README.txt` for the manual trusted-network firewall rules, then start
   the game manually. The cloud build has not started a game or capture.
3. At least two players test quick matching with an existing room and an empty
   lobby, confirming the same roster and game. Three MOD players test all/team
   chat relay. Report a short pass/fail and affected stage; new logs/captures are
   not requested. Do not repeat accepted F3/MAC-11/Chinese/world-camera/VSS checks.

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
