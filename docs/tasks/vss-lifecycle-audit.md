# VSS scope lifecycle — current-source audit

Status: **ready-for-agent**, **needs-info**. Read-only audit on 2026-10-22;
no confirmed cause or new failing regression for `vss-scope-lifetime` was found.
This does not close that issue or alter the accepted VSS EMF/F3 results.

## Inspected lifecycle and evidence

| Path | Current source evidence | What this establishes |
| --- | --- | --- |
| Attachment FIFO | `mac11_combat.rs:31–38` invokes native `CC14C0` once, then passes its returned object to `vss_attachment::complete`; `vss_attachment.rs:65–73` reloads that object's config through `4D43E0`. | The completion path does not issue a second attachment factory/enqueue call. The native FIFO implementation itself is outside these Rust sources. |
| Supplemental weapon lookup | `scis_visual.rs:13–29,61–72` scopes the manager in thread-local storage, suppresses only its default-upgrade import, restores that scope with `Drop`, and requires the original FP object to be returned. | The extra view lookup is explicitly prevented from adding default attachments; no persistent object cache is introduced here. |
| Import flags | `vss_attachment.rs:23–49` requires a clean three-word view context, rejects nested/incomplete imports, and restores all three originally-zero words with `Drop`. | The normal Rust-return/unwind path restores that context. This is not a claim about native access violations or concurrent engine imports. |
| Owner and lease | `scis_scope_actor.rs:81–88` checks pawn address/name, world, current actor slot, actor type/name/world. `:153–201` creates only an eligible missing local scope; `:175–183` retires an exact previous owner only after possession moved in the same world. | The guards distinguish changed pawn/actor names and worlds. Source does not establish whether every possible engine address reuse also changes these name values. |
| Destruction/reset | `scis_scope_actor.rs:265–288` clears the lease, then destroys only an actor still owned by that lease. `:376–400` disables hooks and clears the lease without calling actor functions from the unload thread. | Normal pawn cleanup and DLL unload deliberately use different ownership paths. A new map's old actors remain the native world's responsibility. |
| Entry and late resources | `scis_scope_policy.rs:19–34` defers inactive entry for missing local controller/viewport and allows active scopes to tick/exit. `scis_resource_policy.rs:13–17` retries only recognized geometry keys with zero ready sections; `scis_resource_request.rs:25–43` uses the campaign bank and a reentry guard. | Controller readiness, resource references, resource payload readiness and actual rendering are separate checks. A retry call is not proof that asynchronous payload completion or a draw occurred. |
| Bank restoration | `scis_render_context.rs:13–21` restores an acquired context through `Drop`; `scis_render_bank.rs:63–71` checks the prior bank/name/flags and disables the adapter if restoration differs. | The current source does not silently treat a different restored context as success. |

Paths above are relative to `src/native/client/hooks/gameplay/src/`.
The inspected entry points were `vss_attachment.rs`, `scis_scope_actor.rs` and
`scis_preload.rs`; their current helper/caller sources supplied the rows above.
No historical transaction scripts, private logs, game binaries or captures were
read or executed.

## Actual portable execution

A small ignored Rust test wrapper used `#[path = "…"] mod …;` to compile these
four **unchanged production files directly**:

- `scis_scope_policy.rs`: 2 tests.
- `vss_scope_policy.rs`: 1 test.
- `scis_resource_policy.rs`: 1 test.
- `scis_render_context.rs`: 3 tests.

Compiler: existing `nightly-2025-10-15`, `rustc --edition=2021 --test`.
Compilation exited 0; execution returned **7 passed, 0 failed, 0 ignored**,
exit 0. The wrapper did not extract/reimplement functions, link a game DLL, call
retail addresses or load a native replay fixture. It therefore does not execute
the `ensure`/native-create/`release` sequence or the attachment import itself.

The wrapper, commands, outputs, source SHA256 values and inspected commit are in
ignored `.local/evidence/cloud/vss-lifecycle-audit/20261022T220218Z/receipt.json`
and sibling files. No product source, acceptance record or project state was
changed by this audit.

## Smallest next regression

Add an explicitly owned x86 native callback replay for **late geometry on an
already-created VSS scope actor**. Keep controller/viewport readiness valid so it
does not repeat the already-covered entry-readiness case. Supply the same pawn,
world and actor identity, valid normal/ADS resource references, but zero loaded
geometry sections at the first entry. Then make the same resource's sections
available and execute subsequent native scope ticks.

Record native binding attempts, actor creation/destruction, scoped state and
attachment factory/FIFO counts before and after that single transition. The
required behavior is eventual binding of the current actor without extra
attachment imports or replacement of another generation's actor. If the native
baseline already recovers, preserve that counterexample and move to the next
lifecycle; do not invent a failing assertion or report this hypothesis as a root
cause. A changed-generation control should confirm that a late callback cannot
act on the replacement owner.

The missing input is a reviewed, portable replay of the relevant native
create/bind/tick behavior. The seven passing Rust policy tests cannot supply that
behavior. Until a genuine failure is obtained, no product fix is proposed and no
new user acceptance is requested.
