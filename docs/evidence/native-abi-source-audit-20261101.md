# Non-camera native ABI source audit

Status: **needs-info** for native target/caller verification. This is a source
audit, not a game run, crash reproduction, or proof that the remaining native
signatures are correct. The machine receipt is
[native-abi-source-audit-20261101.json](native-abi-source-audit-20261101.json).

The fixed source is `e4f899072c53e2ea1079b8dd1e54616026357b21`. The reported new
runs loaded its main/gameplay hashes recorded in
[the complete-package receipt](complete-package-download-e4f8990.json); they
must not be attributed to the earlier package mismatch. The reported execute
fault at EIP `1` and address fingerprints are not a complete unwindable stack.
In particular, gameplay `+1B5690` is an exception thunk, not an identified caller.

The separately confirmed camera Disable mismatch is documented in
[the binary mapping](native-disable-binary-map-20261101.json): the supplied
native `B52610` body ends at `B5271F` with `C2 04 00`, but the old hook declaration
omitted the stack argument. The camera implementation/replay owns that repair.
Cleanup's supplied `B52608: C3` and virtual slot `+4D8` are not changed by this
audit. The additional rows below do not have equivalent newly reviewed native
instruction/callsite evidence.

## Reproduce the inventory without changing the worktree

```sh
python3 -B scripts/audit_native_abi.py --revision e4f899072c53e2ea1079b8dd1e54616026357b21 --output .local/evidence/cloud/protocol-feedback-20261101/abi-declarations-e4.json
python3 -B -m unittest discover -s tests/ci -p test_native_abi_audit.py -v
```

The inventory finds 245 lexical `static_detour!` declarations in 68 files:
219 `thiscall`, 11 `stdcall`, 8 `C`, 7 `cdecl`. It includes test/disabled-feature
code, does not resolve installation/cfg, and does not enumerate every manually
built vtable or patched callsite. It is a lexical subset, not a Rust type resolver.
Simple named pointer targets are treated as sized. Unsupported aggregates,
fat pointers, hidden aggregate returns and `fastcall` register allocation remain
unknown; `ats_counter::RangeHook(AtsEntry)` is the actual unknown stack shape in
this inventory. No result says that a native target matches its Rust declaration.

## Selected production declarations and forwarding review

Sizes below are **declared i686 MSVC ABI obligations**, not disassembled retail
epilogues. For `thiscall`, the receiver is ECX and is excluded from stack bytes.
Return locations describe the declared value; `void` makes no EAX preservation
claim. None of the following native game bodies was executed by this audit.

| Boundary | Declaration / stack bytes | Return | Source result and remaining boundary |
| --- | --- | --- | --- |
| Storm EventHandler | thiscall, callee 20 | EAX | Receiver and all five arguments forwarded; diagnostic work precedes original call and can invoke a native virtual method. |
| Storm SessionPeer* handlers | thiscall, callee 16 | EAX | Four stack arguments forwarded; original event/object lifetime and native epilogue not newly verified. |
| HostMigrationEndProducerB | thiscall, callee 28 | EAX | Seven stack arguments forwarded; native signature not inferred from Rust arity. |
| SessionRestartProducer | C, caller 20 | EAX | Five stack arguments forwarded; declaration differs intentionally from neighboring thiscall wrappers, native evidence still required. |
| StormStateMachineActionExecute | thiscall, callee 8 | void | Original three values forwarded; optional diagnostic hook invokes event `global_event` before forwarding. |
| Winsock recvfrom / sendto | stdcall, callee 24 | EAX | Six original arguments forwarded. recvfrom buffer/address constness is a Rust mutability issue, not a different x86 stack layout. |
| Winsock bind / connect / WSAConnect / closesocket | stdcall, callee 12 / 12 / 28 / 4 | EAX | Forwarding arity agrees with declarations; no socket/network execution in this audit. |
| Quazal InitMessage / AddMethodID | cdecl, caller 12 / 8 | void | Original argument count/order retained. No newly inspected native epilogue. |
| Quazal SendRMCMessage | thiscall, callee 8 | EAX | Original receiver and two arguments retained; no authentication/session bypass. |
| MAC-11/SCIS Register / Attachment / Lookup | thiscall, callee 4 / 8 / 4 | AL / AL / EAX | Existing typed aliases and forwarding agree. Existing ignored external ABI fixture is not an executed result of this review. |
| SCIS Apply / UpdateScope / DestroyPawn | thiscall, callee 4 / 4 / 0 | void | `UpdateScope` takes one f32 stack argument. Raw native create/destroy calls are also declared thiscall(receiver only); their actual return and cleanup remain unverified here. |
| SCIS resource Build / texture Queue | thiscall callee 4 / C caller 8 | void | Original arguments retained; native resource construction/destruction lifetime is not closed by declaration consistency. |
| SCIS preload weapon / upgrade / VFX / FName wrappers | thiscall, callee 16 / 4 / 4 / 12 | EAX / EAX / void / EAX | Manual callsite wrappers are outside the static-detour inventory; declaration/call forwarding reviewed separately. |
| SCIS render bank Select / Load / Current | C, caller 16 / 4 / 0 | void / void / EAX | Manual native pointers, outside detour inventory; native bodies and concurrent lifecycle are not verified by source shape. |
| VSS property parser / VSS and SCIS LoadConfig | thiscall, callee 12 / 16 | EAX / void | Manual native pointers at `511680` / `4D43E0`; wrapper arity agrees internally, actual native return/caller contract not newly verified. |

