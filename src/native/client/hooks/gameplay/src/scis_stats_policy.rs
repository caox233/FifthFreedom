//! Only the configuration query is aliased; saved/catalog names stay distinct.
pub(super) fn config_name(prefix: &[u8], name: &[u8]) -> Option<&'static [u8]> {
    if prefix != b"Echelon.EWeaponArchetype." { return None; }
    match name {
        b"SCBL_Merc_SCIS" => Some(b"Sniper_Proto"),
        b"SCBL_Spy_VSS" => Some(b"Sniper_VSS"),
        _ => None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn scis_stats_alias_is_exact_and_only_for_the_native_config_namespace() {
        assert_eq!(config_name(b"Echelon.EWeaponArchetype.",b"SCBL_Merc_SCIS"),Some(b"Sniper_Proto".as_slice()));
        for n in [b"Sniper_Proto".as_slice(),b"OtherGun",b"SCBL_Merc_SCIS_other",b"SCBL_Merc_SCIS.Optic_HighPowerScope"] {
            assert_eq!(config_name(b"Echelon.EWeaponArchetype.",n),None);
        }
        assert_eq!(config_name(b"OtherNamespace.",b"SCBL_Merc_SCIS"),None);
    }
}

#[test]
fn vss_stats_alias_preserves_campaign_and_attachment_names() {
    assert_eq!(config_name(b"Echelon.EWeaponArchetype.",b"SCBL_Spy_VSS"),Some(b"Sniper_VSS".as_slice()));
    assert_eq!(config_name(b"OtherNamespace.",b"SCBL_Spy_VSS"),None);
    for n in [b"Sniper_VSS".as_slice(),b"SCBL_Spy_VSS_other",b"SCBL_Spy_VSS.Rifle_Ammo_AP"] {
        assert_eq!(config_name(b"Echelon.EWeaponArchetype.",n),None);
    }
}
