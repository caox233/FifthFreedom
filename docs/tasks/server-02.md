# SERVER-02 — authenticated public query bridge

Status: **ready-for-agent**. This record preserves the initial callable
authentication/query seam and its executed evidence. Subsequent native key/grant
work is tracked in [SERVER-03](server-03.md), and the real HTTPS listener and
Linux lifecycle checks in [protected transport](public-transport.md). The full
game runtime and launcher Public adapter remain unconnected. This is not a
deployed public service or a six-milestone completion claim.

## Exact inspected compatibility contract

The preserved donor `server/control_plane_auth.py` and
`server/dedicated-server/src/api.rs` use:

- `v1.<base64url JSON>.<base64url HMAC-SHA256 signature>`;
- unpadded canonical base64url, signature over the ASCII `v1.<payload>` string;
- a 32-byte key, 4096-byte token maximum, 300–604800-second lifetime,
  a maximum 60-second future-issued allowance, and expiry strictly after now;
- signed user ID, canonical username/source IP, issue/expiry time and revocation
  version, followed by an authoritative database identity check.

The retained LAN issuer additionally signs `release` as the immutable storage
key `1|release_id|compat_id|manifest_sha256` and optional
`game_run: {product, instance}`. Account tokens have no game-run authority. These
signed fields, not request headers or the package declaration, establish the
identity passed to the read-only query adapter.

At the end of SERVER-02, two concrete differences blocked process integration
(the subsequent SERVER-03 implementation addresses these explicit seams):

1. The LAN issuer generates a new private signing key inside its server process
   on each start. The donor used an explicit `SCBL_AUTH_TOKEN_KEY`. There is no
   reviewed trusted key/lifecycle handoff from the retained native issuer to a
   separate public control-plane process. Merely configuring an unrelated key
   cannot make its tokens valid, and no key is extracted or printed by this work.
2. Observer admission is returned as native login-response metadata tied to the
   admitted run. It is not a signed token field. The bridge therefore cannot
   infer an observer grant from a request boolean or an extra token property.
   Observer requests remain denied until a trusted grant handoff is implemented.

## Production implementation

`src/public/fifth_freedom_control/authenticated_bridge.py` adds
`AuthenticatedSessionBridge` with explicit bridge contract version 1. It ports
the small donor verification path and uses standard-library `hmac`/`hashlib`,
preserving the token algorithm and envelope. It introduces no token issuer,
automatic key generation, environment loader, listener or deployment action.
`NOTICE.md` records input paths, baseline and retained GPLv3 notices.

`AuthBridgeConfig` accepts exactly 32 immutable bytes, or exactly 64 hex characters
through `from_hex`. Missing/malformed keys and unsupported bridge versions fail
with bounded reason codes; key contents are excluded from its representation.
TTL/skew/token-length limits are fixed to the inspected contract and cannot be
widened through request data. The injectable clock must produce a bounded integer
Unix timestamp. Clock exceptions and token errors do not include raw values or
chained private exception messages.

The bridge verifies the signature before decoding claims; rejects duplicate JSON
keys, invalid UTF-8, NaN/non-integer numeric claims, oversized/recursive data and
noncanonical encodings; validates source and time; and requires the signed
release plus native product/run nonce. Legacy donor account-only tokens are
rejected rather than upgraded. It calls the production `SessionReader` on every
request so package admission, current database revocation, run ownership,
source/mode/release/freshness and room filtering remain mandatory. It does not
return or cache a standalone "verified" identity that could skip those checks.

`source_ip` is a transport-owned argument: a future listener must supply its
observed authenticated connection source, never blindly copy a forwarding header
or JSON field. The configured issuer key and verified package/network policy are
also server-owned inputs. Calling this in-process function is not evidence that
those production trust boundaries have been implemented.

## Executed checks

From the repository root:

```bash
python3 -B -m unittest discover -s tests/server -p test_authenticated_bridge.py -v
python3 -B -m unittest discover -s tests/server -v
```

The focused bridge run executed **14 tests, all passed**, exit 0. The combined
server suite executed **42 tests, all passed**, exit 0. All SQLite databases,
signing keys, tokens, accounts and room records in these checks are synthetic.
Actual LAN migration SQL and the production bridge/reader are exercised; no real
database or service is opened. The successful read preserves database SHA256.

Covered failures include wrong source/signature, expiry/future issue/invalid TTL,
oversized token, duplicate JSON and malformed types, missing/wrong-sized key,
unsupported version/protocol, revoked user, missing/retired/different native run,
missing/mismatched signed release, missing/mixed package roles, forged observer
extension, absent database and invalid/throwing clock.

