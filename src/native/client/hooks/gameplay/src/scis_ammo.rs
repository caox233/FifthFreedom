//! Copied MP rifles' reserve contract at native base-magazine, spawn and UI seams.
//! Never edit the shared campaign archetype/config. Native attachment and armor
//! reserve modifiers run after Base; native clamps/refills consume the same cap.
use crate::equipment_native::{readable, signature, word};
use retour::static_detour;
use std::sync::atomic::{AtomicBool, Ordering};
#[path = "scis_ammo_policy.rs"] mod policy;
static READY: AtomicBool = AtomicBool::new(false);
static_detour! {
    static Base: unsafe extern "thiscall" fn(usize) -> i32;
    static Reset: unsafe extern "thiscall" fn(usize);
    static Menu: unsafe extern "thiscall" fn(usize, u32) -> i32;
}

fn weapon_context(weapon: usize, read: impl Fn(usize, usize) -> Option<u32>) -> Option<(u32, usize)> {
    let info = read(0x339e5a0, 0)? as usize;
    if read(info, 0x330)? & 0x10 == 0 || read(weapon, 0)? != 0x02a85b7c { return None; }
    let props = read(weapon, 0x744)? as usize;
    let logic = read(weapon, 0x748)? as usize;
    if read(props, 0)? != 0x02a176c4
        || read(logic, 0x28)? as usize != weapon || read(logic, 0x2c)? as usize != props { return None; }
    Some((read(weapon, 0x740)?, logic))
}
unsafe fn owned(weapon: usize) -> Option<usize> {
    if !READY.load(Ordering::Acquire) { return None; }
    let (name, logic) = weapon_context(weapon, |p, o| word(p, o))?;
    // These identities have copied MP catalog entries but no copied INI section.
    // Do not touch the shared campaign config or the native clip capacity.
    (super::scis_name(name) || super::native_name_is(name, super::vss::RETAIL_NAME))
        .then_some(logic)
}
fn base(logic: usize) -> i32 { unsafe {
    let original = Base.call(logic);
    let enabled = word(logic, 0x28).and_then(|w| owned(w as usize)) == Some(logic);
    policy::base_magazines(enabled, original)
}}
fn reset(weapon: usize) { unsafe {
    Reset.call(weapon);
    let Some(logic) = owned(weapon) else { return; };
    // Only the native fresh-weapon reset, not tick/reload/weapon switching.
    let capacity: unsafe extern "thiscall" fn(usize) -> i32 = std::mem::transmute(0xdff1f0usize);
    let magazines: unsafe extern "thiscall" fn(usize) -> i32 = std::mem::transmute(0xe00260usize);
    let Some(rounds) = policy::initial_reserve(true, capacity(logic), magazines(logic)) else { return; };
    let set: unsafe extern "thiscall" fn(usize, i32, i32) = std::mem::transmute(0xe0b930usize);
    set(logic, rounds, 0);
}}
unsafe fn selected_name(slot: u32) -> Option<Vec<u8>> {
    if slot > 2 { return None; }
    // Same lookup as 195D820; retain the saved/catalog name, not its archetype alias.
    let root = word(0x339e5a4, 0)? as usize;
    let owner = word(root, 0x2c)? as usize;
    let loadout = word(owner, 0x60)? as usize;
    if !readable(loadout, 4) { return None; }
    let get: unsafe extern "thiscall" fn(usize, u32) -> usize = std::mem::transmute(0xe751c0usize);
    let text = get(loadout, slot);
    let data = word(text, 0)? as usize;
    let len = word(text, 4)? as usize;
    let capacity = word(text, 8)? as usize;
    if !(1..=128).contains(&len) || capacity < len || capacity > 1024 || !readable(data, len) { return None; }
    let bytes = std::slice::from_raw_parts(data as *const u8, len);
    if bytes[len-1] != 0 || bytes[..len-1].contains(&0) { return None; }
    Some(bytes[..len-1].to_vec())
}
fn menu(context: usize, slot: u32) -> i32 { unsafe {
    if READY.load(Ordering::Acquire) && word(context, 0x39c).is_some_and(|v| v != 0) {
        if let Some(count) = selected_name(slot).and_then(|n| policy::menu_magazines(true, &n)) { return count; }
    }
    Menu.call(context, slot)
}}
pub(super) unsafe fn init() {
    if READY.load(Ordering::Acquire) { return; }
    for &(p, n, h) in SIGNATURES {
        if !signature(p, n, h) { tracing::error!("SCBL_MOD_SCIS_AMMO enabled=false reason=signature"); return; }
    }
    if Base.initialize(std::mem::transmute::<usize, unsafe extern "thiscall" fn(usize)->i32>(0xde96a0), base).is_err()
        || Reset.initialize(std::mem::transmute::<usize, unsafe extern "thiscall" fn(usize)>(0xe18fe0), reset).is_err()
        || Menu.initialize(std::mem::transmute::<usize, unsafe extern "thiscall" fn(usize,u32)->i32>(0x195d820), menu).is_err()
        || Base.enable().is_err() || Reset.enable().is_err() || Menu.enable().is_err() {
        deinit(); tracing::error!("SCBL_MOD_SCIS_AMMO enabled=false reason=detour"); return;
    }
    READY.store(true, Ordering::Release);
    tracing::info!("SCBL_MOD_SCIS_AMMO enabled=true weapons=scis_vss reserve_magazines=3 total_magazines=4 spawn=full_native_capacity campaign_config=unchanged");
}
pub(super) unsafe fn deinit() {
    READY.store(false, Ordering::Release);
    if Menu.is_enabled() { let _ = Menu.disable(); }
    if Reset.is_enabled() { let _ = Reset.disable(); }
    if Base.is_enabled() { let _ = Base.disable(); }
}
const SIGNATURES: &[(usize,usize,&str)] = &[
    (0xde96a0,170,"09b111e4886f18a44aa9b2cc3a1229a7b0e117adb693645bd07ff512ac92693e"),
    (0xe18fe0,363,"026c45676631ce4ce21ab77c94bda1656b6ed32ca831fd771502dc3e6ed195fa"),
    (0x195d820,536,"423dd1a9bbc53fb149f940740cd96cde14789ea2c13762e6edef0eb6056c491c"),
    (0xe751c0,64,"426b92b83f5ead2ebfe5a0d1b7c7d91336d33f334ff937bcb7a5ceeccda8706b"),
    (0xe00260,188,"29be471b2bfb797500b21411170693fc487138ce347a70d836b18078e3f663de"),
    (0xe0b930,182,"4b1904bfe3f8b0a00fef2e394de72eb133f67a61e22b72c8a125c6af683d9502"),
    (0xdff1f0,214,"054a82ed3655a1d8fe71a5685bf522320b4e30465ac08688fd44e9a139ab2ee8"),
];
#[cfg(test)] mod tests {
    use super::*;
    #[test] fn scis_ammo_owner_gate_rejects_campaign_and_detached_logic() {
        use std::collections::HashMap;
        let (w,p,l,info)=(0x10000,0x20000,0x30000,0x40000);
        let m=HashMap::from([((0x339e5a0,0),info),((info as usize,0x330),0x10),((w,0),0x02a85b7c),
            ((w,0x744),p as u32),((w,0x748),l as u32),((w,0x740),123),((p,0),0x02a176c4),((l,0x28),w as u32),((l,0x2c),p as u32)]);
        assert_eq!(weapon_context(w,|p,o|m.get(&(p,o)).copied()),Some((123,l)));
        for k in m.keys() { let mut bad=m.clone();bad.remove(k);assert_eq!(weapon_context(w,|p,o|bad.get(&(p,o)).copied()),None); }
        let mut campaign=m.clone();campaign.insert((info as usize,0x330),0);assert_eq!(weapon_context(w,|p,o|campaign.get(&(p,o)).copied()),None);
        let mut stale=m;stale.insert((l,0x28),0x99999);assert_eq!(weapon_context(w,|p,o|stale.get(&(p,o)).copied()),None);
    }
}
