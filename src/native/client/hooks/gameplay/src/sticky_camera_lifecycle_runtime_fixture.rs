//! Test-only access to the production whistle boundary. No hook is enabled and
//! no retail image is loaded. The original leaf is a synthetic thiscall sink.
use super::*;

pub(crate) unsafe fn initialize(original: unsafe extern "thiscall" fn(usize), sender: unsafe extern "thiscall" fn(usize)) {
    Whistle.initialize(original, whistle).expect("synthetic whistle trampoline");
    WhistleSend.initialize(sender, whistle_send).expect("synthetic send trampoline");
    READY.store(true, Ordering::Release);
}
pub(crate) fn run(camera: usize) { whistle(camera); }
pub(crate) unsafe fn destroy(camera: usize) { forget(camera); }
pub(crate) fn ready(enabled: bool) { READY.store(enabled, Ordering::Release); }
pub(crate) unsafe fn detonate(camera: usize) -> bool {
    device(camera).is_some_and(|owner| execute(owner, Command::Detonate))
}
