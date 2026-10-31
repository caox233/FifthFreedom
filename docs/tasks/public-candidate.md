# Reproducible control-plane candidate

Status: ready-for-agent — packaging and integrity validation implemented; this
component artifact is not a complete public game-server runtime.

`scripts/public_candidate.py` builds a standalone Python zipapp from the reviewed
flat `src/public/fifth_freedom_control` package. The archive includes the package
notice and repository license. It excludes runtime configuration, issuer keys,
TLS certificates/keys, databases, logs and saves. It does not install services,
launch games, provision a deployment, or relax the native Public startup gate.

```sh
python3 -B scripts/public_candidate.py build \
  --source-commit ACTUAL_40_CHARACTER_CHECKOUT_SHA \
  --output artifacts/ci/control-candidate
python3 -B scripts/public_candidate.py verify \
  --directory artifacts/ci/control-candidate
```

The output directory must be new. The two files are `FifthFreedom-Control.pyz`
and `candidate.json`. Fixed ZIP metadata and sorted entries give identical bytes
for identical inputs on the same Python/zlib toolchain. The manifest binds every
member and the archive by length/SHA256 and explicitly says this is not a full
game package and does not enable the native Public profile. The supplied commit
is a caller declaration: CI must pass its actual checkout revision; the builder
does not itself prove Git cleanliness or grant publication approval.

Verification reads without extraction or execution. It rejects changed contents,
additional files, duplicate entries/JSON keys, unsafe member paths, missing entry
points, special files, excessive input sizes and misleading runtime flags. These
hashes establish artifact self-consistency, not a trusted release signature.

Initial portable regression: `python3 -B -m unittest discover -s
tests/public_candidate -v` passed 10/10 on Linux. The standalone execution test
uses an explicitly synthetic module and removes its temporary checkout before
running it; it proves zipapp independence, not real service readiness. The real
entry point, TLS lifecycle tests and hosted artifact execution are recorded by
the protected transport task and CI lane after their separate checks execute.

Rollback removes this build utility and its tests; no runtime data or service
state is changed. Original package inputs and prior cloud receipts are retained.
