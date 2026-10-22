//! Two scoped call sites, not a global HUD force-visible override. Skipping
//! only the SC-IS scope mode leaves death, menu and other HUD requests intact.
use crate::{equipment_native::{patch, readable, writable}, scis_hud_policy as policy};
const ENTER: usize = 0xda6d21;
const EXIT: usize = 0xda6dff;

fn bytes(site: usize, target: usize) -> [u8; 5] {
    let mut b = [0xe8, 0, 0, 0, 0];
    b[1..].copy_from_slice(&(target as u32).wrapping_sub(site as u32 + 5).to_le_bytes());
    b
}

unsafe extern "thiscall" fn enter(bus: usize, event: usize, deferred: u32) -> u8 {
    if readable(event, 12) && writable(event + 8, 4)
        && policy::skip_entry(bus, ((event+4) as *const u32).read_unaligned(),
                             ((event+8) as *const u32).read_unaligned()) {
        ((event+8) as *mut u32).write_unaligned(policy::SKIPPED);
        return 1;
    }
    let native: unsafe extern "thiscall" fn(usize,usize,u32)->u8 = std::mem::transmute(0xb07b90usize);
    native(bus,event,deferred)
}

unsafe extern "thiscall" fn exit(bus: usize, event: usize, deferred: u32) -> u8 {
    if readable(event, 8)
        && policy::skip_exit(bus, ((event+4) as *const u32).read_unaligned()) { return 1; }
    let native: unsafe extern "thiscall" fn(usize,usize,u32)->u8 = std::mem::transmute(0xb07a90usize);
    native(bus,event,deferred)
}

const SITES: [(usize,usize,unsafe extern "thiscall" fn(usize,usize,u32)->u8);2] =
    [(ENTER,0xb07b90,enter), (EXIT,0xb07a90,exit)];

// Parent checks the entire exact retail DA6B10 before installing either site.
pub(super) unsafe fn init() -> bool {
    for (site,native,hook) in SITES {
        if !patch(site,&bytes(site,native),&bytes(site,hook as usize)) {
            deinit(); return false;
        }
    }
    true
}
pub(super) unsafe fn deinit() {
    for (site,native,hook) in SITES {
        if readable(site,5) && std::slice::from_raw_parts(site as *const u8,5)==bytes(site,hook as usize)
            && !patch(site,&bytes(site,hook as usize),&bytes(site,native)) {
            tracing::error!("SCBL_MOD_SCIS_HUD restore_failed site={site:x}");
        }
    }
}
