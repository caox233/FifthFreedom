//! Exact campaign texture dependencies for SC-IS and VSS gun/ADS meshes.
//! Model geometry being ready does not imply that its texture payloads exist.
pub(super) fn should_route(
    enabled: bool,
    key: usize,
    owner: usize,
    read: impl Fn(usize, usize) -> Option<u32>,
) -> bool {
    if !enabled || read(owner, 0) != Some(0x029f_f7d4) {
        return false;
    }
    let Some(asset) = read(key, 0) else { return false; };
    if !matches!(asset,
        0x0873_408f | 0xdb80_8893 | 0x7042_6597 |
        0x9eb9_545e | 0x4d4a_9c42 | 0xe688_7146 |
        0xe41c_5b8e | 0x37ef_9392 | 0x9c2d_7e96 |
        0x3dc5_b76c | 0xee36_7f70 | 0x45f4_9274 |
        0x5ed2_e444 | 0x8153_ea9b |
        // VSS body D/N/S; GRU ADS body D/N/S; ACOG body D/N/S; reticle 03.
        // Only redirect requests that native code has actually issued. Do not
        // treat these keys as proof that the material created its dependencies.
        0x9289_88c1 | 0x417a_40dd | 0xeab8_add9 |
        0xf497_c532 | 0x2764_0d2e | 0x8ca6_e02a |
        0xdf98_d613 | 0x0c6b_1e0f | 0xa7a9_f30b | 0xce04_828a) {
        return false;
    }
    let Some(resource) = read(owner, 0x30).map(|p| p as usize) else { return false; };
    read(resource, 0) == Some(0x02c0_14f8)
        && read(resource, 0x14) == Some(asset)
        && read(resource, 0x2c) == Some(0)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn vss_campaign_textures_route_only_when_missing_and_enabled() {
        // Actual PEC-Main exports: VSS body D/N/S, GRU zoom D/N/S,
        // ACOG zoom body D/N/S and reticle 03. Shared MP textures stay native.
        for asset in [
            0x9289_88c1, 0x417a_40dd, 0xeab8_add9,
            0xf497_c532, 0x2764_0d2e, 0x8ca6_e02a,
            0xdf98_d613, 0x0c6b_1e0f, 0xa7a9_f30b, 0xce04_828a,
        ] {
            for (enabled, handle, expected) in [(true, 0, true), (false, 0, false), (true, 1, false)] {
                assert_eq!(should_route(enabled, 1, 2, |p, o| match (p, o) {
                    (1, 0) | (3, 0x14) => Some(asset),
                    (2, 0) => Some(0x029f_f7d4), (2, 0x30) => Some(3),
                    (3, 0) => Some(0x02c0_14f8), (3, 0x2c) => Some(handle),
                    _ => None,
                }), expected, "asset={asset:08x}");
            }
        }
    }
    #[test]
    fn missing_texture_gate_rejects_unrelated_or_invalid_resource() {
        for (enabled, owner_vt, resource_vt, resource_key, handle, expected) in [
            (true, 0x029f_f7d4, 0x02c0_14f8, 0x0873_408f, 0, true),
            (false, 0x029f_f7d4, 0x02c0_14f8, 0x0873_408f, 0, false),
            (true, 0, 0x02c0_14f8, 0x0873_408f, 0, false),
            (true, 0x029f_f7d4, 0, 0x0873_408f, 0, false),
            (true, 0x029f_f7d4, 0x02c0_14f8, 123, 0, false),
            (true, 0x029f_f7d4, 0x02c0_14f8, 0x0873_408f, 1, false),
        ] {
            assert_eq!(should_route(enabled, 1, 2, |p, o| match (p, o) {
                (1, 0) => Some(0x0873_408f), (2, 0) => Some(owner_vt),
                (2, 0x30) => Some(3), (3, 0) => Some(resource_vt),
                (3, 0x14) => Some(resource_key), (3, 0x2c) => Some(handle),
                _ => None,
            }), expected);
        }
        assert!(!should_route(true, 1, 2, |_, _| None));
    }
}
