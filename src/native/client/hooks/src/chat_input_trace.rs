//! Manual F4 diagnostics: fixed scalar fields only, never input text/key values.
//! No background timer, keyboard polling, context ownership or game behavior changes.
use std::sync::atomic::{AtomicBool, AtomicU32, AtomicU64, Ordering};
static ACTIVE: AtomicBool = AtomicBool::new(false);
static COUNT: AtomicU32 = AtomicU32::new(0);
static START_MS: AtomicU64 = AtomicU64::new(0);
pub(crate) const LIMIT: u32 = 96;
const MAX_MS: u64 = 120_000;

#[link(name = "kernel32")]
extern "system" {
    fn GetTickCount64() -> u64;
}

fn within_window(start: u64, now: u64) -> bool {
    now.checked_sub(start)
        .is_some_and(|elapsed| elapsed < MAX_MS)
}

fn running_at(now: u64) -> bool {
    if !ACTIVE.load(Ordering::Acquire) {
        return false;
    }
    if !within_window(START_MS.load(Ordering::Acquire), now) {
        ACTIVE.store(false, Ordering::Release);
        return false;
    }
    true
}

fn running() -> bool {
    ACTIVE.load(Ordering::Acquire) && running_at(unsafe { GetTickCount64() })
}

#[derive(Clone, Copy, Debug)]
pub(crate) enum Stage {
    Open,
    Close,
    Resume,
    ImeStart,
    ImeEnd,
    ImeResult,
    Commit,
}

// Pure gate shared by production and the isolated diagnostic contract.
pub(crate) fn control(message: u32, key: usize, flags: isize, active: bool) -> Option<bool> {
    (message == 0x100 && key == 0x73 && flags as usize & (1 << 30) == 0).then_some(!active)
}
pub(crate) fn window(message: u32, key: usize, flags: isize) {
    // Raw mouse reports and ordinary keys must not touch diagnostic atomics.
    if message != 0x100 || key != 0x73 || flags as usize & (1 << 30) != 0 {
        return;
    }
    if let Some(active) = control(message, key, flags, running()) {
        if active {
            COUNT.store(0, Ordering::Release);
            START_MS.store(unsafe { GetTickCount64() }, Ordering::Release);
        }
        ACTIVE.store(active, Ordering::Release);
        tracing::info!(
            "SCBL_CHAT_IME_DIAG active={} events={} limit={} max_ms={} text_logged=false",
            active,
            COUNT.load(Ordering::Acquire),
            LIMIT,
            MAX_MS
        );
    }
}
pub(crate) fn active() -> bool {
    running() && COUNT.load(Ordering::Acquire) < LIMIT
}
pub(crate) fn record(stage: Stage, fields: [u32; 9]) -> bool {
    if !running() {
        return false;
    }
    if COUNT
        .fetch_update(Ordering::AcqRel, Ordering::Acquire, |n| {
            (n < LIMIT).then_some(n + 1)
        })
        .is_err()
    {
        return false;
    }
    tracing::info!("SCBL_CHAT_IME_DIAG stage={:?} known={} mode={} visible={} locked={} window_bound={} focus_matches={} owned_context={} associated={} units={}", stage, fields[0], fields[1], fields[2], fields[3], fields[4], fields[5], fields[6], fields[7], fields[8]);
    true
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn manual_f4_requires_fresh_keydown_and_has_a_hard_event_cap() {
        assert_eq!(control(0x100, 0x73, 0, false), Some(true));
        assert_eq!(control(0x100, 0x73, 0, true), Some(false));
        for (msg, key, flags) in [
            (0x101, 0x73, 0),
            (0x100, 0x73, 1 << 30),
            (0xff, 0, 0),
            (0x200, 0, 0),
            (0x100, 0x41, 0),
        ] {
            assert_eq!(control(msg, key, flags, false), None);
        }
        ACTIVE.store(false, Ordering::Release);
        COUNT.store(0, Ordering::Release);
        assert!(!record(Stage::Open, [0; 9]));
        window(0x100, 0x73, 0);
        for _ in 0..LIMIT {
            assert!(record(Stage::Open, [0; 9]));
        }
        assert!(!active());
        assert!(!record(Stage::Commit, [0; 9]));
        window(0x100, 0x73, 0);
        assert!(!record(Stage::Close, [0; 9]));
        window(0x100, 0x73, 0);
        assert!(active());
        assert!(record(Stage::Resume, [0; 9]));
        window(0x100, 0x73, 0);
        assert!(!active());
        window(0x100, 0x73, 0);
        let start = START_MS.load(Ordering::Acquire);
        assert!(running_at(start + MAX_MS - 1));
        assert!(!running_at(start + MAX_MS));
        assert!(!record(Stage::Commit, [0; 9]));
        window(0x100, 0x73, 0);
        assert!(active());
        window(0x100, 0x73, 0);
        assert!(!active());
    }

    #[test]
    fn elapsed_time_is_bounded_without_background_sampling() {
        assert!(within_window(0, 0));
        assert!(within_window(123, 123 + MAX_MS - 1));
        assert!(!within_window(123, 123 + MAX_MS));
        assert!(!within_window(123, 122));
        assert!(!within_window(123, u64::MAX));
    }
}
