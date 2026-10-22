//! Read-only retail DX11 sticky-camera input adapter. No key polling/getters.
//! Caller gates support/local pawn/health/UI before invoking the gesture policy.
//! Inventory current type is not equated with virtual type: DDC080 returns 0x11
//! after deployment, while inventory rows retain their native selected key.
use crate::sticky_camera_gesture::Edge;

const ADV_CAMERA_VT: u32 = 0x02ab095c;
const SPY_VT: u32 = 0x02ad3234;
const TYPE_GETTER: u32 = 0x00ddc080;
const SIGNATURES: &[(usize, &[u8])] = &[
    (0xe9ec60, &[85, 139, 236, 139, 137, 80, 5, 0, 0, 139, 81, 44, 83, 86, 51, 192, 87, 133, 210, 126, 34, 139, 73, 40, 139, 117, 12, 139, 125, 8, 139, 255, 15, 182, 25, 59, 222, 117, 8, 15, 182, 89, 1, 59, 223, 116, 17, 64, 131, 193, 4, 59, 194, 124, 233, 95, 94, 50, 192, 91, 93, 194, 8, 0, 95, 94, 176, 1, 91, 93, 194, 8, 0]),
    (0xd6ffd0, &[139, 137, 16, 20, 0, 0, 233, 229, 250, 254, 255]),
    (0xd5fac0, &[83, 86, 87, 139, 249, 139, 119, 60, 51, 192, 133, 246, 126, 41, 139, 79, 56, 139, 209, 131, 58, 0, 116, 10, 139, 26, 138, 91, 40, 58, 95, 48, 116, 14, 64, 131, 194, 4, 59, 198, 124, 233, 95, 94, 51, 192, 91, 195, 139, 4, 129, 133, 192, 117, 6, 95, 94, 51, 192, 91, 195, 131, 120, 48, 0, 126, 244, 139, 64, 44, 139, 0, 95, 94, 91, 195]),
    (0xddc080, &[128, 185, 96, 8, 0, 0, 2, 15, 182, 129, 156, 4, 0, 0, 117, 5, 184, 17, 0, 0, 0, 195]),
    (0xe04430, &[198, 134, 96, 8, 0, 0, 2]),
    (0xdfa510, &[85, 139, 236, 243, 15, 16, 69, 12, 86, 139, 241, 129, 166, 172, 4, 0, 0, 255, 255, 255, 251, 87, 139, 125, 8, 137, 190, 140, 6, 0, 0]),
    (0xe047ae, &[131, 190, 212, 8, 0, 0, 0, 186, 0, 0, 128, 0, 117, 21, 139, 134, 140, 6, 0, 0, 133, 192, 116, 11, 133, 80, 92, 116, 6, 137, 134, 212, 8, 0, 0]),
 ];

fn word(address: usize) -> Option<u32> {
    if address < 0x10000 || address.checked_add(3)? > u32::MAX as usize { return None; }
    unsafe { crate::equipment_native::word(address, 0) }
}
fn at(base: usize, offset: usize) -> Option<u32> { word(base.checked_add(offset)?) }
fn ptr(base: usize, offset: usize) -> Option<usize> {
    let value = at(base, offset)? as usize;
    (value >= 0x10000).then_some(value)
}
fn byte(base: usize, offset: usize) -> Option<u8> { at(base, offset).map(|v| v as u8) }

/// Exact retail signatures; caller checks at installation, not for every input.
/// Read failures/unsupported code must leave native input handling untouched.
pub fn supported() -> bool {
    SIGNATURES.iter().all(|(p, expected)| expected.iter().enumerate().all(|(i, b)| byte(*p, i) == Some(*b)))
        && at(ADV_CAMERA_VT as usize, 0x60c) == Some(TYPE_GETTER)
        && at(ADV_CAMERA_VT as usize, 0x654) == Some(0x00aeabe0)
}

