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

## Actual Python/Rust composition and isolated main harness

A later local composition used the production `NativeOwnerLease` with the real
Rust lease test peer and all five retained services on ephemeral loopback ports.
Both directions carried kernel credentials; READY, renewals, endpoint revocation,
exit92, exact-child reaping and private fixture removal passed. This fixture
immediately pinned handles for its own raw `Popen` before wrapping them in
`OwnedChild`; it does **not** claim to exercise the production bootstrap or the
actual Public main path. Its receipt is
`.local/evidence/cloud/server10/python-native-composition.json` and the initial
local command-path error is retained separately.

`scripts/ci_native_lifecycle.py` supplies the separate privileged main test. Its
input must be the **same run/attempt/commit** component-only candidate already
verified by the native dual-identity reader. It checks every source hash and
scope, copies into a fresh root-owned private tree, and runs the copied real
native verifier. Original downloaded files are never chmodded, chowned or
rewritten. Root's private-key ancestry rules require a trusted temporary parent;
`/tmp` is the root-owned sticky parent and every fresh test directory is0700.
The runner-owned checkout/`RUNNER_TEMP` ancestors are not relaxed or changed.
Only the sanitized result is written to the requested artifact report location.
That exclusive final receipt is0644 for the ordinary runner's artifact uploader,
including under a restrictive sudo umask. Private inputs remain0600/0700; no
downloaded or shared directory ownership is changed. The ordinary runner must
create the report directory before sudo; the harness rejects a missing parent
instead of creating a root-only directory that the uploader cannot traverse.

The harness reuses SERVER-06's exact owned namespace handles. Server, admitted
peer and wrong-interface peer have only the private veth topology and no default
routes; no host link, route, firewall or sysctl is changed. The Python parent
runs inside the server namespace and uses actual `OwnedChild.spawn` and
`NativeOwnerLease`. Runtime/profile digest values are deliberately synthetic
native-protocol test inputs. No EasyTier, encrypted peer, game or production
admission provider is started or claimed.

Five bounded cases cover withheld admission and mismatched admission before
state creation, followed by actual startup with endpoint EOF, lease expiry and
parent death. READY must follow all five retained listener callbacks; the parent
also matches their kernel socket inodes to its exact child. The admitted peer
performs real configuration/content HTTP and API TCP connections. The wrong
interface cannot complete the three TCP connections. A real established TCP
connection carrying an HTTP/2 preface must close on revocation (no HTTP/2
handshake or account RPC is claimed), and all three TCP/two UDP ports must be
released afterward. Timeouts do not count as a closed established connection.
The parent-death case uses an isolated subreaper and the exact pinned native
pidfd so the adopted child is reaped, without PID scans or process-group kills.
Image validation takes place after confirmed worker-child ownership transfers
the pidfd to outer cleanup, so an image mismatch cannot abandon that child.
CI SIGTERM triggers bounded finally cleanup; repeated SIGTERM is ignored during
cleanup. SIGKILL or loss of the whole runner cannot run userspace cleanup.

Example for the dedicated isolated GitHub-hosted Linux job:

```sh
mkdir -p "$RUNNER_TEMP/native-lifecycle"
sudo python3 scripts/ci_native_lifecycle.py \
  --package "$CANDIDATE" \
  --candidate-receipt "$CANDIDATE/CANDIDATE-RECEIPT.json" \
  --runner-temp /tmp --report "$RUNNER_TEMP/native-lifecycle/receipt.json" \
  --commit "$GITHUB_SHA" --run-id "$GITHUB_RUN_ID" \
  --run-attempt "$GITHUB_RUN_ATTEMPT"
```

Successful output requires private-tree removal, unchanged source/copy package
bytes, all owned children reaped, all three owned namespace handles removed,
and all five cases passing. The receipt keeps `encrypted_overlay_verified`,
`production_provider_open`, `game_executed`, `complete_windows_client`,
`deployed` and `published` false. No raw worker logs, configuration, key, database,
private path or private config hash is uploaded.

