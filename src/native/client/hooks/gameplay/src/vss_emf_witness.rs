//! Private, manually armed VSS scope evidence. No hotkey or auto capture.
#[derive(Clone, Copy, Default, Debug, PartialEq, Eq)]
struct Ticket { run: u32, view: u8 }
#[derive(Default)]
struct Gate { started: u64, views: u8, active: bool, run: u32 }
impl Gate {
    const fn new() -> Self { Self { started: 0, views: 0, active: false, run: 0 } }
    fn live(&mut self, now: u64) -> bool {
        if self.active && (now < self.started || now - self.started >= 5000) { self.stop(); }
        self.active
    }
    fn signal(&mut self, now: u64, signaled: bool) -> bool {
        self.signal_at(now, now, signaled)
    }
    fn signal_at(&mut self, now: u64, requested: u64, signaled: bool) -> bool {
        let was_active = self.active;
        self.live(now);
        // Active requests, including timeout edge, cannot extend/restart a run.
        if signaled && !was_active && self.run < 32 && now >= requested && now-requested < 5000 {
            self.run += 1; self.started = requested; self.views = 0; self.active = true;
        }
        self.active
    }
    fn take(&mut self, now: u64) -> Option<Ticket> {
        if !self.live(now) { return None; }
        if self.views >= 8 { self.stop(); return None; }
        self.views += 1;
        Some(Ticket { run: self.run, view: self.views })
    }
    fn accepts(&mut self, now: u64, ticket: Ticket) -> bool {
        self.live(now) && ticket.run == self.run && ticket.view != 0 && ticket.view <= self.views
    }
    fn stop(&mut self) { self.active = false; }
}

// Ordinary builds have no event/clock polling, target reads or capture logging.
#[cfg(not(feature = "vss-emf-witness"))]
pub(super) unsafe fn with_view(_pass: usize, native: impl FnOnce()) { native(); }
#[cfg(not(feature = "vss-emf-witness"))]
pub(super) fn init() {}
#[cfg(not(feature = "vss-emf-witness"))]
pub(super) fn deinit() {}
#[cfg(not(feature = "vss-emf-witness"))]
pub(super) fn classified(_pass: usize, _phase: u8) {}
#[cfg(not(feature = "vss-emf-witness"))]
pub(super) fn material(_root: usize, _render: usize, _node: usize, _surface: usize, _selected: usize) {}
#[cfg(feature = "vss-emf-witness")]
#[path = "vss_emf_witness_runtime.rs"]
mod runtime;
#[cfg(feature = "vss-emf-witness")]
pub(super) use runtime::{init, deinit, with_view, classified, material};

#[cfg(test)] mod tests {
    use super::*;
    #[test] fn default_never_starts() {
        let mut g=Gate::new(); assert!(!g.signal(0,false)); assert_eq!(g.take(0),None);
    }
    #[test] fn view_budget_exactly_eight() {
        let mut g=Gate::new(); assert!(g.signal(100,true));
        for i in 1..=8 { assert_eq!(g.take(101),Some(Ticket{run:1,view:i})); }
        assert_eq!(g.take(102),None);
    }
    #[test] fn repeated_signal_never_extends_run() {
        let mut g=Gate::new();g.signal(100,true);
        assert_eq!(g.take(101),Some(Ticket{run:1,view:1}));
        assert!(g.signal(5099,true));assert!(!g.signal(5100,true));
        assert_eq!(g.take(5100),None);assert!(!g.signal(5101,false));
    }
    #[test] fn callback_at_deadline_or_clock_reversal_is_rejected() {
        for now in [5100,99] {
            let mut g=Gate::new();g.signal(100,true);let t=g.take(101).unwrap();
            assert!(!g.accepts(now,t)); assert!(!g.accepts(102,t));
        }
    }
    #[test] fn stopped_owner_or_old_run_cannot_resume() {
        let mut g=Gate::new();g.signal(100,true);let old=g.take(101).unwrap();g.stop();
        assert!(!g.accepts(102,old));g.signal(103,true);
        assert!(!g.accepts(104,old));assert_eq!(g.take(104).unwrap().run,2);
    }
    #[test] fn manual_rearm_has_process_limit() {
        let mut g=Gate::new();
        for _ in 0..32 { assert!(g.signal(100,true));g.stop(); }
        assert!(!g.signal(100,true));
    }
    #[test] fn delayed_request_uses_request_time_and_expired_requests_never_arm() {
        let mut g=Gate::new();assert!(!g.signal_at(5100,100,true));
        assert!(!g.signal_at(99,100,true));assert!(g.signal_at(5099,100,true));
        let t=g.take(5099).unwrap();assert!(!g.accepts(5100,t));
    }
    #[test] fn many_competing_reservations_share_budget() {
        use std::sync::{Arc,Mutex};
        let gate=Arc::new(Mutex::new(Gate::new()));gate.lock().unwrap().signal(100,true);
        let jobs:Vec<_>=(0..32).map(|_|{let g=gate.clone();std::thread::spawn(move||g.lock().unwrap().take(101).is_some())}).collect();
        assert_eq!(jobs.into_iter().map(|j|j.join().unwrap() as usize).sum::<usize>(),8);
    }
    #[cfg(not(feature="vss-emf-witness"))]
    #[test] fn normal_build_calls_native_once() {
        let calls=std::cell::Cell::new(0);
        unsafe{with_view(usize::MAX,||calls.set(calls.get()+1));}
        init();classified(usize::MAX,0);material(usize::MAX,usize::MAX,usize::MAX,usize::MAX,usize::MAX);deinit();
        assert_eq!(calls.get(),1);
    }
}
