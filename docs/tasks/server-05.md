# SERVER-05 — executable public deployment preflight, activation still blocked

Status: **ready-for-agent**. A real, read-only native CLI path now checks the
assembled dual-identity package and operator inputs, and constructs the actual
retained service configuration. It explicitly reports `startup_allowed: false`.
Ordinary `--deployment-profile public` still returns `PUBLIC_PROFILE_NOT_READY`
before package access, state creation, database, log, observer or listener work.

## Concrete runtime blockers

The inspected donor EasyTier templates set `enable_encryption = true`, while its
Linux lifecycle checks `systemctl is-active`. Those observations do not establish
ownership of a specific encrypted process/interface, verified running bytes or
continuous liveness for this new native server. Nothing in this task downloads,
installs, starts or trusts an EasyTier runtime.

Binding an IPv4 address alone is also insufficient on Linux: weak-host routing
can accept traffic addressed to a local overlay IP from a different interface.
The API, HTTP config/content services, Quazal UDP listeners and the P2P diagnostic
UDP socket all require enforced interface/network isolation. SERVER-06 supplies
a shared socket policy seam; passing the static inspection does not activate
that policy for Public or prove encrypted transport ownership. The existing LAN
source policy accepts unicast IPv4 and cannot stand in for a Public client-CIDR
policy. An overlay exit, interface replacement/address change and routing loss
also need supervised fail-closed teardown.

These are named blockers in every successful preflight receipt. No configuration
boolean, interface name, private IP or success exit code overrides them. Native
observer admission stays disabled for standalone Public; a separate bridge
policy does not enable it.

## Explicit command and owner-controlled inputs

```sh
ABSOLUTE_PACKAGE_ROOT/bin/FifthFreedom.Server \
  --deployment-profile public \
  --check-public-deployment ABSOLUTE_PRIVATE_CONFIG
```

The check rejects combinations with `--init-lan`, `--config`, `--auth-key-file`,
`--data-dir`, `--smoke-ports` or `--launcher`, and requires the explicit Public
profile. On non-Linux systems it reports `PUBLIC_PREFLIGHT_LINUX_REQUIRED`.
The JSON document is bounded to 8192 bytes, with duplicate and unknown fields
rejected:

```json
{
  "schema": 1,
  "overlay_ipv4": "10.77.0.1",
  "overlay_prefix": 24,
  "overlay_interface": "ffpublic0",
  "auth_key_file": "/ABSOLUTE_PRIVATE_DIRECTORY/issuer.key",
  "data_directory": "/ABSOLUTE_PRIVATE_DIRECTORY/data"
}
```

These are inert examples, not an existing deployment or default connection.
The whole CIDR must fit within RFC1918 or `100.64.0.0/10`; network/broadcast
addresses, loopback, link-local, WAN addresses and `/31` or `/32` fail. Interface
names have a bounded Linux-compatible ASCII form and cannot be `lo`; they are
declarations only and are not probed or treated as encryption evidence. This
server restriction is deliberately narrower than UI-04's current general
non-reserved CIDR parser. A valid UI profile is not server acceptance; the
future protected connection must negotiate and enforce server policy.

The configuration and key use the same `auth_key_file` descriptor-based trust
checks: absolute normalized paths, no symlink components, trusted root/effective
UID ancestors, private effective-UID parent (`0500`/`0700`), regular single-link
effective-UID file (`0400`/`0600`), bounded stable reads. The issuer key is exactly
32 raw bytes. The data directory must already exist, belong to the effective
UID and have mode `0700`; it is not created or locked by this check. Configuration,
key and mutable data must all be outside the immutable package root.

Key loading still reads directly into the sodium wrapper that clears key bytes
on destruction. Refactoring common private-path/document checks did not replace
the default LAN random key or introduce a Public random-key fallback. Raw input,
key contents, private paths, addresses and exception payloads are not returned.

## Real package/auth/config connection

The inspection calls SERVER-04's `verify_public_server_package` with the actual
current executable. It verifies the Linux service and unchanged four Windows
roles, then passes **the admitted Windows `ReleaseIdentity`** into the existing
`AuthTokenConfig::from_startup` with the explicit key file. The Linux service
manifest digest is kept separate and never becomes client/account policy.

The first actual Debug executable check exposed two identical complete server
markers in its read-only data section: both package entrypoints independently
retained the generated constant. Synthetic single-marker fixtures had not
exercised this compiler artifact. The real main and independent package checker
correctly rejected it (`PUBLIC_PREFLIGHT_PACKAGE_INVALID` and
`PUBLIC_SERVICE_MARKER_MISMATCH`). The shared API now has one non-inlined marker
retention site used by both entrypoints. Exact-one marker admission, hashes,
Windows role paths and release identities remain unchanged; the fix changes the
produced bytes rather than relaxing admission. Debug and Release artifact
checks are recorded independently below.

