# EasyTier 2.6.4: require the encrypted flag at the secure-session boundary

Status: **tested local candidate patch; not an adopted runtime**.

The original `PeerSession::decrypt_payload` accepts a frame produced by the real
encryptor when its encrypted flag is cleared. The retained nonce tail passes the
session/replay checks, while the selected ring decryptor returns success without
AEAD verification for a flag-cleared packet. The added secure-session guard rejects
that packet before parsing or committing its sequence. Valid encrypted traffic,
tampered-tag rejection and replay rejection retain their tested behavior.

This is a finite production API contract result with synthetic, already
established sessions. It does **not** demonstrate a network/Noise-handshake
exploit, an account/game traffic leak, or completion of the full Public ingress
contract. In particular, the `PeerSessionTunnelFilter` sender-ID/pass-through
paths still need separate production-linked tests. See [SERVER-07](../../tasks/server-07.md).

## Exact inputs and changes

Upstream is [EasyTier/EasyTier](https://github.com/EasyTier/EasyTier), tag `v2.6.4`,
commit `8428a89d2dabc94c97d370ec607c6ca142473626`. The untouched files were preserved
and checked against that commit's actual Git blobs. The two patches apply cleanly
to that base; forward application reproduces the tested files, and reverse
application restores their exact original SHA256 values.

- [contract-tests.patch](contract-tests.patch) adds four tests to the real
  `PeerSession` module. Each test creates a fresh sender/receiver pair and uses
  `encrypt_payload` to produce a real AES-256-GCM frame. The flag-cleared and
  tampered-tag cases must reject the altered clone, then accept the untouched
  original sequence, so replay state cannot produce a false passing result.
- [require-encrypted-flag.patch](require-encrypted-flag.patch) adds six lines in
  `SecureDatagramSession::decrypt_payload`, after session-validity checking and
  before tail parsing. It leaves legacy shared decryptors and their control
  packet compatibility unchanged.
- [receipt.json](receipt.json) records original/patched source hashes, patch
  hashes, exact commands, outcomes and hashes of the retained local logs.

The patches concern the upstream LGPL-3.0 source; preserve its
[license notice](../../../integration/scbl3/THIRD_PARTY_LICENSES/EasyTier-LGPL-3.0.txt)
and corresponding source with any future reviewed distribution. No dependency
binary is included here. The official archive/member lock still identifies the
**original** bytes and has not been changed to imply they contain this patch.
Any future patched runtime needs a separately identified build and receipt.

## Executed results

| Run | Exit | Result |
| --- | --- | --- |
| Initial compilation | 101 | `kcp-sys` bindgen could not find `stddef.h`; no tests ran. |
| Original production source plus test patch | 101 | 3 passed, 1 failed: flag-cleared frame was accepted. |
| Same tests plus the six-line guard | 0 | 4 passed, 0 failed. |
| Existing `peers::secure_datagram::tests` after guard | 0 | 9 passed, 0 failed. |
| Existing `peers::peer_session::tests` after guard | 0 | 2 passed, 0 failed. |

The test build used Rust 1.95.0, GNU Linux x86_64, the unchanged upstream lock and
default features. The selected AES-256-GCM factory uses ring (`wireguard` enabled;
`openssl-crypto` and `aes-gcm` absent). Upstream's official Linux x86_64 workflow
uses musl plus jemalloc, so this is not a reproduction of that release binary.
No core/CLI runtime, TUN, network namespace, STUN client, listener, gameplay or
driver was started. Only the selected library unit tests ran; the full upstream
test suite contains networking tests and was not run.

## Reproduction

Use an isolated checkout of the exact upstream commit with its Rust 1.95
toolchain and normal C/build/protobuf prerequisites. Apply the test patch first
and preserve the nonzero **test** result before applying the guard. A compiler
failure does not establish the contract failure.

```sh
git apply --check /absolute/path/to/contract-tests.patch
git apply /absolute/path/to/contract-tests.patch
cargo +1.95.0 test --locked -p easytier --lib fifth_freedom_secure_profile_contract -- --nocapture
# Expected before: exit 101, 3 passed and the flag-cleared test failed.

git apply --check /absolute/path/to/require-encrypted-flag.patch
git apply /absolute/path/to/require-encrypted-flag.patch
cargo +1.95.0 test --locked -p easytier --lib fifth_freedom_secure_profile_contract -- --nocapture
cargo +1.95.0 test --locked -p easytier --lib peers::secure_datagram::tests -- --nocapture
cargo +1.95.0 test --locked -p easytier --lib peers::peer_session::tests -- --nocapture
# Expected after: exits 0, with 4, 9 and 2 tests passed respectively.
```

In this cloud workspace, mold and Clang resource headers were absent. For the
recorded runs only, empty `CARGO_ENCODED_RUSTFLAGS` selected the available GNU
linker, and `BINDGEN_EXTRA_CLANG_ARGS='-isystem /usr/lib/gcc/x86_64-linux-gnu/14/include'`
selected existing GCC builtin C headers. Build concurrency was two and dev/test
debug info was disabled; protoc 35.1 was explicitly selected. These are documented
environment adjustments, not changes to upstream code, features or dependency
resolution. They may be unnecessary on a normally provisioned runner.

Raw logs and preserved originals remain in ignored
`.local/evidence/cloud/server07-contract/`. The tracked receipt contains bounded
test summaries, without environment dumps or runtime logs. The native Public
gate and the separate owned-overlay lifecycle requirement remain unchanged.
