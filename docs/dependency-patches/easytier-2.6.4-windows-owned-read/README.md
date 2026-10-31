# Additive Instance-owned Windows read endpoint

`owned-read.patch` is an independent addition to the successful ten-patch paired
Windows source (`efb765a2…`). The existing recipe and source evidence remain
unchanged. `receipt.json` pins the original/patched four files and resulting
whole paired inventory; only `launcher.rs`, `lib.rs` and the two new modules
change. The two files under `source/` exactly equal the added production bytes
and are directly linked by `tests/native/windows-owned-read`.

Apply only after checking the declared original file identities. The first two
files must match exactly and the new paths must be absent; patch bytes must match
the receipt. The existing bounded `ci_easytier_windows_build.apply_patch` helper
can apply it. A future combined builder must pin its complete ordered patch list
and recompute the complete source identity, including any separately reviewed
no-Packet or peer additions. This receipt does not silently add those patches to
the successful ten-patch recipe.

Eight native contract tests and Windows-target type checking cover the linked
protocol/owner modules. The actual full-core launcher hook still needs a combined
hosted build. The [actual Windows synthetic pipe and native-helper lanes](../../evidence/hosted-owned-read-4b49da7.json)
passed managed 19, native 8, interop 4 and retained owner 17 with zero skips.
This source addition executes no core, driver or game and grants no
Public admission. See [the task and exact lifecycle boundaries](../../tasks/owned-core-read.md).

## Independent complete-source composition

`scripts/easytier-windows-owned-read.lock.json` pins the separately successful
[no-Packet eleven-patch input](../../evidence/hosted-windows-no-packet-82dbda3.json)
and this additive patch. The original recipes are unchanged. The independent
[combined receipt](combined-receipt.json) binds the twelve ordered patches,
703 EasyTier files plus 49 paired tun files, and whole-tree digest
`980bb96687a83f7da040d8aa4c4f7134c1c07183637510f2f39a805eaa08a294`.
Only the four declared owner-read paths change from the no-Packet tree. Its
Cargo lock remains `37c7e066…`; the canonical archives and original trees remain
preserved. Reverse/forward application restored both complete inventories.

`scripts/ci_easytier_windows_owned_read.py` verifies those identities before and
after preparation and compilation. It executes the eight linked owner-read
contracts, then invokes the existing no-Packet builder's eight interface checks,
eight typed-session checks, paired metadata resolution, selected Windows
dependency graph, full release core/CLI build and normal/delay import checks.
It does not add peer-observation or nonce patches, run either executable, load
drivers, or upload binaries. The new job uploads only its run, complete source,
and aggregate build JSON receipts. Nested delegate receipts/logs stay on the
disposable runner. A failing delegated import or changed source leaves a failed
outer receipt, without publishing successful binary identities. Failed delegate
stages retain only bounded source-bound error codes, known locations and log
digests after strict projection; raw output and unknown fields are rejected.

Fourteen local wrapper tests cover pinned inputs, real small git-apply transitions,
unrelated-source mutation, changed linked modules, native test count and delegated
failure propagation. Compilation is mocked only in orchestration tests and is
not full-core evidence. Actionlint passes. The combined actual Instance hook
still awaits its own complete hosted Windows build; neither the earlier
no-Packet binary nor the thin Rust helper proves that build. A later finite
owned-core/NIC experiment needs the newly compiled image and separate runtime
checks; this build job does not activate it.
