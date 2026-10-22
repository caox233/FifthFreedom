use std::collections::HashMap;
use std::collections::HashSet;
use std::sync::Mutex;
use std::time::Duration;
use std::time::Instant;

use slog::Logger;
use sqlx::sqlite::SqliteConnectOptions;
use sqlx::sqlite::SqliteJournalMode;
use sqlx::sqlite::SqlitePool;
use sqlx::sqlite::SqlitePoolOptions;

mod accounts;
mod invites;
mod parties;
mod presence;
mod sessions;
mod station_urls;

type Result<T> = eyre::Result<T>;

pub(crate) const INVITE_KIND_PRIVATE_ROOM: i32 = 1;
pub(crate) const INVITE_KIND_LOBBY_PARTY: i32 = 2;
pub(crate) const INVITE_KIND_PARTY_FOLLOW: i32 = 3;
pub(crate) const INVITE_KIND_LOBBY_RESTORE: i32 = 4;
const MANUAL_INVITATION_SEARCH_TTL: Duration = Duration::from_secs(15);
const GAME_ROOM_DEPARTURE_CORRELATION_TTL: Duration = Duration::from_millis(750);
const LOBBY_RESTORE_SPLIT_TTL: Duration = Duration::from_secs(2);
const NATIVE_HOST_MIGRATION_TTL: Duration = Duration::from_secs(15);
const QUICK_MATCH_FORMER_HOST_RESTORE_TTL: Duration = Duration::from_secs(30);

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) enum DeleteGameSessionOutcome {
    NotFound,
    Destroyed,
    MigrationPending { remaining_participants: u32 },
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(super) struct PendingAcceptedNormalInvite {
    invite_id: i64,
    target_session_id: u32,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(super) enum AcceptedNormalInviteTransition {
    Pending(PendingAcceptedNormalInvite),
    Joined,
}

#[derive(Debug, Clone, Copy)]
struct PendingQuickMatchMemberLeave {
    recorded_at: Instant,
    accepted_invite: Option<PendingAcceptedNormalInvite>,
}

#[derive(Debug, Clone)]
struct QuickMatchPartyDeparture {
    captured_at: Instant,
    party_id: i64,
    leader_id: u32,
    room_host_id: u32,
    lobby_session_id: u32,
    members: Vec<u32>,
    departed_members: HashSet<u32>,
}

#[derive(Debug, Clone, Copy)]
struct QuickMatchFormerHostRestoreGate {
    started_at: Instant,
    seed_lobby_session_id: u32,
    party_id: i64,
}

#[derive(Debug, Clone, Copy)]
struct LobbyRestoreSplitTransition {
    started_at: Instant,
    removal_deferred: bool,
}

fn session_attribute_value(attributes: &str, attribute_id: u32) -> Option<u32> {
    attributes.split(';').find_map(|entry| {
        let (raw_id, raw_value) = entry.split_once("=>")?;
        let id = raw_id.trim().parse::<u32>().ok()?;
        if id != attribute_id {
            return None;
        }
        raw_value.trim().parse::<u32>().ok()
    })
}

fn is_game_room_attributes(attributes: &str) -> bool {
    session_attribute_value(attributes, 113) == Some(0)
}

fn is_private_room_attributes(attributes: &str) -> bool {
    is_game_room_attributes(attributes) && session_attribute_value(attributes, 3) == Some(0) && session_attribute_value(attributes, 4).is_some_and(|slots| slots > 0)
}

/// Response-only compatibility projection for an accepted direct private invite.
/// Persistent attributes and every field except Session class `113` remain
/// byte-for-byte equivalent at the semantic attribute level.
fn project_private_invite_session_class_only(attributes: &str) -> String {
    let mut projected = Vec::new();
    let mut replaced = false;

    for entry in attributes.split(';') {
        let entry = entry.trim();
        if entry.is_empty() {
            continue;
        }
        if let Some((raw_id, _)) = entry.split_once("=>") {
            if raw_id.trim() == "113" {
                projected.push(String::from("113 => 1"));
                replaced = true;
                continue;
            }
        }
        projected.push(entry.to_string());
    }

    if !replaced {
        projected.push(String::from("113 => 1"));
    }
    projected.join(";")
}

fn run<F>(future: F) -> Result<F::Output>
where
    F: std::future::Future,
{
    Ok(tokio::runtime::Builder::new_current_thread().enable_time().build()?.block_on(future))
}

pub struct Storage {
    logger: Logger,
    pool: SqlitePool,
    manual_invitation_searches: Mutex<HashMap<(u32, u32), Instant>>,
    recent_game_room_departures: Mutex<HashMap<(u32, u32, u32), Instant>>,
    native_host_migrations: Mutex<HashMap<(u32, u32), Instant>>,
    lobby_restore_split_transitions: Mutex<HashMap<(u32, u32, u32), LobbyRestoreSplitTransition>>,
    quick_match_party_departures: Mutex<HashMap<(u32, u32, i64), QuickMatchPartyDeparture>>,
    quick_match_former_host_restore_gates: Mutex<HashMap<(u32, u32), QuickMatchFormerHostRestoreGate>>,
    pending_quick_match_member_leaves: Mutex<HashMap<(u32, u32, u32), PendingQuickMatchMemberLeave>>,
    published_game_sessions: Mutex<HashMap<u32, u32>>,
}

pub enum LoginError {
    NotFound,
    InvalidPassword,
}

impl Storage {
    pub fn init(logger: Logger) -> Result<Self> {
        Self::init_with_database_url(logger, "sqlite://5th-echelon.db?mode=rwc")
    }

    fn init_with_database_url(logger: Logger, database_url: &str) -> Result<Self> {
        let pool = run(async {
            let connect_options = database_url
                .parse::<SqliteConnectOptions>()?
                .foreign_keys(true)
                .busy_timeout(Duration::from_secs(5))
                .journal_mode(SqliteJournalMode::Wal);
            let pool = SqlitePoolOptions::new().connect_with(connect_options).await?;
            sqlx::migrate!("src/storage/migrations").run(&pool).await?;
            Ok::<_, eyre::Error>(pool)
        })??;
        Ok(Self {
            logger,
            pool,
            manual_invitation_searches: Mutex::new(HashMap::new()),
            recent_game_room_departures: Mutex::new(HashMap::new()),
            native_host_migrations: Mutex::new(HashMap::new()),
            lobby_restore_split_transitions: Mutex::new(HashMap::new()),
            quick_match_party_departures: Mutex::new(HashMap::new()),
            quick_match_former_host_restore_gates: Mutex::new(HashMap::new()),
            pending_quick_match_member_leaves: Mutex::new(HashMap::new()),
            published_game_sessions: Mutex::new(HashMap::new()),
        })
    }

    fn note_native_host_migration(&self, type_id: u32, session_id: u32) {
        if let Ok(mut migrations) = self.native_host_migrations.lock() {
            migrations.insert((type_id, session_id), Instant::now());
        }
    }

    fn native_host_migration_is_active(&self, type_id: u32, session_id: u32) -> bool {
        let Ok(migrations) = self.native_host_migrations.lock() else {
            return false;
        };
        migrations.get(&(type_id, session_id)).is_some_and(|started| started.elapsed() <= NATIVE_HOST_MIGRATION_TTL)
    }

    fn clear_native_host_migration(&self, type_id: u32, session_id: u32) {
        if let Ok(mut migrations) = self.native_host_migrations.lock() {
            migrations.remove(&(type_id, session_id));
        }
    }

    pub(crate) fn native_host_migration_cleanup_due(&self) -> bool {
        self.native_host_migrations
            .lock()
            .is_ok_and(|migrations| migrations.values().any(|started| started.elapsed() > NATIVE_HOST_MIGRATION_TTL))
    }

    fn take_expired_native_host_migrations(&self) -> Vec<(u32, u32)> {
        let Ok(mut migrations) = self.native_host_migrations.lock() else {
            return Vec::new();
        };
        let expired = migrations
            .iter()
            .filter_map(|(key, started)| (started.elapsed() > NATIVE_HOST_MIGRATION_TTL).then_some(*key))
            .collect::<Vec<_>>();
        for key in &expired {
            migrations.remove(key);
        }
        expired
    }

    pub fn record_published_game_session(&self, user_id: u32, session_id: u64) {
        if let Ok(mut sessions) = self.published_game_sessions.lock() {
            if let Ok(session_id) = u32::try_from(session_id) {
                sessions.insert(user_id, session_id);
            } else {
                sessions.remove(&user_id);
            }
        }
    }

    pub fn clear_published_game_session(&self, user_id: u32) {
        if let Ok(mut sessions) = self.published_game_sessions.lock() {
            sessions.remove(&user_id);
        }
    }

    fn published_game_session(&self, user_id: u32) -> Option<u32> {
        self.published_game_sessions.lock().ok()?.get(&user_id).copied()
    }

    fn record_game_room_departure(&self, type_id: u32, session_id: u32, user_id: u32) {
        if let Ok(mut departures) = self.recent_game_room_departures.lock() {
            let now = Instant::now();
            departures.retain(|_, recorded_at| now.duration_since(*recorded_at) <= GAME_ROOM_DEPARTURE_CORRELATION_TTL);
            departures.insert((type_id, session_id, user_id), now);
        }
    }

    fn recent_game_room_departures(&self, type_id: u32, session_id: u32) -> Vec<u32> {
        let Ok(mut departures) = self.recent_game_room_departures.lock() else {
            return Vec::new();
        };
        let now = Instant::now();
        departures.retain(|_, recorded_at| now.duration_since(*recorded_at) <= GAME_ROOM_DEPARTURE_CORRELATION_TTL);
        departures
            .iter()
            .filter_map(|((departure_type, departure_session, user_id), _)| (*departure_type == type_id && *departure_session == session_id).then_some(*user_id))
            .collect()
    }

    fn record_pending_quick_match_member_leave(&self, type_id: u32, session_id: u32, user_id: u32) {
        self.record_pending_quick_match_member_leave_for_invite(type_id, session_id, user_id, None);
    }

    fn record_pending_quick_match_member_leave_for_invite(&self, type_id: u32, session_id: u32, user_id: u32, accepted_invite: Option<PendingAcceptedNormalInvite>) {
        if let Ok(mut departures) = self.pending_quick_match_member_leaves.lock() {
            departures.insert(
                (type_id, session_id, user_id),
                PendingQuickMatchMemberLeave {
                    recorded_at: Instant::now(),
                    accepted_invite,
                },
            );
        }
    }

    fn pending_quick_match_member_leaves(&self, type_id: u32, session_id: u32) -> Vec<u32> {
        let Ok(departures) = self.pending_quick_match_member_leaves.lock() else {
            return Vec::new();
        };
        departures
            .keys()
            .filter_map(|(departure_type, departure_session, user_id)| (*departure_type == type_id && *departure_session == session_id).then_some(*user_id))
            .collect()
    }

    fn clear_pending_quick_match_member_leaves(&self, type_id: u32, session_id: u32) {
        if let Ok(mut departures) = self.pending_quick_match_member_leaves.lock() {
            departures.retain(|(departure_type, departure_session, _), _| *departure_type != type_id || *departure_session != session_id);
        }
    }

    pub(crate) fn pending_quick_match_member_leave_cleanup_due(&self) -> bool {
        self.pending_quick_match_member_leaves
            .lock()
            .is_ok_and(|departures| departures.values().any(|pending| pending.recorded_at.elapsed() > GAME_ROOM_DEPARTURE_CORRELATION_TTL))
    }

    fn take_expired_quick_match_member_leaves(&self) -> Vec<(u32, u32, u32, Option<PendingAcceptedNormalInvite>)> {
        let Ok(mut departures) = self.pending_quick_match_member_leaves.lock() else {
            return Vec::new();
        };
        let expired = departures
            .iter()
            .filter_map(|(key, pending)| (pending.recorded_at.elapsed() > GAME_ROOM_DEPARTURE_CORRELATION_TTL).then_some((key.0, key.1, key.2, pending.accepted_invite)))
            .collect::<Vec<_>>();
        for (type_id, session_id, user_id, _) in &expired {
            departures.remove(&(*type_id, *session_id, *user_id));
        }
        expired
    }

    fn clear_game_room_departures(&self, type_id: u32, session_id: u32) {
        if let Ok(mut departures) = self.recent_game_room_departures.lock() {
            departures.retain(|(departure_type, departure_session, _), _| *departure_type != type_id || *departure_session != session_id);
        }
    }

    fn clear_game_room_departure_for_rejoin(&self, type_id: u32, session_id: u32, user_id: u32) {
        if let Ok(mut departures) = self.recent_game_room_departures.lock() {
            departures.remove(&(type_id, session_id, user_id));
        }
        if let Ok(mut departures) = self.pending_quick_match_member_leaves.lock() {
            departures.remove(&(type_id, session_id, user_id));
        }
        if let Ok(mut snapshots) = self.quick_match_party_departures.lock() {
            snapshots.retain(|(snapshot_type, snapshot_session, _), _| *snapshot_type != type_id || *snapshot_session != session_id);
        }
    }

    fn remember_quick_match_party_departure(&self, type_id: u32, session_id: u32, snapshot: QuickMatchPartyDeparture) {
        if let Ok(mut snapshots) = self.quick_match_party_departures.lock() {
            let now = Instant::now();
            snapshots.retain(|_, pending| now.duration_since(pending.captured_at) <= GAME_ROOM_DEPARTURE_CORRELATION_TTL);
            let key = (type_id, session_id, snapshot.party_id);
            if let Some(pending) = snapshots.get_mut(&key) {
                pending.captured_at = snapshot.captured_at;
                pending.members.extend(snapshot.members);
                pending.members.sort_unstable();
                pending.members.dedup();
                pending.departed_members.extend(snapshot.departed_members);
            } else {
                snapshots.insert(key, snapshot);
            }
        }
    }

    fn note_quick_match_party_member_departed(&self, type_id: u32, session_id: u32, user_id: u32) -> Option<QuickMatchPartyDeparture> {
        let mut snapshots = self.quick_match_party_departures.lock().ok()?;
        let now = Instant::now();
        snapshots.retain(|_, pending| now.duration_since(pending.captured_at) <= GAME_ROOM_DEPARTURE_CORRELATION_TTL);
        let key = snapshots.iter_mut().find_map(|(key, pending)| {
            if key.0 != type_id || key.1 != session_id || !pending.members.contains(&user_id) {
                return None;
            }
            pending.departed_members.insert(user_id);
            (pending.departed_members.len() >= 2).then_some(*key)
        })?;
        snapshots.remove(&key)
    }

    fn clear_quick_match_party_departure(&self, type_id: u32, session_id: u32, party_id: i64) {
        if let Ok(mut snapshots) = self.quick_match_party_departures.lock() {
            snapshots.remove(&(type_id, session_id, party_id));
        }
    }

    fn note_quick_match_former_host_restore_gate(&self, type_id: u32, user_id: u32, seed_lobby_session_id: u32, party_id: i64) {
        if let Ok(mut gates) = self.quick_match_former_host_restore_gates.lock() {
            gates.insert(
                (type_id, user_id),
                QuickMatchFormerHostRestoreGate {
                    started_at: Instant::now(),
                    seed_lobby_session_id,
                    party_id,
                },
            );
        }
    }

    fn quick_match_former_host_restore_gate(&self, type_id: u32, user_id: u32) -> Option<QuickMatchFormerHostRestoreGate> {
        let mut gates = self.quick_match_former_host_restore_gates.lock().ok()?;
        let now = Instant::now();
        gates.retain(|_, gate| now.duration_since(gate.started_at) <= QUICK_MATCH_FORMER_HOST_RESTORE_TTL);
        gates.get(&(type_id, user_id)).copied()
    }

    fn clear_quick_match_former_host_restore_gate(&self, type_id: u32, user_id: u32) {
        if let Ok(mut gates) = self.quick_match_former_host_restore_gates.lock() {
            gates.remove(&(type_id, user_id));
        }
    }

    /// Acquires SQLite's single writer slot before a multi-step mutation reads
    /// any state. A deferred transaction can otherwise read successfully and
    /// then fail immediately with SQLITE_BUSY while upgrading to a writer,
    /// even though every pooled connection has a busy timeout.
    async fn begin_write(&self) -> sqlx::Result<sqlx::Transaction<'static, sqlx::Sqlite>> {
        self.pool.begin_with("BEGIN IMMEDIATE").await
    }

    /// Handles a secure PRUDP connection closing or expiring. When the
    /// in-game overlay has checked in recently, only the obsolete secure ticket
    /// is removed; the lobby/room membership and station URL stay available.
    /// The preserved state is cleaned after the API heartbeat becomes stale.
    /// Returns true when game state was preserved.
    pub fn close_secure_session(&self, user_id: u32) -> Result<bool> {
        let (preserved, _handoffs) = run(async {
            let mut transaction = self.begin_write().await?;
            let (recent_api,): (i64,) = sqlx::query_as("SELECT EXISTS(SELECT 1 FROM users WHERE id = ? AND last_api_seen_at >= datetime('now', '-15 seconds'))")
                .bind(user_id)
                .fetch_one(&mut *transaction)
                .await?;

            sqlx::query("DELETE FROM user_sessions WHERE user_id = ?").bind(user_id).execute(&mut *transaction).await?;
            sqlx::query("UPDATE users SET is_online = 0 WHERE id = ?").bind(user_id).execute(&mut *transaction).await?;

            if recent_api != 0 {
                transaction.commit().await?;
                return Ok::<(bool, Vec<(u32, u32, u32)>), sqlx::Error>((true, Vec::new()));
            }

            let handoffs = Self::cleanup_user_game_state_async(&mut transaction, user_id).await?;
            transaction.commit().await?;
            Ok::<(bool, Vec<(u32, u32, u32)>), sqlx::Error>((false, handoffs))
        })??;
        Ok(preserved)
    }

    async fn cleanup_user_game_state_async(transaction: &mut sqlx::Transaction<'_, sqlx::Sqlite>, user_id: u32) -> sqlx::Result<Vec<(u32, u32, u32)>> {
        let affected_sessions = sqlx::query_as::<_, (u32, u32, u32)>(
            r"
            SELECT DISTINCT g.type_id, g.id, g.creator_id
            FROM game_sessions AS g
            LEFT JOIN participants AS p ON p.game_id = g.id
            WHERE g.destroyed_at IS NULL
              AND (g.creator_id = ? OR p.user_id = ?)
            ",
        )
        .bind(user_id)
        .bind(user_id)
        .fetch_all(&mut **transaction)
        .await?;

        let mut pending_migrations = Vec::new();
        for (type_id, session_id, creator_id) in &affected_sessions {
            if *creator_id != user_id {
                continue;
            }
            let (remaining_participants,): (i64,) = sqlx::query_as("SELECT COUNT(*) FROM participants WHERE game_id = ? AND user_id != ?")
                .bind(session_id)
                .bind(user_id)
                .fetch_one(&mut **transaction)
                .await?;
            if remaining_participants > 0 {
                #[allow(clippy::cast_possible_truncation, clippy::cast_sign_loss)]
                pending_migrations.push((*type_id, *session_id, remaining_participants as u32));
            }
        }

        // A departing host remains the recorded owner/member only until the
        // game-selected successor submits SplitSession.  Preselecting the
        // lowest participant here makes two clients believe they are host and
        // splits the N-1 Party.  Non-host memberships and solo host state can
        // still be removed immediately.
        let has_pending_migration = !pending_migrations.is_empty();
        if !has_pending_migration {
            sqlx::query("DELETE FROM parties WHERE leader_id = ?").bind(user_id).execute(&mut **transaction).await?;
            sqlx::query("DELETE FROM party_members WHERE user_id = ?").bind(user_id).execute(&mut **transaction).await?;
        }
        sqlx::query(
            r"
            DELETE FROM participants
            WHERE user_id = ?
              AND NOT EXISTS (
                  SELECT 1
                  FROM game_sessions AS session
                  INNER JOIN participants AS survivor
                      ON survivor.game_id = session.id AND survivor.user_id != ?
                  WHERE session.id = participants.game_id
                    AND session.creator_id = ?
                    AND session.destroyed_at IS NULL
              )
            ",
        )
        .bind(user_id)
        .bind(user_id)
        .bind(user_id)
        .execute(&mut **transaction)
        .await?;
        sqlx::query("DELETE FROM station_urls WHERE user_id = ?").bind(user_id).execute(&mut **transaction).await?;
        sqlx::query("DELETE FROM user_sessions WHERE user_id = ?").bind(user_id).execute(&mut **transaction).await?;
        sqlx::query("UPDATE users SET is_online = 0 WHERE id = ?").bind(user_id).execute(&mut **transaction).await?;

        for (type_id, session_id, _) in affected_sessions {
            Self::reconcile_game_session_async(transaction, type_id, session_id).await?;
        }
        Ok(pending_migrations)
    }

    /// Removes preserved lobby/room state after both the secure session and
    /// overlay heartbeat are gone. Active secure users have `is_online=1`
    /// and are never selected by this sweep.
    pub async fn cleanup_stale_api_sessions_async(&self) -> Result<()> {
        let mut transaction = self.begin_write().await?;
        let stale_users: Vec<(u32,)> = sqlx::query_as(
            r"
            SELECT id
            FROM users
            WHERE is_online = 0
              AND (last_api_seen_at IS NULL OR last_api_seen_at < datetime('now', '-15 seconds'))
              AND (
                  EXISTS (SELECT 1 FROM station_urls WHERE station_urls.user_id = users.id)
                  OR EXISTS (SELECT 1 FROM participants WHERE participants.user_id = users.id)
              )
            ",
        )
        .fetch_all(&mut *transaction)
        .await?;

        let mut _handoffs = Vec::new();
        for (user_id,) in stale_users {
            _handoffs.extend(
                Self::cleanup_user_game_state_async(&mut transaction, user_id)
                    .await?
                    .into_iter()
                    .map(|(type_id, session_id, remaining_participants)| (type_id, session_id, user_id, remaining_participants)),
            );
        }
        transaction.commit().await?;
        Ok(())
    }

    pub fn invalidate_sessions(&self) -> Result<()> {
        run(async {
            sqlx::query("DELETE FROM parties").execute(&self.pool).await?;
            sqlx::query("DELETE FROM party_members").execute(&self.pool).await?;
            sqlx::query("DELETE FROM station_urls").execute(&self.pool).await?;
            sqlx::query("DELETE FROM user_sessions").execute(&self.pool).await?;
            sqlx::query("UPDATE game_sessions SET destroyed_at=CURRENT_TIMESTAMP WHERE destroyed_at IS NULL")
                .execute(&self.pool)
                .await?;
            sqlx::query("UPDATE users SET is_online=0").execute(&self.pool).await
        })??;

        Ok(())
    }

    pub fn create_game_session(&self, user_id: u32, type_id: u32, attributes: String) -> Result<u32> {
        let game_room = is_game_room_attributes(&attributes);
        let personal_lobby = session_attribute_value(&attributes, 113) == Some(1);
        let (id, follow_count, retired_solo_lobbies, regroup_followers, personal_party_ensured) = run(async {
            let mut transaction = self.begin_write().await?;
            let id = sqlx::query("INSERT INTO game_sessions (type_id, creator_id, attributes) VALUES (?, ?, ?)")
                .bind(type_id)
                .bind(user_id)
                .bind(&attributes)
                .execute(&mut *transaction)
                .await?
                .last_insert_rowid();
            sqlx::query("INSERT OR IGNORE INTO participants (game_id, user_id) VALUES (?, ?)")
                .bind(id)
                .bind(user_id)
                .execute(&mut *transaction)
                .await?;
            let retired_solo_lobbies = if personal_lobby {
                #[allow(clippy::cast_possible_truncation, clippy::cast_sign_loss)]
                Self::retire_superseded_solo_lobbies_async(&mut transaction, user_id, type_id, id as u32).await?
            } else {
                0
            };
            let regroup_followers = if personal_lobby {
                #[allow(clippy::cast_possible_truncation, clippy::cast_sign_loss)]
                Self::complete_private_room_party_leader_restore_async(&mut transaction, user_id, type_id, id as u32).await?
            } else {
                0
            };
            let personal_party_ensured = if personal_lobby {
                Self::ensure_personal_party_if_unassigned_async(&mut transaction, user_id).await?
            } else {
                false
            };
            let follow_count = if game_room {
                #[allow(clippy::cast_possible_truncation, clippy::cast_sign_loss)]
                Self::queue_party_follow_invites_async(&mut transaction, user_id, type_id, id as u32).await?
            } else {
                0
            };
            transaction.commit().await?;
            Ok::<(i64, u64, u64, u64, bool), sqlx::Error>((id, follow_count, retired_solo_lobbies, regroup_followers, personal_party_ensured))
        })??;

        #[allow(clippy::cast_possible_truncation)]
        #[allow(clippy::cast_sign_loss)]
        let session_id = id as u32;
        if follow_count > 0 {
            info!(
                self.logger,
                "PartySessionBound leader_id={user_id} session_type={type_id} session_id={session_id} follow_invites={follow_count}"
            );
        }
        if retired_solo_lobbies > 0 {
            info!(
                self.logger,
                "SupersededSoloLobbyRetired user_id={user_id} type_id={type_id} replacement_session_id={session_id} retired_count={retired_solo_lobbies}"
            );
        }
        if regroup_followers > 0 {
            info!(
                self.logger,
                "PrivateRoomPartyLeaderRestored leader_id={user_id} session_type={type_id} lobby_session_id={session_id} follower_invites={regroup_followers}"
            );
        }
        if personal_party_ensured {
            info!(
                self.logger,
                "PersonalLobbyPartyEnsured user_id={user_id} type_id={type_id} session_id={session_id} member_count=1"
            );
        }
        Ok(session_id)
    }

    /// Records a validated JoinSession caller as a participant without applying
    /// any capacity gate. The game may repeat JoinSession and AddParticipants,
    /// so INSERT OR IGNORE keeps the operation idempotent.
    ///
    /// Once a participant is newly persisted, all of that receiver's outstanding
    /// invitations are consumed in the same transaction. Repeated JoinSession
    /// calls remain idempotent and cannot erase invitations received later.
    ///
    /// Returns false only when the referenced session is missing or destroyed.
    pub fn join_game_session_unbounded(&self, user_id: u32, type_id: u32, session_id: u32) -> Result<bool> {
        let (joined, follow_count, regroup_followers) = run(async {
            let mut transaction = self.begin_write().await?;
            let session = sqlx::query_as::<_, (u32, String)>("SELECT creator_id, attributes FROM game_sessions WHERE id = ? AND type_id = ? AND destroyed_at IS NULL")
                .bind(session_id)
                .bind(type_id)
                .fetch_optional(&mut *transaction)
                .await?;
            let Some((creator_id, attributes)) = session else {
                transaction.rollback().await?;
                return Ok::<(bool, u64, u64), sqlx::Error>((false, 0, 0));
            };

            let inserted = sqlx::query("INSERT OR IGNORE INTO participants (game_id, user_id) VALUES (?, ?)")
                .bind(session_id)
                .bind(user_id)
                .execute(&mut *transaction)
                .await?
                .rows_affected()
                > 0;

            let accepted_party_invite = Self::attach_accepted_invite_party_async(&mut transaction, user_id, type_id, session_id).await?;

            let regroup_followers = if creator_id == user_id && session_attribute_value(&attributes, 113) == Some(1) {
                Self::complete_private_room_party_leader_restore_async(&mut transaction, user_id, type_id, session_id).await?
            } else {
                0
            };

            if inserted || accepted_party_invite.is_some() {
                Self::consume_all_invites_for_receiver_async(&mut transaction, user_id).await?;
            }

            if accepted_party_invite == Some(INVITE_KIND_LOBBY_RESTORE) {
                // The native client can ask the current lobby owner to remove
                // this just-restored member immediately before that member's
                // SplitSession elects the final transport host. Keep a short,
                // explicit transition after the invite row is consumed so the
                // two calls cannot be misread as an explicit Leave Party.
                self.note_lobby_restore_split_transition(type_id, session_id, user_id);
            }

            let private_room = is_private_room_attributes(&attributes);
            let follow_count = if is_game_room_attributes(&attributes) {
                let follow_leader_id = if private_room { creator_id } else { user_id };
                Self::queue_party_follow_invites_async(&mut transaction, follow_leader_id, type_id, session_id).await?
            } else {
                0
            };
            transaction.commit().await?;
            Ok::<(bool, u64, u64), sqlx::Error>((true, follow_count, regroup_followers))
        })??;
        if follow_count > 0 {
            info!(
                self.logger,
                "PartySessionBound session_type={type_id} session_id={session_id} follow_invites={follow_count}"
            );
        }
        if regroup_followers > 0 {
            info!(
                self.logger,
                "PrivateRoomPartyLeaderRestored leader_id={user_id} session_type={type_id} lobby_session_id={session_id} follower_invites={regroup_followers}"
            );
        }
        if joined {
            // A successful rejoin starts a new room-membership generation.
            // Old departure correlation for this same user/session must not be
            // combined with a later coordinated leave during rapid testing.
            self.clear_game_room_departure_for_rejoin(type_id, session_id, user_id);
        }
        Ok(joined)
    }

    pub fn add_participants(&self, caller_id: u32, type_id: u32, session_id: u32, private_participants: Vec<u32>, public_participants: Vec<u32>) -> Result<()> {
        let private_participants = private_participants.into_iter().collect::<HashSet<_>>();
        let public_participants = public_participants.into_iter().collect::<HashSet<_>>();
        if private_participants.is_empty() && public_participants.is_empty() {
            warn!(self.logger, "Empty participant list");
            return Ok(());
        }
        let (mut deferred_private, mut host_admitted_private, mut persisted_private, mut newly_admitted) = run(async {
            let mut transaction = self.begin_write().await?;
            let session = sqlx::query_as::<_, (u32, String)>("SELECT creator_id, attributes FROM game_sessions WHERE id = ? AND type_id = ? AND destroyed_at IS NULL")
                .bind(session_id)
                .bind(type_id)
                .fetch_optional(&mut *transaction)
                .await?;
            let Some((creator_id, _attributes)) = session else {
                transaction.rollback().await?;
                return Ok::<(Vec<u32>, Vec<u32>, Vec<u32>, Vec<u32>), sqlx::Error>((Vec::new(), Vec::new(), Vec::new(), Vec::new()));
            };

            let mut newly_admitted = Vec::new();
            for user_id in public_participants {
                let (already_joined,): (i64,) = sqlx::query_as("SELECT EXISTS(SELECT 1 FROM participants WHERE game_id = ? AND user_id = ?)")
                    .bind(session_id)
                    .bind(user_id)
                    .fetch_one(&mut *transaction)
                    .await?;
                sqlx::query("INSERT OR IGNORE INTO participants (game_id, user_id) VALUES (?, ?)")
                    .bind(session_id)
                    .bind(user_id)
                    .execute(&mut *transaction)
                    .await?;
                if already_joined == 0 {
                    newly_admitted.push(user_id);
                }
            }

            let mut deferred_private = Vec::new();
            let mut host_admitted_private = Vec::new();
            let mut persisted_private = Vec::new();
            for user_id in private_participants {
                let pending_invite_sender = sqlx::query_as::<_, (u32,)>(
                    r"
                    SELECT sender FROM invites
                    WHERE receiver = ? AND kind IN (?, ?) AND session_type = ? AND session_id = ?
                      AND accepted_at IS NOT NULL AND consumed_at IS NULL AND expires_at > CURRENT_TIMESTAMP
                    ORDER BY id DESC LIMIT 1
                    ",
                )
                .bind(user_id)
                .bind(INVITE_KIND_PRIVATE_ROOM)
                .bind(INVITE_KIND_PARTY_FOLLOW)
                .bind(type_id)
                .bind(session_id)
                .fetch_optional(&mut *transaction)
                .await?;
                let (already_joined,): (i64,) = sqlx::query_as("SELECT EXISTS(SELECT 1 FROM participants WHERE game_id = ? AND user_id = ?)")
                    .bind(session_id)
                    .bind(user_id)
                    .fetch_one(&mut *transaction)
                    .await?;

                if let Some((_invite_sender,)) = pending_invite_sender {
                    if already_joined == 0 {
                        // The room owner is the authoritative P2P admission
                        // source. The accepted invitation may have been sent by
                        // any current room/Party member, so requiring that the
                        // invitation sender is also the owner strands legitimate
                        // non-host invitees outside formal membership. They then
                        // miss lobby restore and host-migration notifications.
                        if caller_id == creator_id {
                            sqlx::query("INSERT OR IGNORE INTO participants (game_id, user_id) VALUES (?, ?)")
                                .bind(session_id)
                                .bind(user_id)
                                .execute(&mut *transaction)
                                .await?;
                            host_admitted_private.push(user_id);
                            newly_admitted.push(user_id);
                        } else {
                            deferred_private.push(user_id);
                        }
                        continue;
                    }
                }
                sqlx::query("INSERT OR IGNORE INTO participants (game_id, user_id) VALUES (?, ?)")
                    .bind(session_id)
                    .bind(user_id)
                    .execute(&mut *transaction)
                    .await?;
                persisted_private.push(user_id);
                if already_joined == 0 {
                    newly_admitted.push(user_id);
                }
            }
            Self::reconcile_game_session_async(&mut transaction, type_id, session_id).await?;
            for user_id in &newly_admitted {
                let invite_kind = Self::attach_accepted_invite_party_async(&mut transaction, *user_id, type_id, session_id).await?;
                Self::consume_all_invites_for_receiver_async(&mut transaction, *user_id).await?;
                if invite_kind == Some(INVITE_KIND_LOBBY_RESTORE) {
                    self.note_lobby_restore_split_transition(type_id, session_id, *user_id);
                }
            }
            transaction.commit().await?;
            Ok::<(Vec<u32>, Vec<u32>, Vec<u32>, Vec<u32>), sqlx::Error>((deferred_private, host_admitted_private, persisted_private, newly_admitted))
        })??;

        deferred_private.sort_unstable();
        host_admitted_private.sort_unstable();
        persisted_private.sort_unstable();
        newly_admitted.sort_unstable();
        if !deferred_private.is_empty() {
            info!(
                self.logger,
                "PrivateParticipantsDeferredUntilJoin type_id={type_id} session_id={session_id} participant_ids={deferred_private:?} invitation_kinds=[1,3] formal_membership=JoinSession"
            );
        }
        if !host_admitted_private.is_empty() {
            info!(
                self.logger,
                "PrivateInviteOwnerAdmissionPersisted caller_id={caller_id} session_creator_id={caller_id} type_id={type_id} session_id={session_id} participant_ids={host_admitted_private:?} membership_source=owner-AddParticipants-after-P2P invitation_kinds=[1,3]"
            );
        }
        if !persisted_private.is_empty() {
            info!(
                self.logger,
                "PrivateParticipantsPersisted type_id={type_id} session_id={session_id} participant_ids={persisted_private:?} accepted_invite_pending=false"
            );
        }
        if !newly_admitted.is_empty() {
            info!(
                self.logger,
                "InviteLifecycleClearedOnAdmission type_id={type_id} session_id={session_id} participant_ids={newly_admitted:?} scope=all-receiver-invites"
            );
        }
        Ok(())
    }

    /// Handles the owner leaving a session. A populated session must remain
    /// alive so the retail client can run its native host-migration sequence;
    /// destroying it here makes every follower enter StateError before
    /// SplitSession can elect the real replacement host.
    pub fn delete_game_session(&self, creator_id: u32, type_id: u32, session_id: u32) -> Result<DeleteGameSessionOutcome> {
        let recent_departures = self.recent_game_room_departures(type_id, session_id);
        let published_session_id = self.published_game_session(creator_id);
        let (outcome, party_handoff, party_return) = run(async {
            let mut transaction = self.begin_write().await?;
            let owned = sqlx::query_as::<_, (String,)>("SELECT attributes FROM game_sessions WHERE creator_id = ? AND type_id = ? AND id = ? AND destroyed_at IS NULL")
                .bind(creator_id)
                .bind(type_id)
                .bind(session_id)
                .fetch_optional(&mut *transaction)
                .await?;
            if owned.is_none() {
                transaction.rollback().await?;
                return Ok::<(DeleteGameSessionOutcome, Option<(u32, u32, u64, u64)>, Option<(u32, u64, u64, u64)>), sqlx::Error>((DeleteGameSessionOutcome::NotFound, None, None));
            }
            let attributes = owned.expect("checked above").0;

            let remaining_participants = sqlx::query_as::<_, (u32,)>("SELECT user_id FROM participants WHERE game_id = ? AND user_id != ? ORDER BY user_id")
                .bind(session_id)
                .bind(creator_id)
                .fetch_all(&mut *transaction)
                .await?;
            let mut remaining_participant_ids = remaining_participants.iter().map(|(user_id,)| *user_id).collect::<Vec<_>>();
            if is_private_room_attributes(&attributes) && !recent_departures.is_empty() {
                // A follower removes its Party row as soon as it leaves an
                // otherwise live private match. The in-memory marker is
                // therefore the authoritative proof for the narrow native
                // unwind in which followers leave immediately before the
                // owner. Older voluntary departures have already expired.
                remaining_participant_ids.extend(recent_departures.iter().copied());
                remaining_participant_ids.sort_unstable();
                remaining_participant_ids.dedup();
            }
            let party_return = if is_private_room_attributes(&attributes) {
                if let Some(published_lobby_session_id) = published_session_id {
                    Self::restore_published_lobby_party_async(&mut transaction, creator_id, session_id, published_lobby_session_id)
                        .await?
                        .map(|(member_count, restore_invites, cancelled_personal_restores)| {
                            (published_lobby_session_id, member_count, restore_invites, cancelled_personal_restores)
                        })
                } else {
                    None
                }
            } else {
                None
            };
            let party_handoff = if is_private_room_attributes(&attributes) && party_return.is_none() {
                Self::handoff_private_room_party_async(&mut transaction, creator_id, type_id, session_id, &remaining_participant_ids).await?
            } else {
                None
            };

            if remaining_participants.is_empty() {
                sqlx::query(
                    "UPDATE game_sessions SET destroyed_at=CURRENT_TIMESTAMP, host_leave_pending=0 WHERE creator_id = ? AND type_id = ? AND id = ? AND destroyed_at IS NULL",
                )
                .bind(creator_id)
                .bind(type_id)
                .bind(session_id)
                .execute(&mut *transaction)
                .await?;
                transaction.commit().await?;
                return Ok((DeleteGameSessionOutcome::Destroyed, party_handoff, party_return));
            }

            // Keep the exact membership and owner stable until the native
            // election names its successor through SplitSession.  Removing or
            // transferring here races the game's election state machine.
            sqlx::query("UPDATE game_sessions SET host_leave_pending = 1 WHERE id = ? AND type_id = ? AND creator_id = ? AND destroyed_at IS NULL")
                .bind(session_id)
                .bind(type_id)
                .bind(creator_id)
                .execute(&mut *transaction)
                .await?;
            transaction.commit().await?;
            #[allow(clippy::cast_possible_truncation)]
            Ok((
                DeleteGameSessionOutcome::MigrationPending {
                    remaining_participants: remaining_participants.len() as u32,
                },
                party_handoff,
                party_return,
            ))
        })??;
        self.clear_game_room_departures(type_id, session_id);
        if let Some((new_leader_id, lobby_session_id, member_count, restore_invites)) = party_handoff {
            info!(
                self.logger,
                "PrivateRoomPartyHandedOff old_leader_id={creator_id} new_leader_id={new_leader_id} type_id={type_id} room_session_id={session_id} lobby_session_id={lobby_session_id} member_count={member_count} restore_invites={restore_invites} delivery_gate=left-old-room"
            );
        }
        if let Some((lobby_session_id, member_count, restore_invites, cancelled_personal_restores)) = party_return {
            info!(
                self.logger,
                "PrivateRoomPartyReturnedWithHost host_id={creator_id} type_id={type_id} room_session_id={session_id} lobby_session_id={lobby_session_id} member_count={member_count} late_member_restore_invites={restore_invites} cancelled_personal_restores={cancelled_personal_restores} source=published-multiplayer-lobby"
            );
        }
        Ok(outcome)
    }

    /// Returns active sessions the user may invite another player into. Direct
    /// Quazal membership is authoritative. Persistent Party
    /// membership is also accepted because a room transition can briefly
    /// publish the host's new private session before every follower has been
    /// recorded in `participants`. Party membership is matched to the current
    /// session creator, rather than the original party leader, so a legitimate
    /// host migration does not strand the remaining members.
    pub async fn find_invitable_session_candidates_async(&self, user_id: u32) -> Result<Vec<InvitableGameSession>> {
        let mut candidates = sqlx::query_as::<_, (u32, u32, u32, String, i64, i64)>(
            r"
            SELECT DISTINCT
                g.type_id AS session_type,
                g.id AS session_id,
                g.creator_id,
                g.attributes,
                (SELECT COUNT(*) FROM participants AS member WHERE member.game_id = g.id) AS participant_count,
                EXISTS (
                    SELECT 1 FROM participants AS direct_member
                    WHERE direct_member.game_id = g.id
                      AND direct_member.user_id = ?
                ) AS direct_member
            FROM game_sessions AS g
            INNER JOIN participants AS host
                ON host.game_id = g.id AND host.user_id = g.creator_id
            WHERE g.destroyed_at IS NULL
              AND EXISTS (SELECT 1 FROM station_urls AS s WHERE s.user_id = g.creator_id)
              AND (
                  EXISTS (
                      SELECT 1 FROM participants AS direct_member
                      WHERE direct_member.game_id = g.id
                        AND direct_member.user_id = ?
                  )
                  OR EXISTS (
                      SELECT 1
                      FROM party_members AS party_member
                      INNER JOIN party_members AS host_party_member
                          ON host_party_member.party_id = party_member.party_id
                      WHERE party_member.user_id = ?
                        AND host_party_member.user_id = g.creator_id
                  )
              )
            ORDER BY g.id DESC
            ",
        )
        .bind(user_id)
        .bind(user_id)
        .bind(user_id)
        .fetch_all(&self.pool)
        .await?;

        // Private rooms remain first, including the transition window before
        // every Party follower is recorded there. Among lobbies, a shared
        // migrated Party outranks a newer preserved solo fallback. The solo
        // lobby remains available for native recovery, but it must not replace
        // the N-1 Party as the target of a later invitation.
        candidates.sort_by_key(|(_, session_id, _, attributes, participant_count, _)| {
            let class = if is_game_room_attributes(attributes) {
                0
            } else if *participant_count > 1 {
                1
            } else {
                2
            };
            (class, std::cmp::Reverse(*session_id))
        });

        Ok(candidates
            .into_iter()
            .map(|(session_type, session_id, creator_id, attributes, _, direct_member)| InvitableGameSession {
                session: GameSession {
                    session_type,
                    session_id,
                    creator_id,
                    attributes,
                    participants: Vec::new(),
                },
                direct_member: direct_member != 0,
            })
            .collect())
    }

    #[cfg(test)]
    pub async fn find_invitable_sessions_async(&self, user_id: u32) -> Result<Vec<GameSession>> {
        Ok(self
            .find_invitable_session_candidates_async(user_id)
            .await?
            .into_iter()
            .map(|candidate| candidate.session)
            .collect())
    }

    #[cfg(test)]
    pub async fn find_active_session_creator_async(&self, session_type: u32, session_id: u32) -> Result<Option<u32>> {
        Ok(self
            .find_active_session_invite_target_async(session_type, session_id)
            .await?
            .map(|(creator_id, _)| creator_id))
    }

    pub async fn find_active_session_invite_target_async(&self, session_type: u32, session_id: u32) -> Result<Option<(u32, String)>> {
        Ok(sqlx::query_as(
            r"
            SELECT g.creator_id, g.attributes
            FROM game_sessions AS g
            INNER JOIN participants AS host
                ON host.game_id = g.id AND host.user_id = g.creator_id
            WHERE g.type_id = ?
              AND g.id = ?
              AND g.destroyed_at IS NULL
              AND EXISTS (SELECT 1 FROM station_urls AS endpoint WHERE endpoint.user_id = g.creator_id)
            ",
        )
        .bind(session_type)
        .bind(session_id)
        .fetch_optional(&self.pool)
        .await?)
    }
}

#[derive(Debug, sqlx::FromRow)]
pub struct User {
    pub id: u32,
    pub username: String,
    pub ubi_id: String,
    pub is_online: bool,
}

#[derive(Debug, Clone, PartialEq, Eq, sqlx::FromRow)]
pub struct AuthIdentity {
    pub user_id: u32,
    pub username: String,
    pub account_id: String,
    pub auth_version: i64,
}

#[derive(Debug, sqlx::FromRow)]
pub struct GameSession {
    pub session_type: u32,
    pub session_id: u32,
    pub creator_id: u32,
    pub attributes: String,
    #[sqlx(skip)]
    pub participants: Vec<Participant>,
}

#[derive(Debug)]
pub struct InvitableGameSession {
    pub session: GameSession,
    pub direct_member: bool,
}

#[derive(Debug, sqlx::FromRow)]
pub struct Participant {
    pub user_id: u32,
    pub name: String,
    #[sqlx(skip)]
    pub station_urls: Vec<String>,
}

#[derive(Debug)]
pub struct InvitedGameSession {
    pub invite_id: i64,
    pub kind: i32,
    pub session: GameSession,
}

#[derive(Debug, Clone, Copy, sqlx::FromRow)]
pub struct Invite {
    pub id: i64,
    pub sender: u32,
    pub receiver: u32,
    pub kind: i32,
    pub session_type: u32,
    pub session_id: u32,
    pub delivery_count: i64,
}

#[cfg(test)]
mod tests {
    use std::sync::Arc;
    use std::time::SystemTime;
    use std::time::UNIX_EPOCH;

    use super::*;

    fn create_test_storage(name: &str) -> Storage {
        let unique = SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_nanos();
        let path = std::env::temp_dir().join(format!("5th-echelon-{name}-{}-{unique}.db", std::process::id()));
        let url = format!("sqlite://{}?mode=rwc", path.display());
        Storage::init_with_database_url(Logger::root(slog::Discard, slog::o!()), &url).unwrap()
    }

    #[test]
    fn fresh_database_keeps_tracking_service_principal_but_no_development_players() {
        let storage = create_test_storage("no-demo-seed-data");
        run(async {
            let demo_users: i64 = sqlx::query_scalar("SELECT COUNT(*) FROM users WHERE username IN ('Foo', 'AAAABBBB', 'sam_the_fisher')")
                .fetch_one(&storage.pool)
                .await?;
            let tracking_principal: i64 =
                sqlx::query_scalar("SELECT COUNT(*) FROM users WHERE id = 105 AND username = 'Tracking' AND password = 'JaDe!' AND password_hash IS NULL AND ubi_id IS NULL")
                    .fetch_one(&storage.pool)
                    .await?;
            let demo_sessions: i64 = sqlx::query_scalar("SELECT COUNT(*) FROM game_sessions WHERE id = 1 AND creator_id = 1000")
                .fetch_one(&storage.pool)
                .await?;
            assert_eq!(demo_users, 0);
            assert_eq!(tracking_principal, 1);
            assert_eq!(demo_sessions, 0);
            Ok::<(), sqlx::Error>(())
        })
        .unwrap()
        .unwrap();
    }

    #[test]
    fn fresh_player_registration_uses_the_proven_non_service_principal_range() {
        let storage = create_test_storage("player-principal-range");
        run(async {
            storage
                .register_user_with_recovery_async("first-player", "password", Some("first-account"), Some("first-recovery"))
                .await?;
            storage
                .register_user_with_recovery_async("second-player", "password", Some("second-account"), Some("second-recovery"))
                .await?;

            let principals: Vec<(String, u32)> = sqlx::query_as("SELECT username, id FROM users WHERE username IN ('first-player', 'second-player') ORDER BY id")
                .fetch_all(&storage.pool)
                .await?;

            assert_eq!(principals, vec![("first-player".to_string(), 1000), ("second-player".to_string(), 1001)]);
            assert!(principals.iter().all(|(_, id)| !matches!(*id, 1 | 2 | 105 | 0x1000)));
            Ok::<(), eyre::Report>(())
        })
        .unwrap()
        .unwrap();
    }

    #[test]
    fn tracking_service_principal_restore_rejects_conflicting_identity() {
        let storage = create_test_storage("tracking-principal-conflict");
        run(async {
            sqlx::query("DELETE FROM users WHERE id = 105").execute(&storage.pool).await?;
            sqlx::query("INSERT INTO users (id, username, password) VALUES (105, 'not-tracking', 'different')")
                .execute(&storage.pool)
                .await?;

            let result = sqlx::raw_sql(include_str!("migrations/20260823010000_restore_tracking_service_principal.sql"))
                .execute(&storage.pool)
                .await;
            assert!(result.is_err());

            let preserved: String = sqlx::query_scalar("SELECT username FROM users WHERE id = 105").fetch_one(&storage.pool).await?;
            assert_eq!(preserved, "not-tracking");
            Ok::<(), sqlx::Error>(())
        })
        .unwrap()
        .unwrap();
    }

    #[test]
    fn demo_cleanup_preserves_an_authenticated_matching_identity() {
        let storage = create_test_storage("preserve-authenticated-demo-fingerprint");
        run(async {
            sqlx::query("INSERT INTO users (id, username, password_hash, ubi_id, last_login) VALUES (1002, 'sam_the_fisher', ?, 'SAM', CURRENT_TIMESTAMP)")
                .bind("$argon2id$v=19$m=16,t=2,p=1$c1hvR0JwNTMyaTQ2Nk9uMQ$JOefJqEVFQhLM580DBnqXA")
                .execute(&storage.pool)
                .await?;

            sqlx::raw_sql(include_str!("migrations/20260822090000_remove_untouched_demo_data.sql"))
                .execute(&storage.pool)
                .await?;

            let preserved: i64 = sqlx::query_scalar("SELECT COUNT(*) FROM users WHERE id = 1002 AND username = 'sam_the_fisher'")
                .fetch_one(&storage.pool)
                .await?;
            assert_eq!(preserved, 1);
            Ok::<(), sqlx::Error>(())
        })
        .unwrap()
        .unwrap();
    }

    fn insert_users(storage: &Storage, count: u32) {
        run(async {
            for user_id in 1..=count {
                sqlx::query("INSERT OR REPLACE INTO users (id, username, password_hash, ubi_id) VALUES (?, ?, ?, ?)")
                    .bind(user_id)
                    .bind(format!("user-{user_id}"))
                    .bind("test-password-hash")
                    .bind(format!("ubi-{user_id}"))
                    .execute(&storage.pool)
                    .await?;
            }
            Ok::<(), sqlx::Error>(())
        })
        .unwrap()
        .unwrap();
    }

    fn expire_pending_quick_match_member_leaves_for_test(storage: &Storage) -> usize {
        for pending in storage.pending_quick_match_member_leaves.lock().unwrap().values_mut() {
            pending.recorded_at = Instant::now() - GAME_ROOM_DEPARTURE_CORRELATION_TTL - Duration::from_millis(1);
        }
        assert!(storage.pending_quick_match_member_leave_cleanup_due());
        storage.expire_pending_quick_match_member_leaves().unwrap()
    }

    #[test]
    fn sqlite_pool_applies_safety_pragmas_to_every_connection() {
        let storage = create_test_storage("pool-safety-pragmas");
        run(async {
            // Keep the acquisitions alive together so the pool must provide
            // distinct physical connections instead of reusing one handle.
            let mut connections = Vec::new();
            for _ in 0..3 {
                connections.push(storage.pool.acquire().await?);
            }

            for connection in &mut connections {
                let foreign_keys: i64 = sqlx::query_scalar("PRAGMA foreign_keys").fetch_one(&mut **connection).await?;
                let busy_timeout_ms: i64 = sqlx::query_scalar("PRAGMA busy_timeout").fetch_one(&mut **connection).await?;
                let journal_mode: String = sqlx::query_scalar("PRAGMA journal_mode").fetch_one(&mut **connection).await?;

                assert_eq!(foreign_keys, 1);
                assert_eq!(busy_timeout_ms, 5_000);
                assert_eq!(journal_mode.to_ascii_lowercase(), "wal");
            }
            Ok::<(), sqlx::Error>(())
        })
        .unwrap()
        .unwrap();
    }

    #[test]
    fn immediate_write_admission_waits_for_the_current_writer() {
        let storage = Arc::new(create_test_storage("immediate-write-admission"));
        insert_users(&storage, 2);
        let session_id = storage.create_game_session(1, 1, String::new()).unwrap();

        let mut blocker = run(storage.begin_write()).unwrap().unwrap();
        run(async {
            sqlx::query("UPDATE users SET last_api_seen_at = CURRENT_TIMESTAMP WHERE id = 1")
                .execute(&mut *blocker)
                .await?;
            Ok::<(), sqlx::Error>(())
        })
        .unwrap()
        .unwrap();

        let worker_storage = Arc::clone(&storage);
        let worker = std::thread::spawn(move || worker_storage.add_participants(1, 1, session_id, Vec::new(), vec![2]));
        std::thread::sleep(Duration::from_millis(100));
        run(blocker.commit()).unwrap().unwrap();
        worker.join().expect("writer thread must not panic").expect("second writer must wait and then commit");

        let (_, _, participants) = storage.game_session_diagnostics(1, session_id).unwrap().unwrap();
        assert_eq!(participants, vec![1, 2]);
    }

    #[test]
    fn join_session_records_twelve_participants_without_capacity_gate() {
        let storage = create_test_storage("join-twelve-participants");
        insert_users(&storage, 12);
        for user_id in 1..=12 {
            storage
                .register_urls(user_id, vec![format!("prudp:/address=10.66.0.{user_id};port=13000;sid=14;type=2")])
                .unwrap();
        }

        let session_id = storage.create_game_session(1, 42, "101 => 1".to_string()).unwrap();
        for user_id in 2..=12 {
            assert!(storage.join_game_session_unbounded(user_id, 42, session_id).unwrap());
        }
        // A repeated JoinSession and a later AddParticipants callback must not
        // inflate the participant count.
        assert!(storage.join_game_session_unbounded(12, 42, session_id).unwrap());
        storage.add_participants(1, 42, session_id, Vec::new(), vec![12]).unwrap();

        let (_, _, participant_ids) = storage.game_session_diagnostics(42, session_id).unwrap().unwrap();
        assert_eq!(participant_ids, (1..=12).collect::<Vec<_>>());
        let sessions_with_twelfth = storage.search_sessions_with_participants(42, &[12]).unwrap();
        assert_eq!(sessions_with_twelfth.len(), 1);
        assert_eq!(sessions_with_twelfth[0].participants.len(), 12);

        assert!(!storage.join_game_session_unbounded(12, 42, session_id + 10_000).unwrap());
    }

    #[test]
    fn supports_twelve_participants_without_capacity_gate() {
        let storage = create_test_storage("twelve-participants");
        insert_users(&storage, 12);
        for user_id in 1..=12 {
            storage
                .register_urls(user_id, vec![format!("prudp:/address=10.66.0.{user_id};port=13000;sid=14;type=2")])
                .unwrap();
        }

        let session_id = storage.create_game_session(1, 42, "101 => 1".to_string()).unwrap();
        for user_id in 2..=12 {
            storage.add_participants(1, 42, session_id, Vec::new(), vec![user_id]).unwrap();
        }

        let (_, _, participant_ids) = storage.game_session_diagnostics(42, session_id).unwrap().unwrap();
        assert_eq!(participant_ids, (1..=12).collect::<Vec<_>>());
        assert!(storage.game_session_is_active(42, session_id).unwrap());

        let all_sessions = storage.search_sessions_with_participants(42, &[]).unwrap();
        assert_eq!(all_sessions.len(), 1);
        assert_eq!(all_sessions[0].participants.len(), 12);
        let sessions_with_twelfth = storage.search_sessions_with_participants(42, &[12]).unwrap();
        assert_eq!(sessions_with_twelfth.len(), 1);
    }

    #[test]
    fn leave_waits_for_each_native_migration_and_final_leave_destroys_session() {
        let storage = create_test_storage("host-migration");
        insert_users(&storage, 3);
        let session_id = storage.create_game_session(1, 7, String::new()).unwrap();
        storage.add_participants(1, 7, session_id, Vec::new(), vec![2, 3]).unwrap();

        storage.leave_game_session(1, 7, session_id).unwrap();
        let (creator_id, _, participant_ids) = storage.game_session_diagnostics(7, session_id).unwrap().unwrap();
        assert_eq!(creator_id, 1);
        assert_eq!(participant_ids, vec![1, 2, 3]);
        assert!(storage.migrate_game_session_host(2, 7, session_id).unwrap());

        storage.leave_game_session(2, 7, session_id).unwrap();
        let (creator_id, _, participant_ids) = storage.game_session_diagnostics(7, session_id).unwrap().unwrap();
        assert_eq!(creator_id, 2);
        assert_eq!(participant_ids, vec![2, 3]);
        assert!(storage.migrate_game_session_host(3, 7, session_id).unwrap());

        storage.leave_game_session(3, 7, session_id).unwrap();
        assert!(!storage.game_session_is_active(7, session_id).unwrap());
        assert!(storage.game_session_diagnostics(7, session_id).unwrap().is_none());
    }

    #[test]
    fn only_current_session_owner_can_remove_participants() {
        let storage = create_test_storage("remove-participants-owner");
        insert_users(&storage, 4);
        let session_id = storage.create_game_session(1, 7, String::new()).unwrap();
        storage.add_participants(1, 7, session_id, Vec::new(), vec![2, 3]).unwrap();

        assert!(!storage.remove_participants(2, 7, session_id, vec![3]).unwrap());
        assert!(!storage.remove_participants(4, 7, session_id, vec![3]).unwrap());
        let (creator_id, _, participant_ids) = storage.game_session_diagnostics(7, session_id).unwrap().unwrap();
        assert_eq!(creator_id, 1);
        assert_eq!(participant_ids, vec![1, 2, 3]);

        assert!(storage.remove_participants(1, 7, session_id, vec![3, 3]).unwrap());
        let (creator_id, _, participant_ids) = storage.game_session_diagnostics(7, session_id).unwrap().unwrap();
        assert_eq!(creator_id, 1);
        assert_eq!(participant_ids, vec![1, 2]);

        assert!(!storage.remove_participants(1, 7, session_id + 1, vec![2]).unwrap());
    }

    #[test]
    fn native_owner_migration_revokes_previous_owner_removal_rights() {
        let storage = create_test_storage("remove-participants-after-owner-migration");
        insert_users(&storage, 3);
        let session_id = storage.create_game_session(1, 7, String::new()).unwrap();
        storage.add_participants(1, 7, session_id, Vec::new(), vec![2, 3]).unwrap();

        storage.leave_game_session(1, 7, session_id).unwrap();
        let (creator_id, _, participant_ids) = storage.game_session_diagnostics(7, session_id).unwrap().unwrap();
        assert_eq!(creator_id, 1);
        assert_eq!(participant_ids, vec![1, 2, 3]);
        assert!(storage.migrate_game_session_host(2, 7, session_id).unwrap());

        assert!(!storage.remove_participants(1, 7, session_id, vec![3]).unwrap());
        let (creator_id, _, participant_ids) = storage.game_session_diagnostics(7, session_id).unwrap().unwrap();
        assert_eq!(creator_id, 2);
        assert_eq!(participant_ids, vec![2, 3]);

        assert!(storage.remove_participants(2, 7, session_id, vec![3]).unwrap());
        let (creator_id, _, participant_ids) = storage.game_session_diagnostics(7, session_id).unwrap().unwrap();
        assert_eq!(creator_id, 2);
        assert_eq!(participant_ids, vec![2]);
    }

    #[test]
    fn split_restores_callers_own_just_abandoned_session() {
        let storage = create_test_storage("split-restore-abandoned");
        insert_users(&storage, 2);
        let session_id = storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();

        storage.leave_game_session(1, 1, session_id).unwrap();
        assert!(!storage.game_session_is_active(1, session_id).unwrap());
        assert!(!storage.restore_abandoned_game_session_for_split(2, 1, session_id).unwrap());
        assert!(storage.restore_abandoned_game_session_for_split(1, 1, session_id).unwrap());

        let (creator_id, _, participant_ids) = storage.game_session_diagnostics(1, session_id).unwrap().unwrap();
        assert_eq!(creator_id, 1);
        assert_eq!(participant_ids, vec![1]);
    }

    #[test]
    fn deleting_hosted_game_preserves_remaining_players_for_native_migration() {
        let storage = create_test_storage("host-delete-native-migration");
        insert_users(&storage, 3);
        storage.register_urls(1, vec!["prudp:/address=10.66.0.1;port=13000;sid=14;type=2".to_string()]).unwrap();
        storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        let game_id = storage.create_game_session(1, 1, "113 => 0;3 => 0;4 => 8;112 => 4".to_string()).unwrap();
        assert!(storage.join_game_session_unbounded(2, 1, game_id).unwrap());
        assert!(storage.join_game_session_unbounded(3, 1, game_id).unwrap());

        assert_eq!(
            storage.delete_game_session(1, 1, game_id).unwrap(),
            DeleteGameSessionOutcome::MigrationPending { remaining_participants: 2 }
        );
        let (creator_id, _, participant_ids) = storage.game_session_diagnostics(1, game_id).unwrap().unwrap();
        assert_eq!(creator_id, 1);
        assert_eq!(participant_ids, vec![1, 2, 3]);
        assert!(run(storage.take_invite_async(2)).unwrap().unwrap().is_none());
        assert!(run(storage.take_invite_async(3)).unwrap().unwrap().is_none());

        // SplitSession is the first and only ownership decision.
        assert!(storage.migrate_game_session_host(3, 1, game_id).unwrap());
        let (creator_id, _, participant_ids) = storage.game_session_diagnostics(1, game_id).unwrap().unwrap();
        assert_eq!(creator_id, 3);
        assert_eq!(participant_ids, vec![2, 3]);
    }

    #[test]
    fn successful_four_player_host_migration_keeps_match_and_suppresses_lobby_regroup() {
        let storage = create_test_storage("four-player-host-migration-solo-fallbacks");
        insert_users(&storage, 4);
        for user_id in 1..=4 {
            storage
                .register_urls(user_id, vec![format!("prudp:/address=10.66.0.{user_id};port=13000;sid={user_id};type=2")])
                .unwrap();
        }

        let mut solo_lobbies = Vec::new();
        for receiver in 2..=4 {
            solo_lobbies.push((receiver, storage.create_game_session(receiver, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap()));
        }
        let game_id = storage.create_game_session(1, 1, "113 => 0;3 => 0;4 => 8;112 => 4".to_string()).unwrap();

        for (receiver, lobby_id) in &solo_lobbies {
            let invite_id = run(storage.add_invite_async(1, *receiver, INVITE_KIND_PRIVATE_ROOM, 1, game_id)).unwrap().unwrap().unwrap();
            assert_eq!(run(storage.take_invite_async(*receiver)).unwrap().unwrap().unwrap().id, invite_id);
            assert!(run(storage.accept_invite_async(*receiver, invite_id)).unwrap().unwrap().is_some());

            // Retail sends both AbandonSession and LeaveSession for the old
            // solo lobby while it is crossing into the private match.
            storage.leave_game_session(*receiver, 1, *lobby_id).unwrap();
            storage.leave_game_session(*receiver, 1, *lobby_id).unwrap();
            assert!(storage.game_session_is_active(1, *lobby_id).unwrap());
        }

        storage.add_participants(1, 1, game_id, vec![2, 3, 4], Vec::new()).unwrap();
        assert_eq!(storage.game_session_diagnostics(1, game_id).unwrap().unwrap().2, vec![1, 2, 3, 4]);

        for (receiver, lobby_id) in &solo_lobbies {
            // The accepted invite is consumed after admission. A late retail
            // lobby Leave is still harmless because room membership now owns
            // the fallback lifetime.
            storage.leave_game_session(*receiver, 1, *lobby_id).unwrap();
            assert!(storage.game_session_is_active(1, *lobby_id).unwrap());
        }

        assert_eq!(
            storage.delete_game_session(1, 1, game_id).unwrap(),
            DeleteGameSessionOutcome::MigrationPending { remaining_participants: 3 }
        );
        assert_eq!(storage.game_session_diagnostics(1, game_id).unwrap().unwrap().2, vec![1, 2, 3, 4]);
        for (_, lobby_id) in &solo_lobbies {
            assert!(storage.game_session_is_active(1, *lobby_id).unwrap());
        }

        // A member may leave after the migration without affecting the two
        // players who remain in the migrated match.
        storage.leave_game_session(4, 1, game_id).unwrap();
        assert_eq!(storage.game_session_diagnostics(1, game_id).unwrap().unwrap().2, vec![1, 2, 3]);
        assert!(storage.migrate_game_session_host(3, 1, game_id).unwrap());
        let (creator_id, _, participant_ids) = storage.game_session_diagnostics(1, game_id).unwrap().unwrap();
        assert_eq!(creator_id, 3);
        assert_eq!(participant_ids, vec![2, 3]);
        // SplitSession itself is the migration contract. The server must not
        // inject a second invitation state machine while retail is migrating.
        assert!(run(storage.take_invite_async(2)).unwrap().unwrap().is_none());
        assert!(run(storage.take_invite_async(3)).unwrap().unwrap().is_none());

        // StateLeaveWithoutParty may create a fresh solo lobby. That creation
        // retires only this player's preserved predecessor, preventing two
        // personal lobbies without touching the migrated match or teammates.
        let old_user_four_lobby = solo_lobbies[2].1;
        let replacement = storage.create_game_session(4, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        assert!(!storage.game_session_is_active(1, old_user_four_lobby).unwrap());
        assert!(storage.game_session_is_active(1, replacement).unwrap());
        assert_eq!(storage.game_session_diagnostics(1, game_id).unwrap().unwrap().2, vec![2, 3]);
    }

    #[test]
    fn two_player_host_migration_completes_after_the_only_survivor_becomes_host() {
        let storage = create_test_storage("two-player-host-migration-confirmation");
        insert_users(&storage, 2);
        for user_id in 1..=2 {
            storage
                .register_urls(user_id, vec![format!("prudp:/address=10.66.0.{user_id};port=13000;sid={user_id};type=2")])
                .unwrap();
        }
        let game_id = storage.create_game_session(1, 1, "113 => 0;3 => 0;4 => 8;112 => 4".to_string()).unwrap();
        assert!(storage.join_game_session_unbounded(2, 1, game_id).unwrap());
        assert!(matches!(
            storage.delete_game_session(1, 1, game_id).unwrap(),
            DeleteGameSessionOutcome::MigrationPending { remaining_participants: 1 }
        ));
        assert!(storage.migrate_game_session_host(2, 1, game_id).unwrap());
        assert_eq!(storage.game_session_diagnostics(1, game_id).unwrap().unwrap().2, vec![2]);
        assert!(run(storage.take_invite_async(2)).unwrap().unwrap().is_none());
    }

    #[test]
    fn deleting_hosted_game_does_not_restore_player_who_already_left() {
        let storage = create_test_storage("host-delete-no-restore-after-leave");
        insert_users(&storage, 2);
        storage.register_urls(1, vec!["prudp:/address=10.66.0.1;port=13000;sid=14;type=2".to_string()]).unwrap();
        storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        let game_id = storage.create_game_session(1, 1, "113 => 0;3 => 0;4 => 8;112 => 4".to_string()).unwrap();
        assert!(storage.join_game_session_unbounded(2, 1, game_id).unwrap());
        storage.leave_game_session(2, 1, game_id).unwrap();

        assert_eq!(storage.delete_game_session(1, 1, game_id).unwrap(), DeleteGameSessionOutcome::Destroyed);
        assert!(!storage.game_session_is_active(1, game_id).unwrap());
        assert!(run(storage.take_invite_async(2)).unwrap().unwrap().is_none());
    }

    #[test]
    fn deleting_session_rejects_non_owner_and_missing_session() {
        let storage = create_test_storage("host-delete-ownership");
        insert_users(&storage, 2);
        let game_id = storage.create_game_session(1, 1, "113 => 0;3 => 0;4 => 8;112 => 4".to_string()).unwrap();
        assert!(storage.join_game_session_unbounded(2, 1, game_id).unwrap());

        assert_eq!(storage.delete_game_session(2, 1, game_id).unwrap(), DeleteGameSessionOutcome::NotFound);
        assert_eq!(storage.delete_game_session(1, 1, game_id + 1).unwrap(), DeleteGameSessionOutcome::NotFound);
        assert_eq!(storage.game_session_diagnostics(1, game_id).unwrap().unwrap().2, vec![1, 2]);
    }

    #[test]
    fn registering_urls_replaces_stale_endpoints() {
        let storage = create_test_storage("replace-urls");
        insert_users(&storage, 1);
        storage.register_urls(1, vec!["prudp:/address=10.66.0.5;port=13000;sid=14;type=2".to_string()]).unwrap();
        storage.register_urls(1, vec!["prudp:/address=10.66.0.6;port=13000;sid=14;type=2".to_string()]).unwrap();
        let urls = run(storage.list_urls(1)).unwrap().unwrap();
        assert_eq!(urls, vec!["prudp:/address=10.66.0.6;port=13000;sid=14;type=2"]);
    }

    #[test]
    fn private_participant_add_persists_private_membership() {
        let storage = create_test_storage("private-participant-membership");
        insert_users(&storage, 3);
        let session_id = storage.create_game_session(1, 1, "113 => 0;3 => 0;4 => 8;112 => 4".to_string()).unwrap();

        storage.add_participants(1, 1, session_id, vec![2], Vec::new()).unwrap();
        let (_, _, participant_ids) = storage.game_session_diagnostics(1, session_id).unwrap().unwrap();
        assert_eq!(participant_ids, vec![1, 2]);

        storage.add_participants(1, 1, session_id, Vec::new(), vec![3]).unwrap();
        let (_, _, participant_ids) = storage.game_session_diagnostics(1, session_id).unwrap().unwrap();
        assert_eq!(participant_ids, vec![1, 2, 3]);
    }

    #[test]
    fn private_room_invite_survives_delivery_acceptance_and_repeated_search_until_join() {
        let storage = create_test_storage("private-room-invite");
        insert_users(&storage, 3);
        storage.register_urls(1, vec!["prudp:/address=10.66.0.2;port=13000;sid=14;type=2".to_string()]).unwrap();

        let lobby_id = storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        let private_id = storage.create_game_session(1, 1, "113 => 0;103 => 0;3 => 0;4 => 8;112 => 4".to_string()).unwrap();
        let host_sessions = run(storage.find_invitable_sessions_async(1)).unwrap().unwrap();
        assert_eq!(host_sessions.iter().map(|session| session.session_id).collect::<Vec<_>>(), vec![private_id, lobby_id]);

        let invite_id = run(storage.add_invite_async(1, 2, 1, 1, private_id)).unwrap().unwrap().unwrap();
        let delivered = run(storage.take_invite_async(2)).unwrap().unwrap().unwrap();
        assert_eq!(delivered.id, invite_id);
        assert_eq!(delivered.kind, 1);
        assert_eq!(delivered.delivery_count, 1);
        assert!(run(storage.find_accepted_invited_session_async(2, 1)).unwrap().unwrap().is_none());
        let unrelated_invite = run(storage.add_invite_async(1, 2, INVITE_KIND_LOBBY_PARTY, 1, lobby_id)).unwrap().unwrap().unwrap();

        let accepted = run(storage.accept_invite_async(2, invite_id)).unwrap().unwrap().unwrap();
        assert_eq!(accepted.receiver, 2);
        let (_, _, authorized_participants) = storage.game_session_diagnostics(1, private_id).unwrap().unwrap();
        assert_eq!(authorized_participants, vec![1]);
        let first = run(storage.find_accepted_invited_session_async(2, 1)).unwrap().unwrap().unwrap();
        let second = run(storage.find_accepted_invited_session_async(2, 1)).unwrap().unwrap().unwrap();
        assert_eq!(first.invite_id, invite_id);
        assert_eq!(first.kind, 1);
        assert_eq!(first.session.session_id, private_id);
        assert_eq!(first.session.participants.iter().map(|participant| participant.user_id).collect::<Vec<_>>(), vec![1]);
        assert_eq!(second.session.session_id, private_id);
        assert_eq!(session_attribute_value(&first.session.attributes, 113), Some(1));
        assert_eq!(session_attribute_value(&second.session.attributes, 113), Some(1));
        assert_eq!(session_attribute_value(&first.session.attributes, 3), Some(0));
        assert_eq!(session_attribute_value(&second.session.attributes, 3), Some(0));
        assert_eq!(session_attribute_value(&first.session.attributes, 4), Some(8));
        assert_eq!(session_attribute_value(&second.session.attributes, 4), Some(8));

        let unrelated_consumed: (i64,) = run(sqlx::query_as("SELECT COUNT(*) FROM invites WHERE id = ? AND consumed_at IS NOT NULL")
            .bind(unrelated_invite)
            .fetch_one(&storage.pool))
        .unwrap()
        .unwrap();
        assert_eq!(unrelated_consumed.0, 1);
        let recovery_invite = run(storage.add_invite_async(1, 2, INVITE_KIND_LOBBY_PARTY, 1, lobby_id)).unwrap().unwrap().unwrap();
        storage.add_participants(1, 1, private_id, vec![2], Vec::new()).unwrap();
        let (_, _, deferred_participants) = storage.game_session_diagnostics(1, private_id).unwrap().unwrap();
        assert_eq!(deferred_participants, vec![1, 2]);
        assert!(run(storage.find_accepted_invited_session_async(2, 1)).unwrap().unwrap().is_none());
        let remaining: (i64,) = run(sqlx::query_as("SELECT COUNT(*) FROM invites WHERE receiver = 2 AND consumed_at IS NULL").fetch_one(&storage.pool))
            .unwrap()
            .unwrap();
        assert_eq!(remaining.0, 0, "successful admission must clear every pending or accepted invite");
        let recovery_consumed: (i64,) = run(sqlx::query_as("SELECT COUNT(*) FROM invites WHERE id = ? AND consumed_at IS NOT NULL")
            .bind(recovery_invite)
            .fetch_one(&storage.pool))
        .unwrap()
        .unwrap();
        assert_eq!(recovery_consumed.0, 1);

        let (_, stored_attributes, stored_participants) = storage.game_session_diagnostics(1, private_id).unwrap().unwrap();
        assert_eq!(session_attribute_value(&stored_attributes, 113), Some(0));
        assert_eq!(session_attribute_value(&stored_attributes, 3), Some(0));
        assert_eq!(session_attribute_value(&stored_attributes, 4), Some(8));
        assert_eq!(stored_participants, vec![1, 2]);
        assert!(run(storage.find_accepted_invited_session_async(3, 1)).unwrap().unwrap().is_none());

        assert!(storage.join_game_session_unbounded(2, 1, private_id).unwrap());
        let (_, _, participant_ids) = storage.game_session_diagnostics(1, private_id).unwrap().unwrap();
        assert_eq!(participant_ids, vec![1, 2]);
    }

    #[test]
    fn session_member_can_invite_a_friend_into_the_real_hosts_session() {
        let storage = create_test_storage("member-invites-friend");
        insert_users(&storage, 3);
        storage.register_urls(1, vec!["prudp:/address=10.66.0.2;port=13000;sid=14;type=2".to_string()]).unwrap();
        let lobby_id = storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        assert!(storage.join_game_session_unbounded(2, 1, lobby_id).unwrap());

        let member_sessions = run(storage.find_invitable_sessions_async(2)).unwrap().unwrap();
        assert_eq!(member_sessions.iter().map(|session| session.session_id).collect::<Vec<_>>(), vec![lobby_id]);
        assert_eq!(member_sessions[0].creator_id, 1);
        assert!(run(storage.find_invitable_sessions_async(3)).unwrap().unwrap().is_empty());

        let invite_id = run(storage.add_invite_async(2, 3, INVITE_KIND_LOBBY_PARTY, 1, lobby_id))
            .unwrap()
            .unwrap()
            .expect("a current session member may invite another friend");
        let delivered = run(storage.take_invite_async(3)).unwrap().unwrap().expect("member invitation should remain valid");
        assert_eq!(delivered.id, invite_id);
        assert_eq!(delivered.sender, 2);
        assert_eq!(delivered.session_id, lobby_id);
        assert!(run(storage.accept_invite_async(3, invite_id)).unwrap().unwrap().is_some());
        let target = run(storage.find_accepted_invited_session_async(3, 1)).unwrap().unwrap().unwrap();
        assert_eq!(target.session.session_id, lobby_id);
        assert_eq!(target.session.creator_id, 1);

        assert!(run(storage.add_invite_async(3, 2, INVITE_KIND_LOBBY_PARTY, 1, lobby_id)).unwrap().unwrap().is_none());
    }

    #[test]
    fn migrated_shared_lobby_outranks_a_newer_preserved_solo_fallback_for_invites() {
        let storage = create_test_storage("migrated-party-invite-versus-solo-fallback");
        insert_users(&storage, 3);
        for user_id in 1..=3 {
            storage
                .register_urls(user_id, vec![format!("prudp:/address=10.66.0.{user_id};port=13000;sid={user_id};type=2")])
                .unwrap();
        }

        let shared_lobby = storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        assert!(storage.join_game_session_unbounded(2, 1, shared_lobby).unwrap());

        // Retail keeps this newer personal lobby alive while the player is
        // crossing through a private room, so it remains a valid fallback.
        let preserved_solo = storage.create_game_session(2, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        let private_room = storage.create_game_session(1, 1, "113 => 0;103 => 0;112 => 4".to_string()).unwrap();
        assert!(storage.join_game_session_unbounded(2, 1, private_room).unwrap());
        storage.leave_game_session(2, 1, preserved_solo).unwrap();
        assert!(storage.game_session_is_active(1, preserved_solo).unwrap());

        storage.leave_game_session(2, 1, private_room).unwrap();
        assert_eq!(storage.delete_game_session(1, 1, private_room).unwrap(), DeleteGameSessionOutcome::Destroyed);

        let candidates = run(storage.find_invitable_sessions_async(2)).unwrap().unwrap();
        assert_eq!(candidates.iter().map(|session| session.session_id).collect::<Vec<_>>(), vec![shared_lobby, preserved_solo]);

        let invite_id = run(storage.add_invite_async(2, 3, INVITE_KIND_LOBBY_PARTY, 1, candidates[0].session_id))
            .unwrap()
            .unwrap()
            .expect("the migrated shared lobby remains joinable");
        let delivered = run(storage.take_invite_async(3)).unwrap().unwrap().unwrap();
        assert_eq!(delivered.id, invite_id);
        assert_eq!(delivered.session_id, shared_lobby);
    }

    #[test]
    fn owner_admits_non_host_invitee_and_hands_off_every_room_member() {
        let storage = create_test_storage("member-invite-owner-admission-handoff");
        insert_users(&storage, 3);
        storage.register_urls(1, vec!["prudp:/address=10.66.0.1;port=13000;sid=14;type=2".to_string()]).unwrap();

        let lobby_id = storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        let member_invite = run(storage.add_invite_async(1, 2, INVITE_KIND_LOBBY_PARTY, 1, lobby_id)).unwrap().unwrap().unwrap();
        assert!(run(storage.accept_invite_async(2, member_invite)).unwrap().unwrap().is_some());
        assert!(storage.join_game_session_unbounded(2, 1, lobby_id).unwrap());

        let room_id = storage.create_game_session(1, 1, "113 => 0;103 => 0;112 => 4".to_string()).unwrap();
        assert!(storage.join_game_session_unbounded(2, 1, room_id).unwrap());
        let friend_invite = run(storage.add_invite_async(2, 3, INVITE_KIND_PRIVATE_ROOM, 1, room_id)).unwrap().unwrap().unwrap();
        assert!(run(storage.take_invite_async(3)).unwrap().unwrap().is_some());
        assert!(run(storage.accept_invite_async(3, friend_invite)).unwrap().unwrap().is_some());

        // A non-owner may send the invitation but cannot self-authorize the
        // resulting membership record.
        storage.add_participants(2, 1, room_id, vec![3], Vec::new()).unwrap();
        assert_eq!(storage.game_session_diagnostics(1, room_id).unwrap().unwrap().2, vec![1, 2]);

        // The real game sends AddParticipants from the room owner after P2P
        // succeeds; the invited friend does not necessarily send JoinSession.
        storage.add_participants(1, 1, room_id, vec![3], Vec::new()).unwrap();
        assert_eq!(storage.game_session_diagnostics(1, room_id).unwrap().unwrap().2, vec![1, 2, 3]);
        let (party_leader, party_roster) = run(storage.find_party_roster_async(3)).unwrap().unwrap().unwrap();
        assert_eq!(party_leader, 1);
        assert_eq!(party_roster.into_iter().map(|member| member.id).collect::<Vec<_>>(), vec![1, 2, 3]);

        // Model the private-room transition: followers are no longer members
        // of the old lobby. Owner departure must preserve their shared room
        // for the game's native migration instead of pulling them backwards.
        storage.leave_game_session(2, 1, lobby_id).unwrap();
        assert_eq!(
            storage.delete_game_session(1, 1, room_id).unwrap(),
            DeleteGameSessionOutcome::MigrationPending { remaining_participants: 2 }
        );
        assert_eq!(storage.game_session_diagnostics(1, room_id).unwrap().unwrap().2, vec![1, 2, 3]);
        assert!(storage.migrate_game_session_host(2, 1, room_id).unwrap());
        assert_eq!(storage.game_session_diagnostics(1, room_id).unwrap().unwrap().2, vec![2, 3]);
        assert!(run(storage.take_invite_async(2)).unwrap().unwrap().is_none());
        assert!(run(storage.take_invite_async(3)).unwrap().unwrap().is_none());
    }

    #[test]
    fn party_member_can_invite_during_lobby_to_private_membership_lag() {
        let storage = create_test_storage("party-member-transition-invite");
        insert_users(&storage, 4);
        storage.register_urls(1, vec!["prudp:/address=10.66.0.1;port=13000;sid=14;type=2".to_string()]).unwrap();

        let lobby_id = storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        let party_invite = run(storage.add_invite_async(1, 2, INVITE_KIND_LOBBY_PARTY, 1, lobby_id)).unwrap().unwrap().unwrap();
        assert!(run(storage.accept_invite_async(2, party_invite)).unwrap().unwrap().is_some());
        assert!(storage.join_game_session_unbounded(2, 1, lobby_id).unwrap());

        // The host has already published the private room, but the follower's
        // Quazal AddParticipants has not reached the server yet. This is the
        // exact transition window observed in the three-client diagnostics.
        let room_id = storage.create_game_session(1, 1, "113 => 0;103 => 0;112 => 4".to_string()).unwrap();
        let member_sessions = run(storage.find_invitable_sessions_async(2)).unwrap().unwrap();
        assert_eq!(member_sessions[0].session_id, room_id);
        assert_eq!(member_sessions[0].creator_id, 1);
        assert_eq!(storage.game_session_diagnostics(1, room_id).unwrap().unwrap().2, vec![1]);
        assert!(run(storage.find_invitable_sessions_async(4)).unwrap().unwrap().is_empty());

        let invite_id = run(storage.add_invite_async(2, 3, INVITE_KIND_PRIVATE_ROOM, 1, room_id))
            .unwrap()
            .unwrap()
            .expect("a persistent party member may invite during participant-capture lag");
        let delivered = run(storage.take_invite_async(3)).unwrap().unwrap().expect("transition invite must survive cleanup");
        assert_eq!(delivered.id, invite_id);
        assert_eq!(delivered.sender, 2);
        assert_eq!(run(storage.find_active_session_creator_async(1, room_id)).unwrap().unwrap(), Some(1));
        assert!(run(storage.accept_invite_async(3, invite_id)).unwrap().unwrap().is_some());
        let target = run(storage.find_accepted_invited_session_async(3, 1)).unwrap().unwrap().unwrap();
        assert_eq!(target.session.session_id, room_id);
        assert_eq!(target.session.creator_id, 1);

        assert!(run(storage.add_invite_async(4, 3, INVITE_KIND_PRIVATE_ROOM, 1, room_id)).unwrap().unwrap().is_none());
    }

    #[test]
    fn room_member_private_invites_collapse_to_one_target_generation() {
        let storage = create_test_storage("member-private-invite-race");
        insert_users(&storage, 4);
        storage.register_urls(1, vec!["prudp:/address=10.66.0.1;port=13000;sid=14;type=2".to_string()]).unwrap();
        let room_id = storage.create_game_session(1, 1, "113 => 0;103 => 0;112 => 4".to_string()).unwrap();
        assert!(storage.join_game_session_unbounded(2, 1, room_id).unwrap());
        assert!(storage.join_game_session_unbounded(3, 1, room_id).unwrap());

        let member_invite = run(storage.add_invite_async(2, 4, INVITE_KIND_PRIVATE_ROOM, 1, room_id)).unwrap().unwrap().unwrap();
        let repeated_member_invite = run(storage.add_invite_async(2, 4, INVITE_KIND_PRIVATE_ROOM, 1, room_id)).unwrap().unwrap().unwrap();
        assert_eq!(repeated_member_invite, member_invite);

        let second_member_invite = run(storage.add_invite_async(3, 4, INVITE_KIND_PRIVATE_ROOM, 1, room_id)).unwrap().unwrap().unwrap();
        assert_eq!(second_member_invite, member_invite);
        let pending: Vec<(i64, u32)> =
            run(sqlx::query_as("SELECT id, sender FROM invites WHERE receiver = 4 AND accepted_at IS NULL AND consumed_at IS NULL ORDER BY sender").fetch_all(&storage.pool))
                .unwrap()
                .unwrap();
        assert_eq!(pending, vec![(member_invite, 2)]);

        assert!(run(storage.accept_invite_async(4, member_invite)).unwrap().unwrap().is_some());
        let repeated_after_accept = run(storage.add_invite_async(3, 4, INVITE_KIND_PRIVATE_ROOM, 1, room_id)).unwrap().unwrap().unwrap();
        assert_eq!(
            repeated_after_accept, member_invite,
            "an accepted target must not create another native join event before admission"
        );
        let target = run(storage.find_accepted_invited_session_async(4, 1)).unwrap().unwrap().unwrap();
        assert_eq!(target.session.session_id, room_id);
        assert_eq!(target.session.creator_id, 1);
    }

    #[test]
    fn lobby_host_and_member_invites_collapse_to_one_target_generation() {
        let storage = create_test_storage("lobby-host-member-invite-race");
        insert_users(&storage, 3);
        storage.register_urls(1, vec!["prudp:/address=10.66.0.1;port=13000;sid=14;type=2".to_string()]).unwrap();
        let lobby_id = storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        assert!(storage.join_game_session_unbounded(2, 1, lobby_id).unwrap());

        let host_invite = run(storage.add_invite_async(1, 3, INVITE_KIND_LOBBY_PARTY, 1, lobby_id)).unwrap().unwrap().unwrap();
        let member_invite = run(storage.add_invite_async(2, 3, INVITE_KIND_LOBBY_PARTY, 1, lobby_id)).unwrap().unwrap().unwrap();
        assert_eq!(member_invite, host_invite);
        let delivered = run(storage.take_invite_async(3)).unwrap().unwrap().unwrap();
        assert_eq!(delivered.id, host_invite);
        assert_eq!(delivered.sender, 1);

        assert!(run(storage.accept_invite_async(3, host_invite)).unwrap().unwrap().is_some());
        let repeated_after_accept = run(storage.add_invite_async(2, 3, INVITE_KIND_LOBBY_PARTY, 1, lobby_id)).unwrap().unwrap().unwrap();
        assert_eq!(repeated_after_accept, host_invite);
        let target = run(storage.find_accepted_invited_session_async(3, 1)).unwrap().unwrap().unwrap();
        assert_eq!(target.session.session_id, lobby_id);
        assert_eq!(target.session.creator_id, 1);
    }

    #[test]
    fn accepting_one_legacy_equivalent_invite_retires_its_sibling() {
        let storage = create_test_storage("legacy-equivalent-invite-race");
        insert_users(&storage, 3);
        storage.register_urls(1, vec!["prudp:/address=10.66.0.1;port=13000;sid=14;type=2".to_string()]).unwrap();
        let lobby_id = storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        assert!(storage.join_game_session_unbounded(2, 1, lobby_id).unwrap());

        let host_invite = run(storage.add_invite_async(1, 3, INVITE_KIND_LOBBY_PARTY, 1, lobby_id)).unwrap().unwrap().unwrap();
        let legacy_sibling = run(sqlx::query(
            "INSERT INTO invites (sender, receiver, kind, session_type, session_id, expires_at) VALUES (?, ?, ?, ?, ?, datetime('now', '+30 seconds'))",
        )
        .bind(2_u32)
        .bind(3_u32)
        .bind(INVITE_KIND_LOBBY_PARTY)
        .bind(1_u32)
        .bind(lobby_id)
        .execute(&storage.pool))
        .unwrap()
        .unwrap()
        .last_insert_rowid();

        assert!(run(storage.accept_invite_async(3, host_invite)).unwrap().unwrap().is_some());
        let (consumed_at,): (Option<String>,) = run(sqlx::query_as("SELECT consumed_at FROM invites WHERE id = ?").bind(legacy_sibling).fetch_one(&storage.pool))
            .unwrap()
            .unwrap();
        assert!(consumed_at.is_some());
        assert!(run(storage.accept_invite_async(3, legacy_sibling)).unwrap().unwrap().is_none());
    }

    #[test]
    fn accepting_one_party_retires_the_current_competing_invite_batch() {
        let storage = create_test_storage("competing-party-invite-race");
        insert_users(&storage, 3);
        storage.register_urls(1, vec!["prudp:/address=10.66.0.1;port=13000;sid=14;type=2".to_string()]).unwrap();
        storage.register_urls(2, vec!["prudp:/address=10.66.0.2;port=13000;sid=14;type=2".to_string()]).unwrap();
        let first_lobby = storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        let second_lobby = storage.create_game_session(2, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();

        let first_invite = run(storage.add_invite_async(1, 3, INVITE_KIND_LOBBY_PARTY, 1, first_lobby)).unwrap().unwrap().unwrap();
        let second_invite = run(storage.add_invite_async(2, 3, INVITE_KIND_LOBBY_PARTY, 1, second_lobby)).unwrap().unwrap().unwrap();
        assert_ne!(first_invite, second_invite);

        assert!(run(storage.accept_invite_async(3, first_invite)).unwrap().unwrap().is_some());
        assert!(run(storage.accept_invite_async(3, second_invite)).unwrap().unwrap().is_none());
        let recovery_invite = run(storage.add_invite_async(2, 3, INVITE_KIND_LOBBY_PARTY, 1, second_lobby)).unwrap().unwrap().unwrap();
        assert_ne!(recovery_invite, second_invite, "a fresh invitation after a failed join must be immediately available");

        assert!(storage.join_game_session_unbounded(3, 1, first_lobby).unwrap());
        assert!(run(storage.add_invite_async(2, 3, INVITE_KIND_LOBBY_PARTY, 1, second_lobby)).unwrap().unwrap().is_some());
    }

    #[test]
    fn repeated_invite_keeps_one_generation_and_the_original_thirty_second_expiry() {
        let storage = create_test_storage("invite-fixed-thirty-second-window");
        insert_users(&storage, 2);
        storage.register_urls(1, vec!["prudp:/address=10.66.0.1;port=13000;sid=14;type=2".to_string()]).unwrap();
        let room_id = storage.create_game_session(1, 1, "113 => 0;103 => 0;112 => 4".to_string()).unwrap();

        let invite_id = run(storage.add_invite_async(1, 2, INVITE_KIND_PRIVATE_ROOM, 1, room_id)).unwrap().unwrap().unwrap();
        let lifetime_seconds: (i64,) = run(sqlx::query_as("SELECT strftime('%s', expires_at) - strftime('%s', created) FROM invites WHERE id = ?")
            .bind(invite_id)
            .fetch_one(&storage.pool))
        .unwrap()
        .unwrap();
        assert_eq!(lifetime_seconds.0, 30);

        run(sqlx::query("UPDATE invites SET expires_at = datetime('now', '+20 seconds') WHERE id = ?")
            .bind(invite_id)
            .execute(&storage.pool))
        .unwrap()
        .unwrap();
        let before: (String,) = run(sqlx::query_as("SELECT expires_at FROM invites WHERE id = ?").bind(invite_id).fetch_one(&storage.pool))
            .unwrap()
            .unwrap();
        let repeated = run(storage.add_invite_async(1, 2, INVITE_KIND_PRIVATE_ROOM, 1, room_id)).unwrap().unwrap().unwrap();
        let after: (String,) = run(sqlx::query_as("SELECT expires_at FROM invites WHERE id = ?").bind(invite_id).fetch_one(&storage.pool))
            .unwrap()
            .unwrap();
        assert_eq!(repeated, invite_id);
        assert_eq!(after, before, "a repeated click must not extend the original expiry");
    }

    #[test]
    fn lobby_invites_allow_many_receivers_to_join_the_same_party_session() {
        let storage = create_test_storage("lobby-party-invite");
        insert_users(&storage, 4);
        storage.register_urls(1, vec!["prudp:/address=10.66.0.2;port=13000;sid=14;type=2".to_string()]).unwrap();
        let lobby_id = storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();

        for receiver in 2..=4 {
            let invite_id = run(storage.add_invite_async(1, receiver, 2, 1, lobby_id)).unwrap().unwrap().unwrap();
            assert_eq!(run(storage.take_invite_async(receiver)).unwrap().unwrap().unwrap().kind, 2);
            assert!(run(storage.accept_invite_async(receiver, invite_id)).unwrap().unwrap().is_some());
            let (_, _, participants_after_accept) = storage.game_session_diagnostics(1, lobby_id).unwrap().unwrap();
            assert!(!participants_after_accept.contains(&receiver));
            let target = run(storage.find_accepted_invited_session_async(receiver, 1)).unwrap().unwrap().unwrap();
            assert_eq!(target.kind, 2);
            assert_eq!(target.session.session_id, lobby_id);
            assert!(!target.session.participants.iter().any(|participant| participant.user_id == receiver));
            assert!(storage.join_game_session_unbounded(receiver, 1, lobby_id).unwrap());
        }

        let (_, _, participant_ids) = storage.game_session_diagnostics(1, lobby_id).unwrap().unwrap();
        assert_eq!(participant_ids, vec![1, 2, 3, 4]);

        let (leader_id, roster) = run(storage.find_party_roster_async(3)).unwrap().unwrap().unwrap();
        assert_eq!(leader_id, 1);
        assert_eq!(roster.into_iter().map(|member| member.id).collect::<Vec<_>>(), vec![1, 2, 3, 4]);

        storage.leave_game_session(1, 1, lobby_id).unwrap();
        let (leader_id, roster) = run(storage.find_party_roster_async(3)).unwrap().unwrap().unwrap();
        assert_eq!(leader_id, 1);
        assert_eq!(roster.into_iter().map(|member| member.id).collect::<Vec<_>>(), vec![1, 2, 3, 4]);

        assert!(storage.migrate_game_session_host(3, 1, lobby_id).unwrap());
        let (leader_id, roster) = run(storage.find_party_roster_async(4)).unwrap().unwrap().unwrap();
        assert_eq!(leader_id, 3);
        assert_eq!(roster.into_iter().map(|member| member.id).collect::<Vec<_>>(), vec![3, 2, 4]);
    }

    #[test]
    fn lobby_host_migration_detaches_old_leader_even_while_private_room_is_active() {
        let storage = create_test_storage("party-migration-with-active-room");
        insert_users(&storage, 3);
        storage.register_urls(1, vec!["prudp:/address=10.66.0.1;port=13000;sid=14;type=2".to_string()]).unwrap();

        let lobby_id = storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        for receiver in 2..=3 {
            let invite_id = run(storage.add_invite_async(1, receiver, INVITE_KIND_LOBBY_PARTY, 1, lobby_id)).unwrap().unwrap().unwrap();
            assert!(run(storage.accept_invite_async(receiver, invite_id)).unwrap().unwrap().is_some());
            assert!(storage.join_game_session_unbounded(receiver, 1, lobby_id).unwrap());
        }

        let room_id = storage.create_game_session(1, 1, "113 => 0;103 => 0;112 => 4".to_string()).unwrap();
        assert!(storage.join_game_session_unbounded(2, 1, room_id).unwrap());
        assert!(storage.join_game_session_unbounded(3, 1, room_id).unwrap());

        storage.leave_game_session(1, 1, lobby_id).unwrap();
        assert!(storage.migrate_game_session_host(2, 1, lobby_id).unwrap());

        assert!(run(storage.find_party_roster_async(1)).unwrap().unwrap().is_none());
        let (leader_id, roster) = run(storage.find_party_roster_async(3)).unwrap().unwrap().unwrap();
        assert_eq!(leader_id, 2);
        assert_eq!(roster.into_iter().map(|member| member.id).collect::<Vec<_>>(), vec![2, 3]);
    }

    #[test]
    fn direct_private_room_host_exit_regroups_exact_n_minus_one_cohort_in_successor_lobby() {
        let storage = create_test_storage("direct-private-room-party-handoff");
        insert_users(&storage, 4);
        storage.register_urls(1, vec!["prudp:/address=10.66.0.1;port=13000;sid=14;type=2".to_string()]).unwrap();
        storage.register_urls(3, vec!["prudp:/address=10.66.0.3;port=13000;sid=14;type=2".to_string()]).unwrap();

        // Start with the same split shape seen in the real trace: the room
        // host already has one Party member, while the late invitees are not
        // necessarily part of that Party yet.
        let lobby_id = storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        let lobby_invite = run(storage.add_invite_async(1, 2, INVITE_KIND_LOBBY_PARTY, 1, lobby_id)).unwrap().unwrap().unwrap();
        assert!(run(storage.accept_invite_async(2, lobby_invite)).unwrap().unwrap().is_some());
        assert!(storage.join_game_session_unbounded(2, 1, lobby_id).unwrap());
        let successor_lobby_id = storage.create_game_session(3, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        storage.create_game_session(4, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();

        let room_id = storage.create_game_session(1, 1, "113 => 0;3 => 0;4 => 12;103 => 0;112 => 5".to_string()).unwrap();
        storage.add_participants(1, 1, room_id, vec![2, 3, 4], Vec::new()).unwrap();
        let (leader_id, roster) = run(storage.find_party_roster_async(3)).unwrap().unwrap().unwrap();
        assert_eq!(leader_id, 3);
        assert_eq!(roster.into_iter().map(|member| member.id).collect::<Vec<_>>(), vec![3]);

        // A genuinely earlier voluntary departure is not part of the exact
        // N-1 cohort, even if that player remains in the social Party.
        storage.leave_game_session(2, 1, room_id).unwrap();
        storage
            .recent_game_room_departures
            .lock()
            .unwrap()
            .insert((1, room_id, 2), Instant::now() - GAME_ROOM_DEPARTURE_CORRELATION_TTL - Duration::from_millis(1));
        assert_eq!(
            storage.delete_game_session(1, 1, room_id).unwrap(),
            DeleteGameSessionOutcome::MigrationPending { remaining_participants: 2 }
        );
        assert!(run(storage.find_party_roster_async(1)).unwrap().unwrap().is_none());
        let (departed_leader_id, departed_roster) = run(storage.find_party_roster_async(2)).unwrap().unwrap().unwrap();
        assert_eq!(departed_leader_id, 2);
        assert_eq!(departed_roster.into_iter().map(|member| member.id).collect::<Vec<_>>(), vec![2]);
        let (leader_id, roster) = run(storage.find_party_roster_async(4)).unwrap().unwrap().unwrap();
        assert_eq!(leader_id, 3);
        assert_eq!(roster.into_iter().map(|member| member.id).collect::<Vec<_>>(), vec![3, 4]);

        // Phase one is sent only to the successor. Followers cannot race the
        // old-room unwind or an unpublished successor lobby.
        assert!(run(storage.take_invite_async(4)).unwrap().unwrap().is_none());
        assert!(run(storage.take_invite_async(3)).unwrap().unwrap().is_none());
        storage.leave_game_session(4, 1, room_id).unwrap();
        assert!(run(storage.take_invite_async(3)).unwrap().unwrap().is_none());
        storage.leave_game_session(3, 1, room_id).unwrap();

        let leader_restore = run(storage.take_invite_async(3))
            .unwrap()
            .unwrap()
            .expect("the successor should restore its own lobby first");
        assert_eq!(leader_restore.kind, INVITE_KIND_LOBBY_RESTORE);
        assert_eq!(leader_restore.sender, 3);
        assert_eq!(leader_restore.receiver, 3);
        assert_eq!(leader_restore.session_id, successor_lobby_id);
        assert!(run(storage.accept_invite_async(3, leader_restore.id)).unwrap().unwrap().is_some());
        assert!(storage.join_game_session_unbounded(3, 1, successor_lobby_id).unwrap());

        let follower_restore = run(storage.take_invite_async(4))
            .unwrap()
            .unwrap()
            .expect("the follower should be invited only after the successor lobby is restored");
        assert_eq!(follower_restore.sender, 3);
        assert_eq!(follower_restore.session_id, successor_lobby_id);
        assert!(run(storage.accept_invite_async(4, follower_restore.id)).unwrap().unwrap().is_some());
        let target = run(storage.find_accepted_invited_session_async(4, 1)).unwrap().unwrap().unwrap();
        assert_eq!(target.session.session_id, successor_lobby_id);
        assert!(storage.join_game_session_unbounded(4, 1, successor_lobby_id).unwrap());
        assert_eq!(storage.game_session_diagnostics(1, successor_lobby_id).unwrap().unwrap().2, vec![3, 4]);
    }

    #[test]
    fn leave_with_party_restores_original_lobby_and_includes_late_room_guest() {
        let storage = create_test_storage("private-room-leave-with-original-party");
        insert_users(&storage, 3);
        for user_id in 1..=3 {
            storage
                .register_urls(user_id, vec![format!("prudp:/address=10.66.0.{user_id};port=13000;sid={user_id};type=2")])
                .unwrap();
        }

        let original_lobby = storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        let original_invite = run(storage.add_invite_async(1, 2, INVITE_KIND_LOBBY_PARTY, 1, original_lobby)).unwrap().unwrap().unwrap();
        assert!(run(storage.accept_invite_async(2, original_invite)).unwrap().unwrap().is_some());
        assert!(storage.join_game_session_unbounded(2, 1, original_lobby).unwrap());
        storage.create_game_session(3, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();

        let room_id = storage.create_game_session(1, 1, "113 => 0;3 => 0;4 => 12;103 => 0;112 => 5".to_string()).unwrap();
        storage.record_published_game_session(1, u64::from(room_id));
        storage.add_participants(1, 1, room_id, vec![2, 3], Vec::new()).unwrap();

        let (late_leader, late_roster) = run(storage.find_party_roster_async(3)).unwrap().unwrap().unwrap();
        assert_eq!(late_leader, 3);
        assert_eq!(late_roster.into_iter().map(|member| member.id).collect::<Vec<_>>(), vec![3]);

        storage.record_published_game_session(1, u64::from(original_lobby));
        assert_eq!(
            storage.delete_game_session(1, 1, room_id).unwrap(),
            DeleteGameSessionOutcome::MigrationPending { remaining_participants: 2 }
        );

        let (original_leader, original_roster) = run(storage.find_party_roster_async(2)).unwrap().unwrap().unwrap();
        assert_eq!(original_leader, 1);
        assert_eq!(original_roster.into_iter().map(|member| member.id).collect::<Vec<_>>(), vec![1, 2, 3]);
        let (late_leader, late_roster) = run(storage.find_party_roster_async(3)).unwrap().unwrap().unwrap();
        assert_eq!(late_leader, 1);
        assert_eq!(late_roster.into_iter().map(|member| member.id).collect::<Vec<_>>(), vec![1, 2, 3]);
        assert!(run(storage.take_invite_async(2)).unwrap().unwrap().is_none());

        storage.leave_game_session(3, 1, room_id).unwrap();
        storage.leave_game_session(2, 1, room_id).unwrap();
        let late_restore = run(storage.take_invite_async(3)).unwrap().unwrap().expect("late room member should return with the Party");
        assert_eq!(late_restore.kind, INVITE_KIND_LOBBY_RESTORE);
        assert_eq!(late_restore.sender, 1);
        assert_eq!(late_restore.session_id, original_lobby);
        assert!(run(storage.accept_invite_async(3, late_restore.id)).unwrap().unwrap().is_some());
        assert!(storage.join_game_session_unbounded(3, 1, original_lobby).unwrap());
        assert_eq!(storage.game_session_diagnostics(1, original_lobby).unwrap().unwrap().2, vec![1, 2, 3]);
        let (original_leader, original_roster) = run(storage.find_party_roster_async(2)).unwrap().unwrap().unwrap();
        assert_eq!(original_leader, 1);
        assert_eq!(original_roster.into_iter().map(|member| member.id).collect::<Vec<_>>(), vec![1, 2, 3]);
    }

    #[test]
    fn native_migration_preserves_late_private_room_guests_pending_lobby_restore() {
        let storage = create_test_storage("native-migration-preserves-late-guest-restore");
        insert_users(&storage, 3);
        for user_id in 1..=3 {
            storage
                .register_urls(user_id, vec![format!("prudp:/address=10.66.0.{user_id};port=13000;sid={user_id};type=2")])
                .unwrap();
        }

        // Users 1 and 2 own the published native Party lobby. User 3 later
        // joins only the private Game Session, matching the three-machine
        // reproduction where TEST2 had left the Party and was re-invited.
        let published_lobby = storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        let party_invite = run(storage.add_invite_async(1, 2, INVITE_KIND_LOBBY_PARTY, 1, published_lobby)).unwrap().unwrap().unwrap();
        assert!(run(storage.accept_invite_async(2, party_invite)).unwrap().unwrap().is_some());
        assert!(storage.join_game_session_unbounded(2, 1, published_lobby).unwrap());
        storage.create_game_session(3, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();

        let room_id = storage.create_game_session(1, 1, "113 => 0;3 => 0;4 => 12;103 => 0;112 => 5".to_string()).unwrap();
        storage.record_published_game_session(1, u64::from(room_id));
        storage.add_participants(1, 1, room_id, vec![2, 3], Vec::new()).unwrap();
        storage.record_published_game_session(1, u64::from(published_lobby));

        assert_eq!(
            storage.delete_game_session(1, 1, room_id).unwrap(),
            DeleteGameSessionOutcome::MigrationPending { remaining_participants: 2 }
        );

        let (restore_id, restore_session_id): (i64, u32) = run(sqlx::query_as("SELECT id, session_id FROM invites WHERE receiver = 3 AND kind = ? AND consumed_at IS NULL")
            .bind(INVITE_KIND_LOBBY_RESTORE)
            .fetch_one(&storage.pool))
        .unwrap()
        .unwrap();
        assert_eq!(restore_session_id, published_lobby);

        // User 2 is elected by retail SplitSession. Its successful migration
        // must not consume user 3's still-undelivered restore event.
        assert!(storage.migrate_game_session_host(2, 1, room_id).unwrap());
        let restore_still_pending: i64 = run(sqlx::query_scalar("SELECT EXISTS(SELECT 1 FROM invites WHERE id = ? AND consumed_at IS NULL)")
            .bind(restore_id)
            .fetch_one(&storage.pool))
        .unwrap()
        .unwrap();
        assert_eq!(restore_still_pending, 1);

        let (leader_id, roster) = run(storage.find_party_roster_async(3)).unwrap().unwrap().unwrap();
        assert_eq!(leader_id, 2);
        assert_eq!(roster.into_iter().map(|member| member.id).collect::<Vec<_>>(), vec![2, 3]);
    }

    #[test]
    fn private_leave_with_party_cancels_member_restore_that_precedes_host_delete() {
        let storage = create_test_storage("private-room-member-leaves-before-host-party-return");
        insert_users(&storage, 3);
        for user_id in 1..=3 {
            storage
                .register_urls(user_id, vec![format!("prudp:/address=10.66.0.{user_id};port=13000;sid={user_id};type=2")])
                .unwrap();
        }

        let published_lobby = storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        for receiver in [2, 3] {
            let invite = run(storage.add_invite_async(1, receiver, INVITE_KIND_LOBBY_PARTY, 1, published_lobby))
                .unwrap()
                .unwrap()
                .unwrap();
            assert!(run(storage.accept_invite_async(receiver, invite)).unwrap().unwrap().is_some());
            assert!(storage.join_game_session_unbounded(receiver, 1, published_lobby).unwrap());
        }

        let room_id = storage.create_game_session(1, 1, "113 => 0;3 => 0;4 => 12;103 => 0;112 => 5".to_string()).unwrap();
        storage.add_participants(1, 1, room_id, vec![2, 3], Vec::new()).unwrap();
        storage.record_published_game_session(1, u64::from(published_lobby));

        // Native private-room unwind observed in the three-client trace:
        // one follower leaves first, then the host confirms a Party return.
        storage.leave_game_session(3, 1, room_id).unwrap();
        let pending_restore = run(sqlx::query_as::<_, (i64, u32)>(
            "SELECT id, session_id FROM invites WHERE receiver = 3 AND kind = ? AND consumed_at IS NULL ORDER BY id DESC LIMIT 1",
        )
        .bind(INVITE_KIND_LOBBY_RESTORE)
        .fetch_one(&storage.pool))
        .unwrap()
        .unwrap();
        assert_ne!(pending_restore.1, published_lobby);
        assert!(storage.game_session_is_active(1, pending_restore.1).unwrap());

        assert_eq!(
            storage.delete_game_session(1, 1, room_id).unwrap(),
            DeleteGameSessionOutcome::MigrationPending { remaining_participants: 1 }
        );

        let (pending_count,): (i64,) = run(sqlx::query_as("SELECT COUNT(*) FROM invites WHERE receiver = 3 AND kind = ? AND consumed_at IS NULL")
            .bind(INVITE_KIND_LOBBY_RESTORE)
            .fetch_one(&storage.pool))
        .unwrap()
        .unwrap();
        assert_eq!(pending_count, 0);
        assert!(!storage.game_session_is_active(1, pending_restore.1).unwrap());
        assert_eq!(storage.game_session_diagnostics(1, published_lobby).unwrap().unwrap().2, vec![1, 2, 3]);
        let (leader_id, roster) = run(storage.find_party_roster_async(3)).unwrap().unwrap().unwrap();
        assert_eq!(leader_id, 1);
        assert_eq!(roster.into_iter().map(|member| member.id).collect::<Vec<_>>(), vec![1, 2, 3]);
    }

    #[test]
    fn late_private_room_guest_leaves_to_its_existing_personal_party() {
        let storage = create_test_storage("private-room-late-guest-personal-party");
        insert_users(&storage, 2);
        storage.register_urls(2, vec!["prudp:/address=10.66.0.2;port=13000;sid=2;type=2".to_string()]).unwrap();
        let host_lobby = storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        let guest_lobby = storage.create_game_session(2, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        let room_id = storage.create_game_session(1, 1, "113 => 0;3 => 0;4 => 12;103 => 0;112 => 5".to_string()).unwrap();
        storage.add_participants(1, 1, room_id, vec![2], Vec::new()).unwrap();

        storage.leave_game_session(2, 1, room_id).unwrap();
        let (leader_id, roster) = run(storage.find_party_roster_async(2)).unwrap().unwrap().unwrap();
        assert_eq!(leader_id, 2);
        assert_eq!(roster.into_iter().map(|member| member.id).collect::<Vec<_>>(), vec![2]);
        assert_eq!(storage.game_session_diagnostics(1, guest_lobby).unwrap().unwrap().2, vec![2]);
        let restore = run(storage.take_invite_async(2))
            .unwrap()
            .unwrap()
            .expect("departed member should restore its personal lobby");
        assert_eq!(restore.kind, INVITE_KIND_LOBBY_RESTORE);
        assert_eq!(restore.sender, 2);
        assert_eq!(restore.receiver, 2);
        assert_eq!(restore.session_id, guest_lobby);
        let (host_leader, host_roster) = run(storage.find_party_roster_async(1)).unwrap().unwrap().unwrap();
        assert_eq!(host_leader, 1);
        assert_eq!(host_roster.into_iter().map(|member| member.id).collect::<Vec<_>>(), vec![1]);
        assert_eq!(storage.game_session_diagnostics(1, host_lobby).unwrap().unwrap().2, vec![1]);
    }

    #[test]
    fn departed_private_room_member_without_personal_lobby_gets_a_seeded_restore_target() {
        let storage = create_test_storage("private-room-member-seeded-lobby-restore");
        insert_users(&storage, 2);
        storage.register_urls(1, vec!["prudp:/address=10.66.0.1;port=13000;sid=1;type=2".to_string()]).unwrap();
        storage.register_urls(2, vec!["prudp:/address=10.66.0.2;port=13000;sid=2;type=2".to_string()]).unwrap();
        let room_id = storage.create_game_session(1, 1, "113 => 0;3 => 0;4 => 12;103 => 0;112 => 5".to_string()).unwrap();
        let invite_id = run(storage.add_invite_async(1, 2, INVITE_KIND_PRIVATE_ROOM, 1, room_id)).unwrap().unwrap().unwrap();
        assert!(run(storage.accept_invite_async(2, invite_id)).unwrap().unwrap().is_some());
        assert!(storage.join_game_session_unbounded(2, 1, room_id).unwrap());

        storage.leave_game_session(2, 1, room_id).unwrap();
        let restore = run(storage.take_invite_async(2)).unwrap().unwrap().expect("a missing personal lobby should be seeded");
        assert_eq!(restore.kind, INVITE_KIND_LOBBY_RESTORE);
        assert_eq!(restore.sender, 2);
        assert_eq!(restore.receiver, 2);
        assert_ne!(restore.session_id, room_id);
        let (creator_id, attributes, participants) = storage.game_session_diagnostics(1, restore.session_id).unwrap().unwrap();
        assert_eq!(creator_id, 2);
        assert_eq!(session_attribute_value(&attributes, 113), Some(1));
        assert_eq!(participants, vec![2]);
    }

    #[test]
    fn owner_remove_then_member_leave_queues_internal_personal_lobby_restore() {
        let storage = create_test_storage("private-room-owner-remove-before-member-leave");
        insert_users(&storage, 2);
        storage.register_urls(1, vec!["prudp:/address=10.66.0.1;port=13000;sid=1;type=2".to_string()]).unwrap();
        storage.register_urls(2, vec!["prudp:/address=10.66.0.2;port=13000;sid=2;type=2".to_string()]).unwrap();
        storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        let guest_lobby = storage.create_game_session(2, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        let room_id = storage.create_game_session(1, 1, "113 => 0;3 => 0;4 => 12;103 => 0;112 => 5".to_string()).unwrap();
        let invite_id = run(storage.add_invite_async(1, 2, INVITE_KIND_PRIVATE_ROOM, 1, room_id)).unwrap().unwrap().unwrap();
        assert!(run(storage.accept_invite_async(2, invite_id)).unwrap().unwrap().is_some());
        assert!(storage.join_game_session_unbounded(2, 1, room_id).unwrap());

        // This is the retail wire order: the owner removes the member first,
        // then the departing client sends its own LeaveSession milliseconds
        // later. RemoveParticipants alone must not emit a restore transition.
        assert!(storage.remove_participants(1, 1, room_id, vec![2]).unwrap());
        assert!(run(storage.take_invite_async(2)).unwrap().unwrap().is_none());
        storage.leave_game_session(2, 1, room_id).unwrap();

        // Party projection is already a singleton, but the retail UI still
        // needs the internal friend-search transition to create its native
        // personal-lobby Session.
        let restore = run(storage.take_invite_async(2))
            .unwrap()
            .unwrap()
            .expect("owner-remove ordering must still restore the native personal lobby");
        assert_eq!(restore.kind, INVITE_KIND_LOBBY_RESTORE);
        assert_eq!(restore.sender, 2);
        assert_eq!(restore.receiver, 2);
        assert_eq!(restore.session_id, guest_lobby);
        let (leader_id, roster) = run(storage.find_party_roster_async(2)).unwrap().unwrap().unwrap();
        assert_eq!(leader_id, 2);
        assert_eq!(roster.into_iter().map(|member| member.id).collect::<Vec<_>>(), vec![2]);
        let fresh_lobby = storage.create_game_session(2, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        assert_ne!(fresh_lobby, guest_lobby);
        assert_eq!(storage.game_session_diagnostics(1, fresh_lobby).unwrap().unwrap().2, vec![2]);
    }

    #[test]
    fn quick_match_owner_remove_then_member_leave_defers_then_restores_personal_lobby() {
        let storage = create_test_storage("quick-match-owner-remove-before-member-leave");
        insert_users(&storage, 2);
        storage.register_urls(1, vec!["prudp:/address=10.66.0.1;port=13000;sid=1;type=2".to_string()]).unwrap();
        storage.register_urls(2, vec!["prudp:/address=10.66.0.2;port=13000;sid=2;type=2".to_string()]).unwrap();
        storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        let guest_lobby = storage.create_game_session(2, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        let quick_match = storage.create_game_session(1, 1, "113 => 0;3 => 12;4 => 0;103 => 4008726265;112 => 6".to_string()).unwrap();
        storage.add_participants(1, 1, quick_match, vec![2], Vec::new()).unwrap();

        // Retail quick match uses the same owner-remove/member-leave ordering
        // as a private room. The correlation must therefore be based on the
        // game-session class, not on private-room matchmaking attributes.
        assert!(storage.remove_participants(1, 1, quick_match, vec![2]).unwrap());
        assert!(run(storage.take_invite_async(2)).unwrap().unwrap().is_none());
        storage.leave_game_session(2, 1, quick_match).unwrap();

        // Owner-remove is also the first half of "leave with Party". Keep the
        // social Party intact during the bounded correlation window; if no
        // leader transaction follows, the expiry path performs the original
        // one-player restore exactly once.
        assert!(run(storage.take_invite_async(2)).unwrap().unwrap().is_none());
        assert_eq!(storage.pending_quick_match_member_leaves(1, quick_match), vec![2]);
        assert_eq!(expire_pending_quick_match_member_leaves_for_test(&storage), 1);

        let restore = run(storage.take_invite_async(2))
            .unwrap()
            .unwrap()
            .expect("quick-match owner-remove ordering must restore the native personal lobby");
        assert_eq!(restore.kind, INVITE_KIND_LOBBY_RESTORE);
        assert_eq!(restore.sender, 2);
        assert_eq!(restore.receiver, 2);
        assert_eq!(restore.session_id, guest_lobby);
        let (leader_id, roster) = run(storage.find_party_roster_async(2)).unwrap().unwrap().unwrap();
        assert_eq!(leader_id, 2);
        assert_eq!(roster.into_iter().map(|member| member.id).collect::<Vec<_>>(), vec![2]);
    }

    #[test]
    fn private_room_host_exit_correlates_follower_leave_immediately_before_delete() {
        let storage = create_test_storage("private-room-host-exit-order");
        insert_users(&storage, 3);
        for user_id in 1..=3 {
            storage
                .register_urls(user_id, vec![format!("prudp:/address=10.66.0.{user_id};port=13000;sid={user_id};type=2")])
                .unwrap();
        }
        storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        // User 2 is numerically first but has no surviving lobby. Repeated
        // native migration left user 3 as the authoritative owner of the only
        // N-1 lobby, matching the production Session 1430/1434 ordering.
        let successor_lobby_id = storage.create_game_session(3, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        let room_id = storage.create_game_session(1, 1, "113 => 0;3 => 0;4 => 12;103 => 0;112 => 5".to_string()).unwrap();
        storage.add_participants(1, 1, room_id, vec![2, 3], Vec::new()).unwrap();

        // Retail unwinds one follower a few milliseconds before the owner's
        // DeleteSession. The transient LeaveSession must not shrink N-1.
        storage.leave_game_session(2, 1, room_id).unwrap();
        assert_eq!(
            storage.delete_game_session(1, 1, room_id).unwrap(),
            DeleteGameSessionOutcome::MigrationPending { remaining_participants: 1 }
        );
        let (leader_id, roster) = run(storage.find_party_roster_async(3)).unwrap().unwrap().unwrap();
        assert_eq!(leader_id, 3);
        assert_eq!(roster.into_iter().map(|member| member.id).collect::<Vec<_>>(), vec![3, 2]);

        storage.leave_game_session(3, 1, room_id).unwrap();
        let leader_restore = run(storage.take_invite_async(3)).unwrap().unwrap().expect("correlated successor restore");
        assert_eq!(leader_restore.kind, INVITE_KIND_LOBBY_RESTORE);
        assert_eq!(leader_restore.session_id, successor_lobby_id);
        assert!(run(storage.accept_invite_async(3, leader_restore.id)).unwrap().unwrap().is_some());
        assert!(storage.join_game_session_unbounded(3, 1, successor_lobby_id).unwrap());
        let follower_restore = run(storage.take_invite_async(2)).unwrap().unwrap().expect("correlated follower restore");
        assert_eq!(follower_restore.session_id, successor_lobby_id);
    }

    #[test]
    fn repeated_private_room_host_exit_retires_old_generation_and_restores_immediately() {
        let storage = create_test_storage("repeated-private-room-party-handoff");
        insert_users(&storage, 3);
        for user_id in 1..=3 {
            storage
                .register_urls(user_id, vec![format!("prudp:/address=10.66.0.{user_id};port=13000;sid={user_id};type=2")])
                .unwrap();
        }
        let lobby_1 = storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        let lobby_2 = storage.create_game_session(2, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        storage.create_game_session(3, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();

        let room_1 = storage.create_game_session(1, 1, "113 => 0;3 => 0;4 => 12;103 => 0;112 => 5".to_string()).unwrap();
        storage.add_participants(1, 1, room_1, vec![2, 3], Vec::new()).unwrap();
        assert_eq!(
            storage.delete_game_session(1, 1, room_1).unwrap(),
            DeleteGameSessionOutcome::MigrationPending { remaining_participants: 2 }
        );
        storage.leave_game_session(2, 1, room_1).unwrap();
        storage.leave_game_session(3, 1, room_1).unwrap();
        assert!(!storage.game_session_is_active(1, room_1).unwrap());

        let first_restore = run(storage.take_invite_async(2))
            .unwrap()
            .unwrap()
            .expect("first successor restore must be immediately deliverable");
        assert_eq!(first_restore.session_id, lobby_2);
        assert!(run(storage.accept_invite_async(2, first_restore.id)).unwrap().unwrap().is_some());
        assert!(storage.join_game_session_unbounded(2, 1, lobby_2).unwrap());
        let first_follower = run(storage.take_invite_async(3)).unwrap().unwrap().expect("first follower restore");
        assert!(run(storage.accept_invite_async(3, first_follower.id)).unwrap().unwrap().is_some());
        assert!(storage.join_game_session_unbounded(3, 1, lobby_2).unwrap());

        // A second cycle deliberately chooses the previous room owner as the
        // successor. Before pending-host retirement, room_1 remained active
        // with user 1 alone and blocked this restore indefinitely.
        let room_2 = storage.create_game_session(2, 1, "113 => 0;3 => 0;4 => 12;103 => 0;112 => 5".to_string()).unwrap();
        storage.add_participants(2, 1, room_2, vec![1, 3], Vec::new()).unwrap();
        assert_eq!(
            storage.delete_game_session(2, 1, room_2).unwrap(),
            DeleteGameSessionOutcome::MigrationPending { remaining_participants: 2 }
        );
        storage.leave_game_session(1, 1, room_2).unwrap();
        storage.leave_game_session(3, 1, room_2).unwrap();
        assert!(!storage.game_session_is_active(1, room_2).unwrap());

        let second_restore = run(storage.take_invite_async(1))
            .unwrap()
            .unwrap()
            .expect("second successor restore must not be blocked by room_1");
        assert_eq!(second_restore.session_id, lobby_1);
        assert!(run(storage.accept_invite_async(1, second_restore.id)).unwrap().unwrap().is_some());
        assert!(storage.join_game_session_unbounded(1, 1, lobby_1).unwrap());
        let second_follower = run(storage.take_invite_async(3)).unwrap().unwrap().expect("second follower restore");
        assert!(run(storage.accept_invite_async(3, second_follower.id)).unwrap().unwrap().is_some());
        assert!(storage.join_game_session_unbounded(3, 1, lobby_1).unwrap());
        assert_eq!(storage.game_session_diagnostics(1, lobby_1).unwrap().unwrap().2, vec![1, 3]);
    }

    #[test]
    fn unmarked_solo_private_room_is_not_retired_as_stale_host_state() {
        let storage = create_test_storage("live-solo-private-room");
        insert_users(&storage, 1);
        let room_id = storage.create_game_session(1, 1, "113 => 0;3 => 0;4 => 12;103 => 0;112 => 5".to_string()).unwrap();

        assert!(storage.remove_participants(1, 1, room_id, Vec::new()).unwrap());
        assert!(storage.game_session_is_active(1, room_id).unwrap());
        assert_eq!(storage.game_session_diagnostics(1, room_id).unwrap().unwrap().2, vec![1]);
    }

    #[test]
    fn four_player_private_room_host_exit_preserves_three_player_party_and_two_restore_targets() {
        let storage = create_test_storage("four-player-private-room-party-handoff");
        insert_users(&storage, 4);
        for user_id in 1..=4 {
            storage
                .register_urls(user_id, vec![format!("prudp:/address=10.66.0.{user_id};port=13000;sid={user_id};type=2")])
                .unwrap();
        }
        storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        let superseded_successor_lobby_id = storage.create_game_session(2, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        storage.create_game_session(3, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        storage.create_game_session(4, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        let room_id = storage.create_game_session(1, 1, "113 => 0;3 => 0;4 => 12;103 => 0;112 => 5".to_string()).unwrap();
        storage.add_participants(1, 1, room_id, vec![2, 3, 4], Vec::new()).unwrap();

        assert_eq!(
            storage.delete_game_session(1, 1, room_id).unwrap(),
            DeleteGameSessionOutcome::MigrationPending { remaining_participants: 3 }
        );
        let (leader_id, roster) = run(storage.find_party_roster_async(4)).unwrap().unwrap().unwrap();
        assert_eq!(leader_id, 2);
        assert_eq!(roster.into_iter().map(|member| member.id).collect::<Vec<_>>(), vec![2, 3, 4]);
        let (restore_count,): (i64,) = run(
            sqlx::query_as("SELECT COUNT(*) FROM invites WHERE kind = ? AND session_id = ? AND sender = receiver AND consumed_at IS NULL")
                .bind(INVITE_KIND_LOBBY_RESTORE)
                .bind(superseded_successor_lobby_id)
                .fetch_one(&storage.pool),
        )
        .unwrap()
        .unwrap();
        assert_eq!(restore_count, 1);

        for user_id in 2..=4 {
            storage.leave_game_session(user_id, 1, room_id).unwrap();
        }
        run(sqlx::query("UPDATE invites SET created = datetime('now', '-3 seconds') WHERE receiver = 2 AND kind = ?")
            .bind(INVITE_KIND_LOBBY_RESTORE)
            .execute(&storage.pool))
        .unwrap()
        .unwrap();
        let leader_restore = run(storage.take_invite_async(2)).unwrap().unwrap().expect("successor restore event");
        assert_eq!(leader_restore.sender, 2);
        assert_eq!(leader_restore.receiver, 2);
        assert!(run(storage.accept_invite_async(2, leader_restore.id)).unwrap().unwrap().is_some());

        // Retail may host a fresh lobby instead of rejoining the preserved
        // one. Retiring the old lobby must not erase the in-flight Party.
        let successor_lobby_id = storage.create_game_session(2, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        assert!(!storage.game_session_is_active(1, superseded_successor_lobby_id).unwrap());
        let (leader_id, roster) = run(storage.find_party_roster_async(4)).unwrap().unwrap().unwrap();
        assert_eq!(leader_id, 2);
        assert_eq!(roster.into_iter().map(|member| member.id).collect::<Vec<_>>(), vec![2, 3, 4]);

        run(
            sqlx::query("UPDATE invites SET created = datetime('now', '-3 seconds') WHERE receiver IN (3, 4) AND kind = ?")
                .bind(INVITE_KIND_LOBBY_RESTORE)
                .execute(&storage.pool),
        )
        .unwrap()
        .unwrap();
        for receiver in [3, 4] {
            let restore = run(storage.take_invite_async(receiver))
                .unwrap()
                .unwrap()
                .expect("each N-1 follower should receive one restore target");
            assert_eq!(restore.sender, 2);
            assert_eq!(restore.session_id, successor_lobby_id);
            assert!(run(storage.accept_invite_async(receiver, restore.id)).unwrap().unwrap().is_some());
            assert!(storage.join_game_session_unbounded(receiver, 1, successor_lobby_id).unwrap());
        }
        assert_eq!(storage.game_session_diagnostics(1, successor_lobby_id).unwrap().unwrap().2, vec![2, 3, 4]);
    }

    #[test]
    fn successful_native_private_match_migration_cancels_lobby_restore_lane() {
        let storage = create_test_storage("native-migration-cancels-lobby-restore");
        insert_users(&storage, 3);
        for user_id in 1..=3 {
            storage
                .register_urls(user_id, vec![format!("prudp:/address=10.66.0.{user_id};port=13000;sid={user_id};type=2")])
                .unwrap();
        }
        storage.create_game_session(2, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        storage.create_game_session(3, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        let match_id = storage.create_game_session(1, 1, "113 => 0;3 => 0;4 => 12;103 => 0;112 => 5".to_string()).unwrap();
        storage.add_participants(1, 1, match_id, vec![2, 3], Vec::new()).unwrap();

        assert_eq!(
            storage.delete_game_session(1, 1, match_id).unwrap(),
            DeleteGameSessionOutcome::MigrationPending { remaining_participants: 2 }
        );
        let pending_before: i64 = run(sqlx::query_scalar("SELECT host_leave_pending FROM game_sessions WHERE id = ?")
            .bind(match_id)
            .fetch_one(&storage.pool))
        .unwrap()
        .unwrap();
        assert_eq!(pending_before, 1);
        assert!(storage.migrate_game_session_host(2, 1, match_id).unwrap());
        let pending_after: i64 = run(sqlx::query_scalar("SELECT host_leave_pending FROM game_sessions WHERE id = ?")
            .bind(match_id)
            .fetch_one(&storage.pool))
        .unwrap()
        .unwrap();
        assert_eq!(pending_after, 0);
        let (pending_restore_count,): (i64,) = run(sqlx::query_as("SELECT COUNT(*) FROM invites WHERE kind = ? AND consumed_at IS NULL")
            .bind(INVITE_KIND_LOBBY_RESTORE)
            .fetch_one(&storage.pool))
        .unwrap()
        .unwrap();
        assert_eq!(pending_restore_count, 0);
        let (leader_id, roster) = run(storage.find_party_roster_async(3)).unwrap().unwrap().unwrap();
        assert_eq!(leader_id, 2);
        assert_eq!(roster.into_iter().map(|member| member.id).collect::<Vec<_>>(), vec![2, 3]);
    }

    #[test]
    fn native_migration_preserves_survivor_cleanup_leaves_until_split_session() {
        let storage = create_test_storage("native-migration-survivor-cleanup");
        insert_users(&storage, 3);
        let room_id = storage.create_game_session(1, 1, "113 => 0;3 => 0;4 => 12;103 => 0;112 => 5".to_string()).unwrap();
        storage.add_participants(1, 1, room_id, vec![2, 3], Vec::new()).unwrap();

        // The owner begins native migration via AbandonSession/LeaveSession.
        storage.abandon_game_session(1, 1, room_id).unwrap();
        let pending: i64 = run(sqlx::query_scalar("SELECT host_leave_pending FROM game_sessions WHERE id = ?")
            .bind(room_id)
            .fetch_one(&storage.pool))
        .unwrap()
        .unwrap();
        assert_eq!(pending, 1);

        // Retail then sends cleanup LeaveSession calls from the surviving
        // clients before the elected host submits SplitSession. Membership
        // must remain intact so either survivor can be elected.
        storage.leave_game_session(2, 1, room_id).unwrap();
        storage.leave_game_session(3, 1, room_id).unwrap();
        assert_eq!(storage.game_session_diagnostics(1, room_id).unwrap().unwrap().2, vec![1, 2, 3]);

        assert!(storage.migrate_game_session_host(2, 1, room_id).unwrap());
        let (creator_id, _, participant_ids) = storage.game_session_diagnostics(1, room_id).unwrap().unwrap();
        assert_eq!(creator_id, 2);
        assert_eq!(participant_ids, vec![2, 3]);
    }

    #[test]
    fn private_native_migration_detaches_old_party_leader_before_split_session() {
        let storage = create_test_storage("native-migration-party-leader-preparation");
        insert_users(&storage, 4);
        for user_id in 1..=4 {
            storage
                .register_urls(user_id, vec![format!("prudp:/address=10.66.0.{user_id};port=13000;sid={user_id};type=2")])
                .unwrap();
        }

        let lobby_id = storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        for receiver in 2..=4 {
            let invite_id = run(storage.add_invite_async(1, receiver, INVITE_KIND_LOBBY_PARTY, 1, lobby_id)).unwrap().unwrap().unwrap();
            assert!(run(storage.accept_invite_async(receiver, invite_id)).unwrap().unwrap().is_some());
            assert!(storage.join_game_session_unbounded(receiver, 1, lobby_id).unwrap());
        }

        let room_id = storage.create_game_session(1, 1, "113 => 0;3 => 0;4 => 12;103 => 0;112 => 5".to_string()).unwrap();
        for receiver in 2..=4 {
            assert!(storage.join_game_session_unbounded(receiver, 1, room_id).unwrap());
        }

        storage.abandon_game_session(1, 1, room_id).unwrap();

        // The social Party must stop following the departing host immediately,
        // while the private game Session remains untouched for native election.
        // The old host gets an independent single-member Party and one silent
        // personal-lobby restore instead of an impossible zero-member UI.
        let (departed_leader_id, departed_roster) = run(storage.find_party_roster_async(1)).unwrap().unwrap().unwrap();
        assert_eq!(departed_leader_id, 1);
        assert_eq!(departed_roster.into_iter().map(|member| member.id).collect::<Vec<_>>(), vec![1]);
        let (provisional_leader_id, provisional_roster) = run(storage.find_party_roster_async(3)).unwrap().unwrap().unwrap();
        assert_eq!(provisional_leader_id, 2);
        assert_eq!(provisional_roster.into_iter().map(|member| member.id).collect::<Vec<_>>(), vec![2, 3, 4]);
        let (creator_id, _, participant_ids) = storage.game_session_diagnostics(1, room_id).unwrap().unwrap();
        assert_eq!(creator_id, 1);
        assert_eq!(participant_ids, vec![1, 2, 3, 4]);
        let (pending_restore_count,): (i64,) = run(sqlx::query_as("SELECT COUNT(*) FROM invites WHERE kind = ? AND consumed_at IS NULL")
            .bind(INVITE_KIND_LOBBY_RESTORE)
            .fetch_one(&storage.pool))
        .unwrap()
        .unwrap();
        assert_eq!(pending_restore_count, 1);
        assert!(run(storage.take_invite_async(1)).unwrap().unwrap().is_none());

        // Cleanup Leaves are transport unwind and must not remove survivors.
        for survivor_id in 2..=4 {
            storage.leave_game_session(survivor_id, 1, room_id).unwrap();
        }
        assert_eq!(storage.game_session_diagnostics(1, room_id).unwrap().unwrap().2, vec![1, 2, 3, 4]);

        // The game-selected SplitSession remains authoritative and may correct
        // the provisional Party successor without creating a second pathway.
        assert!(storage.migrate_game_session_host(4, 1, room_id).unwrap());
        let (creator_id, _, participant_ids) = storage.game_session_diagnostics(1, room_id).unwrap().unwrap();
        assert_eq!(creator_id, 4);
        assert_eq!(participant_ids, vec![2, 3, 4]);
        let (leader_id, roster) = run(storage.find_party_roster_async(2)).unwrap().unwrap().unwrap();
        assert_eq!(leader_id, 4);
        assert_eq!(roster.into_iter().map(|member| member.id).collect::<Vec<_>>(), vec![4, 2, 3]);
        let departed_restore = run(storage.take_invite_async(1))
            .unwrap()
            .unwrap()
            .expect("departing host should restore a personal lobby after native migration completes");
        assert_eq!(departed_restore.kind, INVITE_KIND_LOBBY_RESTORE);
        assert_eq!(departed_restore.sender, 1);
        assert_eq!(departed_restore.receiver, 1);
        assert_ne!(departed_restore.session_id, lobby_id);
        assert_eq!(storage.game_session_diagnostics(1, departed_restore.session_id).unwrap().unwrap().2, vec![1]);
    }

    #[test]
    fn quick_match_host_abandon_restores_only_the_departing_host_lobby() {
        let storage = create_test_storage("quick-host-personal-lobby-restore");
        insert_users(&storage, 3);
        for user_id in 1..=3 {
            storage
                .register_urls(user_id, vec![format!("prudp:/address=10.66.0.{user_id};port=13000;sid={user_id};type=2")])
                .unwrap();
        }

        let shared_lobby = storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        for receiver in 2..=3 {
            let invite_id = run(storage.add_invite_async(1, receiver, INVITE_KIND_LOBBY_PARTY, 1, shared_lobby))
                .unwrap()
                .unwrap()
                .unwrap();
            assert!(run(storage.accept_invite_async(receiver, invite_id)).unwrap().unwrap().is_some());
            assert!(storage.join_game_session_unbounded(receiver, 1, shared_lobby).unwrap());
        }
        let quick_match = storage.create_game_session(1, 1, "113 => 0;3 => 12;4 => 0;103 => 4008726265".to_string()).unwrap();
        storage.add_participants(1, 1, quick_match, vec![2, 3], Vec::new()).unwrap();

        storage.abandon_game_session(1, 1, quick_match).unwrap();

        let (creator_id, _, participant_ids) = storage.game_session_diagnostics(1, quick_match).unwrap().unwrap();
        assert_eq!(creator_id, 1);
        assert_eq!(participant_ids, vec![1, 2, 3]);
        let (departed_leader, departed_roster) = run(storage.find_party_roster_async(1)).unwrap().unwrap().unwrap();
        assert_eq!(departed_leader, 1);
        assert_eq!(departed_roster.into_iter().map(|member| member.id).collect::<Vec<_>>(), vec![1]);
        let (survivor_leader, survivor_roster) = run(storage.find_party_roster_async(3)).unwrap().unwrap().unwrap();
        assert_eq!(survivor_leader, 2);
        assert_eq!(survivor_roster.into_iter().map(|member| member.id).collect::<Vec<_>>(), vec![2, 3]);

        assert!(run(storage.take_invite_async(1)).unwrap().unwrap().is_none());

        // Retail follows AbandonSession with LeaveSession. That cleanup must
        // not queue a second restore or alter the still-migrating roster.
        storage.leave_game_session(1, 1, quick_match).unwrap();
        let pending_restores: i64 = run(sqlx::query_scalar("SELECT COUNT(*) FROM invites WHERE receiver = 1 AND kind = ? AND consumed_at IS NULL")
            .bind(INVITE_KIND_LOBBY_RESTORE)
            .fetch_one(&storage.pool))
        .unwrap()
        .unwrap();
        assert_eq!(pending_restores, 1);
        assert_eq!(storage.game_session_diagnostics(1, quick_match).unwrap().unwrap().2, vec![1, 2, 3]);

        assert!(storage.migrate_game_session_host(3, 1, quick_match).unwrap());
        let (creator_id, _, participant_ids) = storage.game_session_diagnostics(1, quick_match).unwrap().unwrap();
        assert_eq!(creator_id, 3);
        assert_eq!(participant_ids, vec![2, 3]);
        for survivor in [2, 3] {
            let candidates = run(storage.find_invitable_session_candidates_async(survivor)).unwrap().unwrap();
            assert!(candidates
                .iter()
                .any(|candidate| candidate.session.session_id == quick_match && candidate.session.creator_id == 3 && candidate.direct_member));
        }
        let restore = run(storage.take_invite_async(1))
            .unwrap()
            .unwrap()
            .expect("departing quick-match host should restore a personal lobby after native migration completes");
        assert_eq!(restore.kind, INVITE_KIND_LOBBY_RESTORE);
        assert_eq!(restore.sender, 1);
        assert_eq!(restore.receiver, 1);
        assert_ne!(restore.session_id, shared_lobby);
        assert_ne!(restore.session_id, quick_match);
        assert_eq!(storage.game_session_diagnostics(1, restore.session_id).unwrap().unwrap().2, vec![1]);
    }

    #[test]
    fn expired_native_migration_uses_existing_n_minus_one_lobby_fallback() {
        let storage = create_test_storage("native-migration-expired-fallback");
        insert_users(&storage, 3);
        for user_id in 1..=3 {
            storage
                .register_urls(user_id, vec![format!("prudp:/address=10.66.0.{user_id};port=13000;sid={user_id};type=2")])
                .unwrap();
        }
        storage.create_game_session(2, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        storage.create_game_session(3, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        let room_id = storage.create_game_session(1, 1, "113 => 0;3 => 0;4 => 12;103 => 0;112 => 5".to_string()).unwrap();
        storage.add_participants(1, 1, room_id, vec![2, 3], Vec::new()).unwrap();
        storage.abandon_game_session(1, 1, room_id).unwrap();

        storage
            .native_host_migrations
            .lock()
            .unwrap()
            .insert((1, room_id), Instant::now() - NATIVE_HOST_MIGRATION_TTL - Duration::from_secs(1));
        assert!(!storage.native_host_migration_is_active(1, room_id));
        assert!(storage.native_host_migration_cleanup_due());
        assert_eq!(storage.expire_native_host_migrations().unwrap(), 1);
        assert!(!storage.game_session_is_active(1, room_id).unwrap());

        run(sqlx::query("UPDATE invites SET created = datetime('now', '-3 seconds') WHERE receiver = 2 AND kind = ?")
            .bind(INVITE_KIND_LOBBY_RESTORE)
            .execute(&storage.pool))
        .unwrap()
        .unwrap();
        let leader_restore = run(storage.take_invite_async(2)).unwrap().unwrap().expect("N-1 leader restore");
        assert_eq!(leader_restore.kind, INVITE_KIND_LOBBY_RESTORE);
        assert!(run(storage.accept_invite_async(2, leader_restore.id)).unwrap().unwrap().is_some());
        assert!(storage.join_game_session_unbounded(2, 1, leader_restore.session_id).unwrap());

        run(sqlx::query("UPDATE invites SET created = datetime('now', '-3 seconds') WHERE receiver = 3 AND kind = ?")
            .bind(INVITE_KIND_LOBBY_RESTORE)
            .execute(&storage.pool))
        .unwrap()
        .unwrap();
        let restore = run(storage.take_invite_async(3)).unwrap().unwrap().expect("N-1 follower restore");
        assert_eq!(restore.kind, INVITE_KIND_LOBBY_RESTORE);
        assert!(run(storage.accept_invite_async(3, restore.id)).unwrap().unwrap().is_some());
        assert!(storage.join_game_session_unbounded(3, 1, restore.session_id).unwrap());

        let (leader_id, roster) = run(storage.find_party_roster_async(2)).unwrap().unwrap().unwrap();
        assert_eq!(leader_id, 2);
        assert_eq!(roster.into_iter().map(|member| member.id).collect::<Vec<_>>(), vec![2, 3]);
    }

    #[test]
    fn expired_private_migration_keeps_an_already_complete_native_party_lobby() {
        let storage = create_test_storage("native-migration-complete-party-lobby");
        insert_users(&storage, 3);
        for user_id in 1..=3 {
            storage
                .register_urls(user_id, vec![format!("prudp:/address=10.66.0.{user_id};port=13000;sid={user_id};type=2")])
                .unwrap();
        }

        let survivor_lobby = storage.create_game_session(2, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        assert!(storage.join_game_session_unbounded(3, 1, survivor_lobby).unwrap());
        let room_id = storage.create_game_session(1, 1, "113 => 0;3 => 0;4 => 12;103 => 0;112 => 5".to_string()).unwrap();
        storage.add_participants(1, 1, room_id, vec![2, 3], Vec::new()).unwrap();
        storage.abandon_game_session(1, 1, room_id).unwrap();

        storage
            .native_host_migrations
            .lock()
            .unwrap()
            .insert((1, room_id), Instant::now() - NATIVE_HOST_MIGRATION_TTL - Duration::from_secs(1));
        assert_eq!(storage.expire_native_host_migrations().unwrap(), 1);

        let (leader_id, roster) = run(storage.find_party_roster_async(3)).unwrap().unwrap().unwrap();
        assert_eq!(leader_id, 2);
        assert_eq!(roster.into_iter().map(|member| member.id).collect::<Vec<_>>(), vec![2, 3]);
        assert_eq!(storage.game_session_diagnostics(1, survivor_lobby).unwrap().unwrap().2, vec![2, 3]);
        let (restore_count,): (i64,) = run(sqlx::query_as("SELECT COUNT(*) FROM invites WHERE kind = ? AND consumed_at IS NULL")
            .bind(INVITE_KIND_LOBBY_RESTORE)
            .fetch_one(&storage.pool))
        .unwrap()
        .unwrap();
        assert_eq!(restore_count, 0);
    }

    #[test]
    fn removing_party_member_while_private_room_is_active_detaches_membership_and_allows_reinvite() {
        let storage = create_test_storage("party-remove-with-active-room");
        insert_users(&storage, 4);
        storage.register_urls(1, vec!["prudp:/address=10.66.0.1;port=13000;sid=14;type=2".to_string()]).unwrap();

        let lobby_id = storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        for receiver in 2..=4 {
            let invite_id = run(storage.add_invite_async(1, receiver, INVITE_KIND_LOBBY_PARTY, 1, lobby_id)).unwrap().unwrap().unwrap();
            assert!(run(storage.accept_invite_async(receiver, invite_id)).unwrap().unwrap().is_some());
            assert!(storage.join_game_session_unbounded(receiver, 1, lobby_id).unwrap());
        }

        let room_id = storage.create_game_session(1, 1, "113 => 0;103 => 0;112 => 4".to_string()).unwrap();
        assert!(storage.join_game_session_unbounded(2, 1, room_id).unwrap());
        assert!(storage.join_game_session_unbounded(3, 1, room_id).unwrap());
        assert!(storage.join_game_session_unbounded(4, 1, room_id).unwrap());

        assert!(storage.remove_participants(1, 1, lobby_id, vec![2]).unwrap());
        assert_eq!(storage.game_session_diagnostics(1, lobby_id).unwrap().unwrap().2, vec![1, 3, 4]);
        let (departed_leader_id, departed_roster) = run(storage.find_party_roster_async(2)).unwrap().unwrap().unwrap();
        assert_eq!(departed_leader_id, 2);
        assert_eq!(departed_roster.into_iter().map(|member| member.id).collect::<Vec<_>>(), vec![2]);
        let (leader_id, roster) = run(storage.find_party_roster_async(3)).unwrap().unwrap().unwrap();
        assert_eq!(leader_id, 1);
        assert_eq!(roster.into_iter().map(|member| member.id).collect::<Vec<_>>(), vec![1, 3, 4]);

        let reinvite_id = run(storage.add_invite_async(3, 2, INVITE_KIND_LOBBY_PARTY, 1, lobby_id))
            .unwrap()
            .unwrap()
            .expect("a remaining Party member must be able to reinvite the departed player");
        let delivered = run(storage.take_invite_async(2))
            .unwrap()
            .unwrap()
            .expect("reinvite must be delivered instead of AlreadyExists");
        assert_eq!(delivered.id, reinvite_id);
        assert_eq!(delivered.sender, 3);
    }

    #[test]
    fn invite_generations_are_monotonic_and_stale_accept_cannot_hit_a_new_lifecycle() {
        const LEGACY_GENERATION_HIGH_WATER: i64 = 1_000_000_000;

        let storage = create_test_storage("invite-generation-monotonicity");
        insert_users(&storage, 2);
        storage.register_urls(1, vec!["prudp:/address=10.66.0.2;port=13000;sid=14;type=2".to_string()]).unwrap();

        let first_lobby = storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        let old_generation = run(storage.add_invite_async(1, 2, INVITE_KIND_LOBBY_PARTY, 1, first_lobby)).unwrap().unwrap().unwrap();
        assert!(run(storage.accept_invite_async(2, old_generation)).unwrap().unwrap().is_some());
        assert!(storage.join_game_session_unbounded(2, 1, first_lobby).unwrap());

        let second_lobby = storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        let new_generation = run(storage.add_invite_async(1, 2, INVITE_KIND_LOBBY_PARTY, 1, second_lobby)).unwrap().unwrap().unwrap();

        assert!(old_generation > LEGACY_GENERATION_HIGH_WATER);
        assert!(new_generation > old_generation);
        assert!(u32::try_from(new_generation).is_ok());
        assert!(run(storage.accept_invite_async(2, old_generation)).unwrap().unwrap().is_none());

        let (new_accepted_at,): (Option<String>,) = run(sqlx::query_as("SELECT accepted_at FROM invites WHERE id = ?").bind(new_generation).fetch_one(&storage.pool))
            .unwrap()
            .unwrap();
        assert!(new_accepted_at.is_none());
        let delivered = run(storage.take_invite_async(2)).unwrap().unwrap().unwrap();
        assert_eq!(delivered.id, new_generation);
        assert_eq!(delivered.session_id, second_lobby);
    }

    #[test]
    fn invite_generation_migration_preserves_existing_rows_and_advances_sequence() {
        run(async {
            let pool = SqlitePoolOptions::new().max_connections(1).connect("sqlite::memory:").await?;
            sqlx::raw_sql(
                r"
                PRAGMA foreign_keys = ON;
                CREATE TABLE users (id INTEGER PRIMARY KEY);
                INSERT INTO users(id) VALUES (1), (2);
                CREATE TABLE invites (
                    sender INTEGER NOT NULL REFERENCES users(id),
                    receiver INTEGER NOT NULL REFERENCES users(id),
                    created DATETIME DEFAULT CURRENT_TIMESTAMP,
                    session_type INTEGER,
                    session_id INTEGER,
                    delivered_at DATETIME,
                    consumed_at DATETIME,
                    expires_at DATETIME,
                    kind INTEGER NOT NULL DEFAULT 1,
                    accepted_at DATETIME,
                    delivery_count INTEGER NOT NULL DEFAULT 0,
                    party_id INTEGER
                );
                INSERT INTO invites (
                    rowid, sender, receiver, created, session_type, session_id,
                    delivered_at, expires_at, kind, delivery_count, party_id
                ) VALUES (
                    42, 1, 2, '2026-08-09 00:00:00', 1, 501,
                    '2026-08-09 00:00:03', '2099-01-01 00:00:00', 2, 7, 9
                );
                ",
            )
            .execute(&pool)
            .await?;

            sqlx::raw_sql(include_str!("migrations/20260809180000_invite_generation_ids.sql")).execute(&pool).await?;

            let preserved: (i64, u32, u32, i32, u32, i64, Option<i64>) = sqlx::query_as("SELECT id, sender, receiver, kind, session_id, delivery_count, party_id FROM invites")
                .fetch_one(&pool)
                .await?;
            assert_eq!(preserved, (42, 1, 2, INVITE_KIND_LOBBY_PARTY, 501, 7, Some(9)));

            let sequence: i64 = sqlx::query_scalar("SELECT seq FROM sqlite_sequence WHERE name = 'invites'").fetch_one(&pool).await?;
            assert_eq!(sequence, 1_000_000_000);
            let next = sqlx::query("INSERT INTO invites (sender, receiver) VALUES (1, 2)")
                .execute(&pool)
                .await?
                .last_insert_rowid();
            assert_eq!(next, 1_000_000_001);
            assert!(u32::try_from(next).is_ok());

            Ok::<(), sqlx::Error>(())
        })
        .unwrap()
        .unwrap();
    }

    #[test]
    fn invite_rejects_wrong_host_and_expires_safely() {
        let storage = create_test_storage("invite-validation");
        insert_users(&storage, 3);
        storage.register_urls(1, vec!["prudp:/address=10.66.0.2;port=13000;sid=14;type=2".to_string()]).unwrap();
        storage.register_urls(3, vec!["prudp:/address=10.66.0.4;port=13000;sid=14;type=2".to_string()]).unwrap();
        let other_host_session = storage.create_game_session(3, 1, "113 => 0".to_string()).unwrap();
        assert!(run(storage.add_invite_async(1, 2, 1, 1, other_host_session)).unwrap().unwrap().is_none());

        let own_session = storage.create_game_session(1, 1, "113 => 0".to_string()).unwrap();
        let invite_id = run(storage.add_invite_async(1, 2, 1, 1, own_session)).unwrap().unwrap().unwrap();
        run(async {
            sqlx::query("UPDATE invites SET expires_at = datetime('now', '-1 second') WHERE receiver = 2")
                .execute(&storage.pool)
                .await
        })
        .unwrap()
        .unwrap();
        assert!(run(storage.accept_invite_async(2, invite_id)).unwrap().unwrap().is_none());
        assert!(run(storage.find_accepted_invited_session_async(2, 1)).unwrap().unwrap().is_none());
        assert!(run(storage.take_invite_async(2)).unwrap().unwrap().is_none());
    }

    #[test]
    fn api_heartbeat_preserves_idle_secure_session_until_heartbeat_stops() {
        let storage = create_test_storage("api-heartbeat");
        insert_users(&storage, 1);
        storage.register_urls(1, vec!["prudp:/address=10.66.0.2;port=13000;sid=14;type=2".to_string()]).unwrap();
        let lobby_id = storage.create_game_session(1, 1, "113 => 1".to_string()).unwrap();
        run(storage.touch_api_presence_async(1)).unwrap().unwrap();

        assert!(storage.close_secure_session(1).unwrap());
        assert!(storage.game_session_is_active(1, lobby_id).unwrap());
        assert!(!run(storage.list_urls(1)).unwrap().unwrap().is_empty());
        let users = run(storage.list_users_async()).unwrap().unwrap();
        assert!(users.iter().any(|user| user.id == 1 && user.is_online));

        run(async {
            sqlx::query("UPDATE users SET last_api_seen_at = datetime('now', '-30 seconds') WHERE id = 1")
                .execute(&storage.pool)
                .await?;
            storage.cleanup_stale_api_sessions_async().await?;
            Ok::<(), eyre::Error>(())
        })
        .unwrap()
        .unwrap();
        assert!(!storage.game_session_is_active(1, lobby_id).unwrap());
        assert!(run(storage.list_urls(1)).unwrap().unwrap().is_empty());
    }

    #[test]
    fn forced_host_disconnect_waits_for_game_selected_successor() {
        let storage = create_test_storage("forced-host-disconnect-native-migration");
        insert_users(&storage, 3);
        for user_id in 1..=3 {
            storage.create_user_session(user_id, format!("forced-host-{user_id}").as_bytes()).unwrap();
            storage
                .register_urls(user_id, vec![format!("prudp:/address=10.66.0.{user_id};port=13000;sid={user_id};type=2")])
                .unwrap();
        }
        let lobby_id = storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        for receiver in 2..=3 {
            let invite_id = run(storage.add_invite_async(1, receiver, INVITE_KIND_LOBBY_PARTY, 1, lobby_id)).unwrap().unwrap().unwrap();
            assert!(run(storage.accept_invite_async(receiver, invite_id)).unwrap().unwrap().is_some());
            assert!(storage.join_game_session_unbounded(receiver, 1, lobby_id).unwrap());
        }

        assert!(!storage.close_secure_session(1).unwrap());
        let (creator_id, _, participant_ids) = storage.game_session_diagnostics(1, lobby_id).unwrap().unwrap();
        assert_eq!(creator_id, 1);
        assert_eq!(participant_ids, vec![1, 2, 3]);
        let (leader_id, roster) = run(storage.find_party_roster_async(2)).unwrap().unwrap().unwrap();
        assert_eq!(leader_id, 1);
        assert_eq!(roster.into_iter().map(|member| member.id).collect::<Vec<_>>(), vec![1, 2, 3]);

        assert!(storage.migrate_game_session_host(3, 1, lobby_id).unwrap());
        let (creator_id, _, participant_ids) = storage.game_session_diagnostics(1, lobby_id).unwrap().unwrap();
        assert_eq!(creator_id, 3);
        assert_eq!(participant_ids, vec![2, 3]);
        let (leader_id, roster) = run(storage.find_party_roster_async(2)).unwrap().unwrap().unwrap();
        assert_eq!(leader_id, 3);
        assert_eq!(roster.into_iter().map(|member| member.id).collect::<Vec<_>>(), vec![3, 2]);
    }

    #[test]
    fn friend_presence_ignores_login_only_state_until_game_session_is_ready() {
        let storage = create_test_storage("friend-presence-login-only");
        insert_users(&storage, 1);

        run(async {
            // Model the old Launcher-login side effect and an API heartbeat.
            // Neither is sufficient without a secure session or station URL.
            sqlx::query("UPDATE users SET is_online = 1, last_login = CURRENT_TIMESTAMP, last_api_seen_at = CURRENT_TIMESTAMP WHERE id = 1")
                .execute(&storage.pool)
                .await?;
            Ok::<(), sqlx::Error>(())
        })
        .unwrap()
        .unwrap();

        let users = run(storage.list_game_ready_users_async()).unwrap().unwrap();
        assert!(users.iter().any(|user| user.id == 1 && !user.is_online));

        storage.create_user_session(1, b"friend-presence-secure").unwrap();
        let users = run(storage.list_game_ready_users_async()).unwrap().unwrap();
        assert!(users.iter().any(|user| user.id == 1 && user.is_online));
    }

    #[test]
    fn successful_authentication_does_not_publish_gameplay_presence() {
        let storage = create_test_storage("authentication-is-not-presence");
        run(async {
            sqlx::query("INSERT INTO users (id, username, password, ubi_id) VALUES (2001, 'launcher-user', 'password', 'ubi-launcher')")
                .execute(&storage.pool)
                .await?;
            Ok::<(), sqlx::Error>(())
        })
        .unwrap()
        .unwrap();

        let authenticated = run(storage.login_user_async("launcher-user", "password")).unwrap().unwrap();
        assert!(matches!(authenticated, Ok(2001)));
        let (has_last_login, is_online): (i64, bool) = run(async {
            Ok::<_, sqlx::Error>(
                sqlx::query_as("SELECT last_login IS NOT NULL, is_online FROM users WHERE id = 2001")
                    .fetch_one(&storage.pool)
                    .await?,
            )
        })
        .unwrap()
        .unwrap();
        assert_eq!((has_last_login, is_online), (1, false));
    }

    #[test]
    fn friend_presence_survives_transient_secure_disconnect_with_game_endpoint() {
        let storage = create_test_storage("friend-presence-transient-disconnect");
        insert_users(&storage, 1);
        storage.create_user_session(1, b"friend-presence-transient").unwrap();
        storage.register_urls(1, vec!["prudp:/address=10.66.0.2;port=13000;sid=14;type=2".to_string()]).unwrap();
        run(storage.touch_api_presence_async(1)).unwrap().unwrap();

        assert!(storage.close_secure_session(1).unwrap());
        let users = run(storage.list_game_ready_users_async()).unwrap().unwrap();
        assert!(users.iter().any(|user| user.id == 1 && user.is_online));
    }

    #[test]
    fn lobby_party_follows_leader_into_created_private_room() {
        let storage = create_test_storage("party-follow-private");
        insert_users(&storage, 3);
        for user_id in 1..=3 {
            storage
                .register_urls(user_id, vec![format!("prudp:/address=10.66.0.{user_id};port=13000;sid=14;type=2")])
                .unwrap();
        }
        let lobby_id = storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        for receiver in 2..=3 {
            let invite_id = run(storage.add_invite_async(1, receiver, INVITE_KIND_LOBBY_PARTY, 1, lobby_id)).unwrap().unwrap().unwrap();
            assert!(run(storage.take_invite_async(receiver)).unwrap().unwrap().is_some());
            assert!(run(storage.accept_invite_async(receiver, invite_id)).unwrap().unwrap().is_some());
            assert!(storage.join_game_session_unbounded(receiver, 1, lobby_id).unwrap());
        }

        let private_id = storage.create_game_session(1, 1, "113 => 0;103 => 0;112 => 4".to_string()).unwrap();
        run(async {
            sqlx::query("UPDATE invites SET created = datetime('now', '-3 seconds') WHERE kind = ?")
                .bind(INVITE_KIND_PARTY_FOLLOW)
                .execute(&storage.pool)
                .await
        })
        .unwrap()
        .unwrap();
        for receiver in 2..=3 {
            let follow = run(storage.take_invite_async(receiver)).unwrap().unwrap().unwrap();
            assert_eq!(follow.kind, INVITE_KIND_PARTY_FOLLOW);
            assert!(run(storage.accept_invite_async(receiver, follow.id)).unwrap().unwrap().is_some());
            let target = run(storage.find_accepted_invited_session_async(receiver, 1)).unwrap().unwrap().unwrap();
            assert_eq!(target.kind, INVITE_KIND_PARTY_FOLLOW);
            assert_eq!(target.session.session_id, private_id);
            storage.add_participants(1, 1, private_id, vec![receiver], Vec::new()).unwrap();
            assert!(run(storage.find_accepted_invited_session_async(receiver, 1)).unwrap().unwrap().is_none());
            assert!(storage.join_game_session_unbounded(receiver, 1, private_id).unwrap());
        }
        let (_, _, participant_ids) = storage.game_session_diagnostics(1, private_id).unwrap().unwrap();
        assert_eq!(participant_ids, vec![1, 2, 3]);
    }

    #[test]
    fn private_match_member_leave_detaches_stale_party_but_keeps_remaining_member_invitable() {
        let storage = create_test_storage("private-member-reinvite");
        insert_users(&storage, 3);
        for user_id in 1..=3 {
            storage
                .register_urls(user_id, vec![format!("prudp:/address=10.66.0.{user_id};port=13000;sid=21;type=2")])
                .unwrap();
        }

        let lobby_id = storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        for receiver in 2..=3 {
            let invite_id = run(storage.add_invite_async(1, receiver, INVITE_KIND_LOBBY_PARTY, 1, lobby_id)).unwrap().unwrap().unwrap();
            assert!(run(storage.take_invite_async(receiver)).unwrap().unwrap().is_some());
            assert!(run(storage.accept_invite_async(receiver, invite_id)).unwrap().unwrap().is_some());
            assert!(storage.join_game_session_unbounded(receiver, 1, lobby_id).unwrap());
        }

        let room_id = storage.create_game_session(1, 1, "113 => 0;3 => 0;4 => 12;103 => 0;112 => 5".to_string()).unwrap();
        storage.add_participants(1, 1, room_id, vec![2, 3], Vec::new()).unwrap();

        storage.leave_game_session(2, 1, room_id).unwrap();

        let (departed_leader_id, departed_roster) = run(storage.find_party_roster_async(2)).unwrap().unwrap().unwrap();
        assert_eq!(departed_leader_id, 2);
        assert_eq!(departed_roster.into_iter().map(|member| member.id).collect::<Vec<_>>(), vec![2]);
        let (leader_id, roster) = run(storage.find_party_roster_async(3)).unwrap().unwrap().unwrap();
        assert_eq!(leader_id, 1);
        assert_eq!(roster.into_iter().map(|member| member.id).collect::<Vec<_>>(), vec![1, 3]);

        let remaining_candidates = run(storage.find_invitable_session_candidates_async(3)).unwrap().unwrap();
        assert!(remaining_candidates
            .iter()
            .any(|candidate| candidate.session.session_id == room_id && candidate.direct_member));
        let departed_candidates = run(storage.find_invitable_session_candidates_async(2)).unwrap().unwrap();
        assert!(departed_candidates.iter().all(|candidate| candidate.session.session_id != room_id));

        assert!(run(storage.add_invite_async(3, 2, INVITE_KIND_PRIVATE_ROOM, 1, room_id)).unwrap().unwrap().is_some());
    }

    #[test]
    fn public_match_member_leave_projects_one_player_lobby_and_preserves_remaining_party() {
        let storage = create_test_storage("public-member-leave-solo");
        insert_users(&storage, 3);
        for user_id in 1..=3 {
            storage
                .register_urls(user_id, vec![format!("prudp:/address=10.66.0.{user_id};port=13000;sid=22;type=2")])
                .unwrap();
        }

        let shared_lobby = storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        for receiver in 2..=3 {
            let invite_id = run(storage.add_invite_async(1, receiver, INVITE_KIND_LOBBY_PARTY, 1, shared_lobby))
                .unwrap()
                .unwrap()
                .unwrap();
            assert!(run(storage.accept_invite_async(receiver, invite_id)).unwrap().unwrap().is_some());
            assert!(storage.join_game_session_unbounded(receiver, 1, shared_lobby).unwrap());
        }

        let public_match = storage.create_game_session(1, 1, "113 => 0;3 => 12;4 => 0;103 => 4008726265".to_string()).unwrap();
        storage.add_participants(1, 1, public_match, vec![2, 3], Vec::new()).unwrap();
        storage.leave_game_session(2, 1, public_match).unwrap();
        assert_eq!(expire_pending_quick_match_member_leaves_for_test(&storage), 1);

        let (solo_leader, solo_roster) = run(storage.find_party_roster_async(2)).unwrap().unwrap().unwrap();
        assert_eq!(solo_leader, 2);
        assert_eq!(solo_roster.into_iter().map(|member| member.id).collect::<Vec<_>>(), vec![2]);
        let (remaining_leader, remaining_roster) = run(storage.find_party_roster_async(3)).unwrap().unwrap().unwrap();
        assert_eq!(remaining_leader, 1);
        assert_eq!(remaining_roster.into_iter().map(|member| member.id).collect::<Vec<_>>(), vec![1, 3]);

        storage.leave_game_session(2, 1, shared_lobby).unwrap();
        let (restored_leader, restored_roster) = run(storage.find_party_roster_async(2)).unwrap().unwrap().unwrap();
        assert_eq!(restored_leader, 2);
        assert_eq!(restored_roster.into_iter().map(|member| member.id).collect::<Vec<_>>(), vec![2]);

        let personal_lobby = storage.create_game_session(2, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        assert!(storage.game_session_is_active(1, personal_lobby).unwrap());
        let (personal_leader, personal_roster) = run(storage.find_party_roster_async(2)).unwrap().unwrap().unwrap();
        assert_eq!(personal_leader, 2);
        assert_eq!(personal_roster.into_iter().map(|member| member.id).collect::<Vec<_>>(), vec![2]);
    }

    #[test]
    fn social_party_leader_leaving_another_players_match_hands_remaining_party_to_a_survivor() {
        let storage = create_test_storage("party-leader-leaves-foreign-match");
        insert_users(&storage, 4);
        for user_id in 1..=4 {
            storage
                .register_urls(user_id, vec![format!("prudp:/address=10.66.0.{user_id};port=13000;sid=23;type=2")])
                .unwrap();
        }

        let shared_lobby = storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        for receiver in 2..=3 {
            let invite_id = run(storage.add_invite_async(1, receiver, INVITE_KIND_LOBBY_PARTY, 1, shared_lobby))
                .unwrap()
                .unwrap()
                .unwrap();
            assert!(run(storage.accept_invite_async(receiver, invite_id)).unwrap().unwrap().is_some());
            assert!(storage.join_game_session_unbounded(receiver, 1, shared_lobby).unwrap());
        }

        let public_match = storage.create_game_session(4, 1, "113 => 0;3 => 12;4 => 0;103 => 4008726265".to_string()).unwrap();
        storage.add_participants(4, 1, public_match, vec![1, 2, 3], Vec::new()).unwrap();
        storage.leave_game_session(1, 1, public_match).unwrap();
        assert_eq!(expire_pending_quick_match_member_leaves_for_test(&storage), 1);

        let (departed_leader, departed_roster) = run(storage.find_party_roster_async(1)).unwrap().unwrap().unwrap();
        assert_eq!(departed_leader, 1);
        assert_eq!(departed_roster.into_iter().map(|member| member.id).collect::<Vec<_>>(), vec![1]);
        let (remaining_leader, remaining_roster) = run(storage.find_party_roster_async(3)).unwrap().unwrap().unwrap();
        assert_eq!(remaining_leader, 2);
        assert_eq!(remaining_roster.into_iter().map(|member| member.id).collect::<Vec<_>>(), vec![2, 3]);
    }

    #[test]
    fn direct_quick_match_invitee_joins_the_current_party_after_admission() {
        let storage = create_test_storage("quick-match-late-invite-party");
        insert_users(&storage, 4);
        for user_id in 1..=4 {
            storage
                .register_urls(user_id, vec![format!("prudp:/address=10.66.0.{user_id};port=13000;sid=24;type=2")])
                .unwrap();
        }

        let shared_lobby = storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        let member_invite = run(storage.add_invite_async(1, 2, INVITE_KIND_LOBBY_PARTY, 1, shared_lobby)).unwrap().unwrap().unwrap();
        assert!(run(storage.accept_invite_async(2, member_invite)).unwrap().unwrap().is_some());
        assert!(storage.join_game_session_unbounded(2, 1, shared_lobby).unwrap());

        let quick_match = storage.create_game_session(4, 1, "113 => 0;3 => 12;4 => 0;103 => 4008726265".to_string()).unwrap();
        assert!(storage.join_game_session_unbounded(1, 1, quick_match).unwrap());
        assert!(storage.join_game_session_unbounded(2, 1, quick_match).unwrap());
        storage.create_game_session(3, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();

        let late_invite = run(storage.add_invite_async(2, 3, INVITE_KIND_PRIVATE_ROOM, 1, quick_match)).unwrap().unwrap().unwrap();
        assert!(run(storage.accept_invite_async(3, late_invite)).unwrap().unwrap().is_some());
        assert!(storage.join_game_session_unbounded(3, 1, quick_match).unwrap());

        let (leader_id, roster) = run(storage.find_party_roster_async(3)).unwrap().unwrap().unwrap();
        assert_eq!(leader_id, 1);
        assert_eq!(roster.into_iter().map(|member| member.id).collect::<Vec<_>>(), vec![1, 2, 3]);
        let (original_leader_id, original_roster) = run(storage.find_party_roster_async(2)).unwrap().unwrap().unwrap();
        assert_eq!(original_leader_id, 1);
        assert_eq!(original_roster.into_iter().map(|member| member.id).collect::<Vec<_>>(), vec![1, 2, 3]);
        assert!(run(storage.take_invite_async(3)).unwrap().unwrap().is_none());
    }

    #[test]
    fn direct_quick_match_invite_reconciles_the_native_published_party() {
        let storage = create_test_storage("quick-match-native-party-reconcile");
        insert_users(&storage, 3);
        for user_id in 1..=3 {
            storage
                .register_urls(user_id, vec![format!("prudp:/address=10.66.0.{user_id};port=13000;sid=241;type=2")])
                .unwrap();
        }

        let shared_lobby = storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        let member_invite = run(storage.add_invite_async(1, 2, INVITE_KIND_LOBBY_PARTY, 1, shared_lobby)).unwrap().unwrap().unwrap();
        assert!(run(storage.accept_invite_async(2, member_invite)).unwrap().unwrap().is_some());
        assert!(storage.join_game_session_unbounded(2, 1, shared_lobby).unwrap());

        let quick_match = storage.create_game_session(1, 1, "113 => 0;3 => 12;4 => 0;103 => 4008726265".to_string()).unwrap();
        assert!(storage.join_game_session_unbounded(2, 1, quick_match).unwrap());

        // Model the observed retail ordering: the native lobby still contains
        // users 1 and 2, while an earlier leave/split path has temporarily
        // detached user 2 into a one-person persistent Party.
        run(async {
            let mut transaction = storage.begin_write().await?;
            Storage::detach_party_member_to_solo_async(&mut transaction, 2, quick_match).await?;
            transaction.commit().await
        })
        .unwrap()
        .unwrap();
        let (stale_leader_id, stale_roster) = run(storage.find_party_roster_async(2)).unwrap().unwrap().unwrap();
        assert_eq!(stale_leader_id, 2);
        assert_eq!(stale_roster.into_iter().map(|member| member.id).collect::<Vec<_>>(), vec![2]);
        storage.record_published_game_session(2, u64::from(shared_lobby));

        storage.create_game_session(3, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        let late_invite = run(storage.add_invite_async(2, 3, INVITE_KIND_PRIVATE_ROOM, 1, quick_match)).unwrap().unwrap().unwrap();
        assert!(run(storage.accept_invite_async(3, late_invite)).unwrap().unwrap().is_some());
        assert!(storage.join_game_session_unbounded(3, 1, quick_match).unwrap());

        let (leader_id, roster) = run(storage.find_party_roster_async(3)).unwrap().unwrap().unwrap();
        assert_eq!(leader_id, 1);
        assert_eq!(roster.into_iter().map(|member| member.id).collect::<Vec<_>>(), vec![1, 2, 3]);
    }

    #[test]
    fn leave_with_party_during_late_quick_match_join_restores_the_complete_party() {
        let storage = create_test_storage("quick-match-leave-with-late-invite");
        insert_users(&storage, 3);
        for user_id in 1..=3 {
            storage
                .register_urls(user_id, vec![format!("prudp:/address=10.66.0.{user_id};port=13000;sid=25;type=2")])
                .unwrap();
        }

        let shared_lobby = storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        let member_invite = run(storage.add_invite_async(1, 2, INVITE_KIND_LOBBY_PARTY, 1, shared_lobby)).unwrap().unwrap().unwrap();
        assert!(run(storage.accept_invite_async(2, member_invite)).unwrap().unwrap().is_some());
        assert!(storage.join_game_session_unbounded(2, 1, shared_lobby).unwrap());

        let quick_match = storage.create_game_session(1, 1, "113 => 0;3 => 12;4 => 0;103 => 4008726265".to_string()).unwrap();
        assert!(storage.join_game_session_unbounded(2, 1, quick_match).unwrap());
        let late_invite = run(storage.add_invite_async(2, 3, INVITE_KIND_PRIVATE_ROOM, 1, quick_match)).unwrap().unwrap().unwrap();
        assert!(run(storage.accept_invite_async(3, late_invite)).unwrap().unwrap().is_some());
        assert!(storage.join_game_session_unbounded(3, 1, quick_match).unwrap());

        storage.record_published_game_session(1, u64::from(shared_lobby));
        assert!(storage.remove_participants(1, 1, quick_match, vec![2]).unwrap());
        storage.abandon_game_session(1, 1, quick_match).unwrap();

        let (leader_id, roster) = run(storage.find_party_roster_async(1)).unwrap().unwrap().unwrap();
        assert_eq!(leader_id, 1);
        assert_eq!(roster.into_iter().map(|member| member.id).collect::<Vec<_>>(), vec![1, 2, 3]);
        let restored_lobby = run(
            sqlx::query_scalar::<_, u32>("SELECT session_id FROM invites WHERE receiver = 1 AND kind = ? ORDER BY id DESC LIMIT 1")
                .bind(INVITE_KIND_LOBBY_RESTORE)
                .fetch_one(&storage.pool),
        )
        .unwrap()
        .unwrap();
        assert_ne!(restored_lobby, shared_lobby);
        assert!(!storage.game_session_is_active(1, shared_lobby).unwrap());
        assert_eq!(
            storage.game_session_diagnostics(1, restored_lobby).unwrap().unwrap(),
            (1, "113 => 1;3 => 8;4 => 0".to_string(), vec![1, 2, 3])
        );
        assert!(run(storage.take_invite_async(1)).unwrap().unwrap().is_none());
        assert!(run(storage.take_invite_async(3)).unwrap().unwrap().is_none());

        run(
            sqlx::query("UPDATE invites SET created = datetime('now', '-3 seconds'), delivered_at = datetime('now', '-4 seconds') WHERE receiver = 1 AND kind = ?")
                .bind(INVITE_KIND_LOBBY_RESTORE)
                .execute(&storage.pool),
        )
        .unwrap()
        .unwrap();
        let leader_restore = run(storage.take_invite_async(1)).unwrap().unwrap().expect("Party leader must restore first");
        assert_eq!(leader_restore.session_id, restored_lobby);
        assert!(run(storage.accept_invite_async(1, leader_restore.id)).unwrap().unwrap().is_some());
        let active_lobby = storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();

        let restore = run(storage.take_invite_async(3))
            .unwrap()
            .unwrap()
            .expect("late quick-match invitee must follow the restored Party leader");
        assert_eq!(restore.kind, INVITE_KIND_LOBBY_RESTORE);
        assert_eq!(restore.session_id, active_lobby);
    }

    #[test]
    fn quick_match_restore_waits_for_leader_before_delivering_followers() {
        let storage = create_test_storage("quick-match-two-phase-party-restore");
        insert_users(&storage, 3);
        for user_id in 1..=3 {
            storage
                .register_urls(user_id, vec![format!("prudp:/address=10.66.0.{user_id};port=13000;sid=43;type=2")])
                .unwrap();
        }
        let published_lobby = storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        for receiver in [2, 3] {
            let invite = run(storage.add_invite_async(1, receiver, INVITE_KIND_LOBBY_PARTY, 1, published_lobby))
                .unwrap()
                .unwrap()
                .unwrap();
            assert!(run(storage.accept_invite_async(receiver, invite)).unwrap().unwrap().is_some());
            assert!(storage.join_game_session_unbounded(receiver, 1, published_lobby).unwrap());
        }

        let party_id = run(sqlx::query_scalar::<_, i64>("SELECT party_id FROM party_members WHERE user_id = 1").fetch_one(&storage.pool))
            .unwrap()
            .unwrap();
        let match_session = storage.create_game_session(3, 1, "113 => 0;3 => 12;4 => 0;103 => 4008726265".to_string()).unwrap();
        assert!(storage.join_game_session_unbounded(1, 1, match_session).unwrap());
        assert!(storage.join_game_session_unbounded(2, 1, match_session).unwrap());
        let snapshot = QuickMatchPartyDeparture {
            captured_at: Instant::now(),
            party_id,
            leader_id: 1,
            room_host_id: 3,
            lobby_session_id: published_lobby,
            members: vec![1, 2, 3],
            departed_members: [1, 2, 3].into_iter().collect(),
        };

        let (seed_lobby, member_count, initial_restore_count) = run(async {
            let mut transaction = storage.begin_write().await?;
            let restored = storage
                .restore_quick_match_party_snapshot_async(&mut transaction, &snapshot, 1, match_session)
                .await?
                .expect("quick-match Party should enter the restore lane");
            transaction.commit().await?;
            Ok::<_, sqlx::Error>(restored)
        })
        .unwrap()
        .unwrap();
        assert_eq!(member_count, 3);
        assert_eq!(initial_restore_count, 1);
        assert_eq!(storage.game_session_diagnostics(1, seed_lobby).unwrap().unwrap().2, vec![1, 2, 3]);
        assert!(run(storage.take_invite_async(2)).unwrap().unwrap().is_none());
        assert!(run(storage.take_invite_async(3)).unwrap().unwrap().is_none());

        run(sqlx::query(
            "UPDATE invites SET created = datetime('now', '-3 seconds'), delivered_at = datetime('now', '-4 seconds') WHERE receiver = 1 AND kind = ? AND session_id = ?",
        )
        .bind(INVITE_KIND_LOBBY_RESTORE)
        .bind(seed_lobby)
        .execute(&storage.pool))
        .unwrap()
        .unwrap();
        let leader_restore = run(storage.take_invite_async(1)).unwrap().unwrap().expect("leader restore must be delivered first");
        assert_eq!(leader_restore.kind, INVITE_KIND_LOBBY_RESTORE);
        assert_eq!(leader_restore.session_id, seed_lobby);
        assert!(run(storage.accept_invite_async(1, leader_restore.id)).unwrap().unwrap().is_some());

        let active_lobby = storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        assert_ne!(active_lobby, seed_lobby);
        assert!(!storage.game_session_is_active(1, seed_lobby).unwrap());
        assert_eq!(storage.game_session_diagnostics(1, active_lobby).unwrap().unwrap().2, vec![1]);
        run(sqlx::query("UPDATE invites SET created = datetime('now', '-3 seconds') WHERE kind = ? AND session_id = ?")
            .bind(INVITE_KIND_LOBBY_RESTORE)
            .bind(active_lobby)
            .execute(&storage.pool))
        .unwrap()
        .unwrap();

        let ordinary_follower_restore = run(storage.take_invite_async(2))
            .unwrap()
            .unwrap()
            .expect("ordinary follower must follow the leader immediately");
        assert_eq!(ordinary_follower_restore.session_id, active_lobby);
        assert!(run(storage.accept_invite_async(2, ordinary_follower_restore.id)).unwrap().unwrap().is_some());
        assert!(storage.join_game_session_unbounded(2, 1, active_lobby).unwrap());

        assert!(run(storage.take_invite_async(3)).unwrap().unwrap().is_none());
        let former_host_personal_lobby = storage.create_game_session(3, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        assert!(former_host_personal_lobby > seed_lobby);
        let former_host_restore = run(storage.take_invite_async(3))
            .unwrap()
            .unwrap()
            .expect("former transport host must restore after its newer personal lobby exists");
        assert_eq!(former_host_restore.kind, INVITE_KIND_LOBBY_RESTORE);
        assert_eq!(former_host_restore.sender, 1);
        assert_eq!(former_host_restore.session_id, active_lobby);
        assert!(run(storage.accept_invite_async(3, former_host_restore.id)).unwrap().unwrap().is_some());
        assert!(storage.join_game_session_unbounded(3, 1, active_lobby).unwrap());
        assert_eq!(storage.game_session_diagnostics(1, active_lobby).unwrap().unwrap().2, vec![1, 2, 3]);
        let (leader_id, roster) = run(storage.find_party_roster_async(2)).unwrap().unwrap().unwrap();
        assert_eq!(leader_id, 1);
        assert_eq!(roster.into_iter().map(|member| member.id).collect::<Vec<_>>(), vec![1, 2, 3]);
    }

    #[test]
    fn quick_match_member_first_leave_waits_for_the_owner_party_transaction() {
        let storage = create_test_storage("quick-match-member-first-party-return");
        insert_users(&storage, 3);
        for user_id in 1..=3 {
            storage
                .register_urls(user_id, vec![format!("prudp:/address=10.66.0.{user_id};port=13000;sid=32;type=2")])
                .unwrap();
        }

        // Retail may transfer native Party-lobby ownership independently of
        // the public quick-match host. User 3 owns the Party lobby while user
        // 2 owns the matchmaking Session, matching the three-client trace.
        let shared_lobby = storage.create_game_session(3, 1, "113 => 1;3 => 11;4 => 0".to_string()).unwrap();
        let member_invite = run(storage.add_invite_async(3, 2, INVITE_KIND_LOBBY_PARTY, 1, shared_lobby)).unwrap().unwrap().unwrap();
        assert!(run(storage.accept_invite_async(2, member_invite)).unwrap().unwrap().is_some());
        assert!(storage.join_game_session_unbounded(2, 1, shared_lobby).unwrap());

        let quick_match = storage.create_game_session(2, 1, "113 => 0;3 => 12;4 => 0;103 => 4008726265".to_string()).unwrap();
        assert!(storage.join_game_session_unbounded(3, 1, quick_match).unwrap());
        storage.record_published_game_session(2, u64::from(shared_lobby));
        storage.record_published_game_session(3, u64::from(shared_lobby));

        let late_invite = run(storage.add_invite_async(3, 1, INVITE_KIND_PRIVATE_ROOM, 1, quick_match)).unwrap().unwrap().unwrap();
        assert!(run(storage.accept_invite_async(1, late_invite)).unwrap().unwrap().is_some());
        assert!(storage.join_game_session_unbounded(1, 1, quick_match).unwrap());

        // Actual retail ordering: a Party member leaves first; 157ms later the
        // quick-match owner abandons. The first call must not detach user 3 to
        // a solo Party before the coordinated transaction becomes visible.
        storage.leave_game_session(3, 1, quick_match).unwrap();
        let (leader_before_owner, roster_before_owner) = run(storage.find_party_roster_async(3)).unwrap().unwrap().unwrap();
        assert_eq!(leader_before_owner, 3);
        let mut members_before_owner = roster_before_owner.into_iter().map(|member| member.id).collect::<Vec<_>>();
        members_before_owner.sort_unstable();
        assert_eq!(members_before_owner, vec![1, 2, 3]);

        storage.abandon_game_session(2, 1, quick_match).unwrap();

        assert!(!storage.game_session_is_active(1, quick_match).unwrap());
        let restored_lobby = run(
            sqlx::query_scalar::<_, u32>("SELECT session_id FROM invites WHERE receiver = 3 AND kind = ? ORDER BY id DESC LIMIT 1")
                .bind(INVITE_KIND_LOBBY_RESTORE)
                .fetch_one(&storage.pool),
        )
        .unwrap()
        .unwrap();
        assert_ne!(restored_lobby, shared_lobby);
        assert!(!storage.game_session_is_active(1, shared_lobby).unwrap());
        assert_eq!(
            storage.game_session_diagnostics(1, restored_lobby).unwrap().unwrap(),
            (3, "113 => 1;3 => 11;4 => 0".to_string(), vec![1, 2, 3])
        );
        let (leader_id, roster) = run(storage.find_party_roster_async(1)).unwrap().unwrap().unwrap();
        assert_eq!(leader_id, 3);
        let mut member_ids = roster.into_iter().map(|member| member.id).collect::<Vec<_>>();
        member_ids.sort_unstable();
        assert_eq!(member_ids, vec![1, 2, 3]);
        assert!(storage.pending_quick_match_member_leaves.lock().unwrap().is_empty());
    }

    #[test]
    fn quick_match_split_before_old_host_abandon_restores_the_departing_party() {
        let storage = create_test_storage("quick-match-split-before-old-host-abandon");
        insert_users(&storage, 3);
        for user_id in 1..=3 {
            storage
                .register_urls(user_id, vec![format!("prudp:/address=10.66.0.{user_id};port=13000;sid=34;type=2")])
                .unwrap();
        }

        // User 1 is the late member re-invited by successor/Party leader 2;
        // user 3 is the other current member. All clients therefore project
        // the same authoritative three-player Party before the exit begins.
        let successor_lobby = storage.create_game_session(2, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        for receiver in [3, 1] {
            let party_invite = run(storage.add_invite_async(2, receiver, INVITE_KIND_LOBBY_PARTY, 1, successor_lobby))
                .unwrap()
                .unwrap()
                .unwrap();
            assert!(run(storage.accept_invite_async(receiver, party_invite)).unwrap().unwrap().is_some());
            assert!(storage.join_game_session_unbounded(receiver, 1, successor_lobby).unwrap());
        }

        let quick_match = storage.create_game_session(2, 1, "113 => 0;3 => 12;4 => 0;103 => 4008726265".to_string()).unwrap();
        storage.add_participants(2, 1, quick_match, vec![1, 3], Vec::new()).unwrap();
        for user_id in 1..=3 {
            storage.record_published_game_session(user_id, u64::from(successor_lobby));
        }

        // Actual failing ordering: follower 3 is removed and leaves first; late
        // member 1 submits Split next; 12ms later current leader 2 submits
        // Abandon. The first leave freezes all three members, while only the
        // final leader Abandon authorizes one shared lobby transition.
        assert!(storage.remove_participants(2, 1, quick_match, vec![3]).unwrap());
        storage.leave_game_session(3, 1, quick_match).unwrap();
        let (departed_leader, departed_roster) = run(storage.find_party_roster_async(3)).unwrap().unwrap().unwrap();
        assert_eq!(departed_leader, 2);
        let mut departed_members = departed_roster.into_iter().map(|member| member.id).collect::<Vec<_>>();
        departed_members.sort_unstable();
        assert_eq!(departed_members, vec![1, 2, 3]);
        assert_eq!(storage.pending_quick_match_member_leaves(1, quick_match), vec![3]);
        assert!(storage.migrate_game_session_host(1, 1, quick_match).unwrap());
        storage.abandon_game_session(2, 1, quick_match).unwrap();

        assert!(!storage.game_session_is_active(1, quick_match).unwrap());
        let restored_lobby = run(
            sqlx::query_scalar::<_, u32>("SELECT session_id FROM invites WHERE receiver = 2 AND kind = ? ORDER BY id DESC LIMIT 1")
                .bind(INVITE_KIND_LOBBY_RESTORE)
                .fetch_one(&storage.pool),
        )
        .unwrap()
        .unwrap();
        assert_ne!(restored_lobby, successor_lobby);
        assert_eq!(
            storage.game_session_diagnostics(1, restored_lobby).unwrap().unwrap(),
            (2, "113 => 1;3 => 8;4 => 0".to_string(), vec![1, 2, 3])
        );
        let (leader_id, roster) = run(storage.find_party_roster_async(1)).unwrap().unwrap().unwrap();
        assert_eq!(leader_id, 2);
        let mut member_ids = roster.into_iter().map(|member| member.id).collect::<Vec<_>>();
        member_ids.sort_unstable();
        assert_eq!(member_ids, vec![1, 2, 3]);
        assert!(storage.pending_quick_match_member_leaves.lock().unwrap().is_empty());
    }

    #[test]
    fn quick_match_original_leader_rejoins_successor_party_before_coordinated_return() {
        let storage = create_test_storage("quick-match-original-leader-rejoins-successor-party");
        insert_users(&storage, 3);
        for user_id in 1..=3 {
            storage
                .register_urls(user_id, vec![format!("prudp:/address=10.66.0.{user_id};port=13000;sid=36;type=2")])
                .unwrap();
        }

        // Exact retail scenario: user 1 creates the lobby and invites users 2
        // and 3 before entering public matchmaking.
        let original_lobby = storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        for receiver in [2, 3] {
            let party_invite = run(storage.add_invite_async(1, receiver, INVITE_KIND_LOBBY_PARTY, 1, original_lobby))
                .unwrap()
                .unwrap()
                .unwrap();
            assert!(run(storage.accept_invite_async(receiver, party_invite)).unwrap().unwrap().is_some());
            assert!(storage.join_game_session_unbounded(receiver, 1, original_lobby).unwrap());
        }
        let quick_match = storage.create_game_session(1, 1, "113 => 0;3 => 12;4 => 0;103 => 4008726265".to_string()).unwrap();
        storage.add_participants(1, 1, quick_match, vec![2, 3], Vec::new()).unwrap();
        for user_id in 1..=3 {
            storage.record_published_game_session(user_id, u64::from(original_lobby));
        }

        // User 1 leaves alone. Retail moves user 1 to a personal lobby while
        // user 2 takes the quick-match/Party role. The old lobby separately
        // migrates to user 3; that transport migration must not overwrite the
        // Party leader already elected by the public-match departure.
        storage.abandon_game_session(1, 1, quick_match).unwrap();
        assert!(storage.migrate_game_session_host(2, 1, quick_match).unwrap());
        storage.abandon_game_session(1, 1, original_lobby).unwrap();
        assert!(storage.migrate_game_session_host(3, 1, original_lobby).unwrap());
        let personal_lobby = storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        storage.record_published_game_session(1, u64::from(personal_lobby));
        storage.record_published_game_session(2, u64::from(original_lobby));
        storage.record_published_game_session(3, u64::from(original_lobby));

        let (successor_id, successor_roster) = run(storage.find_party_roster_async(2)).unwrap().unwrap().unwrap();
        assert_eq!(successor_id, 2);
        assert_eq!(successor_roster.into_iter().map(|member| member.id).collect::<Vec<_>>(), vec![2, 3]);

        // A current Party member directly invites user 1 back into the public
        // room. Joining that accepted generation must reattach user 1 to the
        // successor Party before any coordinated leave begins.
        let rejoin_invite = run(storage.add_invite_async(2, 1, INVITE_KIND_PRIVATE_ROOM, 1, quick_match)).unwrap().unwrap().unwrap();
        assert!(run(storage.accept_invite_async(1, rejoin_invite)).unwrap().unwrap().is_some());
        assert!(storage.join_game_session_unbounded(1, 1, quick_match).unwrap());
        let (rejoined_leader, rejoined_roster) = run(storage.find_party_roster_async(1)).unwrap().unwrap().unwrap();
        assert_eq!(rejoined_leader, 2);
        let mut rejoined_ids = rejoined_roster.into_iter().map(|member| member.id).collect::<Vec<_>>();
        rejoined_ids.sort_unstable();
        assert_eq!(rejoined_ids, vec![1, 2, 3]);

        // Exact final command ordering from the three-client trace: follower 3
        // leaves first, rejoined user 1 submits Split, then leader 2 abandons.
        assert!(storage.remove_participants(2, 1, quick_match, vec![3]).unwrap());
        storage.leave_game_session(3, 1, quick_match).unwrap();
        assert!(storage.migrate_game_session_host(1, 1, quick_match).unwrap());
        storage.abandon_game_session(2, 1, quick_match).unwrap();

        assert!(!storage.game_session_is_active(1, quick_match).unwrap());
        let restored_lobby = run(
            sqlx::query_scalar::<_, u32>("SELECT session_id FROM invites WHERE receiver = 2 AND kind = ? ORDER BY id DESC LIMIT 1")
                .bind(INVITE_KIND_LOBBY_RESTORE)
                .fetch_one(&storage.pool),
        )
        .unwrap()
        .unwrap();
        let (_, _, restored_members) = storage.game_session_diagnostics(1, restored_lobby).unwrap().unwrap();
        assert_eq!(restored_members, vec![1, 2, 3]);
        let (restored_leader, restored_roster) = run(storage.find_party_roster_async(3)).unwrap().unwrap().unwrap();
        assert_eq!(restored_leader, 2);
        let mut restored_ids = restored_roster.into_iter().map(|member| member.id).collect::<Vec<_>>();
        restored_ids.sort_unstable();
        assert_eq!(restored_ids, vec![1, 2, 3]);
    }

    #[test]
    fn quick_match_successor_invites_a_new_member_without_reclaiming_the_original_leader() {
        let storage = create_test_storage("quick-match-successor-invites-new-member");
        insert_users(&storage, 4);
        for user_id in 1..=4 {
            storage
                .register_urls(user_id, vec![format!("prudp:/address=10.66.0.{user_id};port=13000;sid=37;type=2")])
                .unwrap();
        }

        let original_lobby = storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        for receiver in [2, 3] {
            let invite = run(storage.add_invite_async(1, receiver, INVITE_KIND_LOBBY_PARTY, 1, original_lobby))
                .unwrap()
                .unwrap()
                .unwrap();
            assert!(run(storage.accept_invite_async(receiver, invite)).unwrap().unwrap().is_some());
            assert!(storage.join_game_session_unbounded(receiver, 1, original_lobby).unwrap());
        }
        let quick_match = storage.create_game_session(1, 1, "113 => 0;3 => 12;4 => 0;103 => 4008726265".to_string()).unwrap();
        storage.add_participants(1, 1, quick_match, vec![2, 3], Vec::new()).unwrap();
        for user_id in 1..=3 {
            storage.record_published_game_session(user_id, u64::from(original_lobby));
        }

        storage.abandon_game_session(1, 1, quick_match).unwrap();
        assert!(storage.migrate_game_session_host(2, 1, quick_match).unwrap());
        storage.abandon_game_session(1, 1, original_lobby).unwrap();
        assert!(storage.migrate_game_session_host(3, 1, original_lobby).unwrap());
        let original_leader_lobby = storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        storage.record_published_game_session(1, u64::from(original_leader_lobby));
        storage.record_published_game_session(2, u64::from(original_lobby));
        storage.record_published_game_session(3, u64::from(original_lobby));

        let newcomer_lobby = storage.create_game_session(4, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        storage.record_published_game_session(4, u64::from(newcomer_lobby));
        let newcomer_invite = run(storage.add_invite_async(2, 4, INVITE_KIND_PRIVATE_ROOM, 1, quick_match)).unwrap().unwrap().unwrap();
        assert!(run(storage.accept_invite_async(4, newcomer_invite)).unwrap().unwrap().is_some());
        assert!(storage.join_game_session_unbounded(4, 1, quick_match).unwrap());

        let (current_leader, current_roster) = run(storage.find_party_roster_async(4)).unwrap().unwrap().unwrap();
        assert_eq!(current_leader, 2);
        let mut current_ids = current_roster.into_iter().map(|member| member.id).collect::<Vec<_>>();
        current_ids.sort_unstable();
        assert_eq!(current_ids, vec![2, 3, 4]);
        assert_eq!(run(storage.find_party_roster_async(1)).unwrap().unwrap().unwrap().1.len(), 1);

        assert!(storage.remove_participants(2, 1, quick_match, vec![3]).unwrap());
        storage.leave_game_session(3, 1, quick_match).unwrap();
        assert!(storage.migrate_game_session_host(4, 1, quick_match).unwrap());
        storage.abandon_game_session(2, 1, quick_match).unwrap();

        let restored_lobby = run(
            sqlx::query_scalar::<_, u32>("SELECT session_id FROM invites WHERE receiver = 2 AND kind = ? ORDER BY id DESC LIMIT 1")
                .bind(INVITE_KIND_LOBBY_RESTORE)
                .fetch_one(&storage.pool),
        )
        .unwrap()
        .unwrap();
        assert_eq!(storage.game_session_diagnostics(1, restored_lobby).unwrap().unwrap().2, vec![2, 3, 4]);
        let (restored_leader, restored_roster) = run(storage.find_party_roster_async(3)).unwrap().unwrap().unwrap();
        assert_eq!(restored_leader, 2);
        let mut restored_ids = restored_roster.into_iter().map(|member| member.id).collect::<Vec<_>>();
        restored_ids.sort_unstable();
        assert_eq!(restored_ids, vec![2, 3, 4]);
        assert_eq!(run(storage.find_party_roster_async(1)).unwrap().unwrap().unwrap().1.len(), 1);
    }

    #[test]
    fn quick_match_split_before_leader_abandon_restores_the_complete_n_member_party() {
        let storage = create_test_storage("quick-match-split-before-leader-abandon-n-members");
        insert_users(&storage, 4);
        for user_id in 1..=4 {
            storage
                .register_urls(user_id, vec![format!("prudp:/address=10.66.0.{user_id};port=13000;sid=35;type=2")])
                .unwrap();
        }

        let successor_lobby = storage.create_game_session(2, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        for receiver in [3, 4, 1] {
            let party_invite = run(storage.add_invite_async(2, receiver, INVITE_KIND_LOBBY_PARTY, 1, successor_lobby))
                .unwrap()
                .unwrap()
                .unwrap();
            assert!(run(storage.accept_invite_async(receiver, party_invite)).unwrap().unwrap().is_some());
            assert!(storage.join_game_session_unbounded(receiver, 1, successor_lobby).unwrap());
        }

        let quick_match = storage.create_game_session(2, 1, "113 => 0;3 => 12;4 => 0;103 => 4008726265".to_string()).unwrap();
        storage.add_participants(2, 1, quick_match, vec![1, 3, 4], Vec::new()).unwrap();
        for user_id in 1..=4 {
            storage.record_published_game_session(user_id, u64::from(successor_lobby));
        }

        assert!(storage.remove_participants(2, 1, quick_match, vec![4]).unwrap());
        storage.leave_game_session(4, 1, quick_match).unwrap();
        assert!(storage.migrate_game_session_host(1, 1, quick_match).unwrap());
        storage.abandon_game_session(2, 1, quick_match).unwrap();

        let restored_lobby = run(
            sqlx::query_scalar::<_, u32>("SELECT session_id FROM invites WHERE receiver = 2 AND kind = ? ORDER BY id DESC LIMIT 1")
                .bind(INVITE_KIND_LOBBY_RESTORE)
                .fetch_one(&storage.pool),
        )
        .unwrap()
        .unwrap();
        assert_eq!(
            storage.game_session_diagnostics(1, restored_lobby).unwrap().unwrap(),
            (2, "113 => 1;3 => 8;4 => 0".to_string(), vec![1, 2, 3, 4])
        );
        let (leader_id, roster) = run(storage.find_party_roster_async(3)).unwrap().unwrap().unwrap();
        assert_eq!(leader_id, 2);
        let mut member_ids = roster.into_iter().map(|member| member.id).collect::<Vec<_>>();
        member_ids.sort_unstable();
        assert_eq!(member_ids, vec![1, 2, 3, 4]);
    }

    #[test]
    fn quick_match_solo_member_leave_is_restored_after_the_coordination_window() {
        let storage = create_test_storage("quick-match-solo-member-leave");
        insert_users(&storage, 2);
        for user_id in 1..=2 {
            storage
                .register_urls(user_id, vec![format!("prudp:/address=10.66.0.{user_id};port=13000;sid=33;type=2")])
                .unwrap();
        }

        let shared_lobby = storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        let invite = run(storage.add_invite_async(1, 2, INVITE_KIND_LOBBY_PARTY, 1, shared_lobby)).unwrap().unwrap().unwrap();
        assert!(run(storage.accept_invite_async(2, invite)).unwrap().unwrap().is_some());
        assert!(storage.join_game_session_unbounded(2, 1, shared_lobby).unwrap());
        let quick_match = storage.create_game_session(1, 1, "113 => 0;3 => 12;4 => 0;103 => 4008726265".to_string()).unwrap();
        assert!(storage.join_game_session_unbounded(2, 1, quick_match).unwrap());

        storage.leave_game_session(2, 1, quick_match).unwrap();
        assert_eq!(run(storage.find_party_roster_async(2)).unwrap().unwrap().unwrap().0, 1);
        assert_eq!(expire_pending_quick_match_member_leaves_for_test(&storage), 1);

        let (leader_id, roster) = run(storage.find_party_roster_async(2)).unwrap().unwrap().unwrap();
        assert_eq!(leader_id, 2);
        assert_eq!(roster.into_iter().map(|member| member.id).collect::<Vec<_>>(), vec![2]);
    }

    #[test]
    fn accepted_normal_invite_suppresses_quick_match_lobby_restore_until_join_completes() {
        let storage = create_test_storage("accepted-invite-suppresses-quick-match-restore");
        insert_users(&storage, 3);
        for user_id in 1..=3 {
            storage
                .register_urls(user_id, vec![format!("prudp:/address=10.66.0.{user_id};port=13000;sid=35;type=2")])
                .unwrap();
        }

        storage.create_game_session(2, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        let departed_match = storage.create_game_session(1, 1, "113 => 0;3 => 12;4 => 0;103 => 4008726265".to_string()).unwrap();
        storage.add_participants(1, 1, departed_match, vec![2], Vec::new()).unwrap();
        let target_lobby = storage.create_game_session(3, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();

        storage.leave_game_session(2, 1, departed_match).unwrap();
        let invite_id = run(storage.add_invite_async(3, 2, INVITE_KIND_LOBBY_PARTY, 1, target_lobby)).unwrap().unwrap().unwrap();
        assert!(run(storage.accept_invite_async(2, invite_id)).unwrap().unwrap().is_some());

        // The creation entry point has the same guard as the expiry path, so
        // another caller cannot inject a competing kind-4 event mid-join.
        let direct_restore = run(async {
            let mut transaction = storage.begin_write().await?;
            let restore = Storage::queue_departed_member_lobby_restore_async(&mut transaction, 2, 1, departed_match).await?;
            transaction.rollback().await?;
            Ok::<Option<(u32, bool, u64)>, sqlx::Error>(restore)
        })
        .unwrap()
        .unwrap();
        assert!(direct_restore.is_none());

        assert_eq!(expire_pending_quick_match_member_leaves_for_test(&storage), 0);
        assert_eq!(storage.pending_quick_match_member_leaves(1, departed_match), vec![2]);
        let pending_restores: i64 = run(sqlx::query_scalar("SELECT COUNT(*) FROM invites WHERE receiver = 2 AND kind = ? AND consumed_at IS NULL")
            .bind(INVITE_KIND_LOBBY_RESTORE)
            .fetch_one(&storage.pool))
        .unwrap()
        .unwrap();
        assert_eq!(pending_restores, 0);

        assert!(storage.join_game_session_unbounded(2, 1, target_lobby).unwrap());
        assert_eq!(expire_pending_quick_match_member_leaves_for_test(&storage), 0);
        assert!(storage.pending_quick_match_member_leaves(1, departed_match).is_empty());
        let pending_restores: i64 = run(sqlx::query_scalar("SELECT COUNT(*) FROM invites WHERE receiver = 2 AND kind = ? AND consumed_at IS NULL")
            .bind(INVITE_KIND_LOBBY_RESTORE)
            .fetch_one(&storage.pool))
        .unwrap()
        .unwrap();
        assert_eq!(pending_restores, 0);
    }

    #[test]
    fn invalidated_normal_invite_releases_exactly_one_quick_match_lobby_restore() {
        let storage = create_test_storage("invalidated-invite-releases-quick-match-restore");
        insert_users(&storage, 3);
        for user_id in 1..=3 {
            storage
                .register_urls(user_id, vec![format!("prudp:/address=10.66.0.{user_id};port=13000;sid=36;type=2")])
                .unwrap();
        }

        storage.create_game_session(2, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        let departed_match = storage.create_game_session(1, 1, "113 => 0;3 => 12;4 => 0;103 => 4008726265".to_string()).unwrap();
        storage.add_participants(1, 1, departed_match, vec![2], Vec::new()).unwrap();
        let target_lobby = storage.create_game_session(3, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();

        storage.leave_game_session(2, 1, departed_match).unwrap();
        let invite_id = run(storage.add_invite_async(3, 2, INVITE_KIND_LOBBY_PARTY, 1, target_lobby)).unwrap().unwrap().unwrap();
        assert!(run(storage.accept_invite_async(2, invite_id)).unwrap().unwrap().is_some());
        assert_eq!(expire_pending_quick_match_member_leaves_for_test(&storage), 0);

        run(sqlx::query("UPDATE game_sessions SET destroyed_at = CURRENT_TIMESTAMP WHERE id = ?")
            .bind(target_lobby)
            .execute(&storage.pool))
        .unwrap()
        .unwrap();
        assert_eq!(expire_pending_quick_match_member_leaves_for_test(&storage), 1);

        let pending_restores: i64 = run(sqlx::query_scalar("SELECT COUNT(*) FROM invites WHERE receiver = 2 AND kind = ? AND consumed_at IS NULL")
            .bind(INVITE_KIND_LOBBY_RESTORE)
            .fetch_one(&storage.pool))
        .unwrap()
        .unwrap();
        assert_eq!(pending_restores, 1);
        assert!(storage.pending_quick_match_member_leaves(1, departed_match).is_empty());
    }

    #[test]
    fn quick_match_leave_with_party_reverse_order_restores_the_authoritative_party() {
        let storage = create_test_storage("quick-match-reverse-party-return");
        insert_users(&storage, 3);
        for user_id in 1..=3 {
            storage
                .register_urls(user_id, vec![format!("prudp:/address=10.66.0.{user_id};port=13000;sid=26;type=2")])
                .unwrap();
        }

        let shared_lobby = storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        for receiver in 2..=3 {
            let invite_id = run(storage.add_invite_async(1, receiver, INVITE_KIND_LOBBY_PARTY, 1, shared_lobby))
                .unwrap()
                .unwrap()
                .unwrap();
            assert!(run(storage.accept_invite_async(receiver, invite_id)).unwrap().unwrap().is_some());
            assert!(storage.join_game_session_unbounded(receiver, 1, shared_lobby).unwrap());
        }
        let quick_match = storage.create_game_session(1, 1, "113 => 0;3 => 12;4 => 0;103 => 4008726265".to_string()).unwrap();
        storage.add_participants(1, 1, quick_match, vec![2, 3], Vec::new()).unwrap();
        storage.record_published_game_session(1, u64::from(shared_lobby));

        // Retail sends the owner Abandon first. The elected host then removes
        // the Party members in later calls, which is the ordering seen in the
        // three-client quick-match trace.
        storage.abandon_game_session(1, 1, quick_match).unwrap();
        assert!(storage.migrate_game_session_host(3, 1, quick_match).unwrap());
        assert!(storage.remove_participants(3, 1, quick_match, vec![1]).unwrap());
        assert!(storage.remove_participants(3, 1, quick_match, vec![2]).unwrap());

        assert!(!storage.game_session_is_active(1, quick_match).unwrap());
        let restored_lobby = run(
            sqlx::query_scalar::<_, u32>("SELECT session_id FROM invites WHERE receiver = 1 AND kind = ? ORDER BY id DESC LIMIT 1")
                .bind(INVITE_KIND_LOBBY_RESTORE)
                .fetch_one(&storage.pool),
        )
        .unwrap()
        .unwrap();
        assert_ne!(restored_lobby, shared_lobby);
        assert!(!storage.game_session_is_active(1, shared_lobby).unwrap());
        assert_eq!(
            storage.game_session_diagnostics(1, restored_lobby).unwrap().unwrap(),
            (1, "113 => 1;3 => 8;4 => 0".to_string(), vec![1, 2, 3])
        );
        let (leader_id, roster) = run(storage.find_party_roster_async(3)).unwrap().unwrap().unwrap();
        assert_eq!(leader_id, 1);
        assert_eq!(roster.into_iter().map(|member| member.id).collect::<Vec<_>>(), vec![1, 2, 3]);
    }

    #[test]
    fn quick_match_party_return_does_not_merge_another_party_in_the_same_room() {
        let storage = create_test_storage("quick-match-multiple-party-return");
        insert_users(&storage, 5);
        for user_id in 1..=5 {
            storage
                .register_urls(user_id, vec![format!("prudp:/address=10.66.0.{user_id};port=13000;sid=27;type=2")])
                .unwrap();
        }

        let party_a_lobby = storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        for receiver in 2..=3 {
            let invite_id = run(storage.add_invite_async(1, receiver, INVITE_KIND_LOBBY_PARTY, 1, party_a_lobby))
                .unwrap()
                .unwrap()
                .unwrap();
            assert!(run(storage.accept_invite_async(receiver, invite_id)).unwrap().unwrap().is_some());
            assert!(storage.join_game_session_unbounded(receiver, 1, party_a_lobby).unwrap());
        }
        let party_b_lobby = storage.create_game_session(4, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        let party_b_invite = run(storage.add_invite_async(4, 5, INVITE_KIND_LOBBY_PARTY, 1, party_b_lobby)).unwrap().unwrap().unwrap();
        assert!(run(storage.accept_invite_async(5, party_b_invite)).unwrap().unwrap().is_some());
        assert!(storage.join_game_session_unbounded(5, 1, party_b_lobby).unwrap());

        let quick_match = storage.create_game_session(1, 1, "113 => 0;3 => 12;4 => 0;103 => 4008726265".to_string()).unwrap();
        storage.add_participants(1, 1, quick_match, vec![2, 3, 4, 5], Vec::new()).unwrap();
        storage.record_published_game_session(1, u64::from(party_a_lobby));
        storage.abandon_game_session(1, 1, quick_match).unwrap();
        assert!(storage.migrate_game_session_host(4, 1, quick_match).unwrap());
        assert!(storage.remove_participants(4, 1, quick_match, vec![1]).unwrap());
        assert!(storage.remove_participants(4, 1, quick_match, vec![2]).unwrap());

        let (creator_id, _, remaining_match_members) = storage.game_session_diagnostics(1, quick_match).unwrap().unwrap();
        assert_eq!(creator_id, 4);
        assert_eq!(remaining_match_members, vec![4, 5]);
        let (party_a_leader, party_a_roster) = run(storage.find_party_roster_async(3)).unwrap().unwrap().unwrap();
        assert_eq!(party_a_leader, 1);
        assert_eq!(party_a_roster.into_iter().map(|member| member.id).collect::<Vec<_>>(), vec![1, 2, 3]);
        let restored_party_a_lobby = run(
            sqlx::query_scalar::<_, u32>("SELECT session_id FROM invites WHERE receiver = 1 AND kind = ? ORDER BY id DESC LIMIT 1")
                .bind(INVITE_KIND_LOBBY_RESTORE)
                .fetch_one(&storage.pool),
        )
        .unwrap()
        .unwrap();
        assert_ne!(restored_party_a_lobby, party_a_lobby);
        assert_eq!(storage.game_session_diagnostics(1, restored_party_a_lobby).unwrap().unwrap().2, vec![1, 2, 3]);
        assert_eq!(storage.game_session_diagnostics(1, party_b_lobby).unwrap().unwrap().2, vec![4, 5]);
        let (party_b_leader, party_b_roster) = run(storage.find_party_roster_async(5)).unwrap().unwrap().unwrap();
        assert_eq!(party_b_leader, 4);
        assert_eq!(party_b_roster.into_iter().map(|member| member.id).collect::<Vec<_>>(), vec![4, 5]);
    }

    #[test]
    fn quick_match_host_leaving_alone_transfers_only_their_own_party_leadership() {
        let storage = create_test_storage("quick-match-host-leaves-own-party");
        insert_users(&storage, 4);
        for user_id in 1..=4 {
            storage
                .register_urls(user_id, vec![format!("prudp:/address=10.66.0.{user_id};port=13000;sid=31;type=2")])
                .unwrap();
        }

        let party_a_lobby = storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        let party_a_invite = run(storage.add_invite_async(1, 2, INVITE_KIND_LOBBY_PARTY, 1, party_a_lobby)).unwrap().unwrap().unwrap();
        assert!(run(storage.accept_invite_async(2, party_a_invite)).unwrap().unwrap().is_some());
        assert!(storage.join_game_session_unbounded(2, 1, party_a_lobby).unwrap());

        let party_b_lobby = storage.create_game_session(3, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        let party_b_invite = run(storage.add_invite_async(3, 4, INVITE_KIND_LOBBY_PARTY, 1, party_b_lobby)).unwrap().unwrap().unwrap();
        assert!(run(storage.accept_invite_async(4, party_b_invite)).unwrap().unwrap().is_some());
        assert!(storage.join_game_session_unbounded(4, 1, party_b_lobby).unwrap());

        let quick_match = storage.create_game_session(1, 1, "113 => 0;3 => 12;4 => 0;103 => 4008726265".to_string()).unwrap();
        storage.add_participants(1, 1, quick_match, vec![2, 3, 4], Vec::new()).unwrap();
        storage.record_published_game_session(1, u64::from(party_a_lobby));

        // User 1 is both the quick-match host and Party A leader, but leaves
        // alone. The game may elect user 3 as the room host; Party A must
        // independently elect its own remaining member, user 2.
        storage.abandon_game_session(1, 1, quick_match).unwrap();
        assert!(storage.migrate_game_session_host(3, 1, quick_match).unwrap());
        assert!(storage.remove_participants(3, 1, quick_match, vec![1]).unwrap());

        let (room_host, _, room_members) = storage.game_session_diagnostics(1, quick_match).unwrap().unwrap();
        assert_eq!(room_host, 3);
        assert_eq!(room_members, vec![2, 3, 4]);

        let (party_a_leader, party_a_roster) = run(storage.find_party_roster_async(2)).unwrap().unwrap().unwrap();
        assert_eq!(party_a_leader, 2);
        assert_eq!(party_a_roster.into_iter().map(|member| member.id).collect::<Vec<_>>(), vec![2]);

        let (party_b_leader, party_b_roster) = run(storage.find_party_roster_async(4)).unwrap().unwrap().unwrap();
        assert_eq!(party_b_leader, 3);
        assert_eq!(party_b_roster.into_iter().map(|member| member.id).collect::<Vec<_>>(), vec![3, 4]);
    }

    #[test]
    fn lobby_party_follows_leader_into_quick_match_hosted_by_another_user() {
        let storage = create_test_storage("party-follow-quick-match");
        insert_users(&storage, 4);
        for user_id in 1..=4 {
            storage
                .register_urls(user_id, vec![format!("prudp:/address=10.66.0.{user_id};port=13000;sid=14;type=2")])
                .unwrap();
        }
        let lobby_id = storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        let invite_id = run(storage.add_invite_async(1, 2, INVITE_KIND_LOBBY_PARTY, 1, lobby_id)).unwrap().unwrap().unwrap();
        assert!(run(storage.take_invite_async(2)).unwrap().unwrap().is_some());
        assert!(run(storage.accept_invite_async(2, invite_id)).unwrap().unwrap().is_some());
        assert!(storage.join_game_session_unbounded(2, 1, lobby_id).unwrap());

        let match_id = storage.create_game_session(4, 1, "113 => 0;103 => 0;112 => 4".to_string()).unwrap();
        assert!(storage.join_game_session_unbounded(1, 1, match_id).unwrap());
        run(async {
            sqlx::query("UPDATE invites SET created = datetime('now', '-3 seconds') WHERE kind = ?")
                .bind(INVITE_KIND_PARTY_FOLLOW)
                .execute(&storage.pool)
                .await
        })
        .unwrap()
        .unwrap();

        let follow = run(storage.take_invite_async(2)).unwrap().unwrap().unwrap();
        assert_eq!(follow.kind, INVITE_KIND_PARTY_FOLLOW);
        assert_eq!(follow.sender, 1);
        assert!(run(storage.accept_invite_async(2, follow.id)).unwrap().unwrap().is_some());
        let target = run(storage.find_accepted_invited_session_async(2, 1)).unwrap().unwrap().unwrap();
        assert_eq!(target.session.session_id, match_id);
        assert_eq!(target.session.creator_id, 4);
        assert!(storage.join_game_session_unbounded(2, 1, match_id).unwrap());

        let (_, _, participant_ids) = storage.game_session_diagnostics(1, match_id).unwrap().unwrap();
        assert_eq!(participant_ids, vec![1, 2, 4]);
    }

    #[test]
    fn native_party_follow_suppresses_redundant_fallback_invite() {
        let storage = create_test_storage("party-follow-native-suppression");
        insert_users(&storage, 2);
        for user_id in 1..=2 {
            storage
                .register_urls(user_id, vec![format!("prudp:/address=10.66.0.{user_id};port=13000;sid=14;type=2")])
                .unwrap();
        }
        let lobby_id = storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        let lobby_invite = run(storage.add_invite_async(1, 2, INVITE_KIND_LOBBY_PARTY, 1, lobby_id)).unwrap().unwrap().unwrap();
        assert!(run(storage.accept_invite_async(2, lobby_invite)).unwrap().unwrap().is_some());
        assert!(storage.join_game_session_unbounded(2, 1, lobby_id).unwrap());

        let private_id = storage.create_game_session(1, 1, "113 => 0;103 => 0;112 => 4".to_string()).unwrap();
        assert!(storage.join_game_session_unbounded(2, 1, private_id).unwrap());
        assert!(run(storage.take_invite_async(2)).unwrap().unwrap().is_none());

        let (remaining,): (i64,) = run(sqlx::query_as("SELECT COUNT(*) FROM invites WHERE receiver = 2 AND kind = ?")
            .bind(INVITE_KIND_PARTY_FOLLOW)
            .fetch_one(&storage.pool))
        .unwrap()
        .unwrap();
        assert_eq!(remaining, 0);
    }

    #[test]
    fn manual_and_party_follow_invites_use_independent_lanes() {
        let storage = create_test_storage("invite-lane-isolation");
        insert_users(&storage, 3);
        for user_id in 1..=3 {
            storage
                .register_urls(user_id, vec![format!("prudp:/address=10.66.0.{user_id};port=13000;sid=14;type=2")])
                .unwrap();
        }

        let lobby_id = storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        let lobby_invite = run(storage.add_invite_async(1, 2, INVITE_KIND_LOBBY_PARTY, 1, lobby_id)).unwrap().unwrap().unwrap();
        assert!(run(storage.accept_invite_async(2, lobby_invite)).unwrap().unwrap().is_some());
        assert!(storage.join_game_session_unbounded(2, 1, lobby_id).unwrap());

        let friend_room = storage.create_game_session(3, 1, "113 => 0;103 => 0;112 => 4".to_string()).unwrap();
        assert!(run(storage.add_invite_async(3, 2, INVITE_KIND_PRIVATE_ROOM, 1, friend_room)).unwrap().unwrap().is_some());

        let leader_room = storage.create_game_session(1, 1, "113 => 0;103 => 0;112 => 4".to_string()).unwrap();
        let pending =
            run(sqlx::query_as::<_, (i32, u32)>("SELECT kind, session_id FROM invites WHERE receiver = 2 AND consumed_at IS NULL ORDER BY kind").fetch_all(&storage.pool))
                .unwrap()
                .unwrap();
        assert_eq!(pending, vec![(INVITE_KIND_PRIVATE_ROOM, friend_room), (INVITE_KIND_PARTY_FOLLOW, leader_room)]);

        assert!(run(storage.add_invite_async(3, 2, INVITE_KIND_PRIVATE_ROOM, 1, friend_room)).unwrap().unwrap().is_some());
        let pending =
            run(sqlx::query_as::<_, (i32, u32)>("SELECT kind, session_id FROM invites WHERE receiver = 2 AND consumed_at IS NULL ORDER BY kind").fetch_all(&storage.pool))
                .unwrap()
                .unwrap();
        assert_eq!(pending, vec![(INVITE_KIND_PRIVATE_ROOM, friend_room), (INVITE_KIND_PARTY_FOLLOW, leader_room)]);
    }

    #[test]
    fn duplicate_invite_target_detects_same_party_and_same_room() {
        let storage = create_test_storage("duplicate-invite-target");
        insert_users(&storage, 2);
        storage.register_urls(1, vec!["prudp:/address=10.66.0.1;port=13000;sid=14;type=2".to_string()]).unwrap();
        let lobby_id = storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        let lobby_invite = run(storage.add_invite_async(1, 2, INVITE_KIND_LOBBY_PARTY, 1, lobby_id)).unwrap().unwrap().unwrap();
        assert!(run(storage.accept_invite_async(2, lobby_invite)).unwrap().unwrap().is_some());
        assert!(storage.join_game_session_unbounded(2, 1, lobby_id).unwrap());
        assert!(run(storage.invite_target_already_contains_async(2, INVITE_KIND_LOBBY_PARTY, lobby_id)).unwrap().unwrap());

        let room_id = storage.create_game_session(1, 1, "113 => 0;103 => 0;112 => 4".to_string()).unwrap();
        assert!(!run(storage.invite_target_already_contains_async(2, INVITE_KIND_PRIVATE_ROOM, room_id)).unwrap().unwrap());
        assert!(storage.join_game_session_unbounded(2, 1, room_id).unwrap());
        assert!(run(storage.invite_target_already_contains_async(2, INVITE_KIND_PRIVATE_ROOM, room_id)).unwrap().unwrap());
    }

    #[test]
    fn restored_lobby_remove_before_split_keeps_the_authoritative_party() {
        let storage = create_test_storage("restored-lobby-remove-before-split");
        insert_users(&storage, 3);
        for user_id in 1..=3 {
            storage
                .register_urls(user_id, vec![format!("prudp:/address=10.66.0.{user_id};port=13000;sid=41;type=2")])
                .unwrap();
        }

        let original_lobby = storage.create_game_session(3, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        for receiver in [2, 1] {
            let invite = run(storage.add_invite_async(3, receiver, INVITE_KIND_LOBBY_PARTY, 1, original_lobby))
                .unwrap()
                .unwrap()
                .unwrap();
            assert!(run(storage.accept_invite_async(receiver, invite)).unwrap().unwrap().is_some());
            assert!(storage.join_game_session_unbounded(receiver, 1, original_lobby).unwrap());
        }

        let restored_lobby = storage.create_game_session(3, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        let party_id = run(sqlx::query_scalar::<_, i64>("SELECT party_id FROM party_members WHERE user_id = 3").fetch_one(&storage.pool))
            .unwrap()
            .unwrap();
        for receiver in [2, 1] {
            let restore = run(sqlx::query(
                "INSERT INTO invites (sender, receiver, kind, session_type, session_id, party_id, expires_at) \
                     VALUES (3, ?, ?, 1, ?, ?, datetime('now', '+5 minutes'))",
            )
            .bind(receiver)
            .bind(INVITE_KIND_LOBBY_RESTORE)
            .bind(restored_lobby)
            .bind(party_id)
            .execute(&storage.pool))
            .unwrap()
            .unwrap()
            .last_insert_rowid();
            assert!(run(storage.accept_invite_async(receiver, restore)).unwrap().unwrap().is_some());
            assert!(storage.join_game_session_unbounded(receiver, 1, restored_lobby).unwrap());
        }

        assert!(storage.remove_participants(3, 1, restored_lobby, vec![1]).unwrap());
        assert_eq!(storage.game_session_diagnostics(1, restored_lobby).unwrap().unwrap().2, vec![1, 2, 3]);
        assert!(storage.migrate_game_session_host(1, 1, restored_lobby).unwrap());
        assert_eq!(
            storage.game_session_diagnostics(1, restored_lobby).unwrap().unwrap(),
            (3, "113 => 1;3 => 8;4 => 0".to_string(), vec![1, 2, 3])
        );
        assert!(!storage.remove_participants(1, 1, restored_lobby, vec![2, 3]).unwrap());
        assert_eq!(storage.game_session_diagnostics(1, restored_lobby).unwrap().unwrap().2, vec![1, 2, 3]);
        let (leader_id, roster) = run(storage.find_party_roster_async(1)).unwrap().unwrap().unwrap();
        assert_eq!(leader_id, 3);
        let mut roster_ids = roster.into_iter().map(|member| member.id).collect::<Vec<_>>();
        roster_ids.sort_unstable();
        assert_eq!(roster_ids, vec![1, 2, 3]);
    }

    #[test]
    fn restored_lobby_deferred_removal_falls_back_to_normal_party_removal_without_split() {
        let storage = create_test_storage("restored-lobby-deferred-removal-expiry");
        insert_users(&storage, 2);
        storage.register_urls(1, vec!["prudp:/address=10.66.0.1;port=13000;sid=42;type=2".to_string()]).unwrap();
        storage.register_urls(2, vec!["prudp:/address=10.66.0.2;port=13000;sid=42;type=2".to_string()]).unwrap();
        let original_lobby = storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        let party_invite = run(storage.add_invite_async(1, 2, INVITE_KIND_LOBBY_PARTY, 1, original_lobby)).unwrap().unwrap().unwrap();
        assert!(run(storage.accept_invite_async(2, party_invite)).unwrap().unwrap().is_some());
        assert!(storage.join_game_session_unbounded(2, 1, original_lobby).unwrap());

        let restored_lobby = storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        let party_id = run(sqlx::query_scalar::<_, i64>("SELECT party_id FROM party_members WHERE user_id = 1").fetch_one(&storage.pool))
            .unwrap()
            .unwrap();
        let restore = run(sqlx::query(
            "INSERT INTO invites (sender, receiver, kind, session_type, session_id, party_id, expires_at) \
                 VALUES (1, 2, ?, 1, ?, ?, datetime('now', '+5 minutes'))",
        )
        .bind(INVITE_KIND_LOBBY_RESTORE)
        .bind(restored_lobby)
        .bind(party_id)
        .execute(&storage.pool))
        .unwrap()
        .unwrap()
        .last_insert_rowid();
        assert!(run(storage.accept_invite_async(2, restore)).unwrap().unwrap().is_some());
        assert!(storage.join_game_session_unbounded(2, 1, restored_lobby).unwrap());
        assert!(storage.remove_participants(1, 1, restored_lobby, vec![2]).unwrap());
        assert_eq!(storage.game_session_diagnostics(1, restored_lobby).unwrap().unwrap().2, vec![1, 2]);

        storage.lobby_restore_split_transitions.lock().unwrap().get_mut(&(1, restored_lobby, 2)).unwrap().started_at =
            Instant::now() - LOBBY_RESTORE_SPLIT_TTL - Duration::from_millis(1);
        assert!(storage.lobby_restore_split_cleanup_due());
        assert_eq!(storage.expire_lobby_restore_split_transitions().unwrap(), 1);
        assert_eq!(storage.game_session_diagnostics(1, restored_lobby).unwrap().unwrap().2, vec![1]);
        let (leader_id, roster) = run(storage.find_party_roster_async(1)).unwrap().unwrap().unwrap();
        assert_eq!(leader_id, 1);
        assert_eq!(roster.into_iter().map(|member| member.id).collect::<Vec<_>>(), vec![1]);
        assert!(run(storage.find_party_roster_async(2)).unwrap().unwrap().is_none());
    }

    #[test]
    fn manual_invitation_search_intent_can_be_armed_and_cleared() {
        let storage = create_test_storage("manual-invite-search-intent");
        assert!(!storage.manual_invitation_search_is_active(2, 1));
        storage.note_manual_invitation_search(2, 1);
        assert!(storage.manual_invitation_search_is_active(2, 1));
        storage.clear_manual_invitation_search(2, 1);
        assert!(!storage.manual_invitation_search_is_active(2, 1));
    }

    #[test]
    fn password_reset_increments_auth_version_atomically() {
        let storage = create_test_storage("password-reset-auth-version");
        run(async {
            sqlx::query("INSERT INTO users (id, username, password_hash, ubi_id, recovery_code_hash) VALUES (424242, 'player', 'old-hash', 'account-1', 'recovery-v1')")
                .execute(&storage.pool)
                .await?;
            Ok::<(), sqlx::Error>(())
        })
        .unwrap()
        .unwrap();

        assert!(run(storage.reset_password_with_recovery_async("player", "recovery-v1", "new-password", "recovery-v2"))
            .unwrap()
            .unwrap());
        let identity = run(storage.find_auth_identity_by_user_id_async(424242)).unwrap().unwrap().unwrap();
        assert_eq!(identity.auth_version, 2);
        assert!(!run(storage.reset_password_with_recovery_async("player", "recovery-v1", "another-password", "recovery-v3"))
            .unwrap()
            .unwrap());
        let identity = run(storage.find_auth_identity_by_user_id_async(424242)).unwrap().unwrap().unwrap();
        assert_eq!(identity.auth_version, 2);
    }
}