Source anchors: `src/native/client/hooks/src/hooks/storm.rs`, `hooks.rs`,
`hooks/quazal.rs`; `src/native/client/hooks/gameplay/src/mac11_catalog.rs`,
`scis_scope_actor.rs`, `scis_resource_request.rs`, `scis_texture_request.rs`,
`scis_preload.rs`, `scis_render_bank.rs`. The JSON receipt pins these and the
additional manually reviewed files at e4.

## Virtual calls and lifecycle gaps kept open

- `storm.rs::get_storm_event_name` reads virtual slot `+4` and its cached ID.
  If the cache is null, it calls the method using a receiver-only thiscall and
  dereferences its returned pointer. `event_handler` invokes this lookup before
  forwarding even with ordinary event tracing disabled. The separately optional
  `hooks.rs::storm_statemachineaction_execute` calls `global_event` for its log.
  Thus these paths are not wholly passive reads. This is not demonstrated as the
  EIP=1 cause, and no event behavior was changed without that evidence.
- `scis_catalog` builds XML tables with slots `+60`, `+44`, `+DC`, `+2C`, `+E0`.
  They declare respectively pointer / pointer / u8 / i32 / cursor-parent values;
  only `+60` has a four-byte explicit stack argument. The copying cursor returns
  u8 from parent; the recording wrapper and native parent typedef declare void.
  Both have zero stack arguments, so this alone is not a cleanup mismatch. A
  native caller's use of AL has not been established; the source comment that
  retail ignores it is not new binary evidence. Same-declaration Rust fixtures
  cannot prove that contract. These five slots are not the reported `+4D0/+4D8`.
- SCIS scope creation/destruction and lease retirement share pawn/resource
  lifetimes. Pinned addresses, owner checks and synchronous fake objects do not
  establish complete native callback/reentrancy or concurrent destruction safety.
  The sound interior bridge saves/restores registers, flags and FP state by
  source inspection; it is outside the typed-detour inventory and was not run.
- The supplied `EF1A00/EF1A2D/EDA030/EDA244/CE1D8D/5C9514` fingerprints were not
  located as registered hook targets. `42DAB0` appears as the progression array
  resize helper, not a detour installed on that helper. A common helper address
  does not identify the originating caller. No names/types are assigned to
  `EDA030` or the two reported virtual slots from those fingerprints alone.

## Next independent ABI controls

Use owned memory and child-isolated tests, no retail process: a hand-authored x86
caller with explicit stack setup/cleanup should call the actual production
wrapper through a synthetic native target with independently specified `ret N`.
Check ECX, each distinct argument, ESP before/after, nonvolatile registers and
declared EAX/AL return. Include unhooked/hooked/restored/reapplied and wrong-pop
negative controls. Priorities beyond camera are EventHandler (ret20), recvfrom
(ret24, no socket I/O), the three catalog callbacks, and scope Apply/Update/Destroy
(ret4/ret4/ret0, including f32 bit patterns). This validates forwarding against
the authored stub; exact native bytes/caller evidence are still needed to prove
the retail ABI. These additional native replays were **not executed** here.
