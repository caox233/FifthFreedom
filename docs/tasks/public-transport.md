# PUBLIC-03: bounded HTTPS control-plane candidate

Status: **ready-for-agent**. The read-only control plane now has a real Linux
HTTPS listener, strict local configuration, native-key/observer-claim consumers,
owned shutdown and a systemd installation template. Synthetic TLS and child-
process checks passed. The integrated public game transport, native Public
startup profile, launcher Public adapter and real networking acceptance remain
pending. This change does not enable or deploy them.

## Implemented trust boundary

`src/public/fifth_freedom_control/__main__.py` provides `main()->int` and an explicit
`--config ABSOLUTE_PATH` entry point. `--check-config` validates the configuration,
issuer key, certificate/private key and existing SQLite schema without binding a
listener or creating/migrating a database. Missing inputs fail before readiness.
There are no inherited deployment endpoints, default secrets or fallback HTTP
listener. Only fixed reason codes are printed; request bodies, addresses, paths,
tokens and key material are not logged.

`secure_config.py` matches the native opt-in signing-key contract: exactly 32 raw
bytes, Linux only, absolute normalized path, every directory/file opened through
held descriptors with `O_NOFOLLOW`, regular single-link file owned by the effective
uid, mode `0400` or `0600`, immediate directory owned by that uid with mode `0500`
or `0700`. Ancestors must be owned by root/the service uid without group/other
write permission; a root-owned sticky directory is the explicit `/tmp` exception.
Pre/post-read device, inode, ownership, mode, links, size and nanosecond timestamps
must agree. The private configuration, certificate, TLS key and database startup
check use the same file boundary. OpenSSL consumes validated open descriptors,
avoiding a second path lookup after the permission check.

The native issuer and Python consumer must receive the **same key bytes** through
operator-owned files or systemd credentials and use the same dedicated service
uid. No key is copied through an HTTP endpoint, command-line value or log. Rotate
by provisioning a new complete file atomically and coordinating both process
restarts; in-memory keys are never hot-reloaded. Native default LAN startup still
uses an independent random key and is unchanged. Native opt-in loading and the
new exact producer fixture are recorded in [SERVER-03](server-03.md).

The bridge now recognizes optional native-signed `observer_admitted_run`, with
canonical nonzero nonce equality to the signed game run. Old tokens have no grant.
A request boolean, unsigned payload edit or alternate extension cannot add one.
The Python observer policy must also permit the request, and each request checks
current account revocation and live run state in SQLite. This proves admission
at signing time; changing the native observer policy alone does not immediately
revoke already issued grants. Coordinate policy changes/restarts or revoke the
run/account as appropriate.

## Protocol and resource limits

The endpoint is `POST /v1/sessions` over TLS 1.2 or later with HTTP/1.1. It accepts
one request per connection, `Authorization: Bearer <native-token>`, exact
`Content-Type: application/json`, and a JSON object with exactly
`protocol_version`, `package` and `observer_requested`. The response contains only
the authenticated requester's admitted current sessions. All responses disable
caching. There is no login, token-issuance, mutation, static-file or upgrade route.

The source is always the kernel-observed socket peer. Forwarding headers have no
authority. The native token source and HTTPS peer must match, so deployment must
preserve the same direct/overlay source identity. Putting an ordinary HTTP reverse
proxy in front does not satisfy this contract. Actual client network/NAT/overlay
compatibility and the game transport still need integration and real validation.

Limits are enforced before expensive query work: 64 simultaneous connections
including TLS handshakes, 3-second handshake deadline, 10-second complete request
deadline, 32 headers/16 KiB total, 8 KiB maximum line, 16 KiB body, 512 KiB response,
8 database workers, per-source burst 10/refill 2 per second, global burst 128/refill
32 per second, and at most 4096 remembered sources. Saturated source tables deny
new entries until old entries expire. Duplicate headers, chunked transfer,
`Expect`, duplicate JSON keys, malformed UTF-8 and oversized input are rejected.
Database lock wait is at most one second and SQLite VM work has a two-second
deadline. A canceled/timed-out request retains its worker slot until the actual
query completes. Shutdown closes the listener and owned connections, drains those
bounded workers and shuts down the executor.

## Installation template and explicit remaining gates

`deploy/systemd/fifth-freedom-control.service` references the reproducible
`/opt/fifth-freedom-control/FifthFreedom-Control.pyz` candidate and uses a dedicated
`fifth-freedom` uid, `UMask=0077`, `LoadCredential`, read-only filesystem hardening,
no capabilities, a restart bound and a 15-second stop bound. The matching
`deploy/control.example.json` is intentionally invalid until the package identity
and local inputs are supplied. Its loopback network is an example, never an old
operator deployment. These files were not installed/enabled, and a running
systemd credential handoff has not been claimed.

Provision the configuration directory as service-owned `0700`, its config and
existing native database as `0600`, and the native data directory as `0700`.
`UMask=0077` affects new files; it does not repair an existing database or directory.
Both services need the same uid. Validate real `LoadCredential` ownership/modes,
certificate names/trust, and readable existing SQLite WAL/SHM sidecars under the
unit's read-only mount before any deployment. Do not use SQLite `immutable=1` on
an actively written database or copy it to invent current run state.

The configured `approved_release` must be copied from the **verified native
four-component package admission**, never from an incoming request or invented
digest. The control candidate checks equality of this server-owned policy, signed
token identity, declared four roles and database identity; it does not independently
rebuild or validate a complete game package. The 12 held SAV/META inputs, full
package gate and public native startup gate remain unchanged.

## Executed checks

From the repository root in the Linux cloud workspace:

```sh
python3 -B -m unittest discover -s tests/server -p test_protected_transport.py -v
python3 -B -m unittest discover -s tests/server -p test_native_token_fixture.py -v
python3 -B -m unittest discover -s tests/server -v
```

The focused transport suite passed **16/16**. It creates temporary certificates
with the local OpenSSL CLI, validates TLS hostname/trust over real loopback
sockets, exercises the production reader over a migrated synthetic database and
confirms unchanged database SHA256. It covers source-header spoofing, malformed
and oversized requests, account revocation, plaintext rejection, independent slow
handshakes, connection saturation, body timeout, retained worker permits after
timeout, file permissions/symlinks/hardlinks/FIFO/rotation/in-place-change rejection,
configuration failure and a real owned CLI process's config check/SIGTERM cleanup.
Tests require Linux/OpenSSL and explicitly skip on Windows; Windows source
coverage is not presented as Linux service runtime evidence.

The new native observer fixture is consumed without a Python replacement issuer;
the old fixture remains byte-identical. Eleven native-token consumer checks cover
both fixture families, key epochs, source, expiry, signature tampering, revocation,
retirement, and observer policy/run checks. The combined suite passes **69/69**.
Raw receipts are ignored under `.local/cloud-receipts/`; no real key, account,
database, external connection, game execution or production service was used.

## Next executable action

Review the hosted Linux control-plane candidate and its checksums alongside the
native opt-in key/observer changes. Integrate the retained native game services'
public transport and coordinated lifecycle before removing `PUBLIC_PROFILE_NOT_READY`
or enabling the launcher Public option. Exercise a controlled, explicitly configured
network with source identity preserved; keep real Windows/GPU/game acceptance
separate from these source and synthetic service checks.
