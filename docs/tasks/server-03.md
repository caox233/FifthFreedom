# SERVER-03 — explicit issuer-key sharing and native observer grants

Status: **ready-for-agent**. The native trust boundary is implemented and tested.
It is not a public-server activation or deployment. Public transport/lifecycle
integration is tracked separately; the native `public` startup profile still
fails closed before persistent state or listeners.

## Explicit Linux key-file contract

`FifthFreedom.Server --auth-key-file ABSOLUTE_PATH` opts into sharing the native
issuer's HMAC-SHA256 key with a trusted control-plane process. Without this
option, including ordinary Windows LAN startup, the server still generates a
fresh in-memory random key on each start. No signing key is read from an
environment value, generated on disk, printed, or embedded as a deployment
default. Explicit file sharing is rejected on non-Linux platforms.

Both consumers must be provisioned by the same trusted operator/service manager
with the same 32 **raw bytes**, not hex text. `auth_key_file.rs` requires:

- An absolute normalized path, without `.`/`..`, duplicate separators, symlinks
  or NUL. Every directory component is opened relative to an already-open FD
  using `O_DIRECTORY | O_NOFOLLOW`.
- Ancestors owned by root or the effective service UID and not writable by
  group/others. A root-owned sticky directory, such as `/tmp`, is permitted as
  an intermediate ancestor. The immediate parent has no such exception: it
  must belong to the effective UID with mode exactly `0500` or `0700`.
- An effective-UID-owned regular file, mode exactly `0400` or `0600`, one hard
  link, and length exactly 32 bytes. `O_NOFOLLOW | O_NONBLOCK` avoids following
  a replacement symlink or blocking on a nonregular input.
- A bounded read and unchanged ownership, mode, link count, inode, size and
  modification/change timestamps on the opened file before/after reading.
  Failure returns a fixed reason without a key, input path or OS error payload.

The loaded key uses the existing sodium key wrapper, whose destructor clears
its bytes. File replacement is not hot reload: already-running processes retain
their previous key. Rotation requires coordinating both consumers' restart and
requiring clients to log in again. Using the same explicitly provisioned file
across starts intentionally permits that key epoch to persist. Merely setting
an unrelated Python key cannot authorize native tokens.

`main` now resolves the deployment gate, verifies the package, initializes
cryptography and validates the key/TTL before creating the data directory or
starting database/listener work. The already-validated configuration is passed
into the API service. No listener generates a second independent key.

## Signed observer authority

The existing v1 token envelope gains an optional `observer_admitted_run` string.
The native login path produces it only for an explicitly requested, valid native
game run after the existing host observer policy admits that login. It must
equal the signed `game_run.instance`; account tokens and normal game tokens omit
it. Verification rejects a grant without a valid matching game run. Existing
observer response metadata and the final policy recheck before returning the
response remain intact.

This field proves **admission of that run at login**, not continuous observation
of a policy switch. Closing the host observer option does not retroactively
revoke an existing admitted run. Run retirement, account revocation and token
expiry remain effective. The Python bridge must also require its own trusted
observer policy and revalidate the current persisted identity/run on every
request. A request flag or header cannot create the signed grant.

Standalone native servers still default to observer disabled. This change does
not add a Linux operator-policy override or make a Python `observer_allowed`
setting enable native observer admission. That production lifecycle/policy
integration remains pending. No package, capability, protocol namespace,
named-pipe launch or accepted gameplay behavior was replaced.

## Linux data-directory ownership

`instance_lock.rs` adds the missing Linux single-owner guard alongside the
existing Windows exclusive handle. Before initialization/database work, Linux
opens an empty effective-UID-owned `server.lock` with mode `0600`, rejects
symlinks, multiple links, nonregular or populated files, and takes nonblocking
`flock(LOCK_EX)`. The data directory must belong to the effective UID and not
be writable by group/others. An already-held lock fails startup.

The returned file handle remains alive for the server lifetime. Closing the
handle releases the lock; the file is never unlinked or treated as a stale PID
file. A second owned data directory remains independent. Tests preserve a
synthetic pre-existing evidence file when a lock path points to it.

## Executed checks

