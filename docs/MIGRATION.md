# Fifth Freedom migration

## Ordered scope

1. Unify product and launcher branding as **Fifth Freedom**.
2. Adopt the SCBL 3.0 interface while retaining LAN behavior and accepted repairs.
3. Windows server serves LAN; Linux server serves public deployments.
4. Publish reviewed source and a separate development knowledge base on GitHub.
5. Transfer implementation to the existing cloud environment after source and
   documentation arrive; the user tests complete candidate packages.
6. Consolidate development inputs and records into this root. Remove old roots
   only after dependency closure, reproducible builds and preservation checks.

The user additionally requested that the existing build DLL, music and default
save resources accompany the source. This does not include player accounts,
personal saves or logs and does not change third-party ownership or licenses.

## Baselines and seams

`src/` contains the accepted 702-file LAN candidate (release multiplayer3), with
initial naming changes. `integration/scbl3/` preserves 594 donor files, including
its UI, Linux services, Windows services and build resources. Native gameplay
behavior must not be replaced by the older 3.0 gameplay implementation.

The UI currently remains the LAN interface. The donor UI is not integrated merely
because its files are present. Port its presentation through explicit interfaces
to retained launch, diagnostics, account, game-mode and save modules. Network
selection must use separate LAN and public adapters; do not inherit EasyTier-only
assumptions into LAN routing or silently create public listeners in LAN mode.

Public services require authentication, validation, persistence, transport and
deployment review. A successful localhost test does not prove Internet readiness.

## Compatibility identities

Display labels and executable filenames can change independently of persisted
data directories, embedded resource logical names and native protocol markers.
Keep those legacy identities during the first migration so existing accounts,
saves and package checks remain interpretable. Any later migration needs explicit
upgrade and rollback tests; blanket string replacement is not acceptable.

## Retained development data

Private copies of the original project state, retained 14-item record and latest
EMF receipts are in `.local/migration-stage1/`. Hash maps identify source snapshots.
The original candidate and donor repositories are unchanged. More historical
native replay scripts and evidence still require dependency-led migration before
old directories may be deleted. Build caches are not evidence.

## Completion gates

- Launcher uses the 3.0 UI and all retained LAN feature contracts remain covered.
- Windows/LAN and Linux/public build and test independently with documented inputs.
- Clean-checkout CI checks source and packages; licensed build inputs are explicit.
- Public source and knowledge base contain no personal data or credentials.
- Cloud has the exact uploaded revision, can execute its next task, and records
  remaining Windows-only, graphics, networking and real-game acceptance limits.
- A complete candidate is delivered for user testing, without claiming offline
  verification is live acceptance.
- No source, unique evidence or required dependency is lost during cleanup.
