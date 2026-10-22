from __future__ import annotations

import re
from pathlib import Path


STORAGE = Path("server/dedicated-server/src/storage")
DOMAINS = (
    "accounts",
    "presence",
    "sessions",
    "parties",
    "invites",
    "station_urls",
)
COORDINATOR_HELPERS = {
    "accepted_normal_invite_transition_async",
    "attach_accepted_invite_party_async",
    "cancel_private_room_lobby_restore_for_member_async",
    "complete_private_room_party_leader_restore_async",
    "consume_all_invites_for_receiver_async",
    "detach_party_member_to_solo_async",
    "ensure_party_for_leader_async",
    "ensure_personal_party_if_unassigned_async",
    "handoff_private_room_party_async",
    "prepare_native_private_host_migration_party_async",
    "queue_departed_member_lobby_restore_async",
    "queue_party_follow_invites_async",
    "reconcile_game_session_async",
    "restore_published_lobby_party_async",
    "restore_quick_match_party_snapshot_async",
    "retire_superseded_solo_lobbies_async",
    "synchronize_party_from_published_lobby_async",
    "transfer_party_leader_async",
}


mod_text = (STORAGE / "mod.rs").read_text(encoding="utf-8")
for domain in DOMAINS:
    assert re.search(rf"(?m)^mod {re.escape(domain)};$", mod_text), (
        f"storage facade must declare the {domain} domain module"
    )

assert "SqlitePoolOptions" in mod_text
assert "sqlx::migrate!" in mod_text

helpers: set[str] = set()
for domain in DOMAINS:
    path = STORAGE / f"{domain}.rs"
    text = path.read_text(encoding="utf-8")
    assert "impl Storage" in text, f"{path} must extend the shared Storage facade"
    for forbidden in (
        "SqlitePoolOptions",
        "SqliteConnectOptions",
        "sqlx::migrate!",
        ".connect_with(",
    ):
        assert forbidden not in text, (
            f"{path} must use the shared pool; found forbidden token {forbidden}"
        )
    assert not re.search(
        rf"use\s+super::(?:{'|'.join(DOMAINS)})\b", text
    ), f"{path} must not depend directly on another storage domain module"
    helpers.update(
        re.findall(r"pub\(super\)\s+async\s+fn\s+(\w+)", text)
    )

assert helpers == COORDINATOR_HELPERS, (
    "cross-domain storage helpers changed; keep them minimal and review transaction ownership: "
    f"{sorted(helpers)}"
)

print("Dedicated storage architecture boundary checks passed.")
