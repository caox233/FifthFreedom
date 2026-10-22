//! F9 edge/cooldown policy. No game clock, polling, damage or actor writes.
pub(super) const COOLDOWN_MS: u64 = 10_000;
const REQUEST_TTL_MS: u64 = 1_000;
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) struct Identity {
    pub viewport: usize,
    pub window: usize,
    pub controller: usize,
    pub pawn: usize,
    pub world: usize,
    pub controller_identity: u32,
    pub pawn_identity: u32,
    pub world_identity: u32,
}
#[derive(Debug)]
pub(super) struct State {
    held: bool,
    pending: Option<(Identity, u64)>,
    last_dispatch: Option<u64>,
}
impl State {
    pub const fn new() -> Self {
        Self { held: false, pending: None, last_dispatch: None }
    }
    fn cooled(&self, now: u64) -> bool {
        self.last_dispatch.map_or(true, |last| now.checked_sub(last).is_some_and(|d| d >= COOLDOWN_MS))
    }
    pub fn down(&mut self, current: Option<Identity>, now: u64, repeat: bool) {
        let fresh = !self.held && !repeat;
        self.held = true; // A blocked press cannot become a gameplay press later.
        if fresh && self.cooled(now) {
            self.pending = current.map(|id| (id, now));
        }
    }
    pub fn up(&mut self) { self.held = false; }
    pub fn suspend(&mut self) {
        self.pending = None;
        // Win32 previous-key-state bit still rejects a held key after focus loss.
        self.held = false;
        // Cooldown deliberately survives focus, menu, death and world changes.
    }
    pub fn pending_viewport(&self) -> Option<usize> { self.pending.map(|p| p.0.viewport) }
    pub fn take(&mut self, current: Option<Identity>, now: u64) -> Option<Identity> {
        let (id, pressed) = self.pending.take()?;
        if current != Some(id) || !now.checked_sub(pressed).is_some_and(|d| d <= REQUEST_TTL_MS)
            || !self.cooled(now) { return None; }
        self.last_dispatch = Some(now); // Reserve before entering any native call.
        Some(id)
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    fn id() -> Identity { Identity { viewport: 1, window: 2, controller: 3, pawn: 4, world: 5, controller_identity: 6, pawn_identity: 7, world_identity: 8 } }
    #[test] fn one_dispatch_per_physical_press_even_when_held_past_cooldown() {
        let mut s=State::new();s.down(Some(id()),100,false);assert_eq!(s.take(Some(id()),101),Some(id()));
        s.down(Some(id()),20_000,true);assert_eq!(s.take(Some(id()),20_001),None);
        s.down(Some(id()),20_002,false);assert_eq!(s.take(Some(id()),20_003),None);
        s.up();s.down(Some(id()),20_004,false);assert_eq!(s.take(Some(id()),20_005),Some(id()));
    }
    #[test] fn ten_seconds_is_monotonic_and_survives_respawn_or_focus() {
        let mut s=State::new();s.down(Some(id()),0,false);assert!(s.take(Some(id()),1).is_some());
        s.suspend();s.down(Some(id()),10_000,false);assert_eq!(s.take(Some(id()),10_000),None);
        s.up();s.down(Some(id()),10_001,false);assert!(s.take(Some(id()),10_001).is_some());
        s.suspend();s.down(Some(id()),0,false);assert_eq!(s.take(Some(id()),0),None);
    }
    #[test] fn blocked_press_never_arms_after_menu_chat_rename_dead_ob_or_focus_gate_opens() {
        for _gate in ["menu","chat","rename","dead","OB","unfocused","lobby"] {
            let mut s=State::new();s.down(None,100,false);
            assert_eq!(s.take(Some(id()),101),None);
            s.down(Some(id()),200,true);assert_eq!(s.take(Some(id()),201),None);
            s.up();s.down(Some(id()),202,false);assert!(s.take(Some(id()),203).is_some());
        }
    }
    #[test] fn identity_change_and_timeout_cancel_not_defer() {
        let mut s=State::new();s.down(Some(id()),100,false);
        let mut replacement=id();replacement.pawn+=1;
        assert_eq!(s.take(Some(replacement),101),None);assert_eq!(s.take(Some(id()),102),None);
        s.up();s.down(Some(id()),100,false);assert_eq!(s.take(Some(id()),1101),None);
        s.up();s.down(Some(id()),100,false);assert_eq!(s.take(Some(id()),99),None);
        s.up();s.down(Some(id()),100,false);assert_eq!(s.take(None,101),None);
    }
    #[test] fn same_address_identity_fingerprint_change_cancels_queued_request() {
        for which in 0..3 {
            let mut s=State::new();s.down(Some(id()),100,false);let mut replacement=id();
            match which {0=>replacement.controller_identity+=1,1=>replacement.pawn_identity+=1,_=>replacement.world_identity+=1}
            assert_eq!(s.take(Some(replacement),101),None);
            assert_eq!(s.take(Some(id()),102),None);
        }
    }
    #[test] fn focus_loss_does_not_replay_held_key_or_pending_request() {
        let mut s=State::new();s.down(Some(id()),100,false);s.suspend();assert_eq!(s.take(Some(id()),101),None);
        s.down(Some(id()),102,true);assert_eq!(s.take(Some(id()),103),None);
        s.up();s.down(Some(id()),104,false);assert!(s.take(Some(id()),105).is_some());
    }
    #[test] fn release_before_frame_still_dispatches_once() {
        let mut s=State::new();s.down(Some(id()),100,false);s.up();
        assert!(s.take(Some(id()),101).is_some());assert_eq!(s.take(Some(id()),102),None);
    }
}
