//! Keep scope geometry independent of the HUD mode stack. The private marker
//! never enters the native HUD dispatcher, including on exit after a swap.
use std::cell::Cell;
pub(crate) const SKIPPED: u32 = 0xffff_fffe;
thread_local! { static OWNED_ENTRY: Cell<bool> = const { Cell::new(false) }; }

pub(crate) fn during<T>(owned: bool, call: impl FnOnce() -> T) -> T {
    struct Restore(bool);
    impl Drop for Restore {
        fn drop(&mut self) { OWNED_ENTRY.with(|s| s.set(self.0)); }
    }
    let _restore = Restore(OWNED_ENTRY.with(|s| s.replace(owned)));
    call()
}

pub(crate) fn skip_entry(bus: usize, mode: u32, handle: u32) -> bool {
    bus == 0x339ce10 && mode == 0x01000014 && handle == u32::MAX
        && OWNED_ENTRY.with(|s| s.get())
}

pub(crate) fn skip_exit(bus: usize, handle: u32) -> bool {
    bus == 0x339cdec && handle == SKIPPED
}

pub(crate) fn hide_weapon_reticle(owned: bool, same_component: bool, kind: Option<u32>) -> bool {
    owned && same_component && matches!(kind, Some(0 | 3 | 4 | 5 | 6 | 7 | 9))
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn scis_hud_mode_filter_is_call_scoped_and_restores_nested_context() {
        let check = || skip_entry(0x339ce10, 0x1000014, u32::MAX);
        assert!(!check());
        during(true, || {
            assert!(check());
            assert!(!skip_entry(0x339ce14, 0x1000014, u32::MAX));
            assert!(!skip_entry(0x339ce10, 0x1000013, u32::MAX));
            assert!(!skip_entry(0x339ce10, 0x1000014, 123));
            during(false, || assert!(!check()));
            assert!(check());
        });
        assert!(!check());
        let _ = std::panic::catch_unwind(|| during(true, || panic!("scope unwind")));
        assert!(!check());
        assert!(skip_exit(0x339cdec, SKIPPED));
        assert!(!skip_exit(0x339cdec, 42));
        assert!(!skip_exit(0x339cdec, u32::MAX));
        assert!(!skip_exit(0x339ce10, SKIPPED));
    }
    #[test]
    fn scis_reticle_policy_leaves_tools_other_weapons_and_optic_geometry_alone() {
        for kind in 0..12 {
            assert_eq!(hide_weapon_reticle(true, true, Some(kind)), matches!(kind, 0|3|4|5|6|7|9));
            assert!(!hide_weapon_reticle(false, true, Some(kind)));
            assert!(!hide_weapon_reticle(true, false, Some(kind)));
        }
        assert!(!hide_weapon_reticle(true, true, None));
    }
}
