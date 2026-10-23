# Exact Instance peer-map challenge: independent Linux build

Status: **ready-for-agent** — the complete source preparation, recipe and runner
are locally verified. The independent **hosted 71-check and full GNU core/CLI
build has not run**. GitHub authentication is currently unavailable; this batch
uses the retained exact-hash archive without network retries. A local commit is
not a successful push or hosted execution. Ordinary Public remains closed.

The entry is `python -B scripts/ci_easytier_peer_map_build.py`; the build-only
workflow adds the independent `peer-map-source` job. Its source and target
directories and three JSON artifact paths are separate from the successful
guard3 35, authenticated-receive 44 and Data challenge 59 jobs. Those three job
blocks, recipes and runner/preparer implementations are unchanged. Their actual
success receipts remain evidence for their original sources only.

The [recipe](../../scripts/easytier-peer-map-source.lock.json) extends the
successful nonce59 source with the two reviewed
[Instance/PeerMap patches](../dependency-patches/easytier-2.6.4-peer-map-challenge/README.md).
It preserves the prior ten ordered patches and existing added module, then binds
the three changed existing files to exact upstream and tested hashes and the
one new module to its reviewed 19,984 LF bytes, SHA-256 and mode 0644. Duplicate,
foreign or missing source entries and recipe substitutions are rejected. The
strict preparer still compares the complete resulting inventory, not merely
the files mentioned by a patch.

| Identity | SHA-256 |
| --- | --- |
| Independent recipe | `a614cf96abfc07e06b6eeecd48087709f653b958e0abe01cca1d2cb66212d954` |
| All 701 prepared source files | `4f0a55d01c4a25dcb4bd7c62972b51e42425b99959566fb3046887dfecda2140` |
| Twelve ordered patches | `80ced5a4c62459fb105d04dd42d2911559191b955584059c3cf8ad9578d4abb3` |
| Unchanged Cargo.lock | `8251ad8841694cccdeece7e086b7750d66ec93500c7b9c9879e43791fd53dca9` |
| Restored nonce59 source tree | `d4ce70591d7336fee73883bb865874c35d8b407300f0cd65e189f3f06110ba21` |

The runner requires the following actual Rust test counts, each with zero
failures and zero ignored cases. These 71 production-linked checks already
passed locally on the exact source in the
[source receipt](../dependency-patches/easytier-2.6.4-peer-map-challenge/receipt.json).
The newly wired hosted job must execute them independently; local results do not
fill its receipt. Missing, partial, duplicate or skipped summaries fail before
Release compilation.

| Rust filter | Required passing count |
| --- | ---: |
| `fifth_freedom_peer_map_challenge_contract` | 12 |
| `fifth_freedom_data_challenge_` | 15 |
| `fifth_freedom_receive_` | 15 |
| `fifth_freedom_secure_profile_contract` | 4 |
| `fifth_freedom_ingress_contract` | 10 |
| `peers::secure_datagram::tests` | 9 |
| `peers::peer_session::tests` | 2 |
| `ff_explicit_pin_contract` | 3 |
| `peers::peer_conn::tests::peer_conn_secure_mode_data_roundtrip` (`--exact`) | 1 |

After all checks pass, the shared runner builds core and CLI with Rust 1.95.0,
`--locked`, the explicit GNU target and unchanged default/ring features. It
verifies the complete source before and after compilation and statically checks
the resulting ELF identities. It never executes either runtime. Only the run,
source and build JSON receipts are uploaded: no executables, original archives,
dependency caches, raw compiler output or private configuration. Runtime
admission, binary publication and ordinary Public startup remain false. The
existing fixed Git archive downloader and pinned dependency setup are reused;
this wrapper introduces no additional download path.

Local preparation used the preserved archive with its original size and SHA-256
checks. Two fresh preparations produced identical full receipts and every file
matched the independently tested 701-file candidate. Reversing the final two
patches restored the entire 700-file nonce59 inventory and removed the added
module; reapplying restored all 701 files. The local record is
`.local/evidence/cloud/peer-map-build/local-preparation.json`. All seven prior
recipe/runner files and the three prior workflow job blocks were also compared
byte-for-byte against the preceding commit. No local Release compilation or new
Cargo target was created for this integration.

The eight new Python tests cover recipe/source substitution, duplicate and
foreign evidence rows, retained baseline inputs, exact 12+59 command selection,
missing or skipped checks, bounded failure receipts and rejecting a changed
whole tree before compiler invocation. Their compiler output is an inert test
fixture, explicitly not Rust execution. Together with the unchanged preparer
and prior runner tests, all 43 Python checks passed, and actionlint passed.

The source method supplies bounded, sampled current Map/Peer/Conn membership
around an actual authenticated Data challenge. It does not establish atomic
membership, complete Instance/process/NIC lifetime, a kernel TUN path, remote UDP
application ownership or a launcher/native lease. The owned-read IPC and Windows
candidates do not acquire this source through the new Linux recipe. Once formal
GitHub access is restored, push the reviewed batch, retain this first independent
job, and verify its official three-member artifact and exact test/build/source
identities before recording any hosted success.
