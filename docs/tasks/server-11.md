# SERVER-11 — parent-owned native activation lease

Status: **ready-for-agent** — parent IPC and real synthetic-child tests passed;
production profile/namespace/peer admission and actual native composition remain
separate work.

`src/public/fifth_freedom_control/native_lease.py` implements the Python owner of
SERVER-10's inherited Unix `SOCK_SEQPACKET` capability. Both endpoints enable
`SO_PASSCRED` before spawning. `OwnedChild` permits only this one explicit
connected owner-created socket beside its existing bootstrap FDs; it does not
expose general descriptor inheritance. The parent closes its copy of the child
endpoint after spawning and tracks the exact process with its pidfd and image.

The trusted owner supplies a fresh independent `LeaseBinding` callback. It must
derive package/config/runtime hashes and namespace/interface values from its
own observations, never from HELLO. This callback is an integration seam, not
cryptographic admission. Matching metadata does not prove a secure peer or
isolated egress. `create_public_supervisor` still rejects production startup.

Version `ff-public-owner-lease-v1` uses HELLO sequence0 with a random native nonce
and exact binding; ADMIT sequence1 echoes both and grants100–5000ms. RENEW uses
strictly increasing owner sequence numbers. Native READY sequence1 is separate
from that counter and follows actual listener binding. Parent readiness remains
false until READY. Packets are bounded4096 bytes, require exactly the allowed
JSON fields without duplicate keys, and have exactly one kernel credential
matching the owned child. Unexpected rights are closed before rejection;
truncation, wrong PID, malformed frames, changed binding and repeated readiness
permanently revoke the lease.

There is no background heartbeat. `OwnedSupervisor` obtains fresh overlay
observations before calling the native observer. A delayed cycle cannot renew
an expired lease. Revocation closes the endpoint synchronously before awaited
native cleanup; overlay cleanup still follows confirmed native death. Native
SERVER-10 independently enforces deadlines and parent lifetime. This trusts the
local deployment owner; it does not claim resistance to root or an owner issuing
false admission statements.

Actual Linux cloud validation:

- `python3 -B -m unittest discover -s tests/server -p test_native_lease.py -v`:
  9 methods passed, zero skips; independent reviewer reran9/9.
- `python3 -B -m unittest discover -s tests/server -p test_overlay_supervisor.py -v`:
  retained16 methods passed, zero skips; independent reviewer reran16/16.
- Actual owned interpreter ELF copies, Unix sockets, credentials, pidfds and
  cleanup ran. The peer is deliberately synthetic. Cases cover valid exchange,
  no READY, changed binding, expired cycle, wrong child/grandchild, malformed or
  oversized/duplicate frames, forbidden FD transfer without leakage, and
  revoke-before-cleanup ordering.

No game, EasyTier core, TUN, old deployment or production service was started.
Python/Rust positive composition and privileged isolated native startup require
their own receipts. The nine methods do not establish either result.

The actual SERVER-10 composition exposed a distinct preparation clock: native
hashes the real service/client package before sending HELLO. A Debug build can
take longer than three seconds there. The parent's pre-HELLO budget is therefore
separate (default30s, explicit maximum60s), while native's post-HELLO three-second
ADMIT deadline and all lease/READY limits stay unchanged. The supervisor's
explicit overall startup budget can be at most60s; each owned exec bootstrap
remains bounded30s. A real delayed synthetic peer tests both successful3.1s
preparation and a shorter budget's refusal. With this additional case, the
parent suite has10 methods; no package result is inferred from the delay model.