Current local harness evidence is **9 portable input/copy/privacy guard tests**,
plus **3 Linux owned-interpreter cleanup fault tests** covering image mismatch
after child pinning, unconfirmed parent rejection without signalling unrelated
processes, and repeated SIGTERM through finally cleanup. All12 passed locally;
the three Linux-only methods are explicitly platform-skipped on Windows. These
tests do not create namespaces or native listeners. There is also an actual
isolated-Python entrypoint refusal with
`ROOT_NAMESPACE_PRIVILEGES_REQUIRED`, before any report or namespace creation.
This cloud session has not run the privileged main topology. A hosted positive
receipt is required; missing privileges are a failure, never a passing skip.
The earlier Python synthetic-child tests and local Rust loopback composition
remain distinct from that pending hosted result.

## First hosted entry audit: sudo checkout ownership

A read-only audit before the first actual native lifecycle run found that this
harness used unscoped Git commands after sudo. The checkout belongs to the
ordinary runner; its Git safe-directory configuration is not root's global
configuration. Unlike the separate EasyTier/stack runner, this command could
fail before package checks with Git's dubious-ownership refusal.

The fix scopes `safe.directory` to the exact absolute checkout for each of the
two read-only Git invocations. It does not write global configuration, trust
`*`, change ownership, or bypass the expected HEAD and tracked-clean checks.
Git execution errors are reduced to a fixed code, without raw stderr in the
public diagnostic stream.

A real throwaway Git repository with Git's own different-owner test switch
reproduced exit128/dubious ownership before the change and exit0 for the scoped
read. The regression also rejects an incorrect expected revision and changed
tracked bytes, while its empty global configuration remains unchanged. Retained
lifecycle/input checks plus this test: 13/13 passed locally and independently
reviewed with the same 13/13 result, zero skips. This tests Git's
ownership boundary; it does not replace the first privileged native run.

The same audit checked the current producer's additional notice files: its
20 recorded files (plus the separate receipt/checksum list) remain within the
existing 64-file bound. Every recorded notice/provenance file is hashed and
copied by the existing candidate validator. The four-role Windows admission
identity and separate Linux service identity are unchanged. Native CLI fields,
raw configuration digest, owner credentials, READY sequence1, five actual bind
callbacks and EOF/expiry/parent-death cleanup agree with the harness inputs.
No additional definite startup blocker was identified in that source review;
actual hosted execution remains the deciding evidence.

## TCP listener ownership: established and retired connections

The actual hosted native lifecycle run39932526946/job120355233722 at source
528668e failed with `WORKER_NATIVE_LISTENER_OWNERSHIP_INVALID`. Its public log
does not identify the failing case, port or socket rows. That failure remains
historical evidence; the local reproduction below does not establish those
missing details.

Review found a concrete error in the shared native/stack observer: it counted
every TCP row with a matching local address and port before requiring LISTEN.
An accepted connection (ESTABLISHED), or a retired connection (TIME_WAIT), can
legitimately share that tuple with the one owned listener. The check now selects
TCP LISTEN rows before requiring exactly one row and an inode held by the exact
pinned child. Exact IP/port, image checks, the socket-table size bound, and UDP's
existing state07 and uniqueness requirements remain unchanged.

Three Linux regressions use a verified private copy of the Python interpreter,
an exact owned child, three TCP and two UDP sockets on ephemeral loopback ports,
and actual kernel socket tables. The old checker rejects both a real held
ESTABLISHED connection and a subsequent real TIME_WAIT connection alongside the
owned listener; the corrected checker accepts both. A remaining accepted socket
without its listener, a different process's listener, and two simultaneous
LISTEN rows still fail. The duplicate-listener control uses SO_REUSEPORT only
inside the test child; no production socket option changed.

The full local harness suite passes 16/16, including all prior input and cleanup
tests. These controls do not execute the native artifact or create namespaces,
TUNs, game processes or a production provider. A new isolated hosted native and
combined-stack run is still required to determine their actual result.
