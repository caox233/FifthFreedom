use std::collections::HashMap;
use std::path::PathBuf;
use std::time::SystemTime;
use std::time::UNIX_EPOCH;

use serde::Deserialize;

const DEFAULT_STATE_PATH: &str = "/var/lib/scbl/runtime/gameplay-modes.json";
const UNAVAILABLE_READ_FAILED: &str = "player mode state unavailable: snapshot read failed";
const UNAVAILABLE_INVALID: &str = "player mode state unavailable: snapshot invalid";
const UNAVAILABLE_SOURCE_MISSING: &str = "player mode state unavailable: source missing";
const UNAVAILABLE_TARGET_MISSING: &str = "player mode state unavailable: target missing";
const UNAVAILABLE_SOURCE_STALE: &str = "player mode state unavailable: source stale";
const UNAVAILABLE_TARGET_STALE: &str = "player mode state unavailable: target stale";
const UNAVAILABLE_MODE_INVALID: &str = "player mode state unavailable: mode invalid";

#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct GameplayModeSnapshot {
    #[allow(dead_code)]
    schema_version: u32,
    ttl_seconds: u64,
    players: HashMap<String, GameplayModeState>,
}

#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct GameplayModeState {
    gameplay_mode: String,
    gameplay_integrity_valid: bool,
    last_seen_unix_ms: u64,
}

fn state_path() -> PathBuf {
    std::env::var_os("SCBL_GAMEPLAY_MODE_STATE_PATH").map_or_else(|| PathBuf::from(DEFAULT_STATE_PATH), PathBuf::from)
}

fn now_unix_ms() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_or(0, |duration| u64::try_from(duration.as_millis()).unwrap_or(u64::MAX))
}

fn states_compatible(source: &GameplayModeState, target: &GameplayModeState) -> Result<(), &'static str> {
    if !matches!(source.gameplay_mode.as_str(), "official" | "mod") || !matches!(target.gameplay_mode.as_str(), "official" | "mod") {
        return Err(UNAVAILABLE_MODE_INVALID);
    }
    if !source.gameplay_mode.eq_ignore_ascii_case(&target.gameplay_mode) {
        return Err("gameplay mode mismatch");
    }
    if source.gameplay_mode == "mod" && (!source.gameplay_integrity_valid || !target.gameplay_integrity_valid) {
        return Err("mod files verification failed");
    }
    Ok(())
}

fn current_state<'a>(
    snapshot: &'a GameplayModeSnapshot,
    username: &str,
    now_ms: u64,
    missing_reason: &'static str,
    stale_reason: &'static str,
) -> Result<&'a GameplayModeState, &'static str> {
    let key = username.trim().to_lowercase();
    let state = snapshot.players.get(&key).ok_or(missing_reason)?;
    let cutoff = now_ms.saturating_sub(snapshot.ttl_seconds.saturating_mul(1_000));
    if state.last_seen_unix_ms < cutoff {
        return Err(stale_reason);
    }
    Ok(state)
}

pub fn ensure_compatible(source_username: &str, target_username: &str, _context: &str) -> Result<(), &'static str> {
    let bytes = std::fs::read(state_path()).map_err(|_| UNAVAILABLE_READ_FAILED)?;
    let snapshot: GameplayModeSnapshot = serde_json::from_slice(&bytes).map_err(|_| UNAVAILABLE_INVALID)?;
    let now = now_unix_ms();
    let source = current_state(&snapshot, source_username, now, UNAVAILABLE_SOURCE_MISSING, UNAVAILABLE_SOURCE_STALE)?;
    let target = current_state(&snapshot, target_username, now, UNAVAILABLE_TARGET_MISSING, UNAVAILABLE_TARGET_STALE)?;
    states_compatible(source, target)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn state(mode: &str, valid: bool) -> GameplayModeState {
        GameplayModeState {
            gameplay_mode: mode.to_owned(),
            gameplay_integrity_valid: valid,
            last_seen_unix_ms: now_unix_ms(),
        }
    }

    #[test]
    fn multiplayer_separates_official_and_mod_players() {
        assert_eq!(states_compatible(&state("official", true), &state("mod", true)), Err("gameplay mode mismatch"));
        assert!(states_compatible(&state("official", true), &state("official", true)).is_ok());
    }

    #[test]
    fn mod_mode_requires_valid_integrity_for_both_players() {
        assert_eq!(states_compatible(&state("mod", true), &state("mod", false)), Err("mod files verification failed"));
    }

    #[test]
    fn legacy_modes_are_rejected() {
        assert_eq!(states_compatible(&state("casual", true), &state("casual", true)), Err(UNAVAILABLE_MODE_INVALID));
    }

    #[test]
    fn unavailable_reasons_distinguish_missing_and_stale_players() {
        let now = now_unix_ms();
        let mut players = HashMap::new();
        players.insert(
            "source".to_owned(),
            GameplayModeState {
                gameplay_mode: "official".to_owned(),
                gameplay_integrity_valid: true,
                last_seen_unix_ms: now.saturating_sub(91_000),
            },
        );
        let snapshot = GameplayModeSnapshot {
            schema_version: 1,
            ttl_seconds: 90,
            players,
        };
        assert!(matches!(
            current_state(&snapshot, "source", now, UNAVAILABLE_SOURCE_MISSING, UNAVAILABLE_SOURCE_STALE,),
            Err(UNAVAILABLE_SOURCE_STALE)
        ));
        assert!(matches!(
            current_state(&snapshot, "target", now, UNAVAILABLE_TARGET_MISSING, UNAVAILABLE_TARGET_STALE,),
            Err(UNAVAILABLE_TARGET_MISSING)
        ));
    }
}
