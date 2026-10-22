//! Shared sniper kill-feed presentation. Retail category 11 has no HUD branch.
//! Reuse the registered SC6Weapons sniper image from dynamicflash.umd, not an
//! invented image identifier or the assault icon. Future supported sniper
//! registrations can share this resource without changing damage categories.
use crate::equipment_native::{patch, readable, signature, word};
use std::sync::atomic::{AtomicBool, Ordering};

static READY: AtomicBool = AtomicBool::new(false);
const SITE: usize = 0x1a9f933;
const SELECT: usize = 0x1a71020;
// SELECT returns preprocessed HTML: retail 1A87140 expands its cached icons
// with 18BC800 before storing them. The kill-feed consumer does not expand
// {Img...} macros again. Use the same ChatboxIcon2HWeapon dimensions, with the
// full registered resource URI (a dot is a macro argument separator).
const SNIPER_ICON: &str = "<img src='img://SC6Weapons\\Sniper_Proto_128x64.dds' width='58' height='25' vspace='-11'>";

fn display_category(
    enabled: bool,
    weapon: usize,
    category: u32,
    read: impl Fn(usize, usize) -> Option<u32>,
    named: impl Fn(u32) -> bool,
) -> u32 {
    let exact = || -> Option<bool> {
        if !enabled || category != 11 { return Some(false); }
        let info = read(0x339e5a0, 0)? as usize;
        let world = read(info, 0x144)?;
        let props = read(weapon, 0x744)? as usize;
        let logic = read(weapon, 0x748)? as usize;
        Some(world >= 0x10000
            && read(info, 0x330)? & 0x10 != 0
            && read(weapon, 0)? == 0x02a85b7c
            && read(weapon, 0x144)? == world
            && read(props, 0)? == 0x02a176c4
            && (read(props, 0x38)? >> 8) & 0xff == 11
            && read(logic, 0x28)? as usize == weapon
            && read(logic, 0x2c)? as usize == props
            && named(read(weapon, 0x740)?))
    };
    if exact() == Some(true) { 3 } else { category }
}

unsafe extern "C" fn bridge(hud: usize, out: usize, category: u32, weapon: usize) -> usize {
    // Both copied snipers retain their exact retail combat FName. The event
    // weapon, not the local equipped weapon or catalog alias, owns this icon.
    let visual = display_category(READY.load(Ordering::Acquire), weapon, category,
        |p, o| word(p, o), |n| super::scis_name(n) || super::vss_name(n));
    if category == 11 && visual == 3 {
        // The caller supplies an uninitialized FString, exactly like SELECT.
        // Native construction copies UTF-16 into engine-owned storage; never
        // publish a Rust allocation to an engine FString destructor.
        let text: Vec<u16> = SNIPER_ICON.encode_utf16().chain(Some(0)).collect();
        let construct: unsafe extern "thiscall" fn(usize, *const u16) -> usize =
            std::mem::transmute(0x44ef80usize);
        return construct(out, text.as_ptr());
    }
    let native: unsafe extern "thiscall" fn(usize, usize, u32) -> usize = std::mem::transmute(SELECT);
    native(hud, out, category)
}

// This is a single guarded call-site adapter, NOT a general thiscall entry.
// Exact 1A9F700 pins EBP and sets [EBP-6C] to the damage-record weapon before
// 1A9F933. Push that identity plus the original selector arguments for cdecl;
// return with the original selector's RET 8. No TLS or current-weapon guess.
core::arch::global_asm!(
    ".text",
    ".globl _scis_kill_icon_thunk",
    "_scis_kill_icon_thunk:",
    "push dword ptr [ebp - 0x6c]",
    "push dword ptr [esp + 12]",
    "push dword ptr [esp + 12]",
    "push ecx",
    "call {bridge}",
    "add esp, 16",
    "ret 8",
    bridge = sym bridge,
);
extern "C" { fn scis_kill_icon_thunk(); }

