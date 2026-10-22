//! MP SC-IS uses the campaign SVU sound definition. Native CA3E00 owns the
//! imported UObject and its event references; never clone sound/weapon objects.
//! Both listener routes are in that definition (Sam and NPC environment sets).
//! Missing shot references count as failed imports: a named definition alone
//! does not prove that its events were loaded. Keep retail sound and suppression.
use crate::equipment_native::{patch, readable, signature, word, writable};
#[path="scis_sound_owner.rs"] mod owner;
use retour::static_detour;
use std::{cell::Cell, sync::atomic::{AtomicBool, AtomicU32, Ordering}};
static READY: AtomicBool = AtomicBool::new(false);
static IMPORT_LOGS: AtomicU32 = AtomicU32::new(0);
thread_local! { static SCIS_IMPORT: Cell<bool> = const { Cell::new(false) }; }
const IMPORT: usize = 0xca3e00;
const SITES: [usize; 2] = [0xcd6b72, 0xcd6b98];
const PRELOAD_SITES: [usize; 2] = [0xf19117, 0xf19138];
const CACHE_SITE: usize = 0xcd6b83;
const CACHE_ORIGINAL: [u8; 8] = [0x39,0xbe,0x80,0x01,0,0,0x75,0x1e];
#[cfg(feature="scis-runtime-diagnostics")] #[path="scis_sound_trace.rs"] mod shot_trace;
pub(super) fn trace_request(logic:usize,stage:u32) {
 #[cfg(feature="scis-runtime-diagnostics")] unsafe {shot_trace::request(logic,stage);}
 #[cfg(not(feature="scis-runtime-diagnostics"))] let _=(logic,stage);
}
const SVU: &[u8] = b"Sniper_SVU";
type Import = unsafe extern "thiscall" fn(usize, u32) -> usize;
type Suppressed = unsafe extern "thiscall" fn(usize) -> u8;
type Stats = unsafe extern "thiscall" fn(usize, usize, usize) -> usize;
static_detour! {
    static IsSuppressed: unsafe extern "thiscall" fn(usize) -> u8;
    static BuildStats: unsafe extern "thiscall" fn(usize, usize, usize) -> usize;
}
pub(super) fn import_scope<T>(exact_mp_scis: bool, run: impl FnOnce() -> T) -> T {
    struct Reset(bool);
    impl Drop for Reset { fn drop(&mut self) { SCIS_IMPORT.with(|v| v.set(self.0)); } }
    let _reset = Reset(SCIS_IMPORT.with(|v| v.replace(exact_mp_scis)));
    run()
}
fn call(site: usize, target: usize) -> [u8; 5] {
    let mut b = [0xe8, 0, 0, 0, 0];
    b[1..].copy_from_slice(&(target as u32).wrapping_sub(site as u32 + 5).to_le_bytes()); b
}
unsafe fn sound_is(p: usize, name: &[u8]) -> bool {
    word(p, 0) == Some(0x02a0a5ec)
        && word(p, 0x28).is_some_and(|n| super::native_name_is(n, name))
}
// Retail DE8030 selects these End-event fields for single shots, including
// remote listeners. Automatic StartFiring/Sweetener fields may legitimately be
// empty for SVU. Validate at import/cache boundaries, never per audio frame.
unsafe fn shot_events_ready(sound: usize) -> bool {
    [0x98, 0xd4, 0x10c, 0x144, 0x17c].into_iter().all(|start| {
        (0..9).all(|i| word(sound, start + i * 4)
            .is_some_and(|p| word(p as usize, 0) == Some(0x029d5cb4)))
    })
}
// CD6910 calls the sound importer before clearing its MP view-exclusion flags.
// ImportText drops group2 (Sam) or group4 (NPC) accordingly, but the cached SVU
// definition is shared and our readiness check needs all45 single-shot events.
// Both dependency lists are already requested by scis_streams. Allow both
// listener groups only during this exact import; keep group8 excluded and
// restore the caller's context before any fallback or subsequent weapon work.
unsafe fn all_listener_fields<T>(flags: usize, load: impl FnOnce() -> T) -> T {
    let prior = [word(flags, 0), word(flags, 4), word(flags, 8)];
    if !writable(flags, 12) || prior[0] != Some(1)
        || !prior[1..].iter().all(|v| matches!(v, Some(0 | 1)))
    {
        return load();
    }
    let prior = prior.map(|v| v.unwrap());
    struct Restore { flags: usize, prior: [u32; 3] }
    impl Drop for Restore {
        fn drop(&mut self) {
            unsafe {
                for (i, v) in self.prior.iter().enumerate() {
                    ((self.flags + i * 4) as *mut u32).write_unaligned(*v);
                }
            }
        }
    }
    let _restore = Restore { flags, prior };
    ((flags + 4) as *mut u32).write_unaligned(0);
    ((flags + 8) as *mut u32).write_unaligned(0);
    load()
}
// CA3E00 caches a definition as soon as its name is present, even if native
// UObjectProperty::ImportText failed to resolve its shot resources. A second
// import returns that same incomplete object without running LoadConfig again.
// Retry its native configuration at this explicit MP import boundary, not in
// the audio/render loop. Keep engine ownership and the original override order.
unsafe fn refresh_incomplete_svu(manager: usize, sound: usize) {
    if !sound_is(sound, SVU) || shot_events_ready(sound) { return; }
    let Some(flags) = word(manager, 0x28) else { return; };
    let load: unsafe extern "thiscall" fn(usize, u32, usize, usize, usize)
        = std::mem::transmute(0x4d43e0usize);
    let section = b"Echelon.ESoundDefinition.Sniper_SVU\0".as_ptr() as usize;
    load(sound, 0, 0, b"WeaponConfiguration.ini\0".as_ptr() as usize, section);
    if flags & 1 != 0 {
        load(sound, 0, 0, b"ConfigOverrides.ini\0".as_ptr() as usize, section);
    }
}
// The retail MP preloader constructs a temporary ESoundDefinition and collects
// its dependencies without CA3E00. Redirect only its exact SC-IS sound section,
// while its own collector is active. No global loading-mode switch, new bank
// owner or per-frame load: native first/third-person queues retain ownership.
unsafe extern "thiscall" fn preload_config(
    sound: usize, propagate: u32, class: usize, file: usize, section: usize,
) {
    const RETAIL_SECTION: &[u8] = b"Echelon.ESoundDefinition.Sniper_Proto\0";
    let exact = READY.load(Ordering::Acquire) && SCIS_IMPORT.with(Cell::get)
        && word(sound, 0) == Some(0x02a0a5ec)
        && word(0x330c49c, 0).is_some_and(|p| p != 0)
        && word(0x330c498, 0) == Some(1) && word(0x330c4b0, 0) == Some(1)
        && readable(section, RETAIL_SECTION.len())
        && std::slice::from_raw_parts(section as *const u8, RETAIL_SECTION.len()) == RETAIL_SECTION;
    let section = if exact {
        b"Echelon.ESoundDefinition.Sniper_SVU\0".as_ptr() as usize
    } else { section };
    let native: unsafe extern "thiscall" fn(usize, u32, usize, usize, usize)
        = std::mem::transmute(0x4d43e0usize);
    native(sound, propagate, class, file, section);
}
// The native optional-suppression branch can bypass both import callsites on
// cache hits. Re-enter native import only if this exact SC-IS sound cache is for
// the wrong mode. This also restores campaign sound after an MP -> SP switch.
unsafe extern "C" fn reload_cached(props: usize) -> u8 {
    if !READY.load(Ordering::Acquire) || word(props,0) != Some(0x02a176c4)
        || !word(props,0x80).is_some_and(|n| super::scis_name(n)) { return 0; }
    let a = word(props,0x17c).is_some_and(|p| sound_is(p as usize,SVU));
    let b = word(props,0x180).is_some_and(|p| sound_is(p as usize,SVU));
    if SCIS_IMPORT.with(Cell::get) {
        let complete = a && b
            && word(props, 0x17c).is_some_and(|p| shot_events_ready(p as usize))
            && word(props, 0x180).is_some_and(|p| shot_events_ready(p as usize));
        (!complete) as u8
    } else { (a || b) as u8 }
}
static CACHE_IMPORT: usize = 0xcd6b8b;
static CACHE_DONE: usize = 0xcd6ba9;
#[unsafe(naked)]
unsafe extern "C" fn cache_branch() {
    core::arch::naked_asm!(
        // This is an interior branch, not an ABI call boundary. Preserve FPU/
        // SIMD state as well as integer registers, flags and the native frame.
        "pushfd", "pushad", "mov ebp,esp", "sub esp,528", "and esp,-16",
        "fxsave [esp]", "push esi", "call {reload}", "add esp,4",
        "fxrstor [esp]", "mov esp,ebp",
        "test al,al", "jnz 2f", "popad", "popfd",
        "cmp dword ptr [esi+0x180],edi", "jne 3f", "jmp dword ptr [{import}]",
        "2:", "popad", "popfd", "jmp dword ptr [{import}]",
        "3:", "jmp dword ptr [{done}]",
        reload = sym reload_cached, import = sym CACHE_IMPORT, done = sym CACHE_DONE,
    );
}
fn cache_patch() -> [u8;8] {
    let mut bytes = [0x90;8];
    bytes[..5].copy_from_slice(&call(CACHE_SITE,cache_branch as usize));
    bytes[0] = 0xe9; bytes
}
unsafe extern "thiscall" fn import(manager: usize, name: u32) -> usize {
    let native: Import = std::mem::transmute(IMPORT);
    if READY.load(Ordering::Acquire) && SCIS_IMPORT.with(Cell::get)
        && super::scis_name(name) {
        let mut id = 0u32;
        let make_name: unsafe extern "thiscall" fn(usize, usize, u32, u32) -> usize
            = std::mem::transmute(0x4c7d80usize);
        make_name(&mut id as *mut u32 as usize, b"Sniper_SVU\0".as_ptr() as usize, 1, 0);
        if id != 0 && super::native_name_is(id, SVU) {
            let sound = all_listener_fields(0x330c4b4, || {
                let sound = native(manager, id);
                refresh_incomplete_svu(manager, sound);
                sound
            });
            if sound_is(sound, SVU) && shot_events_ready(sound) {
                if IMPORT_LOGS.fetch_add(1, Ordering::Relaxed) < 8 {
                    tracing::info!("SCBL_MOD_SCIS_SOUND stage=import definition=Sniper_SVU shot_references=45 playback=unverified");
                }
                return sound;
            }
        }
        if IMPORT_LOGS.fetch_add(1, Ordering::Relaxed) < 8 {
            tracing::warn!("SCBL_MOD_SCIS_SOUND stage=import fallback=retail reason=svu-definition-or-shot-events-unavailable");
        }
    }
    native(manager, name)
}
// Do not require local-player ownership: peers also instantiate this logic.
// Sound identity ties the override to a successful engine import. The query is
// called during initialization, before the weapon's logic backlink is stored.
unsafe fn unsuppressed_copy(logic: usize) -> bool {
    let Some(world) = word(0x339e5a0, 0).map(|x| x as usize) else { return false; };
    if !word(world, 0x330).is_some_and(|v| v & 0x10 != 0) { return false; }
    let Some(level) = word(world, 0x144).filter(|v| *v >= 0x10000) else { return false; };
    let Some(weapon) = word(logic, 0x28).map(|x| x as usize) else { return false; };
    let Some(props) = word(logic, 0x2c).map(|x| x as usize) else { return false; };
    word(weapon, 0) == Some(0x02a85b7c) && word(weapon, 0x144) == Some(level)
        && word(weapon, 0x744) == Some(props as u32)
        && word(props, 0) == Some(0x02a176c4)
        && word(weapon, 0x740).is_some_and(|n| super::scis_name(n))
        && word(logic, 0x50).is_some_and(|p| sound_is(p as usize, SVU))
}
fn suppressed(logic: usize) -> u8 { unsafe {
    let original = IsSuppressed.call(logic);
    if READY.load(Ordering::Acquire) && unsuppressed_copy(logic) { 0 } else { original }
} }
fn stats(manager: usize, out: usize, name: usize) -> usize { unsafe {
    let result = BuildStats.call(manager, out, name);
    if READY.load(Ordering::Acquire) && result == out && writable(out, 9)
        && super::scis_stats::text(name) == Some(b"SCBL_Merc_SCIS".as_slice()) {
        // EE5E50 output: magazine count, upgrades, suppressed boolean.
        ((out + 8) as *mut u8).write(0);
    }
    result
} }
pub(super) unsafe fn init() {
    if READY.load(Ordering::Acquire) { return; }
    for &(p, n, h) in PINS {
        if !signature(p, n, h) {
            tracing::error!("SCBL_MOD_SCIS_SOUND enabled=false reason=signature address={p:x}"); return;
        }
    }
    if IsSuppressed.initialize(std::mem::transmute::<usize, Suppressed>(0xdfffd0), suppressed).is_err()
        || BuildStats.initialize(std::mem::transmute::<usize, Stats>(0xee5e50), stats).is_err()
        || !SITES.into_iter().all(|site| patch(site, &call(site, IMPORT), &call(site, import as usize)))
        || !PRELOAD_SITES.into_iter().all(|site| patch(site, &call(site, 0x4d43e0), &call(site, preload_config as usize)))
        || !patch(CACHE_SITE, &CACHE_ORIGINAL, &cache_patch())
        || IsSuppressed.enable().is_err() || BuildStats.enable().is_err() {
        deinit(); tracing::error!("SCBL_MOD_SCIS_SOUND enabled=false reason=install"); return;
    }
    READY.store(true, Ordering::Release);
    owner::init();
    tracing::info!("SCBL_MOD_SCIS_SOUND enabled=true source=campaign-SVU mp_copy_only=true native_objects=owned cadence=unchanged");
}
// Diagnostics detour shared Actor::PlaySound. Install only after gameplay
// adapters have validated the pristine entry and acquired their own vtable slots.
pub(super) unsafe fn start_diagnostics() {
    #[cfg(feature="scis-runtime-diagnostics")]
    if READY.load(Ordering::Acquire) { shot_trace::init(); }
}
pub(super) unsafe fn deinit() {
    owner::deinit();
    #[cfg(feature="scis-runtime-diagnostics")] shot_trace::deinit();
    READY.store(false, Ordering::Release);
    if BuildStats.is_enabled() { let _ = BuildStats.disable(); }
    if IsSuppressed.is_enabled() { let _ = IsSuppressed.disable(); }
    for site in PRELOAD_SITES {
        let installed = call(site, preload_config as usize);
        if readable(site, 5) && std::slice::from_raw_parts(site as *const u8, 5) == installed
            && !patch(site, &installed, &call(site, 0x4d43e0)) {
            tracing::error!("SCBL_MOD_SCIS_SOUND restore_failed address={site:x}");
        }
    }
    if readable(CACHE_SITE,8) && std::slice::from_raw_parts(CACHE_SITE as *const u8,8)==cache_patch()
        && !patch(CACHE_SITE,&cache_patch(),&CACHE_ORIGINAL) {
        tracing::error!("SCBL_MOD_SCIS_SOUND restore_failed address=cd6b83");
    }
    for site in SITES {
        let installed = call(site, import as usize);
        if readable(site, 5) && std::slice::from_raw_parts(site as *const u8, 5) == installed
            && !patch(site, &installed, &call(site, IMPORT)) {
            tracing::error!("SCBL_MOD_SCIS_SOUND restore_failed address={site:x}");
        }
    }
}
const PINS: &[(usize, usize, &str)] = &[
    (0xdfffd0,99,"a1da17de7f25336c79f2151f0ac3ca2f1734fbbd832caaa1e9cfa2e2d4ecaa8c"),
    // scis_stats already patched its interior concat call; pin only the entry.
    (0xee5e50,19,"a7e56cce2c4cce4257e757f6e69d876e0491b880f9d7ddb839eb9b2f0c968f4d"),
    (0xcd6b55,84,"99406772622c5061b9007a609971dd070a87190c6b4b5146c7a976532188d4f3"),
    (0x4c7d80,128,"f18d58bb31af44da3906f473ffa052268185ddf199fb0a139542e6ca15c427ee"),
    (0xca3e00,693,"ca7e4322013a4f77bd2e6b70b6b84160552f99ff3f2f4db4374910ba59d86867"),
    (0x4d43e0,1261,"642fed9b67858c7a090d25983c540b9a714c21f56f0e0e04c7340419ee12f00f"),
    (0x511680,1082,"7c70a04828f7fc667f4b7fb3f8006488785ea4a24648bcad0ce748682f883bf4"),
    (0xf190b6,135,"02e404371ee56b46b698dd3f9ae9182ae4935d8f252a96d4fd51ece5c3eb797b"),
];
#[cfg(test)] mod tests {
    use super::*;
    #[test] fn sound_listener_import_preserves_native_context_and_unwind() {
        for prior in [[1u32,1,0],[1,0,1],[1,0,0]] {
            let mut flags=prior;let p=flags.as_mut_ptr() as usize;
            let value=unsafe {all_listener_fields(p,|| {
                assert_eq!([word(p,0),word(p,4),word(p,8)],[Some(1),Some(0),Some(0)]);
                73
            })};
            assert_eq!(value,73);assert_eq!(flags,prior);
            assert!(std::panic::catch_unwind(||unsafe {
                all_listener_fields(p,||panic!("controlled resource error"))
            }).is_err());
            assert_eq!(flags,prior);
        }
    }
    #[test] fn sound_listener_import_leaves_inactive_or_unknown_context_unchanged() {
        for prior in [[0u32,0,0],[0,1,0],[2,0,1],[1,2,0]] {
            let mut flags=prior;let p=flags.as_mut_ptr() as usize;
            unsafe {all_listener_fields(p,|| {
                assert_eq!([word(p,0),word(p,4),word(p,8)],prior.map(Some));
            })};
            assert_eq!(flags,prior);
        }
    }
    #[test] fn scis_sound_scope_resets_nested_calls_threads_and_unwind() {
        assert!(!SCIS_IMPORT.with(Cell::get));
        import_scope(true, || {
            assert!(SCIS_IMPORT.with(Cell::get));
            import_scope(false, || assert!(!SCIS_IMPORT.with(Cell::get)));
            assert!(SCIS_IMPORT.with(Cell::get));
            std::thread::spawn(|| assert!(!SCIS_IMPORT.with(Cell::get))).join().unwrap();
        });
        assert!(!SCIS_IMPORT.with(Cell::get));
        let _ = std::panic::catch_unwind(|| import_scope(true, || panic!("scope unwind")));
        assert!(!SCIS_IMPORT.with(Cell::get));
    }
}
