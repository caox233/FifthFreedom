//! SC-IS first-person model binding. The retail renderer already supports a
//! normal-mesh fallback, but the FP config importer removes that reference.
use crate::equipment_native::{readable, word, writable};
use std::cell::Cell;
use std::sync::atomic::{AtomicU32, Ordering};
const ARCHETYPE_VT: u32 = 0x02a176c4;
static LOGS: AtomicU32 = AtomicU32::new(0);
thread_local! { static SUPPLEMENTAL: Cell<usize> = const { Cell::new(0) }; }

// CD6910 imports a fresh set of default-upgrade properties on EVERY call,
// including cache hits. ED3540 consumes them FIFO by name, not by view. Extra
// TP/FP model lookups must not enqueue attachments for a future respawn.
fn supplemental<T>(manager: usize, call: impl FnOnce() -> T) -> T {
    struct Restore(usize);
    impl Drop for Restore {
        fn drop(&mut self) { SUPPLEMENTAL.with(|slot| slot.set(self.0)); }
    }
    let _restore = Restore(SUPPLEMENTAL.with(|slot| slot.replace(manager)));
    call()
}

pub(super) fn default_upgrade_with(
    manager: usize, name: u32, first_person: u8,
    native: impl FnOnce(usize, u32, u8) -> usize,
) -> usize {
    // Called only from CD6910's default-upgrade loop (CD6BE7). Other import
    // callers and threads retain native behavior; no queue/pointer mutation.
    if manager != 0 && SUPPLEMENTAL.with(|slot| slot.get() == manager) { 0 }
    else { native(manager, name, first_person) }
}
unsafe fn missing(fp: usize) -> bool {
    word(fp, 0) == Some(ARCHETYPE_VT)
        && [0x4c, 0x50, 0x54].iter().all(|&o| word(fp, o) == Some(0))
}

unsafe fn bind_fallback(fp: usize, normal: usize) -> bool {
    if fp == normal
        || !missing(fp)
        || word(normal, 0) != Some(ARCHETYPE_VT)
        || !writable(fp + 0x4c, 4)
    {
        return false;
    }
    let Some(mesh) = word(normal, 0x4c).filter(|&p| p != 0 && readable(p as usize, 0x30)) else {
        return false;
    };
    // A reflected UObject reference in the separate engine-owned FP config.
    // No shallow UObject copy, no class-flag changes, no TP/campaign writes.
    ((fp + 0x4c) as *mut u32).write_unaligned(mesh);
    true
}

pub(super) unsafe fn resolve_with(
    manager: usize,
    name: u32,
    first_person: u8,
    flags: u8,
    allowed: bool,
    mut native: impl FnMut(usize, u32, u8, u8) -> usize,
) -> usize {
    let fp = native(manager, name, first_person, flags);
    if !allowed || first_person != 1 || !missing(fp) {
        return fp;
    }
    // Keep the one original FP import above. Supplemental model lookups still
    // use native view-keyed configs/resource fixups, but enqueue no upgrades.
    let (normal, restored_fp) = supplemental(manager, || {
        (native(manager, name, 0, flags), native(manager, name, first_person, flags))
    });
    // E905D0's view-keyed cache must still return the same object. Refuse a
    // mismatched cache rather than touching an unrelated or replaced config.
    let changed = restored_fp == fp && bind_fallback(fp, normal);
    if LOGS.fetch_add(1, Ordering::Relaxed) < 16 {
        tracing::info!("SCBL_MOD_SCIS_VISUAL stage=archetype normal_mesh={} fp_mesh={} fp_highres={} fallback_bound={} separate_configs={}",
            word(normal,0x4c).is_some_and(|p|p!=0),word(fp,0x4c).is_some_and(|p|p!=0),
            word(fp,0x54).is_some_and(|p|p!=0),changed,fp!=0&&normal!=0&&fp!=normal);
    }
    fp
}

#[cfg(test)]
#[path = "scis_visual_tests.rs"]
mod tests;
