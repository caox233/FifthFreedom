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
hosted build. The new Windows synthetic pipe and native-helper execution lanes
are pending. This source addition executes no core, driver or game and grants no
Public admission. See [the task and exact lifecycle boundaries](../../tasks/owned-core-read.md).
