//! MP-only VSS casing dependency. Keep native VFX ownership and class validation.
//! Retail UClassProperty::ImportText calls its parser at 511B15, then validates
//! the resulting UClass. Never replace the whole VFX record or campaign config.
use crate::equipment_native::{patch, readable, signature, word};
use std::cell::Cell;
use std::sync::atomic::{AtomicBool, AtomicU32, Ordering};
thread_local! { static VSS_MP: Cell<bool> = const { Cell::new(false) }; }
static READY: AtomicBool = AtomicBool::new(false);
static LOGS: AtomicU32 = AtomicU32::new(0);
const SITE: usize = 0x511b15;
const PARSER: usize = 0x511680;
const SOURCE: &[u8] = b"class'EchelonEffect.SC6WeaponGadgetCaseEjectRifle';";
// Parser return cursors are compared by address: every call site must use
// the same backing bytes, not independently promoted const expressions.
static TARGET: &[u8] = b"class'EchelonEffect.SC6ADVWeaponGadgetCaseEjectRifle';\0";
const INSERT: usize = 23; // immediately after SC6

type Parser = unsafe extern "thiscall" fn(usize, usize, usize, u32) -> usize;
pub(super) fn scope<T>(exact_mp_vss: bool, run: impl FnOnce() -> T) -> T {
    struct Reset(bool);
    impl Drop for Reset { fn drop(&mut self) { VSS_MP.with(|v| v.set(self.0)); } }
    let _reset = Reset(VSS_MP.with(|v| v.replace(exact_mp_vss)));
    run()
}
fn bytes(target: usize) -> [u8; 5] {
    let mut result = [0xe8, 0, 0, 0, 0];
    result[1..].copy_from_slice(&(target as u32).wrapping_sub(SITE as u32 + 5).to_le_bytes());
    result
}
// Return a cursor into the caller's original text, not our longer borrowed
// replacement. The native class validator and semicolon handling remain intact.
fn original_cursor(result: usize, original: usize) -> usize {
    let start = TARGET.as_ptr() as usize;
    if result < start || result >= start + TARGET.len() { return result; }
    let offset = result - start;
    original + if offset <= INSERT { offset } else { offset.saturating_sub(3).max(INSERT) }
}
unsafe fn import_with(property: usize, text: usize, out: usize, flags: u32,
    native: impl FnOnce(usize, usize, usize, u32) -> usize) -> usize {
    let exact = READY.load(Ordering::Acquire) && VSS_MP.with(Cell::get)
        && word(property, 0x20).is_some_and(|n| super::super::native_name_is(n, b"m_CaseEjectClass"))
        && super::super::cbytes(text, SOURCE.len() + 1) == Some(SOURCE);
    if !exact { return native(property, text, out, flags); }
    let result = native(property, TARGET.as_ptr() as usize, out, flags);
    if LOGS.fetch_add(1, Ordering::Relaxed) < 8 {
        tracing::info!("SCBL_MOD_VSS_VFX property=m_CaseEjectClass source=campaign target=retail-ADV parser_returned={} class_validation=native", result != 0);
    }
    original_cursor(result, text)
}
unsafe extern "thiscall" fn import(property: usize, text: usize, out: usize, flags: u32) -> usize {
    let native: Parser = std::mem::transmute(PARSER);
    import_with(property, text, out, flags, |p, t, o, f| native(p, t, o, f))
}
pub(super) unsafe fn init() -> bool {
    if READY.load(Ordering::Acquire) { return true; }
    if !signature(0x511b00, 121, "745acef1de7191f9310327719aee9d7f3b3355f269501ef5c88b7b34ede66877")
        || !patch(SITE, &bytes(PARSER), &bytes(import as usize)) { return false; }
    READY.store(true, Ordering::Release);
    true
}
pub(super) unsafe fn deinit() {
    READY.store(false, Ordering::Release);
    if readable(SITE, 5) && std::slice::from_raw_parts(SITE as *const u8, 5) == bytes(import as usize)
        && !patch(SITE, &bytes(import as usize), &bytes(PARSER)) {
        tracing::error!("SCBL_MOD_VSS_VFX restore_failed site=511b15");
    }
}
#[cfg(test)] mod tests {
    use super::*;
    #[test] fn vss_vfx_scope_preserves_nested_threads_and_unwind() {
        assert!(!VSS_MP.with(Cell::get));
        scope(true, || {
            assert!(VSS_MP.with(Cell::get));
            scope(false, || assert!(!VSS_MP.with(Cell::get)));
            assert!(VSS_MP.with(Cell::get));
            std::thread::spawn(|| assert!(!VSS_MP.with(Cell::get))).join().unwrap();
        });
        let _ = std::panic::catch_unwind(|| scope(true, || panic!("scope unwind")));
        assert!(!VSS_MP.with(Cell::get));
    }
    #[test] fn vss_vfx_cursor_maps_inserted_adv_and_native_null() {
        assert_eq!(&TARGET[..INSERT], &SOURCE[..INSERT]);
        assert_eq!(&TARGET[INSERT..INSERT+3], b"ADV");
        assert_eq!(&TARGET[INSERT+3..TARGET.len()-1], &SOURCE[INSERT..]);
        let target = TARGET.as_ptr() as usize;
        for i in 0..=SOURCE.len() {
            let actual = i + if i > INSERT { 3 } else { 0 };
            assert_eq!(original_cursor(target+actual, 1000), 1000+i);
        }
        assert_eq!(original_cursor(0, 1000), 0);
    }
}