Linux cloud toolchain: Rust `nightly-2025-10-15`, protoc 35.1, locked dependencies.
The only lockfile change is adding the already-locked `libc` dependency to the
server's Linux target. No dependency version was changed.

```sh
# From src/native with the documented Rust/protoc toolchain activated:
cargo +nightly-2025-10-15 test --offline --locked -p dedicated_server auth_key_file
cargo +nightly-2025-10-15 test --offline --locked -p dedicated_server native_observer_fixture_tests
cargo +nightly-2025-10-15 test --offline --locked -p dedicated_server native_bridge_fixture_tests
cargo +nightly-2025-10-15 test --offline --locked -p dedicated_server instance_lock
# With SCBL_IDENTITY_TEST_DIR and SCBL_RELEASE_TEST_DIR pointing to fresh,
# owned synthetic fixture directories under ignored .local/evidence/cloud/server03:
cargo +nightly-2025-10-15 test --offline --locked -p dedicated_server
```

Final full server result: **330 passed, 0 failed, 0 ignored**, exit 0. It includes
six file-policy cases, five observer/startup cases, three lock cases and the two
unchanged native bridge fixture cases. The startup tests exercise the real
`AuthTokenConfig::from_startup` → producer → verifier path: default starts have
different keys, two explicit-file consumers accept the same token, and a new key
epoch rejects the old token while an already-loaded consumer retains its epoch.
The file-policy suite covers sizes/modes, missing/nonregular paths, trusted
ancestry, symlinks, hardlinks, repeated loads and explicit replacement. No live
accounts, deployed services, game or capture were used.

The new `tests/server/fixtures/native_observer_token_v1.json` is generated by the
actual Rust `release_for_login`, `observer_grant_for_login` and `issue_auth_token`
functions using explicitly synthetic identity, source, timestamp and two fixed
test-only key epochs. SHA256:
`a5eb4eb155a4435acf618fcd86a7126958e50d41b7aff7d5b31c1b6fc85b93cb`.

Generation uses an opt-in create-new receipt path. The first run produced that
receipt and failed the initially empty new fixture comparison (**3 passed,
1 failed**, exit 101). After review and copying the generated bytes to the new
fixture, the comparison passed. A fifth startup case was subsequently added and
passed in the full run. The old game fixture remains byte-for-byte unchanged:
`ba92c9917c8de43dd6b83ba31c0d985cba88cc868171d4872ce25cbd5416c663`.
Cross-language observer consumption is recorded with the transport task, not
inferred from these Rust checks. Hosted Windows runs establish their own result;
Linux success does not substitute for Windows or live acceptance.

Ignored commands/logs and a scoped supplemental patch receipt reside under
`.local/evidence/cloud/server03/`. These supplement cloud work; they do not
modify or claim to update the private original four-role records. Rollback is
the source patch reversal; there is no live service, database migration or
private resource to undo. An empty owned `server.lock` is safe to retain.

After that full suite, the read-stability comparison was tightened to include
the file group ID and unused error variants were made platform-specific. The
six key-file cases passed again, followed by a successful non-test
`cargo check --offline --locked -p dedicated_server`. No new hosted Windows
result is implied by this Linux compile.

## Next integration gate: distinct server and client package identities

Inspection of `shared/api/src/release_catalog.rs` confirms that
`verify_server_package` currently verifies the fixed four-component Windows
layout: `FifthFreedom.exe`, both runtime DLLs, and
`runtime/FifthFreedom.Server.exe`. The resulting immutable release identity
contains that complete catalog's digest. A Linux server binary has different
bytes from the Windows server component; replacing that component necessarily
changes the catalog digest and would fail the current exact client/server
release admission comparison.

The public startup plan therefore needs an explicit reviewed contract binding
the verified **Linux service package identity** to the **admitted Windows client
catalog identity**. Preserve role, hash, marker and compatibility checks for both
sides; do not skip the four-role verifier for Linux, silently rename paths,
accept a client-supplied digest as policy, or pretend two different catalogs are
equal. Package construction, admission tests and clean-checkout candidate
validation must precede enabling the public profile. The existing complete
Windows privacy/resource gate remains independent and unchanged.
