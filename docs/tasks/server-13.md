# SERVER-13 — owned isolated EasyTier development probe

Status: ready-for-agent. Harness and focused local checks implemented; actual
hosted QUIC/WSS/TCP results are still required. Production Public admission stays
closed. This does not complete the native owner-lease integration or live gameplay
acceptance.

## Fixed scope and inputs

`scripts/ci_easytier_probe.py` is an explicit development operation for a
GitHub-hosted Linux runner with existing TUN, NET_ADMIN and SYS_ADMIN privileges.
It is separate from the build-only workflow. It never installs a driver, runs the
game, uses historical endpoints or opens a production listener.

The same job must first build the six-patch guard3 source using the reviewed
`scripts/easytier-source.lock.json` and `scripts/ci_easytier_build.py`. The probe
checks the receipt's exact upstream/build/lock/ordered-patch identities, all 35
finite contracts and successful Release step. Guard2 receipts fail. Actual core
and CLI bytes must match that job's build receipt; local historical ELF hashes
are not substituted for a new hosted build.

The native synthetic echo executable is built from
`src/native/server/quazal/examples/socket_policy_probe.rs` with
`cargo build --locked -p quazal --example socket_policy_probe` in `src/native`.
Its TCP, UDP and ephemeral UDP listeners use the production interface-bound
`SocketPolicy`. This is not a gameplay server or native owner-lease simulation.

Python uses `scripts/requirements-profile.txt` (`cryptography==50.0.0`). Profile
generation, validation and RPC parsing reuse the production
`fifth_freedom_control.easytier_profile` module. No duplicate recipe schema or
handwritten X25519 implementation is introduced.

## Isolation and ownership

For each transport the parent creates exactly two fresh owned namespaces and a
private veth pair directly between them. Underlay and overlay use separate RFC
1918 /30 pairs. No host link, route, sysctl, NAT or forwarding change is made.
Both IPv4 and IPv6 routing tables must have no default route.

Before core startup, each namespace receives an nftables inet ruleset with
default-drop input/output/forward chains. Permitted traffic is limited to:

- the exact loopback TCP RPC address/port and its responses;
- the exact veth IPv4 peer pair and selected responder listener port (QUIC uses
  UDP; WSS/TCP use TCP), with established reply direction;
- TCP/UDP on the exact owned overlay interface and pair, including the native
  probe's ephemeral ports and responses.

All remaining packets reach drop counters or drop policy. The complete nft JSON
ruleset is read back and compared, preserving expression order and rule order
within each chain. Only handles, metadata and numeric counter values are
normalized; extra accept rules, tables or changed policies fail. Rules and routes
are checked again after the positive/negative controls. TCP STUN is contained,
not claimed disabled. Counter totals show blocked packets without identifying
them as STUN; no packet capture is performed.

Each child enters through `easytier_probe_worker.py`. The parent first acquires
the worker pidfd, then releases its startup barrier. The single-thread worker
checks the inherited namespace fd identity, calls `setns`, confirms its own
namespace identity, and only then starts asyncio or children. It directly uses
production `OwnedChild.spawn` for the core and `KernelTunMonitor` for the exact
child's TUN fd/interface/address. An `ip netns exec` wrapper is used only for
short setup/readback tools, never as the core's owner.

Core/CLI/native-probe bytes are copied only after same-snapshot size/hash checks
to a root-owned private directory under `/tmp`, then validated again with
`VerifiedExecutable`. No runner checkout ancestry is loosened. The core's output
is discarded because upstream configuration output can contain secrets.

CLI and native READY output use a development-only bounded pipe wrapper around
the existing production verified-fd bootstrap (hash, parent-death barrier,
no-new-privileges and fd exec). CLI queries have a 65,536-byte limit and two-second
deadline; failure, overflow and timeout kill/reap the exact owned process before
releasing its handles. CLI stderr goes to DEVNULL, and raw RPC output is never
written to an artifact. The query is the fixed verbose peer-list command for the
same candidate and namespace. The parser checks the expected direct connection,
network, local/remote Noise keys, authentication level 3, role, endpoints and
overlay route. Its route-joined view is not a complete connection inventory or
an independent production admission decision.

