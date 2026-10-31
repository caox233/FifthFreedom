# EasyTier 2.6.4 — explicit peer key before shared-secret acceptance

This is a separately identified source patch for Fifth Freedom's optional
explicit server-key contract, based on upstream commit
`8428a89d2dabc94c97d370ec607c6ca142473626` **plus** the previously recorded secure
session and secure Data ingress guards and tests. It changes no official binary
lock and has not yet produced a new runtime candidate.

The original [`verify_remote_auth`](https://github.com/EasyTier/EasyTier/blob/8428a89d2dabc94c97d370ec607c6ca142473626/easytier/src/peers/peer_conn.rs#L706)
accepts a valid shared-secret proof before comparing an explicitly configured
peer public key. The test uses the actual configured `PeerConfig.uri` matching
the in-memory tunnel's remote address, actual generated X25519 keys and real
client/server Noise handshakes. With a valid PSK, a different explicitly pinned
server key was **accepted** before this patch.

The production change moves the existing optional key mismatch rejection before
the successful PSK return. A correct explicit key plus PSK still yields
`NetworkSecretConfirmed`; an absent pin retains the existing PSK/group behavior.
The responder still has no newly imposed client-key list. Trusted-key handling,
the existing unpinned/credential/default branches and legacy behavior are retained.

## Finite executed contracts

Three independent fresh fixtures use `create_ring_tunnel_pair`,
`get_mock_global_ctx` with `MockStunInfoCollector`, the real `PeerConn` lookup and
handshake APIs. They create no core, TUN, sockets or external STUN activity.

| Contract | Before | After |
| --- | --- | --- |
| Explicit correct key and matching PSK | auth level 3 on both sides | auth level 3 on both sides |
| Explicit wrong valid key and matching PSK | **accepted: failure** | explicit key mismatch rejection |
| No configured key and matching PSK | auth level 3 on both sides | auth level 3 on both sides |

The authoritative before run exited 101 with 2 passed / 1 failed. Its failure
reports only fixed words: actual `accepted`, expected `identity-rejected`.
Timeout, cancellation and other errors never count as identity rejection. The
client's actual decision is obtained before the exact server task is reaped or
aborted. The first diagnostic run used a boolean assertion; its original 2/1
log is preserved separately and is not substituted for the clearer rerun.

After the minimum guard, the three contracts passed. Seven existing secure
handshake tests, six PeerSession tests (including the prior four secure-profile
contracts), nine secure-datagram tests and ten ingress tests also passed:
**35 unique after checks, no ignored tests**. The selected GNU/default features
use ring AES-256-GCM; this is not an official musl/jemalloc build reproduction.

## Exact patch order and receipts

Apply the existing four ordered patches from the clean source lock first, then:

1. `explicit-pin-contract-tests.patch` — 4,765 bytes,
   SHA256 `9a3d13a08259acbe11352ccb084a565e04df465f42dc6d16f4a805429705b7cb`.
2. `require-explicit-peer-pin.patch` — 1,744 bytes,
   SHA256 `7eeeda28458f17d8ee393176a767725c19e3315f0bc49e9471ebe138cc123328`.

The prior fully patched `peer_conn.rs` SHA256 is
`f3c1dd756e01975263d0cf5be5d7f3e6847bd6200616c7519f84dafd360304d0`.
After these new tests and guard it is
`55be4e5f00acd548a9977e5d34874396a84888a7f00f08bbd8b43ab7d6cf85cc`.
Applying only the new production guard to that prior baseline gives
`8a71de7d3c77221e36db56105622911ee9cdf437980b217c8d44ac765ff13716`;
that baseline still contains the previous test modules.

`receipt.json` records the exact source/patch/log digests, commands and outcomes.
New-pair and cumulative six-patch forward application match the tested source;
reverse application restores the exact prior baseline or original upstream
three files respectively. Original source snapshots and all raw logs remain in
ignored `.local/evidence/cloud/server07-explicit-pin/`. The unchanged Cargo.lock
and explicit toolchain setup are the same as the prior SERVER-07 contracts.

## Integration boundary

Upstream sends Noise message 3, which contains its handshake HMAC proof, before
this authentication decision. This patch does not reorder those messages. The
decision occurs before the client's handshake returns successfully and installs
its authenticated connection/session; tests do not send account credentials or
game traffic. Do not claim a broader network attack demonstration or protocol
audit from these finite in-memory contracts.

A provider must consume a **new** candidate built from this patch identity,
verify the explicit configured URI/key and exact owned endpoint, and continue
checking the live expected remote key/authentication state. The earlier guard2
candidate does not acquire this behavior retrospectively. Source/license closure,
owned namespace containment, actual transport/TUN/native lease tests and runtime
admission are separate requirements. No runtime binary, release, deployment or
upstream issue/PR is published by this work.
