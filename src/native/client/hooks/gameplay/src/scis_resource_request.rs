//! A7C410 creates/looks up L3D data, but queues its payload only for argument 1.
//! Late archetype binding can reuse an earlier empty resource without that call.
//! Re-enter only for verified SC-IS/VSS geometry keys with no ready sections.
use crate::equipment_native::{signature, word};
use std::cell::Cell;
use std::sync::atomic::{AtomicU32, Ordering};
#[path = "scis_resource_policy.rs"]
mod policy;
#[path = "scis_texture_request.rs"]
mod textures;
retour::static_detour! {
    static Build: unsafe extern "thiscall" fn(usize, u32);
}
thread_local! { static RETRYING: Cell<bool> = const { Cell::new(false) }; }
static LOGS: AtomicU32 = AtomicU32::new(0);

unsafe fn multiplayer() -> bool {
    word(0x339e5a0,0).and_then(|p|word(p as usize,0x330)).map(|v|v&0x10!=0).unwrap_or(false)
}
unsafe fn state(object:usize)->Option<(u32,u32)> {
    let resource=word(object,0x34)? as usize;
    Some((word(resource,0x14)?,word(resource,0x64)?))
}
unsafe extern "thiscall" fn build(object:usize, incoming:u32) {
    policy::resolve_with(super::ready()&&multiplayer(),incoming,
        &mut |flag|Build.call(object,flag),||state(object),|asset,native| {
            // An outer native preload with a valid bank already submitted flag 1.
            if incoming!=0 && super::render_bank::campaign_loaded() { return false; }
            RETRYING.with(|busy| {
                if busy.replace(true) { return false; }
                struct Reset<'a>(&'a Cell<bool>);
                impl Drop for Reset<'_> { fn drop(&mut self){self.0.set(false);} }
                let _reset=Reset(busy);
                let queued=super::render_bank::import(true,|| {
                    if !super::render_bank::campaign_loaded() { return false; }
                    native(1);
                    true
                });
                if LOGS.fetch_add(1,Ordering::Relaxed)<16 {
                    tracing::info!("SCBL_MOD_SCIS_PAYLOAD asset={:08x} incoming={} native_request={} geometry_ready={}",asset,incoming,queued,state(object).map(|(_,n)|n>0).unwrap_or(false));
                }
                queued
            })
        });
}
pub(super) unsafe fn init()->bool {
    if !signature(0xa7c410,386,"aabfe3a99165f0c224fe44b91b54fc546204bb24c8eb0a251b6b3ca417812463") { return false; }
    let ok = Build.initialize(std::mem::transmute::<usize,unsafe extern "thiscall" fn(usize,u32)>(0xa7c410),|object,flag|build(object,flag)).is_ok()
        && Build.enable().is_ok() && textures::init();
    if !ok { deinit(); }
    ok
}
pub(super) unsafe fn deinit() {
    textures::deinit();
    if Build.is_enabled(){let _=Build.disable();}
}
