# SERVER-04 — bind a Linux service package to the Windows client catalog

Status: **ready-for-agent**. The independent native verifier and read-only CLI
are implemented and tested with synthetic inert files. This does not enable
the native Public profile or establish a running public game service. Real
Windows/Linux component assembly and hosted validation are separate CI evidence.

## Why two identities are necessary

The retained `release_catalog::verify_package` checks a fixed Windows package:
launcher, main DLL, gameplay DLL and Windows server. Its `ReleaseIdentity`
includes the SHA256 of the complete raw catalog. Replacing its server file with
a Linux binary changes that catalog and breaks exact client/server admission.

The new Linux-only `shared/api/src/public_package.rs` verifies a separate service
package and returns `VerifiedPublicPackage` with two different types:

- `PublicServiceIdentity`: current embedded release/compatibility IDs, fixed
  `linux-x86_64` platform, raw public-manifest digest and Linux executable digest.
- The original Windows client's `ReleaseIdentity`, returned by the unchanged
  production `release_catalog::verify_package` and exactly matched to the public
  manifest's explicit client pin. Future login/database policy must use this
  client identity, not the Linux service manifest digest.

The service type has no public constructor or conversion to `ReleaseIdentity`.
The original `ROLE_PATHS`, four-role verifier, admission logic and historical
fixtures are unchanged.

## Fixed layout and manifest

```text
package/PUBLIC-SERVER-CATALOG.json
bin/FifthFreedom.Server                    Linux service binary
client/FifthFreedom.exe                    Original Windows launcher
client/runtime/uplay_r1_loader.dll         Original Windows main DLL
client/runtime/scbl_gameplay.dll           Original Windows gameplay DLL
client/runtime/FifthFreedom.Server.exe     Original Windows server
client/package/RELEASE-CATALOG.json         Original Windows component catalog
```

`PUBLIC-SERVER-CATALOG.json` is bounded to 16 KiB and has this exact schema;
unknown or duplicate fields fail, including in nested objects:

```json
{
  "schema": 1,
  "platform": "linux-x86_64",
  "service": {
    "role": "server",
    "path": "bin/FifthFreedom.Server",
    "release_id": "<current prebuild release_id>",
    "compat_id": "<current prebuild compat_id>",
    "bytes": 1,
    "sha256": "<actual lowercase SHA256 of Linux binary>"
  },
  "admitted_client": {
    "root": "client",
    "catalog": "package/RELEASE-CATALOG.json",
    "release_id": "<verified Windows catalog release_id>",
    "compat_id": "<verified Windows catalog compat_id>",
    "manifest_sha256": "<actual lowercase SHA256 of raw Windows catalog bytes>"
  }
}
```

The example is a schema illustration, not a deployable manifest or invented
artifact hash. `bytes` must be the real positive executable length, at most
512 MiB. All named paths, roles and platform are fixed; values cannot redirect
verification to a different file or root. Service IDs must match the verifier's
compiled prebuild IDs. The client identity must match the output of the original
four-role verifier, which also checks its compiled release and compatibility.

The Linux service requires the exact embedded server marker, one occurrence
only. ASCII and UTF-16 markers crossing read-buffer boundaries are handled;
foreign, old, duplicate or malformed own-role markers fail, including a bad
second prefix beside one good marker. Generic marker-scanner string constants
are not mistaken for a complete embedded identity.

The ELF header must identify ELF64, little endian, x86-64, version 1 and an
executable/shared-object type. Additional public-boundary checks require the
Windows launcher and DLL roles to identify x86 PE32; the Windows server may be
x86 PE32 or x64 PE32+. The PE header must fit within the bounded first 64 KiB.
These identify supported platform formats; they are not complete OS loader
validation, executable safety analysis or proof of dependency closure. They
also prevent a rehashed Linux file being substituted for the original Windows
server role, which the retained format-neutral marker/hash verifier alone
would not distinguish.

## Trust and path boundary