`public_preflight::service_config` constructs the actual production `Config`,
not a parallel test model or arbitrary TOML override. A single declared IPv4
supplies every required endpoint:

| Service | Transport/port |
| --- | --- |
| Online configuration | TCP 80 |
| Content | TCP 8000 |
| Native authentication | UDP 21126 |
| Native secure service | UDP 21127 |
| Native API | TCP 50051 |

The online-config payload, secure-server address and content storage host use
that same address and fixed ports. The content mapping is only
`/mp_balancing.ini` → `data/mp_balancing.ini`; debug bypasses, extra/missing
services, arbitrary file mappings or endpoint drift fail validation. Existing
protocol access/crypto keys, virtual ports and matching native ticket keys are
retained. No configuration is saved, and constructing it starts no service.

`main` routes this explicit inspection before any ordinary startup work. It does
not call `Storage::init`, `instance_lock::acquire`, `observer_policy::start`, LAN
initialization, log creation, service startup or socket creation. Future actual
Public activation must still acquire and hold the existing Linux exclusive data
lock before opening storage; `data_lock_acquired: false` is intentional here.

## Receipt and evidence

A successful invocation exits 0 and emits one JSON object with:

- `status: "static_preflight_verified"`, `startup_allowed: false`;
- the verified service-manifest digest and admitted-client storage identity;
- `shared_auth_key_validated: true`, `data_directory_private: true`;
- `data_lock_acquired: false`, `observer_enabled: false`;
- the five fixed listener-plan ports and `bind_scope: "declared_overlay_ipv4"`;
- nonempty blockers for owned encrypted overlay proof, lifetime supervision,
  Public socket confinement activation and Public source-policy activation.

No readiness result is implied. Failure reasons are fixed codes; input values
are not inserted into errors. CLI framework/source locations may accompany the
native process error, but configuration/key contents are never formatted.

With the documented Rust/protoc toolchain and fresh owned identity/release test
directories, the combined native suite passed **341/341**, exit 0: the previous
330 cases, ten new public-preflight cases and SERVER-06's native API incoming
case. After the marker-retention correction, the full **341/341** native suite
and **45/45** shared API suite passed again. The production Debug and Release
native binaries both built successfully with no warnings; Release used the
existing optimized fat-LTO profile without a special marker-friendly override.
The first focused compile was attempted during parallel socket-signature edits
and failed with three missing policy arguments; after the coordinated main
integration, the full suite/build passed. That intermediate failure is retained
in ignored evidence rather than presented as a successful check.

The new tests use labeled inert package files and synthetic keys. They check
actual dual package admission, actual native auth/config construction, fixed
ports and advertised URLs, drift/wildcard/file-exposure rejection, strict
operator inputs, private state outside the package, missing inputs without
fallback or creation, and unchanged file bytes/directory entries/current
directory. They do not execute a synthetic file or start a game/service.

Both actual binaries were scanned and then executed through the independent
package checker and the real main preflight with explicitly synthetic, inert
Windows-role files. Each binary contained exactly one ASCII server marker, zero
UTF-16LE copies and no complete foreign-role marker. Both main invocations exited
0 with `startup_allowed: false`; ordinary Public and mixed CLI arguments exited
1 with their fixed refusal reasons. File bytes and directory entries stayed
unchanged, and no requested new data directory appeared. The fixture is not a
candidate package or proof of real Windows component assembly.

Actual CLI receipts (`main-probe-debug.json`, `main-probe-release.json`), binary
digests/marker counts and the initial failure are retained under ignored
`.local/evidence/cloud/server05/`. Hosted component-preflight receipts belong to
the separate candidate job; no hosted success is inferred from these fixtures.
Hosted CI must run the real newly built Linux binary after SERVER-04 verifies the
same-run real Windows and Linux components. Its temporary random test key,
configuration and private data directory must be outside the candidate and
excluded from artifacts/logs. The result remains static and blocked; full client
resource/privacy gates and real networking acceptance remain independent.

## Next executable work

Finish the owned encrypted-overlay runtime/verified configuration and lifetime
supervision, enforce the common socket policy plus a Public source network on
every native path, and review startup/teardown ordering with the launcher Public
adapter. Only then reconsider the Public activation gate. Do not treat a static
preflight, interface-bound test socket, donor service state, or private address
as proof that those runtime boundaries exist.
