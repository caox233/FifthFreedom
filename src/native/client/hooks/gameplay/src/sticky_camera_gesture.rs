//! Deployment-button gestures after an owned MP sticky camera is deployed.
//! Native adapter supplies validated scene/device/owner identity fingerprints and semantic
//! key edges. This policy neither polls a physical mouse button nor owns actors.
//! A tap waits for the double-click deadline; detonation never emits a decoy.

pub const DOUBLE_MS: u64 = 250;
pub const SHOT_MS: u64 = 70; // retail ADV_Spy_SMG_Scorpion rate: 0.07 seconds

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Owner {
    pub world: [u32; 2],
    pub pawn: [u32; 2],
    pub camera: [u32; 2],
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Edge { Press, Release, Tick }
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Command { Shot, Detonate }
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Decision { pub consume: bool, pub command: Option<Command> }
const PASS: Decision = Decision { consume: false, command: None };
const TAKE: Decision = Decision { consume: true, command: None };

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Phase {
    Idle,
    FirstDown { at: u64 },
    FirstUp { at: u64 },
    Holding { next: u64 },
    Detonated,
}
#[derive(Clone, Copy, Debug)]
pub struct Gestures { owner: Option<Owner>, phase: Phase, clock: u64 }
impl Default for Gestures {
    fn default() -> Self { Self { owner: None, phase: Phase::Idle, clock: 0 } }
}
impl Gestures {
    pub fn owner(&self) -> Option<Owner> { self.owner }
    pub fn cancel(&mut self) { *self = Self::default(); }
    pub fn step(&mut self, owner: Option<Owner>, edge: Edge, now: u64) -> Decision {
        // Focus, text entry, death, selected-gadget changes, destruction and
        // new scenes all arrive as invalid or changed owners. Never replay an
        // old gesture against a replacement gadget or a reused actor address.
        if owner.is_none() { self.cancel(); return PASS; }
        if self.owner != owner || now < self.clock {
            self.cancel(); self.owner = owner;
        }
        self.clock = now;
        let emit = |command| Decision { consume: true, command: Some(command) };
        match (self.phase, edge) {
            (Phase::Idle, Edge::Press) => {
                self.phase = Phase::FirstDown { at: now }; TAKE
            }
            (Phase::Idle, _) => PASS,
            (Phase::FirstDown { at }, Edge::Release) => {
                if now >= at.saturating_add(DOUBLE_MS) {
                    self.phase = Phase::Idle; emit(Command::Shot)
                } else { self.phase = Phase::FirstUp { at }; TAKE }
            }
            (Phase::FirstDown { at }, Edge::Tick) if now >= at.saturating_add(DOUBLE_MS) => {
                self.phase = Phase::Holding { next: now.saturating_add(SHOT_MS) };
                emit(Command::Shot)
            }
            (Phase::FirstUp { at }, Edge::Press) if now <= at.saturating_add(DOUBLE_MS) => {
                self.phase = Phase::Detonated; emit(Command::Detonate)
            }
            (Phase::FirstUp { at }, _) if now > at.saturating_add(DOUBLE_MS) => {
                self.phase = if edge == Edge::Press { Phase::FirstDown { at: now } } else { Phase::Idle };
                emit(Command::Shot)
            }
            (Phase::Holding { .. }, Edge::Release) => { self.phase = Phase::Idle; TAKE }
            (Phase::Holding { next }, Edge::Tick) if now >= next => {
                // A stalled frame sends at most one shot, never a catch-up burst.
                self.phase = Phase::Holding { next: now.saturating_add(SHOT_MS) };
                emit(Command::Shot)
            }
            (Phase::Detonated, Edge::Release) => { self.phase = Phase::Idle; TAKE }
            _ => TAKE,
        }
    }
}

#[cfg(test)] mod tests {
    use super::*;
    const O: Owner = Owner { world: [1, 1], pawn: [2, 1], camera: [3, 1] };
    fn step(g: &mut Gestures, e: Edge, t: u64) -> Option<Command> { g.step(Some(O), e, t).command }
    #[test] fn tap_is_exactly_one_and_double_is_only_detonation() {
        let mut g = Gestures::default();
        assert_eq!(step(&mut g, Edge::Press, 10), None);
        assert_eq!(step(&mut g, Edge::Release, 30), None);
        assert_eq!(step(&mut g, Edge::Tick, 260), None);
        assert_eq!(step(&mut g, Edge::Tick, 261), Some(Command::Shot));
        assert_eq!(step(&mut g, Edge::Tick, 300), None);
        assert_eq!(step(&mut g, Edge::Press, 400), None);
        assert_eq!(step(&mut g, Edge::Release, 420), None);
        assert_eq!(step(&mut g, Edge::Press, 650), Some(Command::Detonate));
        assert_eq!(step(&mut g, Edge::Tick, 1000), None);
        assert_eq!(step(&mut g, Edge::Release, 1010), None);
        assert_eq!(step(&mut g, Edge::Tick, 2000), None);
    }
    #[test] fn hold_has_a_bound_rate_no_burst_no_release_tail() {
        let mut g = Gestures::default(); step(&mut g, Edge::Press, 100);
        assert_eq!(step(&mut g, Edge::Press, 200), None); // OS repeat is not double
        assert_eq!(step(&mut g, Edge::Tick, 350), Some(Command::Shot));
        assert_eq!(step(&mut g, Edge::Tick, 419), None);
        assert_eq!(step(&mut g, Edge::Tick, 420), Some(Command::Shot));
        assert_eq!(step(&mut g, Edge::Tick, 10000), Some(Command::Shot));
        assert_eq!(step(&mut g, Edge::Tick, 10000), None);
        assert_eq!(step(&mut g, Edge::Release, 10001), None);
        assert_eq!(step(&mut g, Edge::Tick, 20000), None);
    }
    #[test] fn invalid_context_and_replaced_owner_cancel_pending_shot() {
        let mut g = Gestures::default(); step(&mut g, Edge::Press, 1);
        assert_eq!(g.step(None, Edge::Tick, 2), PASS);
        assert_eq!(step(&mut g, Edge::Release, 300), None);
        step(&mut g, Edge::Press, 400);
        let other = Owner { camera: [3, 2], ..O };
        assert_eq!(g.step(Some(other), Edge::Tick, 700), PASS);
        assert_eq!(g.step(Some(other), Edge::Release, 800), PASS);
        step(&mut g, Edge::Press, 900);
        assert_eq!(step(&mut g, Edge::Tick, 1), None); // clock reset is not elapsed
    }
    #[test] fn deployment_and_unrelated_release_never_arm() {
        let mut g = Gestures::default();
        assert_eq!(g.step(None, Edge::Press, 0), PASS);
        assert_eq!(g.step(Some(O), Edge::Release, 300), PASS);
        assert_eq!(step(&mut g, Edge::Tick, 1000), None);
        step(&mut g, Edge::Press, 1100);
        assert_eq!(step(&mut g, Edge::Release, 1400), Some(Command::Shot));
        assert_eq!(step(&mut g, Edge::Tick, 1500), None);
    }
    #[test] fn two_slow_taps_and_repeated_releases_do_not_detonate() {
        let mut g = Gestures::default(); step(&mut g, Edge::Press, 0);
        step(&mut g, Edge::Release, 10);
        assert_eq!(step(&mut g, Edge::Press, 251), Some(Command::Shot));
        step(&mut g, Edge::Release, 260); step(&mut g, Edge::Release, 270);
        assert_eq!(step(&mut g, Edge::Tick, 502), Some(Command::Shot));
        assert_eq!(step(&mut g, Edge::Tick, 503), None);
    }
}
