//! F9 queues the original MP pawn fatal request; the native pawn tick owns death.
//! No HP writes, DestroyActor, invented damage packet, polling or secondary hook.
use super::{gameplay_input_context, memory::{float, signature, word}, suicide_policy::{Identity, State}};
use std::sync::{Mutex, atomic::{AtomicBool, Ordering}};
const REQUEST: usize = 0x00d14aa0;
static READY: AtomicBool = AtomicBool::new(false);
static STATE: Mutex<State> = Mutex::new(State::new());
const SIGNATURES: &[(usize, usize, &str)] = &[
    (0xc71b80, 44, "e04e0feb2d65d32953c4463b2f42c78a69b7f7ed7c80ebd659bb2f7c74ae4241"),
    (0xd14aa0, 57, "16d9d01e673552f1ec3ac423c9227dc49b8edfb466e58df0ed27e8767fed1b12"),
    (0xdb0630, 87, "6d4374294086abc9ae22f68fe779f0ce4ea84d53a222426ec500cc0ba1d6ce26"),
    (0xd14ae0, 109, "86af56644c80441949d2f67505c9da79e30ca85fd1a61fd0a79dceb7cde35124"),
    (0xe65cd0, 31, "3874ff24bfdee2858bdb6c99ad63ae64852eb0086d0599c6e1cae499a3fbaee6"),
    (0x8c8d30, 91, "d94316efe577dd570e1d0ab9d686d236e0e38c19ee31832c3046eda9d9ced38c"),
    (0x8c8f00, 13, "0c75854f23ba9c9008f97f3789184d69b34f025c2cb8f549cf8f2220d23e0d75"),
    (0x8b5e60, 42, "316b0873cfe3c1d6a1732fe5e375dffe56eaefa2da6420be43afc9312bfab600"),
    (0xe5ffa0, 4, "cf038c6c47f8fe220ea8191589a6f143aa127fb93e688ed3a3cd3eeaa1fad646"),
    (0xb8b300, 10, "2c20847dd16be552bfa3d838263dd37a9376bf1cefad573855261e56cdd490b3"),
];
#[link(name="kernel32")]
extern "system" { fn GetTickCount64() -> u64; }
unsafe fn supported() -> bool {
    SIGNATURES.iter().all(|&(address, size, hash)| signature(address, size, hash))
}
pub(super) unsafe fn init() {
    if READY.load(Ordering::Acquire) { return; }
    let ready = supported();
    READY.store(ready, Ordering::Release);
    tracing::info!("SCBL_GAMEPLAY_F9 active={} cooldown_ms=10000 native_request=true", ready);
}
pub(super) fn deinit() {
    READY.store(false, Ordering::Release);
    if let Ok(mut s) = STATE.lock() { s.suspend(); }
}
unsafe fn live_identity(viewport: usize) -> Option<Identity> {
    let c = gameplay_input_context::current(viewport)?;
    let vt = word(c.pawn, 0)? as usize;
    // Retail local player pawn classes only. Preserve their original virtual
    // request/damage/health routes and native local-owned role (byte +0x4d == 1).
    if !matches!(vt, 0x02ad3234 | 0x02ad2abc)
        || word(vt, 0x4fc)? as usize != REQUEST
        || word(vt, 0x31c)? != 0x00cd22b0
        || word(vt, 0x328)? != 0x00c71b80
        || word(vt, 0x330)? != 0x00b8b300
        || (word(c.pawn, 0x4c)? >> 8) & 0xff != 1
        || word(c.pawn, 0x60)? & 0x800 != 0 { return None; }
    let health = word(c.pawn, 0x4dc)? as usize;
    let value = float(health, 0x6c)?; // Native C71B80 -> E5FFA0 getter, read only.
    if !value.is_finite() || value <= 0.0 { return None; }
    let info = word(c.world, 0x6d0)? as usize;
    let mode = word(info, 0x37c)? & 0xff;
    if !(6..=10).contains(&mode) || word(c.pawn, 0x950)? & 0x04000000 != 0 { return None; }
    Some(Identity { viewport: c.viewport, window: c.window, controller: c.controller,
        pawn: c.pawn, world: c.world, controller_identity: c.controller_identity,
        pawn_identity: c.pawn_identity, world_identity: c.world_identity })
}
/// Called by the existing input owner's window detour, including before IME
/// consumes a key. The shared context rejects active text entry independently.
pub(super) unsafe fn window(viewport: usize, message: u32, key: usize, flags: isize) -> bool {
    if !READY.load(Ordering::Acquire) { return false; }
    if message == 0x8 || (message == 0x1c && key == 0) || message == 0x215 || message == 0x2 {
        if let Ok(mut s) = STATE.lock() { s.suspend(); }
        return false;
    }
    if key != 0x78 || !matches!(message, 0x100 | 0x101 | 0x105) { return false; }
    if message != 0x100 {
        if let Ok(mut s) = STATE.lock() { s.up(); }
        return false; // Original key-up/IME cleanup remains intact.
    }
    let id = live_identity(viewport);
    if let Ok(mut s) = STATE.lock() {
        s.down(id, GetTickCount64(), flags as usize & (1 << 30) != 0);
    }
    id.is_some() // Only gameplay F9 is reserved; text/menu keys remain native.
}
/// Queue only; native DB0630 consumes the request on its normal pawn tick.
pub(super) unsafe fn frame(viewport: usize) {
    if !READY.load(Ordering::Acquire) { return; }
    let pending = STATE.lock().ok().and_then(|s| s.pending_viewport());
    if pending != Some(viewport) { return; }
    let current = live_identity(viewport);
    // Validate again immediately before dispatch. Fail closed on foreign changes.
    if !supported() { deinit(); return; }
    let request = STATE.lock().ok().and_then(|mut s| s.take(current, GetTickCount64()));
    if let Some(id) = request {
        let native: unsafe extern "thiscall" fn(usize) = std::mem::transmute(REQUEST);
        native(id.pawn);
        let queued = word(id.pawn, 0x950).is_some_and(|v| v & 0x04000000 != 0);
        tracing::info!("SCBL_GAMEPLAY_F9 native_request_queued={} cooldown_ms=10000", queued);
    }
}
