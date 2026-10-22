//! Copied MP SC-IS and VSS have four magazines total, not the campaign reserve.
//! Keep the one loaded magazine separate from the three reserve magazines.
pub(super) const RESERVE_MAGAZINES: i32 = 3;

pub(super) fn base_magazines(owned_mp_scis: bool, native: i32) -> i32 {
    if owned_mp_scis { RESERVE_MAGAZINES } else { native }
}

pub(super) fn menu_magazines(mp_menu: bool, name: &[u8]) -> Option<i32> {
    (mp_menu && matches!(name, b"SCBL_Merc_SCIS" | b"SCBL_Spy_VSS"))
        .then_some(RESERVE_MAGAZINES + 1)
}

pub(super) fn initial_reserve(owned_mp_scis: bool, capacity: i32, magazines: i32) -> Option<i32> {
    if !owned_mp_scis || !(1..=1000).contains(&capacity) || !(0..=100).contains(&magazines) {
        return None;
    }
    capacity.checked_mul(magazines)
}

#[cfg(test)] mod tests {
    use super::*;
    #[test] fn scis_ammo_four_magazines_include_the_loaded_one() {
        assert_eq!(base_magazines(true, 1), 3);
        assert_eq!(menu_magazines(true, b"SCBL_Merc_SCIS"), Some(4));
        assert_eq!(initial_reserve(true, 8, 3), Some(24));
        assert_eq!(initial_reserve(true, 8, 4), Some(32), "native reserve upgrades remain in effect");
    }
    #[test] fn copied_vss_has_four_magazines_without_changing_capacity_or_campaign() {
        assert_eq!(menu_magazines(true, b"SCBL_Spy_VSS"), Some(4));
        assert_eq!(initial_reserve(true, 10, base_magazines(true, 1)), Some(30));
        assert_eq!(initial_reserve(true, 20, base_magazines(true, 1)), Some(60));
        assert_eq!(initial_reserve(true, 10, 4), Some(40), "native reserve upgrades survive");
        assert_eq!(base_magazines(false, 1), 1);
        for name in [b"Sniper_VSS".as_slice(), b"SCBL_Spy_VSS.Other", b"scbl_spy_vss"] {
            assert_eq!(menu_magazines(true, name), None);
        }
        assert_eq!(menu_magazines(false, b"SCBL_Spy_VSS"), None);
        assert_eq!(initial_reserve(false, 10, 3), None);
    }
    #[test] fn scis_ammo_campaign_other_items_and_invalid_limits_stay_native() {
        for n in [-2, 0, 1, 3, 5] { assert_eq!(base_magazines(false, n), n); }
        for name in [b"Sniper_Proto".as_slice(), b"ADV_Merc_Assault_AK47", b"SCBL_Merc_SCIS.Other", b"scbl_merc_scis"] {
            assert_eq!(menu_magazines(true, name), None);
        }
        assert_eq!(menu_magazines(false, b"SCBL_Merc_SCIS"), None);
        assert_eq!(initial_reserve(false, 8, 3), None);
        for (c, m) in [(0, 3), (-1, 3), (8, -1), (i32::MAX, 3), (8, i32::MAX)] {
            assert_eq!(initial_reserve(true, c, m), None);
        }
    }
}