Each role gets CSPRNG X25519 material and a shared 32-byte random PSK through
private memory/files. Secret TOML uses the reviewed owned-directory, exclusive
0600 writer. Private manifests/configs/keys and runtime ELF copies are excluded
from artifacts and removed at cleanup. Core/CLI launch environments contain only
fixed PATH/LANG/TZ values.

## Required runtime controls

The matrix includes product Fast/QUIC and Compatible/WSS plus TCP as a development
control. Each transport retains the same responder core and native echo process
through all three phases:

1. Correct expected server pin: both owned TUNs and bounded authenticated peer
   observations must be ready; TCP/UDP/ephemeral UDP echoes must pass.
2. Stop/reap only the initiator and restart it with the same local key, network
   and PSK but a different valid expected server public key. Repeated actual echo
   attempts over at least eight seconds must fail, while its core/TUN and the
   responder core/TUN/native probe stay alive. This avoids treating an immediate
   pre-handshake timeout, parser-only rejection or crashed infrastructure as the
   pin negative. It is a bounded observation window, not a perpetual guarantee.
3. Stop/reap that initiator and restore the correct pin; authenticated readiness
   and all three echoes must pass again with the same responder and containment.

Finally the exact responder core is stopped/reaped, its TUN must disappear, and
new echo attempts must fail. This last assertion demonstrates owned overlay
lifetime effects. It does **not** assert production native readiness revocation,
accepted-session closure or owner-lease enforcement; those need the separate
real native lifecycle harness/composition.

Source-level Noise/AEAD/ingress/pin contracts and actual runtime observations are
separate evidence. Encryption flags, RPC fields or a successful echo alone are
not packet-level cryptographic proof. The finite 35 source contracts are retained
with their original scope; no broader exploit claim is added here.

Cleanup uses only retained worker/child handles and exact created namespace
identities. It attempts every owned child/descriptor even if an earlier close
fails. One SIGTERM/SIGINT unwinds cleanup; repeated signals cannot interrupt that
cleanup. SIGKILL cannot yield a cleanup receipt. No process-name searches, global
PID kills or namespace-prefix deletions are used. Missing acknowledgement or
cleanup errors make the receipt fail.

## Execution and evidence

The new explicit hosted workflow should run the source unit suite, same-job
guard3 build and native probe build, then invoke the absolute setup-python binary:

```sh
mkdir -p artifacts/ci/easytier-probe
sudo --non-interactive \
  --preserve-env=GITHUB_ACTIONS,RUNNER_ENVIRONMENT,GITHUB_SHA -- \
  /absolute/setup-python -B scripts/ci_easytier_probe.py \
  --build-receipt /same-job/build/receipt.json \
  --core /same-job/target/release/easytier-core \
  --cli /same-job/target/release/easytier-cli \
  --socket-probe /same-job/native/socket_policy_probe \
  --socket-probe-sha256 ACTUAL_SAME_JOB_SHA256 \
  --output artifacts/ci/easytier-probe/receipt.json
```

Only a sanitized receipt is eligible for the probe artifact: candidate/source
identities, profile digests, transport/phase results, drop counts and cleanup
status. The ordinary runner pre-creates the artifact parent; the new sanitized
receipt is explicitly 0644 even under a private sudo umask. No shared ancestry
permissions are changed. No ELF, private stage, configuration, raw RPC or runtime log is uploaded.
Third-party source/license publication holds remain independent; running this
explicit synthetic test does not approve redistribution or production use.

Local verification on 2026-10-23:

- `python -B -m unittest discover -s tests/ci -p test_ci_easytier_probe.py -v`:
  17 passed. Tests include real benign Linux ELF fd execution/overflow/timeout
  cleanup, receipt identity rejection, full ruleset mutation rejection,
  infrastructure negatives, matrix cleanup and per-child cleanup failure handling.
  Unit tests do not start EasyTier, TUN or namespaces.
- Actual probe entrypoint: exit 1, fixed stdout failure code, zero stderr bytes;
  failures are `GITHUB_HOSTED_REQUIRED`, `ROOT_NAMESPACE_PRIVILEGES_REQUIRED`,
  `NET_ADMIN_AND_SYS_ADMIN_REQUIRED`, `EXISTING_TUN_REQUIRED`, `NFT_REQUIRED`.
  No core or namespace was started. Command/output/receipt originals remain at
  `.local/evidence/cloud/server13-probe-precondition/`.