This is local integrity for an explicitly selected deployment supplied by a
trusted operator, not publisher signature verification or remote attestation.
The caller must keep that tree immutable during verification and use. The root
must be absolute and normalized; raw `.`/`..`, repeated separators and relative
paths are rejected. Symlinks in every ancestor and multiple hard links on the
manifest, service, client catalog or any of the four client files are rejected.
No input is extracted, overwritten, repaired or executed.

The service/manifest reads compare opened file metadata with the checked path
and again after bounded reading. This detects ordinary file changes; it is not
an atomic snapshot or a guarantee against an attacker concurrently replacing
an entire tree. The existing four-role verifier retains its established trusted
offline deployment boundary. Service startup must establish ownership and
immutable installation/lifecycle separately.

`verify_public_server_package(executable)` accepts only the fixed
`bin/FifthFreedom.Server` position under its explicit root. It does not search
ancestors for a catalog. Future startup must pass the actual current executable;
the native main function is deliberately not connected to it yet.

## Read-only CLI and executed checks

```sh
# From src/native with the documented cloud Rust/protoc toolchain activated:
cargo +nightly-2025-10-15 test --offline --locked -p api
cargo +nightly-2025-10-15 build --offline --locked -p api --example verify_public_package
# Verify an explicitly assembled candidate directory without starting a server:
cargo +nightly-2025-10-15 run --locked -p api --example verify_public_package -- ABSOLUTE_PACKAGE_ROOT
```

The actual Linux API suite passed **45/45**, including **16 new public-package
tests** and all 29 retained API tests. The example compiled successfully.
Three actual CLI failure invocations checked missing arguments, relative paths
and a directory with no public manifest: each exited 1, emitted only its fixed
reason on stderr, and produced no stdout. A successful real candidate CLI run
must be recorded independently by the component assembly job; it is not
inferred from the unit suite.

Tests construct explicitly labeled, inert PE/ELF header/marker data inside fresh
owned temporary directories, with no executable permission, real accounts,
SAV/META files, game, listener or production package. The fixture data is never
run or published as a candidate. Coverage includes exact raw catalog pinning,
distinct identity digests, every original client role, rehashed platform
substitution, malformed PE/ELF, missing/mixed/duplicate markers, complete valid
JSON with duplicate fields, oversized otherwise-valid JSON, fixed paths,
symlink ancestors, hard links and preservation of originals. These are source
and unit checks, not hosted runtime or live acceptance.

Successful CLI output is one bounded JSON object containing schema 1,
`result: "verified"`, platform, service release/compatibility/digests, and
`admitted_client.storage_key`. Failures expose fixed reason codes only. It has
no server-start, network, auto-discovery or write path. Non-Linux execution
reports `PUBLIC_PACKAGE_PLATFORM_UNSUPPORTED`.

## Real component assembly and remaining gates

The candidate job must consume original Windows and Linux component artifacts
from the same reviewed source/run, verify their recorded hashes, and copy into
a fresh staging directory. Preserve the original artifacts and Windows role
bytes. Generate the Windows component catalog with the existing catalog
producer over those four copied Windows files; pin its exact raw digest in the
separate public service manifest. The read-only Rust verifier then checks all
of those real component bytes. A failed hash, format, role, marker or identity
comparison fails assembly; no relabeling or replacement with synthetic files
is permitted.

The `client/` subtree is admission evidence containing four components, not a
complete usable Windows client distribution. The 12 privacy-held SAV/META
inputs and complete-package gate remain unchanged. A verified component bundle
does not imply the protected native game/overlay transport, launcher Public
adapter, Linux installation/rollback, operator observer policy, full resource
closure or live networking acceptance are finished. Keep the native Public
startup gate closed until those boundaries are implemented and reviewed.

Commands, local logs and a scoped supplemental patch receipt are retained under
ignored `.local/evidence/cloud/server04/`; original private four-role evidence
is untouched. Rollback reverts the additive module, tests, example and module
export. There is no database migration or live deployment to undo.
