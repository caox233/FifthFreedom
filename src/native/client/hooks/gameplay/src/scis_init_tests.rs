//! Cross-module startup regression, not a GPU or complete game simulation.
use super::*;
use std::ffi::c_void;

#[link(name = "kernel32")]
extern "system" {
    fn VirtualAlloc(p: *mut c_void, n: usize, kind: u32, protect: u32) -> *mut c_void;
    fn VirtualFree(p: *mut c_void, n: usize, kind: u32) -> i32;
    fn LoadLibraryW(p: *const u16) -> *mut c_void;
    fn GetProcAddress(m: *mut c_void, name: *const u8) -> *mut c_void;
}
unsafe extern "thiscall" fn parser_stub(_: usize, _: usize, _: usize) -> u8 { 1 }
unsafe extern "C" fn admitted(text: usize, needle: usize) -> usize {
    if cbytes(text, 13) == Some(b"Sniper_Proto") && cbytes(needle, 4) == Some(b"ADV") { text } else { 0 }
}

#[test]
#[ignore = "requires pinned retail bytes and isolated x86 crash replay"]
fn scis_startup_order_preserves_preload_after_unlock_detour() {
    let bytes = std::fs::read(std::env::var("SCBL_SCIS_CATALOG_NATIVE").unwrap()).unwrap();
    let replay_path = std::env::var("SCBL_SCIS_PRELOAD_REPLAY").unwrap();
    let wide: Vec<u16> = replay_path.encode_utf16().chain([0]).collect();
    unsafe {
        let mem = VirtualAlloc(std::ptr::null_mut(), bytes.len(), 0x3000, 0x40) as *mut u8;
        assert!(!mem.is_null());
        std::ptr::copy_nonoverlapping(bytes.as_ptr(), mem, bytes.len());
        let mut offset = 0;
        let entries: Vec<_> = SIGNATURES.iter().map(|&(address, len, hash)| {
            let mapped = mem.add(offset) as usize; offset += len;
            (address, mapped, len, hash)
        }).collect();
        assert_eq!(offset, bytes.len());
        let failure = || entries.iter().find_map(|&(address, mapped, len, hash)| {
            (!crate::equipment_native::signature(mapped, len, hash)).then_some(address)
        });
        assert_eq!(failure(), None, "pinned game bytes must pass the real SHA gate");
        let entry = entries.iter().find(|e| e.0 == 0xcb6cc0).unwrap().1;
        let parser: unsafe extern "thiscall" fn(usize, usize, usize) -> u8 = std::mem::transmute(entry);
        let hook = retour::GenericDetour::new(parser, parser_stub).unwrap();
        hook.enable().unwrap();
        assert_eq!(failure(), Some(0xcb6cc0), "real unlock detour must reproduce the rejected dependency");
        hook.disable().unwrap();
        assert_eq!(failure(), None, "rollback must restore exact bytes");

        // Read the production startup sequence, not a separately invented order.
        let startup = include_str!("lib.rs");
        let catalog_first = startup.find("mac11_catalog::init(config.world_raycast as usize);").unwrap()
            < startup.find("unlock_catalog::init(config.world_raycast as usize);").unwrap();
        if !catalog_first { hook.enable().unwrap(); }
        let blocked_at = failure();
        let ready = blocked_at.is_none();
        if catalog_first { hook.enable().unwrap(); }
        let module = LoadLibraryW(wide.as_ptr()); assert!(!module.is_null());
        let symbol = GetProcAddress(module, b"scis_preload_replay\0".as_ptr()); assert!(!symbol.is_null());
        let replay: unsafe extern "C" fn(usize) -> u32 = std::mem::transmute(symbol);
        let exception = replay(if ready { admitted as usize } else { 0 });
        println!("SCIS_STARTUP catalog_first={catalog_first} catalog_enabled={ready} preload_enabled={ready} blocked_at={blocked_at:x?} native_select_exception=0x{exception:08x}");
        hook.disable().unwrap();
        assert_eq!(failure(), None);
        // Unknown byte changes must still fail; fixing startup must not bypass validation.
        *mem ^= 1;
        assert_eq!(failure(), Some(SIGNATURES[0].0));
        *mem ^= 1;
        drop(hook); VirtualFree(mem as *mut c_void, 0, 0x8000);
        assert_eq!(exception, 0, "selected SC-IS must not reach F1E2BA with an unprepared resource record");
        assert!(ready);
    }
}
