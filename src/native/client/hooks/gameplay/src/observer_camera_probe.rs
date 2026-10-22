//! Private OB development only. Observe native final view frames on F7.
//! Called by the one resident render hook; never installs a competing detour.
//! This is NOT the observer-mode implementation.
use crate::equipment_native::word;
use std::sync::Mutex;

static STATE: Mutex<Window> = Mutex::new(Window { down: false, until: 0, next: 0, samples: 0 });
#[link(name="user32")]
extern "system" {
    fn GetAsyncKeyState(key: i32) -> i16;
    fn GetForegroundWindow() -> usize;
    fn GetWindowThreadProcessId(window: usize, process: *mut u32) -> u32;
}
#[link(name="kernel32")]
extern "system" { fn GetTickCount64() -> u64; fn GetCurrentProcessId() -> u32; }

struct Window { down: bool, until: u64, next: u64, samples: u32 }
impl Window {
    // At most one sample per 100 ms and 200 samples per keypress, without a worker.
    fn advance(&mut self, now: u64, down: bool, focused: bool) -> (bool, bool, bool) {
        let end = self.until != 0 && (now >= self.until || !focused);
        if end { self.until = 0; }
        let start = down && !self.down && focused && self.until == 0;
        self.down = down;
        if start { self.until = now.saturating_add(20_000); self.next = now; self.samples = 0; }
        let sample = self.until != 0 && now >= self.next && self.samples < 200;
        if sample { self.next = now.saturating_add(100); self.samples += 1; }
        (start, sample, end)
    }
}

unsafe fn sample(frame: usize, now: u64) -> Option<()> {
    let info = word(0x339e5a0, 0)? as usize;
    if word(info, 0x330)? & 0x10 == 0 { return None; }
    let world = word(info, 0x144)? as usize;
    let view = crate::observer_view::inspect(world,frame)?;
    let id = view.identity;
    tracing::info!("SCBL_OB_CAMERA_SAMPLE t={} frame={:08x} viewport={:08x} pc={:08x} generation={} pawn={:?} camera={:08x} frame_actor={:?} renderer={:?} pos={:?} yaw={} pitch={} ownership=validated writes=false",
        now,frame,id.viewport,id.controller,id.generation,word(id.controller,0x520),id.camera,
        word(frame,4),word(frame,0x48),view.pose.position_cm,view.pose.yaw_degrees,view.pose.pitch_degrees);
    Some(())
}
pub(crate) unsafe fn observe(frame: usize) {
    let Ok(mut state) = STATE.try_lock() else { return; };
    let now = GetTickCount64();
    let mut pid = 0;
    GetWindowThreadProcessId(GetForegroundWindow(), &mut pid);
    let (start, take, end) = state.advance(now, GetAsyncKeyState(0x76)<0, pid == GetCurrentProcessId());
    if end { tracing::info!("SCBL_OB_CAMERA_PROBE ended attempted_samples={}",state.samples); }
    if start { tracing::info!("SCBL_OB_CAMERA_PROBE started duration_ms=20000 frame_writes=false pawn_writes=false"); }
    if take && sample(frame,now).is_none() { tracing::info!("SCBL_OB_CAMERA_SAMPLE t={} rejected=unbound-native-view",now); }
}
#[cfg(test)]
mod tests {
    use super::*;
    fn window()->Window {Window{down:false,until:0,next:0,samples:0}}
    #[test] fn bounded_window_and_held_key_do_not_restart() {
        let mut s=window(); assert_eq!(s.advance(1000,true,true),(true,true,false));
        for t in 1001..21000 {s.advance(t,true,true);}
        assert_eq!(s.samples,200);
        assert_eq!(s.advance(21000,true,true),(false,false,true));
        assert_eq!(s.advance(22000,true,true),(false,false,false));
        s.advance(22001,false,true);
        assert_eq!(s.advance(22002,true,true),(true,true,false));
    }
    #[test] fn focus_loss_ends_capture_and_background_key_cannot_start() {
        let mut s=window(); assert_eq!(s.advance(1,true,false),(false,false,false));
        s.advance(2,false,true); s.advance(3,true,true);
        assert_eq!(s.advance(4,false,false),(false,false,true));
        assert_eq!(s.advance(5,true,false),(false,false,false));
    }
}
