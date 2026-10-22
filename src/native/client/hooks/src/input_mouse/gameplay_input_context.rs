//! Shared conservative context for foreground gameplay shortcuts, not mouse math.
use super::memory::word;
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) struct Context {
    pub viewport: usize,
    pub window: usize,
    pub controller: usize,
    pub pawn: usize,
    pub world: usize,
    // Observed UObject identity/index fingerprints, NOT a proven generation.
    // Equality narrows stale requests; same-address/same-fingerprint reuse is not guaranteed detectable.
    pub controller_identity: u32,
    pub pawn_identity: u32,
    pub world_identity: u32,
}
pub(crate) unsafe fn current(viewport: usize) -> Option<Context> {
    if crate::api::observer_requested() || crate::api::observer_admitted()
        || crate::chat_input_native::text_entry_active() { return None; }
    // Reuse the sole input owner's native menu/capture query, foreground window,
    // reciprocal possession and camera-owner checks. Never substitute a key poll.
    let c = super::raw_mouse_native::context(viewport, false)?;
    let controller = word(c.pawn, 0x938)? as usize;
    let world = word(controller, 0x144)? as usize;
    let identity = |p| word(p, 0x20).filter(|v| *v != 0 && *v != u32::MAX);
    Some(Context { viewport, window: c.window, controller, pawn: c.pawn, world,
        controller_identity: identity(controller)?, pawn_identity: identity(c.pawn)?,
        world_identity: identity(world)? })
}
