//! MP Spy VSS: suppressed PFS-12 base damage, 1.5x falloff distances.
//! E098B0 is read-only. A bounded stack view changes only its tuning inputs;
//! shared campaign archetypes, attachments, ammo, body and armor are untouched.
use crate::equipment_native::{readable, word};
type Damage = unsafe extern "thiscall" fn(usize, usize, f32, u32, f32) -> f32;
const LOGIC_BYTES: usize = 0x154;
const PROPS_BYTES: usize = 0xa4;
unsafe fn eligible(logic: usize, victim: usize) -> Option<(usize, usize, bool)> {
    let world = word(0x339e5a0, 0)? as usize;
    let level = word(world, 0x144)?;
    let weapon = word(logic, 0x28)? as usize;
    let props = word(logic, 0x2c)? as usize;
    let owner = word(weapon, 0x8c)? as usize;
    if level < 0x10000 || word(world, 0x330)? & 0x10 == 0
        || word(owner, 0)? != 0x02ad3234 || word(owner, 0x144)? != level
        || !matches!(word(victim, 0)?, 0x02ad3234 | 0x02ad2abc)
        || word(victim, 0x144)? != level || word(weapon, 0x144)? != level
        || word(weapon, 0)? != 0x02a85b7c || word(weapon, 0x748)? as usize != logic
        || word(weapon, 0x744)? as usize != props || word(props, 0)? != 0x02a176c4
        || !super::super::native_name_is(word(weapon, 0x740)?, b"Sniper_VSS")
        || word(logic, 0x150)? != 40 || word(logic, 0x14c)? != 1300
        || word(props, 0x94)? != 3200 || word(props, 0x98)? != 0.7f32.to_bits()
        || !readable(logic, LOGIC_BYTES) || !readable(props, PROPS_BYTES) { return None; }
    let game = word(level as usize, 0x6d0)? as usize;
    let mode = (word(game, 0x37c)? & 0xff) as u8;
    // Pinned retail 8B5E60 accepts exactly modes 6..=10.
    if !(6..=10).contains(&mode) { return None; }
    let count = word(weapon, 0x768)? as usize;
    let cap = word(weapon, 0x76c)? as usize;
    if count > 32 || count > cap || cap > 256 { return None; }
    let list = word(weapon, 0x764)? as usize;
    let mut scope = false;
    for i in 0..count {
        let a = word(list, i * 4)? as usize;
        if a == 0 { continue; }
        if word(a, 0x144)? != level { return None; }
        if word(a, 0x708)? & 1 == 0 { continue; }
        let p = word(a, 0x700)? as usize;
        if word(p, 0)? != 0x02a1772c { return None; }
        if super::super::native_name_is(word(p, 0x28)?, b"Sniper_VSS_Scope") {
            if scope || word(p, 0xe0)? != 0 || word(p, 0xe4)? != 100f32.to_bits() { return None; }
            scope = true;
        }
    }
    Some((weapon, props, scope))
}
pub(super) unsafe fn calculate(logic: usize, victim: usize, distance: f32, bone: u32, attenuation: f32) -> Option<f32> {
    if !distance.is_finite() || distance < 0.0 || !attenuation.is_finite()
        || attenuation <= 0.0 || !(1..=17).contains(&bone) { return None; }
    let (_, props, scope) = eligible(logic, victim)?;
    let mut l = [0u32; LOGIC_BYTES / 4];
    let mut p = [0u32; PROPS_BYTES / 4];
    std::ptr::copy_nonoverlapping(logic as *const u8, l.as_mut_ptr() as *mut u8, LOGIC_BYTES);
    std::ptr::copy_nonoverlapping(props as *const u8, p.as_mut_ptr() as *mut u8, PROPS_BYTES);
    // Native E00170 applies the default scope +100 after this base. Neutralize
    // only that built-in contribution, so the actual default breakpoint is 2250.
    l[0x14c / 4] = 2250 - if scope { 100 } else { 0 };
    l[0x150 / 4] = 15;
    p[0x94 / 4] = 3000;
    p[0x98 / 4] = 0.6f32.to_bits();
    p[0x9c / 4] = 250f32.to_bits();
    p[0xa0 / 4] = 3f32.to_bits();
    l[0x2c / 4] = p.as_ptr() as u32;
    let native: Damage = std::mem::transmute(0xe098b0usize);
    let value = native(l.as_ptr() as usize, victim, distance, bone, attenuation);
    value.is_finite().then_some(value)
}
