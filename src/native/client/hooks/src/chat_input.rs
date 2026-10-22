//! Chat-only UTF-16 commits and reversible per-window IME ownership.
//! No keyboard layout changes, global hooks, raw-device registration or logging
//! of input text. The retail chat FString is UTF-16; its OnChar path is not.
pub(crate) const CHAT_VTABLE: u32 = 0x02c40494;
pub(crate) fn active(vtable: u32, mode: u32, visible: bool, locked: bool) -> bool {
    // Retail Open (1A879F0) stores 0, 1 or 2 in +2C0; only its default
    // branch stores -1. Mode 0 shows the same editable widget. Do not confuse
    // the submit path's mode==1 team discriminator with IME eligibility.
    vtable == CHAT_VTABLE && matches!(mode, 0..=2) && visible && !locked
}

pub(crate) fn commit(text: &[u16], room: usize) -> Vec<u16> {
    // Invalid UTF-16 and controls are rejected, not silently replaced. Keep
    // surrogate pairs together when the native/wire length limit is reached.
    let Ok(s) = String::from_utf16(text) else { return Vec::new() };
    if s.chars().any(char::is_control) { return Vec::new(); }
    let mut out = Vec::new();
    for c in s.chars() {
        if out.len() + c.len_utf16() > room.min(144) { break; }
        let mut units = [0; 2];
        out.extend_from_slice(c.encode_utf16(&mut units));
    }
    out
}

#[derive(Default, Clone, Copy)]
pub(crate) struct Composition {
    pub composing: bool,
    blocked: u8,
}
impl Composition {
    pub fn start(&mut self) { self.composing = true; }
    pub fn end(&mut self) { self.composing = false; }
    pub fn result(&mut self) { self.blocked |= 1; }
    pub fn key_down(&mut self, key: usize, repeated: bool) -> bool {
        let bit = match key { 13 => 1, 27 => 2, _ => 0 };
        if self.composing {
            self.blocked |= bit;
            true
        } else {
            // A selection's key-up/deferred native event must not send/close
            // chat. Only a subsequent independent key-down releases its gate.
            if !repeated { self.blocked &= !bit; }
            self.blocks_native(key)
        }
    }
    pub fn blocks_native(self, key: usize) -> bool {
        self.composing || (match key {13 => self.blocked & 1, 27 => self.blocked & 2, _ => 0} != 0)
    }
}

#[derive(Clone, Copy)]
pub(crate) struct Context { window: usize, owned: usize, previous: usize }
#[link(name="imm32")]
extern "system" {
    fn ImmCreateContext() -> usize;
    fn ImmDestroyContext(context: usize) -> i32;
    fn ImmAssociateContext(window: usize, context: usize) -> usize;
    fn ImmGetContext(window: usize) -> usize;
    fn ImmReleaseContext(window: usize, context: usize) -> i32;
    fn ImmNotifyIME(context: usize, action: u32, index: u32, value: u32) -> i32;
}
impl Context {
    pub unsafe fn open(window: usize) -> Option<Self> {
        if window == 0 { return None; }
        let owned = ImmCreateContext();
        if owned == 0 { return None; }
        let previous = ImmAssociateContext(window, owned);
        let got = ImmGetContext(window);
        if got != 0 { ImmReleaseContext(window, got); }
        if got != owned { ImmDestroyContext(owned); return None; }
        Some(Self { window, owned, previous })
    }
    pub fn handle(self) -> usize { self.owned }
    pub unsafe fn close(self) -> bool {
        let got = ImmGetContext(self.window);
        if got != 0 { ImmReleaseContext(self.window, got); }
        // Another component owns this window now: do not overwrite its HIMC.
        if got == self.owned {
            ImmNotifyIME(self.owned, 0x15, 4, 0); // cancel, never commit on exit
            ImmAssociateContext(self.window, self.previous);
        }
        let after = ImmGetContext(self.window);
        if after != 0 { ImmReleaseContext(self.window, after); }
        after != self.owned && ImmDestroyContext(self.owned) != 0
    }
}

#[cfg(test)] mod tests {
    use super::*;
    #[test] fn composition_commit_enter_is_not_send_enter() {
        let mut c=Composition::default(); c.start();
        assert!(c.key_down(13,false)); c.end();
        assert!(c.blocks_native(13)); assert!(c.key_down(13,true));
        assert!(!c.key_down(13,false)); assert!(!c.blocks_native(13));
        c.start(); assert!(c.key_down(27,false));c.end();assert!(c.blocks_native(27));
        assert!(!c.key_down(27,false));assert!(!c.blocks_native(27));
    }
    #[test] fn bounded_wide_text_no_controls_or_half_surrogates() {
        assert_eq!(commit(&[0x4e2d,0x6587],1),vec![0x4e2d]);
        assert_eq!(commit(&[0xd83d,0xde00],1),Vec::<u16>::new());
        assert_eq!(commit(&[0xd83d,0xde00],2),vec![0xd83d,0xde00]);
        for s in [&[0xd800][..],&[13][..],&[0][..]] {assert!(commit(s,80).is_empty());}
        assert_eq!(commit(&vec![65;200],200).len(),144);
    }
}