fn call(target: usize) -> [u8; 5] {
    let mut bytes = [0xe8, 0, 0, 0, 0];
    bytes[1..].copy_from_slice(&(target as u32).wrapping_sub(SITE as u32 + 5).to_le_bytes());
    bytes
}
pub(super) unsafe fn init() {
    if READY.load(Ordering::Acquire) { return; }
    for &(address, size, hash) in SIGNATURES {
        if !signature(address, size, hash) {
            tracing::error!("SCBL_MOD_SCIS_KILL_ICON enabled=false reason=signature address={address:x}");
            return;
        }
    }
    if !patch(SITE, &call(SELECT), &call(scis_kill_icon_thunk as usize)) {
        tracing::error!("SCBL_MOD_SCIS_KILL_ICON enabled=false reason=callsite");
        return;
    }
    READY.store(true, Ordering::Release);
    tracing::info!("SCBL_MOD_SCIS_KILL_ICON enabled=true display=shared-sniper image=SC6Weapons/Sniper_Proto_128x64.dds representation=html weapon_category=unchanged source=damage-event");
}
pub(super) unsafe fn deinit() {
    READY.store(false, Ordering::Release);
    let installed = call(scis_kill_icon_thunk as usize);
    if readable(SITE, 5) && std::slice::from_raw_parts(SITE as *const u8, 5) == installed
        && !patch(SITE, &installed, &call(SELECT)) {
        tracing::error!("SCBL_MOD_SCIS_KILL_ICON restore_failed site=1a9f933");
    }
}
const SIGNATURES: &[(usize, usize, &str)] = &[
    (0x44ef80, 202, "c9c357c9a0e1147b07928f358b5969a02d5266e4b3772ace4ba97cc79b653a69"),
    (0x1a9f700, 1009, "f0e5cbd96bdae6dfb715d9c1556b73ac082ee30cd59d4727604912f50542fd45"),
    (0x1a71020, 344, "fe51ba36eb62828623546dac24a41c8280a5acb94f8458467032ea693f9c135a"),
    (0xdd51e0, 11, "70571f6c58e99b7eb339e24b209c9816b158f86527fc18a1176a34f8433ef192"),
];

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::HashMap;
    #[test]
    fn scis_kill_icon_returns_consumer_html_not_an_unexpanded_macro() {
        assert_eq!(SNIPER_ICON, "<img src='img://SC6Weapons\\Sniper_Proto_128x64.dds' width='58' height='25' vspace='-11'>");
        assert!(!SNIPER_ICON.contains("{Img."));
        assert!(!SNIPER_ICON.contains("ADV_ICON_CL_Assault"));
        assert!(!SNIPER_ICON.contains('\0'));
    }
    fn fixture() -> HashMap<(usize, usize), u32> {
        HashMap::from([
            ((0x339e5a0, 0), 0x10000),
            ((0x10000, 0x144), 0x50000), ((0x10000, 0x330), 0x10),
            ((0x20000, 0), 0x02a85b7c), ((0x20000, 0x144), 0x50000),
            ((0x20000, 0x740), 123), ((0x20000, 0x744), 0x30000),
            ((0x20000, 0x748), 0x40000), ((0x30000, 0), 0x02a176c4),
            ((0x30000, 0x38), 0x0b00), ((0x40000, 0x28), 0x20000),
            ((0x40000, 0x2c), 0x30000),
        ])
    }
    #[test]
    fn scis_kill_icon_only_maps_display_argument_for_exact_mp_event_weapon() {
        let data = fixture();
        let read = |p, o| data.get(&(p, o)).copied();
        for category in 0..=255 {
            assert_eq!(display_category(true, 0x20000, category, read, |n| n == 123),
                if category == 11 { 3 } else { category });
        }
        assert_eq!(display_category(false, 0x20000, 11, read, |_| panic!("disabled lookup")), 11);
        assert_eq!(display_category(true, 0x20000, 11, read, |_| false), 11);
        assert_eq!(data, fixture());
    }
    #[test]
    fn scis_kill_icon_rejects_missing_identity_world_and_logic_links() {
        let source = fixture();
        for key in source.keys() {
            let mut missing = source.clone(); missing.remove(key);
            assert_eq!(display_category(true, 0x20000, 11,
                |p,o| missing.get(&(p,o)).copied(), |n| n == 123), 11, "missing {key:?}");
            let mut wrong = source.clone(); wrong.insert(*key, 0);
            assert_eq!(display_category(true, 0x20000, 11,
                |p,o| wrong.get(&(p,o)).copied(), |n| n == 123), 11, "zero {key:?}");
        }
    }
    #[test]
    fn scis_kill_icon_does_not_use_adjacent_category_padding_or_selected_weapon() {
        let mut data = fixture(); data.insert((0x30000, 0x38), 0xabcd0bef);
        // Selector category is the native byte +39, not adjoining bytes or a
        // local player's selected gun. This also admits remote SCIS kills.
        assert_eq!(display_category(true, 0x20000, 11,
            |p,o| data.get(&(p,o)).copied(), |n| n == 123), 3);
        data.insert((0x20000, 0x144), 0x60000);
        assert_eq!(display_category(true, 0x20000, 11,
            |p,o| data.get(&(p,o)).copied(), |n| n == 123), 11);
    }
}
