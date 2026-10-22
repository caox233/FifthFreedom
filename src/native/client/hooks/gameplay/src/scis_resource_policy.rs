//! Retry only verified campaign geometry keys; never replace an existing model.
//! BA160A5B is retail WEAPM-TwoHanded.SK_Sniper_VSS_l3d in PEC-Main.
//! VSS mount/ADS geometry are under LIBM-Weapon-ScopeMount.All, not the
//! ungrouped config alias. Both keys are pinned to the package export and bank.
//! The Spy copy also reaches late flag-0 initialization; bank preload alone is insufficient.
pub(super) fn resolve_with(
    enabled: bool,
    incoming: u32,
    original: &mut impl FnMut(u32),
    state: impl FnOnce() -> Option<(u32, u32)>,
    request: impl FnOnce(u32, &mut dyn FnMut(u32)) -> bool,
) -> bool {
    original(incoming);
    if !enabled { return false; }
    let Some((asset, sections)) = state() else { return false; };
    if sections != 0 || !matches!(asset, 0xe342_f7a1 | 0x9889_ca31 | 0xba16_0a5b | 0xa877_2bce | 0x573d_d314) { return false; }
    request(asset, original)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn original_call_is_preserved_and_only_missing_verified_campaign_payloads_retry() {
        for (enabled, asset, sections, expected) in [
            (true, 0xe342_f7a1, 0, true), (true, 0x9889_ca31, 0, true),
            (true, 0xba16_0a5b, 0, true),
            (true, 0xa877_2bce, 0, true), (true, 0x573d_d314, 0, true),
            (true, 0xa877_2bce, 1, false), (true, 0x573d_d314, 1, false),
            (false, 0xa877_2bce, 0, false), (false, 0x573d_d314, 0, false),
            (true, 0xba16_0a5b, 1, false), (false, 0xba16_0a5b, 0, false),
            (true, 123, 0, false), (true, 0xe342_f7a1, 1, false),
            (false, 0xe342_f7a1, 0, false),
        ] {
            let mut calls=Vec::new();
            let retried=resolve_with(enabled,0,&mut |v|calls.push(v),||Some((asset,sections)),|_,native|{native(1);true});
            assert_eq!(retried,expected);
            assert_eq!(calls,if expected {vec![0,1]} else {vec![0]});
        }
    }
}