The first actual hosted probe,
[run 39910723452](https://github.com/caox233/FifthFreedom/actions/runs/39910723452),
completed the same-job guard3 GNU build and native socket-probe compilation, then
failed the runtime step with `CONTAINMENT_CHANGED` and
`TRANSPORT_MATRIX_INCOMPLETE`. Official metadata is preserved in
`docs/evidence/hosted-owned-overlay-0ac6695.json`. The strict nft readback check
stopped the run; no successful overlay result is inferred. Raw nft output was
not recorded by that version and the artifact redirect is unavailable through
the current official network path. No fallback bypass is used.

To get the missing evidence before another expensive build, the script now has
an explicit `--containment-only --output PATH` mode. It uses the same hosted,
capability and tool preconditions, creates the same two owned namespaces for each
transport, installs the same rules and checks the complete readback/routes, then
cleans up. It does not load a build receipt or start any core, CLI, TUN or native
probe. Its distinct `ff-easytier-containment-preflight-v1` receipt marks the
recipe identity synthetic and `runtime_executed: false`; a pass means only that
this containment setup/readback contract passed.

For a mismatch, both modes retain `CONTAINMENT_CHANGED` and include at most 16
fixed structural diagnostic codes. Paths use only source-defined JSON key names
and indices 0..63. Known enum/operator changes, type differences and bounded
counts are described; unknown names, keys, addresses and raw values are not
printed. The equality decision is unchanged. Possible nft canonicalization is
still a hypothesis until the next real readback supplies evidence; no expression
or operator is ignored to obtain a pass.

Two subsequent real preflights,
[39913525945](https://github.com/caox233/FifthFreedom/actions/runs/39913525945) and
[39914866601](https://github.com/caox233/FifthFreedom/actions/runs/39914866601),
reported the same first-rule difference: `NFT_I5_RULE_EXPR_COUNT_6_TO_5`, followed
by `META_MISSING_KEY`, `PAYLOAD_EXTRA_KEY` and `RIGHT_TYPE_STRING_TO_INTEGER` at
expression index 3. Normalized item 5 is the first input loopback TCP sport rule:
its explicit `meta l4proto tcp` preceded a typed `tcp sport` payload match.

The generator now omits that explicit redundant L4 predicate **only** in rules
which already contain a typed `tcp`/`udp` `sport`/`dport` expression. The typed
header selector still requires its transport protocol. Portless overlay rules
retain their explicit `meta l4proto` match. The full readback comparator,
normalizer, IP/interface/port constraints and default-drop policies are unchanged.
Tests mutate the typed protocol, port field and port value and continue to reject
each difference, as well as extra accepts and changed policies.

The protocol dependency was also checked in the actual
[Ubuntu-hosted nftables 1.0.9 source archive](https://archive.ubuntu.com/ubuntu/pool/main/n/nftables/nftables_1.0.9.orig.tar.xz):
971,968 bytes, observed SHA256
`a3c304cd9ba061239ee0474f9afb938a9bb99d89b960246f66f0c3a0a85e14cd`.
This is a recorded source download, not a claim to have independently reproduced
or pinned the runner's nft binary. `src/parser_json.c:628` resolves the named
protocol/header field into a non-raw payload expression;
`src/evaluate.c:771` generates and prepends a missing protocol dependency;
`src/payload.c:401` constructs the required protocol equality, with
`payload_gen_dependency` at line 544 selecting the lower-layer dependency.
On readback, `src/netlink_delinearize.c:2069` and `src/payload.c:925` remove a
redundant displayed dependency when a higher-layer payload implies it. The
archive and selected original source files/hashes are retained privately under
`.local/dependencies/nftables_1.0.9.orig.tar.xz` and
`.local/evidence/cloud/server13-nft-source/`; no downloaded source or binary is
added to the project runtime.

The focused probe suite has 20 passing tests. The narrow correction then passed
the actual privileged quick preflight:
[39916016496](https://github.com/caox233/FifthFreedom/actions/runs/39916016496), job
120289657927 at `f85c515168c35a7d727b0f936ad2254be144e036`. Official GitHub metadata
reports success for the checked containment/cleanup step and its sanitized
receipt upload. The workflow applies the strict three-transport/two-owned-
namespace receipt validator. Bounded metadata is retained in
`docs/evidence/hosted-containment-canonical-f85c515.json`; individual artifact
fields were not independently downloaded through the unavailable archive path.

This is an actual containment configuration/readback/cleanup pass, with no
EasyTier or native runtime execution in that quick workflow. It resolves the
observed predicate presentation mismatch while leaving strict comparisons in
place. The complete owned runtime matrix is a separate run and is still needed;
no full overlay or production admission success follows from this preflight.

The subsequent full run
[39916016529](https://github.com/caox233/FifthFreedom/actions/runs/39916016529),
job 120289658048, passed containment and compiled the reviewed runtime/native
probe, then failed with `WORKER_REPLY_EOF`, `OWNED_WORKER_CLEANUP_FAILED` and
`TRANSPORT_MATRIX_INCOMPLETE`. Its original worker error handler discarded the
exception and stage. These codes do **not** establish whether core startup, TUN
ownership, peer authentication or packet transfer had succeeded. Raw artifact
contents remain unavailable through the attempted official archive path.

The worker now emits a bounded `ff-easytier-worker-failure-v1` protocol record
after its child cleanup attempts. Exact source-defined phase/error enums and a
separate cleanup result are the only fields; private configuration, RPC output,
exception messages and runtime stdout/stderr remain excluded. The parent rejects
unknown schemas, codes, extra keys and inconsistent cleanup booleans. A failure
record always fails the transport and matrix. Its cleanup acknowledgement replaces
the normal stop acknowledgement; the parent still waits for its exact worker and
does not infer child reaping from worker exit or parent-death signals. Failed
spawns that return no ownership handle conservatively report their internal
cleanup unconfirmed. A short-lived peer-query child whose construction or close
does not return confirmed ownership cleanup also fails immediately with a
separate `CLI_CHILD_CLEANUP_UNCONFIRMED` result; it cannot become an ordinary
unauthenticated observation or a claim that all children were reaped.

The new real short-lived subprocess regression also exposed a parent cleanup
race: after a failed stop command, an already-readable pidfd previously bypassed
`Popen.wait()`. The parent now always waits/reaps that exact child. The test checks
that a second `waitpid` reports no remaining child. This reproduced cleanup bug
is independent of the still-unknown cause of the hosted worker EOF.

The focused suite now has 25 passing tests, including actual bounded failure
protocol/reaping and independent source-error plus cleanup-error injection. No
EasyTier core, TUN or namespace runs in those tests. No timeout, containment rule,
readiness check or production admission gate was relaxed; another actual hosted
run must supply the missing runtime diagnosis.

The next full run,
[39919260513](https://github.com/caox233/FifthFreedom/actions/runs/39919260513),
job 120300259865 at `6c11c8b85633ea5e2977ea0131c86ee01b316bbb`, failed with
`OVERLAY_READY_TIMEOUT` and `TRANSPORT_MATRIX_INCOMPLETE`. The original sanitized
receipt was subsequently obtained through the normal official artifact path:
its sole row is QUIC, with no completed transfer phases, no cleanup errors,
two namespaces created and removed, and the private stage removed. It did not
record the last TUN/query observation, so those states remain unknown for that
run. The lack of a worker-failure annotation is not an authentication success.

Source inspection identified the QUIC bind-address parser mismatch documented
in [SERVER-12](server-12.md). Its narrowly corrected parser and producer-linked
synthetic serialization evidence do not retrospectively assign a root cause to
this hosted timeout. The next probe also records a strict
`ff-easytier-readiness-timeout-v1` object on timeout: the role, expected
authentication mode, bounded observation count and final fixed observation.
That observation distinguishes TUN pending, CLI query failure, parser rejection
and authenticated state; parser rejection carries a source-defined reason enum.
It includes only checked booleans and fixed state/bind-kind strings, never raw
RPC, addresses, keys, exception text or arbitrary upstream strings. Unknown and
extra fields, inconsistent states and a successful state represented as a
timeout are rejected. It is a last observation, not a lease or readiness grant.

The 15-second readiness deadline, query output/lifetime limits, namespace/nft
containment, exact-child cleanup and production admission gate are unchanged.
Current focused checks pass: 27 probe, 12 profile, 18 production owned-overlay
monitor and 20 combined-stack tests. The real upstream serialization example
also passed without network activity. Actual QUIC/WSS/TCP matrix acceptance
still requires a subsequent hosted run with these changes.