/// Mirror D6FFD0 -> D5FAC0, then admit only the currently selected deployed
/// advanced camera owned by this spy. Do not scan for another deployed object:
/// the first object of the selected row is the native selected object.
pub fn selected_camera(pawn: usize) -> Option<usize> {
    if at(pawn, 0)? != SPY_VT { return None; }
    let pawn_id = at(pawn, 0x20)?;
    let world = ptr(pawn, 0x144)?;
    let world_id = at(world, 0x20)?;
    let inv = ptr(pawn, 0x1410)?;
    let selected = byte(inv, 0x30)?;
    let entries = ptr(inv, 0x38)?;
    let count = at(inv, 0x3c)?;
    if !(1..=64).contains(&count) { return None; }
    for i in 0..count as usize {
        let row = at(entries, i.checked_mul(4)?)? as usize;
        if row == 0 { continue; }
        if byte(row, 0x28)? != selected { continue; }
        if !(1..=64).contains(&at(row, 0x30)?) { return None; }
        let objects = ptr(row, 0x2c)?;
        let camera = ptr(objects, 0)?;
        if at(camera, 0)? != ADV_CAMERA_VT
            || at(ADV_CAMERA_VT as usize, 0x60c)? != TYPE_GETTER
            || byte(camera, 0x49c)? != 0x10
            || byte(camera, 0x860)? != 2
            || at(camera, 0x78)? & 2 != 0
            || ptr(camera, 0x8d4)? != pawn
            || ptr(camera, 0x144)? != world { return None; }
        let camera_id = at(camera, 0x20)?;
        // Same-thread snapshot revalidation; ownership is not inferred merely
        // from an inventory hit and no secondary inventory fallback is used.
        if at(pawn, 0x20)? != pawn_id || ptr(pawn, 0x144)? != world
            || at(world, 0x20)? != world_id || ptr(pawn, 0x1410)? != inv
            || byte(inv, 0x30)? != selected || ptr(inv, 0x38)? != entries
            || at(inv, 0x3c)? != count || ptr(entries, i * 4)? != row
            || byte(row, 0x28)? != selected || ptr(row, 0x2c)? != objects
            || ptr(objects, 0)? != camera || at(camera, 0x20)? != camera_id
            || at(camera, 0)? != ADV_CAMERA_VT || byte(camera, 0x860)? != 2
            || ptr(camera, 0x8d4)? != pawn || ptr(camera, 0x144)? != world
            || at(camera, 0x78)? & 2 != 0 { return None; }
        return Some(camera);
    }
    None
}

/// Translate delivered native input using the controller's active action map.
/// 0x17 UseItem / 0x64 ThrowItem / 0x56 HoldUseItem are abstract actions, not
/// Windows keys. Hold only yields Tick; Gestures::Idle + Tick remains PASS.
pub fn input_edge(controller: usize, delivered: u32) -> Option<Edge> {
    if delivered == 0 || delivered >= 0xbb { return None; }
    let map = ptr(controller, 0x550)?;
    let records = ptr(map, 0x28)?;
    let count = at(map, 0x2c)?;
    if !(1..=256).contains(&count) { return None; }
    let mut found = None;
    for i in 0..count as usize {
        let record = at(records, i.checked_mul(4)?)?.to_le_bytes();
        if u32::from(record[1]) != delivered { continue; }
        let edge = match record[0] {
            0x17 => Edge::Press, 0x64 => Edge::Release, 0x56 => Edge::Tick,
            _ => continue,
        };
        if found.is_some_and(|prior| prior != edge) { return None; }
        found = Some(edge);
    }
    if ptr(controller, 0x550)? != map || ptr(map, 0x28)? != records || at(map, 0x2c)? != count { return None; }
    found
}

/// Stable non-security fingerprint of the complete active mapping. The caller
/// cancels its pending gesture when this changes, including an in-place rebind.
/// Bounded at 256 records; no mutable cache and no native calls.
pub fn binding_fingerprint(controller: usize) -> Option<u64> {
    let map = ptr(controller, 0x550)?;
    let records = ptr(map, 0x28)?;
    let count = at(map, 0x2c)?;
    if !(1..=256).contains(&count) { return None; }
    let mut hash = 0xcbf29ce484222325u64;
    let mut add = |value: u32| {
        for b in value.to_le_bytes() {
            hash ^= u64::from(b);
            hash = hash.wrapping_mul(0x100000001b3);
        }
    };
    add(map as u32); add(records as u32); add(count);
    for i in 0..count as usize { add(at(records, i.checked_mul(4)?)?); }
    if ptr(controller, 0x550)? != map || ptr(map, 0x28)? != records || at(map, 0x2c)? != count { return None; }
    Some(hash)
}
