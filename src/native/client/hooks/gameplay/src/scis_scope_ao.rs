//! Retail per-node SelfAO selection. This hook returns a borrowed engine-owned
//! texture for this draw only; it never writes nodes, materials or global AO.
use crate::equipment_native::{signature, word};
use retour::static_detour;
use std::sync::atomic::{AtomicUsize, AtomicU32, Ordering};
#[path="scis_scope_ao_policy.rs"]
mod policy;

// Fast negative filter only. A matching pointer still requires full current
// pawn/actor generation, world, possession, weapon and node validation.
static NODE_HINT: AtomicUsize = AtomicUsize::new(0);
static LOGS: AtomicU32 = AtomicU32::new(0);
static_detour! { static Select: unsafe extern "thiscall" fn(usize, usize) -> usize; }

pub(super) fn hint(node: usize) { NODE_HINT.store(node, Ordering::Release); }
fn select(node: usize, fallback: usize) -> usize {
    unsafe {
        let selected = Select.call(node, fallback);
        if node < 0x10000 || NODE_HINT.load(Ordering::Acquire) != node { return selected; }
        let result = policy::select_texture(super::owns_ao_scope_node(node), selected, fallback,
            |p,o| word(p,o));
        if result != selected && LOGS.fetch_add(1,Ordering::Relaxed)<4 {
            tracing::info!("SCBL_MOD_SCIS_AO missing_srv=true native_ready_fallback=true scope_only=true");
        }
        result
    }
}

pub(super) unsafe fn init() -> bool {
    // 175E760's only selector call feeds backend sampler12 (+D4), the captured
    // shader's sSelfAOMap. Pin both selector and the consumer, not a guessed slot.
    if !signature(0x175d3f0,114,"ae7631c5579dd3246db0f92b8dc411f421e35f38c35b0926825ea7da97682cf7")
        || !signature(0x175e793,64,"5ed99de17fe159267c4dd5278ce0328520af0272cd62a2270c67a91f788082a0")
    { return false; }
    if Select.initialize(std::mem::transmute::<usize, unsafe extern "thiscall" fn(usize,usize)->usize>(0x175d3f0),select).is_err()
        || Select.enable().is_err() { deinit(); return false; }
    true
}
pub(super) unsafe fn deinit() {
    hint(0);
    if Select.is_enabled() { let _=Select.disable(); }
}
