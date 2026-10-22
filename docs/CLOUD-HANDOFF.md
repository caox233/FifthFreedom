# Cloud handoff

This is a migration checkpoint, not a completed UI or server integration.

The user explicitly prefers subsequent code changes in the cloud. The local
machine should finish source migration, privacy review, preservation and current
build verification only; UI/server integration and continuing bug fixes belong
to the cloud checkout whenever the platform permits them.

## Work order

Read `PROJECT-STATE.json`, `docs/MIGRATION.md`, and `docs/ACCEPTANCE.md`.
Confirm repository HEAD matches the handoff revision. Continue the first unfinished
milestone; preserve the imported sources and passed behaviors. Record actual
commands, outputs, exit codes and remaining blockers, then update the checkpoint.

The existing cloud session was verified to have a Linux x86_64 Debian 13 runtime,
working GitHub reads, Python and GCC, but no usable Rust or .NET at the initial
check. Recheck installed tools before installing anything. Read the cloud runtime
skill and its network policy. Do not assume the local `.local/` toolchain exists
in a cloud checkout; install supported tools from their official sources under
the environment policy, using the versions pinned by the source manifests.

## First implementation work

1. Run portable Python tests and the source-preservation/branding checks.
2. Establish a Linux server build from the existing donor and compare its protocol
   contracts with the LAN baseline before choosing the shared implementation.
3. Integrate the 3.0 UI against the retained LAN launch/configuration/diagnostic
   contracts. Preserve the original gameplay hooks as the authoritative baseline.
4. Add explicit LAN/Public mode interfaces and deployment documentation.
5. Produce one unified candidate after Windows build verification, not several
   unrelated launcher versions.

## Platform limits

The default build destination is GitHub-hosted CI, not the user's computer.
The Codex Linux workspace implements and reviews changes; Windows GitHub Actions
builds the WPF launcher, x86 hooks and Windows LAN server; Linux Actions builds
the public server. Do not substitute a self-hosted runner on the user's PC.
The first migration checkpoint currently has only portable source checks wired
at the repository root. CI-01 must add and actually run the Windows candidate
and Linux server lanes, retain logs/checksums/artifacts, and keep automatic
GitHub Release and production deployment disabled. Local builds are a fallback
or baseline check only; local game testing remains a separate acceptance step.

Linux cannot by itself prove Windows WPF rendering, MSVC x86 DLL behavior, DX11
rendering, audio, mouse smoothness, game crashes, three-player chat or real LAN/
public-network gameplay. Use a Windows CI builder when available and identify
missing SDK/resource requirements rather than making a stub successful package.
Final live acceptance belongs to user feedback. Do not automatically run a game,
collect a capture or start a public production server.

## Data boundaries

No real credentials, player data, raw logs, crash dumps, graphics captures or local
tool caches are supplied by the public repository. Default build resources have
explicit manifest pins; retain attribution and applicable terms. Never add live
service secrets to source or output. Any problem that needs private evidence must
be reported as such, without requesting repeated already-passed user tests.
