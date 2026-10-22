//! Campaign SC-IS has no ADVFP mesh. Retail MP requests either the first-
//! or third-person resource list, but the weapon renderer can fall back to
//! its normal mesh. Carry that same weapon's resources into the FP request;
//! retain the native streaming, ownership, loading and release lifecycle.
use crate::equipment_native::{readable, word, writable};

const MAX_ASSETS: usize = 1024;
const MESH_PAIRS: &[(usize, usize)] = &[(0x10, 0x1c)];
// Mesh fallback remains TP -> FP only. Sound definitions are shared by both
// listeners and scis_sound validates all 45 single-shot events before import.
// Retail TP streaming otherwise supplies only the 36 NPC events, so a remote
// SC-IS would fall back even after its selected stream completed successfully.
// Carry sound dependencies in both directions, including the suppressed lane
// selected by the original archetype before the MP sound override is imported.
const WEAPON_PAIRS: &[(usize, usize)] = &[
    (0x10, 0x1c), (0x34, 0x40), (0x4c, 0x58), (0x40, 0x34), (0x58, 0x4c),
];
const UPGRADE_PAIRS: &[(usize, usize)] = &[(0x08, 0x14)];

pub(super) fn upgrade(name: &[u8]) -> bool {
    matches!(name, b"Sniper_Proto_Scope" | b"Optic_HighPowerScope")
}

unsafe fn assets(record: usize, offset: usize) -> Option<Vec<u32>> {
    let data = word(record, offset)? as usize;
    let len = word(record, offset + 4)? as usize;
    let cap = word(record, offset + 8)? as usize;
    if len > cap || cap > MAX_ASSETS || (len != 0 && !readable(data, len * 4)) {
        return None;
    }
    if len == 0 {
        return Some(Vec::new());
    }
    Some(std::slice::from_raw_parts(data as *const u32, len).to_vec())
}

// Preflight every list before invoking the native allocator. Never alias the
// arrays: the retail destructors free them independently. No persistent pointer
// cache and no per-frame work. The callback is retail TArray<FName>::AddUnique.
pub(super) unsafe fn bridge(
    record: usize,
    weapon: bool,
    add: impl FnMut(usize, u32),
) -> Option<usize> {
    let pairs = if weapon { WEAPON_PAIRS } else { UPGRADE_PAIRS };
    bridge_pairs(record, pairs, add)
}

unsafe fn bridge_pairs(
    record: usize,
    pairs: &[(usize, usize)],
    mut add: impl FnMut(usize, u32),
) -> Option<usize> {
    let mut pending = Vec::new();
    for &(dst, src) in pairs {
        let data = word(record, dst)? as usize;
        let cap = word(record, dst + 8)? as usize;
        if !writable(record + dst, 12)
            || cap > MAX_ASSETS
            || (cap != 0 && !writable(data, cap * 4))
            || (data != 0 && word(record, src) == Some(data as u32))
        {
            return None;
        }
        let mut existing = assets(record, dst)?;
        for id in assets(record, src)? {
            if id != 0 && !existing.contains(&id) {
                if existing.len() == MAX_ASSETS {
                    return None;
                }
                existing.push(id);
                pending.push((record + dst, id));
            }
        }
    }
    let count = pending.len();
    for (dst, id) in pending {
        add(dst, id);
    }
    Some(count)
}

unsafe fn record(manager: usize, name: u32, weapon: bool) -> Option<usize> {
    let (offset, stride) = if weapon { (0x334, 0x64) } else { (0x340, 0x2c) };
    let data = word(manager, offset)? as usize;
    let len = word(manager, offset + 4)? as usize;
    let cap = word(manager, offset + 8)? as usize;
    if len == 0 || len > cap || cap > 2048 || !readable(data, len * stride) {
        return None;
    }
    let mut found = None;
    for i in 0..len {
        let p = data + i * stride;
        if word(p, 0) == Some(name) {
            if found.is_some() {
                return None;
            }
            found = Some(p);
        }
    }
    found
}

