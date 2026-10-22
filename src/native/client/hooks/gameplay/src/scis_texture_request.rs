//! Route only an existing native request for an observed empty SC-IS texture.
//! Do not manufacture textures, change materials, or poll on render frames.
use crate::equipment_native::{signature, word};
use std::sync::atomic::{AtomicU32, Ordering};
#[path = "scis_texture_policy.rs"]
mod policy;

retour::static_detour! {
    static TextureQueue: unsafe extern "C" fn(usize, usize);
}
static LOGS: AtomicU32 = AtomicU32::new(0);

unsafe extern "C" fn queue(key: usize, owner: usize) {
    let enabled = super::super::ready() && super::multiplayer();
    let route = policy::should_route(enabled, key, owner, |p, o| word(p, o));
    super::super::render_bank::import(route, || {
        if route && LOGS.fetch_add(1, Ordering::Relaxed) < 28 {
            tracing::info!(
                "SCBL_MOD_SCIS_TEXTURE asset={:08x} campaign_ready={} native_request=true",
                word(key, 0).unwrap_or(0), super::super::render_bank::campaign_loaded()
            );
        }
        // Exactly once: the native queue retains its own dependency ownership.
        TextureQueue.call(key, owner);
    });
}

pub(super) unsafe fn init() -> bool {
    if !signature(0x9c8c00, 151,
        "719281f3d9925072e0ddc479c10e4db31ef88d69947325833148a085f4313128") {
        return false;
    }
    TextureQueue.initialize(
        std::mem::transmute::<usize, unsafe extern "C" fn(usize, usize)>(0x9c8c00),
        |key, owner| queue(key, owner),
    ).is_ok() && TextureQueue.enable().is_ok()
}

pub(super) unsafe fn deinit() {
    if TextureQueue.is_enabled() { let _ = TextureQueue.disable(); }
}
