//! The campaign SC-IS config names AK-47's automatic FP camera-shake preset.
//! CA3890 resolves that name to archetype+174; E3FCD0 copies it to logic+48,
//! which DD6B30/DBEB50 consume for ADS shot feedback. Reuse the already
//! resolved SC-IS single-shot preset, only on the MP high-resolution import.
//! This does not alter fire cadence, hand-animation phases, or shared presets.
use crate::equipment_native::{word, writable};
use std::sync::atomic::{AtomicU32, Ordering};
static LOGS: AtomicU32 = AtomicU32::new(0);

fn replacement(
    props: usize,
    first_person: u8,
    allowed: bool,
    read: impl Fn(usize, usize) -> Option<u32>,
    named: impl Fn(u32, &[u8]) -> bool,
) -> Option<u32> {
    if !allowed || first_person != 1 || read(props, 0)? != 0x02a176c4 {
        return None;
    }
    let single_name = read(props, 0x6c)?;
    let automatic_name = read(props, 0x70)?;
    if !named(single_name, b"Sniper_Proto")
        || !named(automatic_name, b"Assault_AK47_FirstPerson")
    {
        return None;
    }
    let single = read(props, 0x170)? as usize;
    let automatic = read(props, 0x174)? as usize;
    if single < 0x10000 || automatic < 0x10000 || single == automatic
        || read(single, 0)? != single_name || read(automatic, 0)? != automatic_name
        || !named(read(single, 4)?, b"CAM_Shake_ShootSingle_1")
        || !named(read(automatic, 4)?, b"CAM_Shake_ShootAuto_2")
    {
        return None;
    }
    // Both are borrowed entries in the engine's definition array. Never
    // allocate/copy an animation object or modify the shared array itself.
    for preset in [single, automatic] {
        let gain = f32::from_bits(read(preset, 8)?);
        if !gain.is_finite() || gain <= 0.0 || gain > 100.0 {
            return None;
        }
    }
    Some(single as u32)
}

pub(super) unsafe fn bind(props: usize, first_person: u8, allowed: bool) {
    let candidate = replacement(props, first_person, allowed,
        |p, o| word(p, o), |n, s| super::super::native_name_is(n, s));
    if let Some(single) = candidate.filter(|_| writable(props + 0x174, 4)) {
        // Native CD6910 calls CA3890 on cache hits too. A later campaign
        // lookup reconstructs its retail pointer from the unchanged +70 name.
        ((props + 0x174) as *mut u32).write_unaligned(single);
        if LOGS.fetch_add(1, Ordering::Relaxed) < 8 {
            tracing::info!("SCBL_MOD_SCIS_SHOT_FEEDBACK fp_preset=Sniper_Proto shared_definitions=unchanged fire_interval=unchanged");
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::HashMap;
    fn fixture() -> HashMap<(usize, usize), u32> {
        HashMap::from([
            ((0x10000, 0), 0x02a176c4), ((0x10000, 0x6c), 101), ((0x10000, 0x70), 102),
            ((0x10000, 0x170), 0x20000), ((0x10000, 0x174), 0x30000),
            ((0x20000, 0), 101), ((0x20000, 4), 201), ((0x20000, 8), 2.2f32.to_bits()),
            ((0x30000, 0), 102), ((0x30000, 4), 202), ((0x30000, 8), 10f32.to_bits()),
        ])
    }
    fn names(n: u32, s: &[u8]) -> bool {
        match n {
            101 => s == b"Sniper_Proto", 102 => s == b"Assault_AK47_FirstPerson",
            201 => s == b"CAM_Shake_ShootSingle_1", 202 => s == b"CAM_Shake_ShootAuto_2",
            _ => false,
        }
    }
    fn check(m: &HashMap<(usize, usize), u32>, fp: u8, allowed: bool) -> Option<u32> {
        replacement(0x10000, fp, allowed, |p, o| m.get(&(p, o)).copied(), names)
    }
    #[test]
    fn precise_fp_pair_only_and_no_mutation_of_definitions() {
        let m = fixture(); let before = m.clone();
        assert_eq!(check(&m, 1, true), Some(0x20000));
        for fp in [0, 2, 255] { assert_eq!(check(&m, fp, true), None); }
        assert_eq!(check(&m, 1, false), None);
        assert_eq!(m, before);
    }
    #[test]
    fn missing_or_changed_presets_fail_closed() {
        for key in fixture().keys() {
            let mut m = fixture(); m.remove(key); assert_eq!(check(&m, 1, true), None);
            let mut m = fixture(); m.insert(*key, 0); assert_eq!(check(&m, 1, true), None);
        }
        for gain in [f32::NAN, f32::INFINITY, -1.0, 0.0, 101.0] {
            for p in [0x20000, 0x30000] {
                let mut m = fixture(); m.insert((p, 8), gain.to_bits());
                assert_eq!(check(&m, 1, true), None);
            }
        }
        let mut m = fixture(); m.insert((0x10000, 0x174), 0x20000);
        assert_eq!(check(&m, 1, true), None);
    }
}
