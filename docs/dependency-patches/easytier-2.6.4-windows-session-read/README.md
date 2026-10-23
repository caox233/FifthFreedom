# Current typed Windows NIC read

Status: implemented; eight linked helper tests and eighteen source/build boundary
checks passed locally. Full paired Windows codegen for this additive patch is
pending. This is a read-only development interface, not a Public lease issuer.

The independent tenth patch extends the original nine-patch Windows preparation.
The previous patches, original upstream bytes, driver probe and successful
[41fe262 full-build evidence](../../evidence/hosted-windows-easytier-41fe262.json)
are preserved. The backend itself is unchanged from the
[319bf44 actual retained-session probe](../../evidence/hosted-wintun-319bf44.json).
The new combined source inventory is
`efb765a226be8ad35826c71868b105f09d5d6971b9d58580754389efec058fcd`;
its ordered ten-patch digest is
`8123326e2c84d5909cab0a14d7c8f6539d73dea59a4e7ad5485f8ae361f68476`.
The transformed Cargo lock remains
`93933b3d72622fb04692d11defdf1ebc5ed979c5feedefceb74114bd2db6b6fc`.

## Actual owner path

`Instance.nic_ctx` is an `Arc<Mutex<Option<NicCtxContainer>>>`. Its opaque slot
holds either the actual `NicCtx` or a fallback task set used while the NIC is
absent. `Instance::observe_windows_session(source)` now locks that current slot,
requires an exact typed downcast to `NicCtx`, and invokes the existing actual
`NicCtx::observe_windows_session(source)`. That method reads the retained weak
Wintun session observer under the inner NIC mutex and checks current kernel
identity, operational state and the explicitly supplied IPv4 address. No
interface is adopted by name and no configured address becomes ownership proof.

The implementation is behind the existing `windows` plus
`ff-windows-session-observer` development feature. The ordinary Public adapter
stays unavailable. The return value is only a `SessionIdentity` snapshot; it may
be stale immediately after return and does not carry a reusable admission right.
The caller must eventually bind live source/route observations to an owned
process, owner IPC and authenticated peer state. Those producers are still
separate work and are not replaced by this accessor.

## Lock and cancellation semantics

The shared helper owns one two-second timeout spanning acquisition of the outer
container mutex and the inner asynchronous NIC read. It holds the outer guard
until the backend observation finishes so a queued replacement cannot swap in a
different NIC mid-read. Replacement is serialized after that read; this is not a
claim that a merely queued close has already completed. Timeout, cancellation or
an error drops the future and releases the guard. The accessor does not clone a
Session, adapter or observer owner. Independent native worker/owner revocation is
still checked by the existing backend, including its post-query invalidation
check.

The examined writers (`use_new_nic_ctx`, `clear_nic_ctx`, resource clear and
`Instance::drop`) acquire the outer container lock. New NIC creation/configuration
occurs before insertion. Existing NIC configuration/task paths acquire the inner
NIC lock but do not re-acquire the outer container lock; this addition introduces
no reverse lock ordering. A long inner async wait can delay a queued replacement
only until the common async deadline/cancellation releases the guard.

The deadline bounds asynchronous waits. Tokio cannot forcibly interrupt a
synchronous kernel query once it starts; neither tests nor documentation claim
otherwise. A future process-level runtime harness must retain its independent
hard child deadline. This patch starts no process or driver and performs no
network configuration or packet send.

## Verification and exact source binding

The standalone test crate imports
[source/windows_session_read.rs](source/windows_session_read.rs) directly. The
same exact bytes are inserted into `easytier/src/instance/windows_session_read.rs`
by the patch. The preparation recipe checks both copies against the same digest
and refuses a mismatch. Test source/Cargo manifest/lock are pinned as well.
These tests exercise the actual production selection/timeout/guard function
using explicit synthetic observations; they do not pretend to implement or run
a Wintun driver. The thin real `NicCtx` binding is included in the full Windows
core/CLI build.

The eight cases cover absent/empty/wrong-type containers, explicit source delivery
to the current typed context, replacement ordering and the next current context,
independent revocation during a pending read, outer wait timeout, inner wait
timeout, a combined 1.5-second outer plus 0.6-second inner wait exceeding the
single two-second budget, and cancellation. The latter controls prove guard
release and subsequent owner disposal. No successful observation can reopen a
revoked native session.

Actual local commands:

```sh
cargo +1.95.0 test --locked --offline \
  --manifest-path tests/native/windows-session-read/Cargo.toml --lib
python3 -B -m unittest discover -s tests/ci \
  -p test_ci_easytier_windows_build.py -v
```

Results: 8 passed / 0 ignored and 18 passed / 0 skipped respectively. Real pinned
archives were prepared with the additive patch, all modified/new bytes checked,
and the patch reversed to exact original hashes. No full EasyTier compilation or
DLL execution was attempted on this Linux workspace. The paired Windows builder
will run exactly eight minimal contracts, reject missing/ignored cases even if
the test process exits zero, and then build the actual feature-enabled core/CLI.
No Packet-linked core/test executable is run and no Npcap requirement is relaxed.
Only source/build JSON receipts are uploaded.
