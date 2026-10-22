//! Shared live ownership guard for the SC-IS presentation adapters.
#[path = "vss_scope_policy.rs"]
mod vss_scope_policy;
pub(crate) fn local_weapon(
    pawn: usize,
    read: impl Fn(usize, usize) -> Option<u32>,
    named: impl Fn(u32) -> bool,
) -> bool {
    let check = || -> Option<bool> {
        let info = read(0x339e5a0, 0)? as usize;
        let world = read(pawn, 0x144)? as usize;
        let pc = read(pawn, 0x938)? as usize;
        let inventory = read(pawn, 0x1410)? as usize;
        let weapon = read(pawn, 0x4b8)? as usize;
        Some(
            world >= 0x10000
                && read(info, 0x144)? as usize == world
                && read(info, 0x330)? & 0x10 != 0
                && read(pawn, 0)? == 0x02ad2abc
                && read(pawn, 0x374)? & 0xe0000 == 0xa0000
                && read(pawn, 0x4c)? & 0xff00 != 0x200
                && read(pc, 0)? == 0x02ac53ec
                && read(pc, 0x60)? & 0x40000 != 0
                && read(pc, 0x520)? as usize == pawn
                && read(pc, 0x144)? as usize == world
                && read(inventory, 0x50)? as usize == weapon
                && weapon >= 0x10000
                && named(read(weapon, 0x740)?)
                && read(pawn, 0x148)? as usize >= 0x10000,
        )
    };
    check() == Some(true)
}

pub(crate) fn owned(pawn: usize) -> bool {
    unsafe { local_weapon(pawn, |p,o| crate::equipment_native::word(p,o),
        |n| crate::mac11_catalog::scis_name(n)) }
}

/// VSS shares BasicScope HUD handling, not the Merc FP/zoom/sway adapters.
/// Use the same local-Spy/MP/current-weapon guard as its scope constructor.
pub(crate) fn vss_owned(pawn: usize) -> bool {
    unsafe { vss_scope_policy::local_weapon(pawn,
        |p,o| crate::equipment_native::word(p,o),
        |n| crate::mac11_catalog::vss_name(n)) }
}

/// Preserve the Spy's hip-fire dot. Hide it only while native BasicScope is
/// active, never from a sampled mouse button or a stale scope actor pointer.
pub(crate) fn vss_scoped(pawn: usize) -> bool {
    vss_owned(pawn) && unsafe {
        crate::equipment_native::word(pawn, 0x948).is_some_and(|v| v & 0x400000 != 0)
    }
}
