# Progression table allocation-range refusal

Status: **needs-triage** for the four live refusals; controlled Windows source correction verified. Recorded2026-10-31 (Asia/Hong_Kong).

The private-log review reports four runs keeping the retail56-row curve after
`allocation-range` and five enabling the999-row curve. It does not retain the
actual allocator byte count, virtual-memory layout or per-search player level.
The issue is separate from an unproven greater-than56 matchmaking cause.

The source confirms a narrower defect: `progression::project_with` asks the
shared `writable` predicate to accept999 ×24 =23976 bytes in **one** VirtualQuery
region. A valid single allocation with multiple committed writable protection
regions is refused. The shared render predicate intentionally remains unchanged.

The scoped correction validates every region of the bounded table before any
slice/copy, requires one nonzero AllocationBase, rejects guard/noaccess/uncommitted
or nonwritable tails and arithmetic/alignment errors, and checks the returned
capacity still covers999 rows. Neighboring independent reservations cannot be
combined to satisfy the replacement. Only then is the complete table committed;
otherwise only the rejected replacement is released and the original header and
all retail rows remain unchanged. Reload validates the complete readable span.
No new offsets, signature pins, rank values, XP, rewards or save fields are used.

The retained allocator contract is the pinned0x42dab0 thiscall taking Array*
and element size24, with `{data:null,len:0,cap:999}` on entry. Current source
inspection and the synthetic callback verify these arguments, **not execution of
the retail allocator or its real allocation size**. The relevant machine-code
fixture is unavailable in this cloud checkout. AllocationBase is a reservation
identity, not proof of an individual heap block's requested size. The existing
native allocator ownership contract is still required; VirtualQuery snapshots
are not a concurrent lifetime lock. No blanket exception is made for an unknown
range or neighboring allocation.

`progression_region_tests.rs` calls the actual production `project_with` against
owned Windows VirtualAlloc/VirtualProtect memory. One reservation is split into
two actual writable protection regions without executing any memory as code.
It checks all999 projected rows,55/56/57/999 thresholds, original56-row identity,
exact old-allocation release and idempotent reload. Guard, noaccess, read-only,
decommit, unrelated adjacent reservations, undersized returned capacity,
misalignment and overflow must refuse before a partial copy and preserve the
original bytes. The allocator callback uses owned synthetic memory; no retail
game, real allocator, player or save is executed by these tests.

Windows execution and baseline/modified/rollback/reapply of those precise
production bytes are required before claiming the code correction verified.
The shared native production replay runner provides this lane; Linux protocol
tests alone cannot establish its result. Even after that controlled regression
passes, the four real `allocation-range` cases and actual multiplayer matching
remain unconfirmed until a candidate's targeted runtime outcome is available.


The exact `e4f8990` Windows component job `41195418269 / 124520488334`
now executes this lane in all four roles. Baseline/rollback each reproduce the
two specified failed assertions; modified/reapplied each pass both real Windows
memory tests, with zero ignored tests. The original production bytes and compiled
test source hashes are bound separately in the
[official four-role receipt](../evidence/hosted-native-production-e4f8990.json).
This progression source needs no address translation. It establishes the bounded
single-allocation/multiple-region correction and negative controls; it does not
execute the retail allocator, prove actual heap-block extent or explain the four
reported allocation-range refusals or real greater-than56 matching symptom.
