# SERVER-16 — owner-controlled Linux observer admission

Status: **ready-for-agent**. This closes the native operator-policy seam left
explicitly open by SERVER-03 and SERVER-10. It does not open the ordinary Public
CLI or the supervisor's production admission provider.

The existing protected deployment JSON accepts one optional field:

```json
{"observer_allowed": false}
```

This fragment supplements the existing schema-1 document; it is not a complete
deployment configuration. Omission defaults to false. Only JSON booleans are
accepted; null, numbers, strings, duplicate fields and unknown fields fail the
same bounded strict parser. The entire original JSON byte sequence remains the
configuration SHA bound into the owner lease. Changing this option therefore
requires the owner to admit the new configuration hash.

The native startup still verifies the independent service package, admitted
Windows catalog, protected key/data paths, actual interface and source policy,
then receives ADMIT and arms the existing watchdog. Only after those checks does
it install the validated observer option. A scoped guard owns this process's
policy through service execution and closes it if startup or services return.
Lease EOF, expiry or owner death still terminates the native process and closes
its listeners. The next process starts false, independently of the previous
process and its data directory. There is no persisted toggle or environment
override. LAN's existing private stdin controller and EOF handling are unchanged.

This is a startup policy. Editing the private file is not online revocation;
the owner must stop/revoke and start a newly admitted process. The read-only
`--check-public-deployment` path never installs policy, even for a document with
`observer_allowed: true`; its `observer_enabled` and `startup_allowed` reports
remain false. Neither this policy nor a successful static report proves overlay
security or authorizes ordinary Public startup.

The actual capabilities, login precheck, pre-run-creation recheck, signed grant
and final response recheck consume the existing single native policy. Observer
admission remains limited to an explicitly requested valid game run. Account
login cannot obtain a grant; ordinary players do not require observer permission.
An accepted grant is signed and bound to the exact run instance. A Python bridge
request or its own observer option cannot enable native policy. The bridge must
still enforce its independent operator policy and current identity/run checks.
Closing policy does not retroactively rewrite already issued grants; existing
run retirement, account revocation and token expiry remain distinct checks.

## Focused evidence

The new private JSON tests call the production `prepare` function with inert,
explicitly synthetic four-role PE and Linux ELF package inputs. They verify
default/false/true, exact raw-byte hash changes, malformed and duplicate values,
static false reports, and unchanged input/state snapshots. No synthetic binary
is executed or described as a candidate.

The lease integration uses the existing isolated native test subprocess and real
AF_UNIX credential messages. Its private configuration and auth identity/key come
from the real preflight consumer; namespace and runtime bindings are explicitly
synthetic test values. It never starts Public listeners. Only after matching
ADMIT does it run actual Users handlers, private SQLite and HMAC issuance:

- Explicit true produces matching capabilities and a signed game-run grant.
- Omitted/false rejects the actual observer metadata request before credential
  last-login, run or ticket changes, while ordinary login remains usable.
- An account observer request is rejected without state changes.
- The real pre-run and final-response checks reject when the scoped policy
  closes at deterministic test checkpoints; final denial does not promise a
  rollback of an already-created run, and no token response is returned.
- The owner closes the credential socket after the handler checks; the exact
  child exits with watchdog code 92. Fresh omitted/false processes do not inherit
  the previous allowed policy. Reusing the old configuration hash after changing
  the option fails before policy installation or any child state writes.

These are source/contract checks, not gameplay or a production observer session.
Historical native token fixtures remain byte-for-byte unchanged. The independently
verified b67 hosted native five-case result predates this feature and the later
pre-revocation OPEN barrier; it is not evidence for either new behavior.

Validation logs and exact source hashes are retained locally under
`.local/evidence/cloud/server16/`. The first compile stopped because the workspace
was full. Only ten regenerable dedicated-server incremental cache directories
were removed; source, executables, dependency outputs and prior evidence were
retained. The fresh focused compile/test result is recorded separately from that
environment failure.

The subsequent focused run passed **3/3** new tests. The exact compiled native
test executable then passed the complete **370/370** dedicated-server suite with
zero ignored tests. Python consumers passed **11/11** current native-token
fixture tests and **14/14** authenticated bridge tests. No historical fixture was
regenerated. These checks ran on Linux; the new owned-policy/lease cases are
Linux-only, while the unchanged LAN controller remains covered by the native
suite and existing Windows lane.

Next: independent review and inclusion in the next source/component checks;
exercise opt-in observer behavior in a separately admitted development profile
when the full owned composition is ready. Keep production Public closed and do
not substitute a bridge flag or old handshake metadata for runtime admission.
