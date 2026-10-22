# Fifth Freedom

Read `PROJECT-STATE.json`, then the named next action. This repository is the new
development root. Keep the accepted LAN implementation in `src/` until each
replacement passes its contract tests; `integration/scbl3/` is the pinned UI and
public-server donor, not a second release product.

## Fixed destination

Use the SCBL 3.0 UI, retain LAN features and accepted repairs, Windows server for
LAN and Linux server for public deployment. Track the remaining bugs alongside
migration, not as a prerequisite for migration. Follow `docs/MIGRATION.md` and
`docs/ACCEPTANCE.md`. Update the next executable action with concrete receipts.

## Verification and privacy

Separate source/unit/build/GPU-replay evidence from user-reported and independent
live acceptance. Preserve passed F3 alignment, MAC-11 HUD, Chinese input, stable
world camera and VSS EMF results. Run focused regression before integration.

Keep `.local/`, build outputs, accounts, live configuration, logs, crash dumps,
captures, private keys and tokens outside Git. Approved default build resources
are listed in `scripts/local-assets.json`; personal saves are not default assets.
Use environment placeholders for deployment secrets. Retain third-party notices.

## Work records

Use local Markdown tasks in `docs/tasks/`, with needs-triage, needs-info,
ready-for-agent, ready-for-human or wontfix. `docs/ACCEPTANCE.md` preserves scope
boundaries; private raw history stays in `.local/`. No automatic game launch,
diagnostic capture, production deployment or GitHub Release. A development source
push is not a release. Cloud-specific checks and the handoff are documented in
`docs/CLOUD-HANDOFF.md`.