The literal donor Rust `fixed_token_vector_is_valid` vector reaches the new
release-required gate with its correct synthetic key/time/source, proving the
preserved envelope/signature is recognized. Altering its signature is rejected
before that gate. New signed LAN claim-shape fixtures use a test-only standard-
library issuer for malformed-input coverage.

### Current native producer → Python bridge

`tests/server/fixtures/native_game_token_v1.json` is a new, explicitly synthetic
fixture produced by the current Rust `release_for_login` and `issue_auth_token`
functions. Its two fixed 32-byte test key epochs, account, timestamp, package and
game nonce are not runtime configuration. No historical fixture was changed.
The Rust test serializes the current producer output and compares every byte
with this checked-in JSON; Python consumes exactly those tokens without a
replacement signing implementation. Fixture SHA256:
`ba92c9917c8de43dd6b83ba31c0d985cba88cc868171d4872ce25cbd5416c663`.

The first native run used an empty new fixture and an opt-in
`SCBL_SYNTHETIC_BRIDGE_FIXTURE_OUT` path under ignored evidence. It generated the
actual producer receipt and failed the byte comparison as expected: **1 passed,
1 failed**, exit 101. The generator uses `create_new` and refuses to overwrite an
existing path. After reviewing and copying that receipt to the new fixture:

```bash
# With the documented cloud Rust/protoc toolchain activated, from src/native:
cargo +nightly-2025-10-15 test --locked -p dedicated_server native_bridge_fixture_tests -- --nocapture
# From the repository root:
python3 -B -m unittest discover -s tests/server -p test_native_token_fixture.py -v
python3 -B -m unittest discover -s tests/server -v
```

The focused native run passed **2/2**, the cross-language Python run passed
**5/5**, and the updated complete server Python suite passed **47/47**, all exit
0. Both languages accept each epoch's own producer token and reject both
crossed key/token pairs. Python also verifies the actual vector's source,
expiry, signature tampering, database revocation and run retirement; the
successful query leaves the synthetic database hash unchanged.

This closes the synthetic producer/consumer byte-compatibility gap. It does not
establish a real process key handoff, automatic key rotation, protected public
transport, observer authorization or a deployed public service. The native
runtime still generates its independent random key per start; only `cfg(test)`
code was added to its API source.

### Windows fixture cleanup follow-up

The GitHub-hosted Windows candidate run
[39859917025](https://github.com/caox233/FifthFreedom/actions/runs/39859917025),
job 120107639036, reported **14 errors** in the original 28-test server suite:
every `PublicSessionReaderTests` method errored. API annotations identified the
methods, but a full exception traceback was not available at diagnosis time.
This is an observed Windows failure, not a passing candidate build.

Inspection found file-backed SQLite test connections using a transaction
context manager that commits/rolls back but does not close the connection.
Windows temporary-directory cleanup can fail while those file handles remain
open. All 11 occurrences across the reader, authenticated-bridge and native
fixture tests now combine `contextlib.closing` with the existing connection
transaction context, preserving commits and closing before cleanup. In-memory
schema tests already register explicit connection cleanup; the production
reader already uses `closing` and is unchanged. The cleanup defect is real;
whether it explains the hosted 14 errors requires the Windows rerun. Linux
test success alone does not establish that result. After the fix the Linux
suite remains **47/47 passed**. An additional instrumented run retained strong
references to all 113 actual SQLite connections, preventing garbage collection
from masking a leak, and checked that executing on them raised
`sqlite3.ProgrammingError` before each of 33 temporary-directory cleanups. It
also passed 47/47; the ignored receipt preserves the command and output.

## Next executable work and rollback

1. Design the trusted native key and observer-grant handoff. Do not replace the
   retained LAN random key with a default/static secret, accept an arbitrary
   observer nonce, or request users to paste live secrets into chat.
2. Connect protected transport with explicit source identity, request bounds,
   rate limiting and fail-closed startup, then the Linux service lifecycle and
   UI Public adapter. Keep `--deployment-profile public` gated until these are
   implemented and reviewed. Windows/LAN and accepted gameplay remain unchanged.

The scoped supplemental patch and hashes are under ignored
`.local/evidence/cloud/server02/`. They do not modify/re-execute the private local
four-role evidence. Rollback consists of reverting the new bridge, its tests,
notice, the native test-only fixture module and this task record; no persistent
schema or live service changed.
