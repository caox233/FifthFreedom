# Data challenge: independent Linux candidate build

Status: **ready-for-agent** — exact source preparation and builder regressions
passed locally; the new hosted 59-contract/core/CLI build has not run yet.
Ordinary Public startup remains closed. This continues the actual owned transport
prerequisite within the full migration and preserves all earlier build/runtime
identities and evidence.

The entry is `python -B scripts/ci_easytier_data_challenge_build.py`. The existing
build-only workflow has a separate `data-challenge-source` job, temporary source
and target directories, and three explicitly named JSON artifacts. The original
guard3 entry still uses its original 35 checks; the authenticated-receive entry
still uses its original 44. Neither recipe nor the current Linux runtime or
Windows candidates adopts this new source.

The [new recipe](../../scripts/easytier-data-challenge-source.lock.json) extends
the successful auth44 source with the two reviewed
[Data challenge patches](../dependency-patches/easytier-2.6.4-data-challenge/README.md).
The runner checks the unchanged baseline recipe, all eight prior ordered patches,
the reviewed source evidence, the changed existing file and the single new module.
The same fixed Git-object archive fetch, strict preparer, locked Cargo runner,
static ELF checks and bounded receipt writer are reused. There is no new downloader
or fallback to old sources. The official archive/member pins are unchanged.

The shared preparer now accepts an optional `added_files` list with exact path,
size, SHA-256 and mode. It rejects duplicate or unsafe names, collisions with
existing/materialized/omitted files, overlap with modified files and the generated
receipt name, invalid modes and malformed identities before writing output. The
reviewed patch must create exactly the declared source: the full inventory still
rejects missing or extra files, wrong bytes, hash or mode. Existing recipes omit
this field and retain their original behavior. Real fresh preparations of both
old recipes produced receipts equal in every field to their preserved receipts.

| Identity | SHA-256 |
| --- | --- |
| New recipe | `745bbca0a70d6e934ef8eb4a23299ab1afa45a5e77beb2f9cad65fe2a687708f` |
| All 700 prepared source files | `d4ce70591d7336fee73883bb865874c35d8b407300f0cd65e189f3f06110ba21` |
| Ten ordered patches | `3fff171ca2d123fd70fea1a8a894d383f9ea477fdc72e8ceda14fa3c874032be` |
| Unchanged Cargo.lock | `8251ad8841694cccdeece7e086b7750d66ec93500c7b9c9879e43791fd53dca9` |
| Restored auth44 source tree | `b0a7d3cf802d3de1f3741943ccd48db02cf883fe68899e9cd7f636627ec74e0f` |

The new job requires each actual Rust filter below to pass with zero failures and
zero ignored tests. A missing, partial or duplicate success summary fails the job
before Release compilation. These are the production-linked tests already run
locally for the reviewed source patch; hosted execution is still pending. The
seven Python recipe/orchestration tests use inert compiler-output fixtures and
do not count as actual Rust execution.

| Actual Rust filter | Required passing count |
| --- | ---: |
| `fifth_freedom_data_challenge_` | 15 |
| `fifth_freedom_receive_` | 15 |
| `fifth_freedom_secure_profile_contract` | 4 |
| `fifth_freedom_ingress_contract` | 10 |
| `peers::secure_datagram::tests` | 9 |
| `peers::peer_session::tests` | 2 |
| `ff_explicit_pin_contract` | 3 |
| `peers::peer_conn::tests::peer_conn_secure_mode_data_roundtrip` (`--exact`) | 1 |

After all 59 checks pass, the job builds core/CLI Release with Rust 1.95.0,
`--locked`, the explicit GNU target and unchanged default/ring features. It checks
the full source digest before and after compilation. It never starts either
resulting runtime or uploads executables, original archives, dependency caches,
raw compiler logs or private configuration. Only the explicitly named run, source
and build JSON receipts are published. They bind source/patch/lock identities,
actual test counts/log hashes, and static ELF output sizes/hashes. Failed stages
retain failure status; runtime execution, binary publication and Public admission
remain false.

Local verification used the preserved pinned canonical archive for two fresh
700-file preparations. Both produced identical receipts and matched every file
of the already tested nonce candidate. Reversing only the new two patches restored
the complete prior 699-file auth44 inventory; reapplying restored all 700 files.
The old guard3/auth44 fresh preparations also matched their preserved receipts.
The 15 preparation tests (including actual patch-created additions and negative
scope/hash/mode cases), six existing builder tests, seven auth44 tests, seven new
recipe/runner tests and workflow actionlint passed. The local receipt is
`.local/evidence/cloud/data-challenge-build/local-preparation.json`; no local
full Release rebuild or new target tree was created for this integration.

The challenge checks exercise the actual `PeerConn::send_msg`, Noise and current
session AEAD/replay boundary with a synthetic UDP echo. They do not run an owned
remote UDP application, kernel TUN path or driver, establish remote application
ownership, or prove remote completion of a new key epoch under the retained sync
grace policy. A response is generated once per ticket and may be read repeatedly
only while that original owner/revision/deadline remains current. This candidate
does not compose it with a retained NIC session or issuer, and does not grant a
launcher/native lease. The next action is to run this independent hosted job and
verify its official three-JSON artifact against the exact recipe, full inventory,
59 counts and Release result before recording any new build identity.
