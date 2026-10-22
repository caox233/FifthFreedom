# Source provenance and license

The public control-plane modules are part of Fifth Freedom. The repository's
[GNU GPL version 3 license](../../../LICENSE) is retained.

`authenticated_bridge.py` adapts the canonical base64url, HMAC-SHA256 envelope,
source binding and time-window verification from these preserved inputs:

- `integration/scbl3/server/control_plane_auth.py` at imported baseline
  `fc8ef510b51be83c31f9f8233997f758b0477acc`.
- `src/native/server/dedicated-server/src/api.rs` at that baseline, specifically
  the v1 token issuer/verifier and its additional signed `release` and `game_run`
  claims. The retained native server remains the compatibility authority.

The donor license remains at `integration/scbl3/LICENSE`, and its existing
attribution/notices remain at `integration/scbl3/THIRD_PARTY_NOTICES.md` and the
repository `THIRD_PARTY_NOTICES.txt`. No donor file or original notice is removed
or overwritten. HMAC itself uses Python's standard-library `hmac`/`hashlib`, not
a replacement cryptographic primitive.

Compared with the donor helper, the bridge adds explicit version/key bounds,
duplicate-JSON-key rejection, typed integer bounds, signed release/game-run
requirements and the retained database admission reader. It has no token issuer,
HTTP listener, environment-key loader, deployment action or automatic key sharing.
No independently verified third-party license claim is added by this provenance
record.