pub(super) unsafe fn complete(manager: usize, name: u32, weapon: bool) {
    complete_with(manager, name, weapon, false);
}

pub(super) unsafe fn complete_mesh(manager: usize, name: u32) {
    complete_with(manager, name, true, true);
}

unsafe fn complete_with(manager: usize, name: u32, weapon: bool, mesh_only: bool) {
    let Some(p) = record(manager, name, weapon) else {
        tracing::warn!(
            "SCBL_MOD_SCIS_STREAM stage=record-missing weapon={}",
            weapon
        );
        return;
    };
    let add: unsafe extern "thiscall" fn(usize, *const u32) -> u32 =
        std::mem::transmute(0x452800usize);
    let append = |dst, id| {
        add(dst, &id);
    };
    let result = if mesh_only {
        bridge_pairs(p, MESH_PAIRS, append)
    } else {
        bridge(p, weapon, append)
    };
    tracing::info!(
        "SCBL_MOD_SCIS_STREAM weapon={} mesh_only={} additional_stream_resources={:?} native_streaming=true",
        weapon,
        mesh_only,
        result
    );
}

#[cfg(test)]
mod tests {
    use super::*;
    fn list(record: &mut [u32], offset: usize, data: &mut [u32], len: usize) {
        record[offset / 4] = data.as_mut_ptr() as u32;
        record[offset / 4 + 1] = len as u32;
        record[offset / 4 + 2] = data.len() as u32;
    }
    unsafe fn add(dst: usize, id: u32) {
        let n = word(dst, 4).unwrap() as usize;
        assert!(n < word(dst, 8).unwrap() as usize);
        let data = word(dst, 0).unwrap() as *mut u32;
        data.add(n).write(id);
        ((dst + 4) as *mut u32).write((n + 1) as u32);
    }
    #[test]
    fn vss_local_mesh_bridge_does_not_modify_sound_or_original_mesh_lists() {
        let mut r = [0u32; 25];
        let mut local = [0u32; 4];
        let mut normal = [77, 78, 0, 0];
        let mut local_sound = [9, 0, 0, 0];
        let mut remote_sound = [36, 0, 0, 0];
        list(&mut r, 0x10, &mut local, 0);
        list(&mut r, 0x1c, &mut normal, 2);
        list(&mut r, 0x34, &mut local_sound, 1);
        list(&mut r, 0x40, &mut remote_sound, 1);
        let original = r;
        let p = r.as_mut_ptr() as usize;
        assert_eq!(unsafe { bridge_pairs(p, MESH_PAIRS, |d, i| add(d, i)) }, Some(2));
        assert_eq!(local, [77, 78, 0, 0]);
        assert_eq!(normal, [77, 78, 0, 0]);
        assert_eq!(local_sound, [9, 0, 0, 0]);
        assert_eq!(remote_sound, [36, 0, 0, 0]);
        assert_eq!(&r[0x28 / 4..], &original[0x28 / 4..]);
        assert_eq!(unsafe { bridge_pairs(p, MESH_PAIRS, |_, _| panic!("duplicate dependency")) }, Some(0));
    }
    #[test]
    fn scis_sound_dependencies_are_complete_for_both_listener_streams() {
        let mut r = [0u32; 25];
        let mut fp = [9, 0, 0, 0];
        let mut tp = [36, 0, 0, 0];
        let mut suppressed_fp = [19, 0, 0, 0];
        let mut suppressed_tp = [46, 0, 0, 0];
        list(&mut r, 0x34, &mut fp, 1);
        list(&mut r, 0x40, &mut tp, 1);
        list(&mut r, 0x4c, &mut suppressed_fp, 1);
        list(&mut r, 0x58, &mut suppressed_tp, 1);
        let before = r;
        let p = r.as_mut_ptr() as usize;
        assert_eq!(unsafe { bridge(p, true, |d, i| add(d, i)) }, Some(4));
        assert_eq!(fp, [9, 36, 0, 0]);
        assert_eq!(tp, [36, 9, 0, 0]);
        assert_eq!(suppressed_fp, [19, 46, 0, 0]);
        assert_eq!(suppressed_tp, [46, 19, 0, 0]);
        for offset in [0x34, 0x40, 0x4c, 0x58] {
            assert_eq!(r[offset / 4], before[offset / 4]);
        }
        assert_eq!(unsafe { bridge(p, true, |d, i| add(d, i)) }, Some(0));
        // Upgrade/other resource records must not acquire weapon sound lanes.
        assert_eq!(unsafe { bridge(p, false, |_, _| panic!("sound lane on upgrade")) }, Some(0));
    }
    #[test]
    fn scis_streams_bridge_is_owned_idempotent_and_rejects_bad_arrays() {
        let mut r = [0u32; 25];
        let mut fp = [11, 0, 0, 0];
        let mut tp = [22, 11, 22, 0];
        list(&mut r, 0x10, &mut fp, 1);
        list(&mut r, 0x1c, &mut tp, 4);
        let before = r;
        let p = r.as_mut_ptr() as usize;
        assert_eq!(unsafe { bridge(p, true, |d, i| add(d, i)) }, Some(1));
        assert_eq!(fp, [11, 22, 0, 0]);
        assert_eq!(tp, [22, 11, 22, 0]);
        assert_eq!(r[0x10 / 4], before[0x10 / 4]);
        assert_eq!(unsafe { bridge(p, true, |d, i| add(d, i)) }, Some(0));
        // Last list invalid: not even the first valid list may be modified.
        r[0x10 / 4 + 1] = 0;
        r[0x58 / 4 + 1] = 2;
        r[0x58 / 4 + 2] = 1;
        let before = r;
        assert_eq!(
            unsafe { bridge(p, true, |_, _| panic!("partial write")) },
            None
        );
        assert_eq!(r, before);
        assert!(upgrade(b"Sniper_Proto_Scope"));
        assert!(upgrade(b"Optic_HighPowerScope"));
        assert!(!upgrade(b"ADV_FP_Assault_Sight_ScopeAcog"));
        assert!(!upgrade(b"Sniper_Proto_Scope_1"));
    }
    #[test]
    #[ignore = "explicit x86 retail first-person stream selector replay"]
    fn scis_streams_native_selector_replay() {
        use std::ffi::c_void;
        #[link(name = "kernel32")]
        extern "system" {
            fn LoadLibraryW(path: *const u16) -> *mut c_void;
            fn GetProcAddress(m: *mut c_void, n: *const u8) -> *mut c_void;
        }
        unsafe extern "C" fn repair(p: usize, weapon: u32) -> u32 {
            bridge(p, weapon != 0, |d, i| add(d, i))
                .map(|_| 1)
                .unwrap_or(0)
        }
        let path = std::env::var("SCBL_SCIS_STREAM_REPLAY").expect("explicit fixture required");
        let wide: Vec<u16> = path.encode_utf16().chain([0]).collect();
        unsafe {
            let h = LoadLibraryW(wide.as_ptr());
            assert!(!h.is_null());
            let p = GetProcAddress(h, b"scis_stream_replay\0".as_ptr());
            assert!(!p.is_null());
            let run: unsafe extern "C" fn(usize) -> u32 = std::mem::transmute(p);
            let installed = super::super::streams_installed_in_source();
            let mask = run(if installed { repair as usize } else { 0 });
            println!(
                "SCIS_STREAM fp_gun={} fp_scope={} native_tp={} source_hooks={}",
                mask & 1 != 0,
                mask & 2 != 0,
                mask & 4 != 0,
                installed
            );
            assert_eq!(mask,7,"local FP request must include original SC-IS gun and scope, without removing remote TP assets");
        }
    }
}
