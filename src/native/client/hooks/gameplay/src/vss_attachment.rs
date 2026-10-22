//! VSS has a campaign on-gun static mesh, but no separate local-view mesh.
//! Native view filtering drops the normal mesh for local imports and the ADS
//! mesh for third-person imports. The attachment FIFO is keyed by name, not
//! view; either object can become the Spy copy's current scope properties.
//! Complete that same engine-owned attachment, without a second CC14C0 call
//! (which would enqueue an extra default attachment for the next respawn).
use crate::equipment_native::{word, writable};
use std::sync::atomic::{AtomicU32, Ordering};

const ATTACHMENT_VT: u32 = 0x02a1772c;
const VIEW_FLAGS: usize = 0x330c4b4;
static LOGS: AtomicU32 = AtomicU32::new(0);

unsafe fn section(name: u32) -> Option<&'static [u8]> {
    super::super::native_name_is(name, b"Sniper_VSS_Scope")
        .then_some(b"Echelon.EWeaponAttachmentArchetype.Sniper_VSS_Scope\0")
}

unsafe fn reload_with(
    props: usize, first_person: u8, allowed: bool, flags: usize,
    load: impl FnOnce(),
) -> bool {
    if !allowed || first_person > 1
        || word(props, 0) != Some(ATTACHMENT_VT)
        || word(props, 0x54) != Some(0)
        || !matches!((word(props, 0x50), word(props, 0x5c)),
            (Some(0), Some(_)) | (Some(_), Some(0)))
        || !writable(flags, 12)
        // CC14C0 resets these before returning. Refuse a nested/incomplete
        // native import instead of changing another object's view context.
        || [0, 4, 8].iter().any(|&o| word(flags, o) != Some(0))
    {
        return false;
    }
    struct Restore(usize);
    impl Drop for Restore {
        fn drop(&mut self) {
            unsafe {
                for o in [0, 4, 8] { ((self.0 + o) as *mut u32).write_unaligned(0); }
            }
        }
    }
    let _restore = Restore(flags);
    // Native ImportText (5116AE..511709) treats the last two flags as
    // EXCLUSIONS, not inclusions. Keep MP filtering active (group 8 remains
    // excluded), but do not exclude either group 4 (normal static mesh) or
    // group 2 (FP/ADS). Reload the SAME object, not a second FIFO entry.
    (flags as *mut u32).write_unaligned(1);
    load();
    word(props, 0x50).is_some_and(|p| p != 0)
        && word(props, 0x5c).is_some_and(|p| p != 0)
}

pub(super) unsafe fn complete(
    props: usize, manager: usize, name: u32, first_person: u8, enabled: bool,
) {
    let mp = word(0x339e5a0, 0).and_then(|w| word(w as usize, 0x330))
        .is_some_and(|bits| bits & 0x10 != 0);
    if !enabled || !mp || first_person > 1 { return; }
    let Some(section) = section(name) else { return; };
    let Some(manager_flags) = word(manager, 0x28) else { return; };
    let before_normal = word(props, 0x50).is_some_and(|p| p != 0);
    let before_ads = word(props, 0x5c).is_some_and(|p| p != 0);
    let clean_context = [0, 4, 8].iter().all(|&o| word(VIEW_FLAGS, o) == Some(0));
    let changed = reload_with(props, first_person, true, VIEW_FLAGS, || {
        super::super::scis_preload::attachment_import(true, || {
            let load: unsafe extern "thiscall" fn(usize, u32, u32, *const u8, *const u8) =
                std::mem::transmute(0x4d43e0usize);
            load(props, 0, 0, b"WeaponConfiguration.ini\0".as_ptr(), section.as_ptr());
            if manager_flags & 1 != 0 {
                load(props, 0, 0, b"ConfigOverrides.ini\0".as_ptr(), section.as_ptr());
            }
        });
    });
    if LOGS.fetch_add(1, Ordering::Relaxed) < 12 {
        tracing::info!("SCBL_MOD_VSS_ATTACHMENT stage=local-import normal_bound={} ads_bound={} completed={} single_native_queue=true view={} before_normal={} before_ads={} clean_context={}",
            word(props,0x50).is_some_and(|p|p!=0),
            word(props,0x5c).is_some_and(|p|p!=0), changed, first_person, before_normal, before_ads, clean_context);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn attachment_import_restores_flags_and_preserves_ads_reference() {
        let mut p = [0u32; 32]; p[0] = ATTACHMENT_VT; p[0x5c/4] = 73;
        let mut flags = [0u32; 3];
        let addr = p.as_mut_ptr() as usize; let f = flags.as_mut_ptr() as usize;
        unsafe {
            assert!(reload_with(addr, 1, true, f, || {
                assert_eq!([word(f,0),word(f,4),word(f,8)],
                    [Some(1),Some(0),Some(0)]);
                ((addr+0x50) as *mut u32).write(72);
            }));
        }
        assert_eq!(flags, [0;3]); assert_eq!(p[0x5c/4], 73);
        assert!(!unsafe { reload_with(addr,1,true,f,||panic!("duplicate import")) });
    }
    #[test]
    fn third_person_fifo_import_keeps_normal_and_completes_missing_ads() {
        for view in [0, 1] {
            let mut p = [0u32; 32]; p[0] = ATTACHMENT_VT; p[0x50/4] = 72;
            let mut flags = [0u32; 3];
            let addr = p.as_mut_ptr() as usize; let f = flags.as_mut_ptr() as usize;
            assert!(unsafe { reload_with(addr, view, true, f, || {
                assert_eq!(word(f, 0), Some(1));
                ((addr+0x5c) as *mut u32).write(73);
            }) });
            assert_eq!(p[0x50/4], 72); assert_eq!(p[0x5c/4], 73);
            assert_eq!(flags, [0; 3]);
            assert!(!unsafe { reload_with(addr, view, true, f, || panic!("already complete")) });
        }
    }
    #[test]
    fn attachment_import_rejects_unrelated_and_active_contexts() {
        for (view, allowed, vt, active) in [(2,true,ATTACHMENT_VT,0),
            (1,false,ATTACHMENT_VT,0),(1,true,0,0),(1,true,ATTACHMENT_VT,1)] {
            let mut p=[0u32;32];p[0]=vt;let mut flags=[active,0,0];
            assert!(!unsafe { reload_with(p.as_mut_ptr() as usize,view,allowed,
                flags.as_mut_ptr() as usize,||panic!("guard failed")) });
            assert_eq!(flags,[active,0,0]);
        }
    }
    #[test]
    fn attachment_import_restores_flags_when_resource_import_unwinds() {
        let mut p = [0u32;32]; p[0] = ATTACHMENT_VT;
        let mut flags = [0u32;3];
        let addr = p.as_mut_ptr() as usize; let f = flags.as_mut_ptr() as usize;
        assert!(std::panic::catch_unwind(|| unsafe {
            reload_with(addr,1,true,f,||panic!("controlled resource failure"))
        }).is_err());
        assert_eq!(flags,[0;3]);
        assert_eq!(p[0x50/4],0);
    }
}
