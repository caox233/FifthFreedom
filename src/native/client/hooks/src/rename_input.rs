//! Wide commits for the retail loadout rename popup only. No storage access.
//! The original OnChar reads one byte; merely broadening its ASCII gate loses
//! the rest of a UTF-16 character. The native completion callback already takes
//! an FString, so keep that callback and its cancel/confirm behavior unchanged.
use crate::input_mouse::memory::{readable, word};

// Native SceneMessageBox constructor 19D1A80 / RTTI; 2C32EB4 is SceneLoading.
pub(super) const VTABLE: u32 = 0x02c30444;
const CALLBACK: u32 = 0x01933cd0;
const LIMIT: usize = 14; // retail count includes the terminating UTF-16 NUL

pub(super) unsafe fn eligible(p: usize) -> bool {
    let check = || -> Option<bool> {
        if word(p, 0)? != VTABLE {
            return Some(false);
        }
        let count = word(p, 0x354)? as usize;
        if !(1..=32).contains(&count) {
            return Some(false);
        }
        let top = (word(p, 0x350)? as usize).checked_add((count - 1) * 0x84)?;
        let delegate = word(top, 0x70)? as usize;
        // Other generic text popups (account names, passwords, codes) are not
        // eligible, even when they use the very same popup class and style.
        if word(top, 0x18)? != 5
            || word(top, 0x1c)? != 3
            || word(delegate, 0x10)? != CALLBACK
            || word(top, 0x78)? != LIMIT as u32
            || word(p, 0x2c0)? != LIMIT as u32
        {
            return Some(false);
        }
        let widget = word(p, 0x2ac)? as usize;
        let gfx = word(widget, 0x1a8)? as usize;
        if !readable(p + 0x304, 1) || !readable(gfx + 0x40, 1) {
            return None;
        }
        Some(*(p as *const u8).add(0x304) != 0 && *(gfx as *const u8).add(0x40) != 0)
    };
    check() == Some(true)
}

pub(super) unsafe fn current_text(p: usize) -> Option<Vec<u16>> {
    let count = word(p, 0x2b8)? as usize;
    if count == 0 {
        return Some(Vec::new());
    }
    if count > LIMIT {
        return None;
    }
    let ptr = word(p, 0x2b4)? as usize;
    if !readable(ptr, count * 2) {
        return None;
    }
    let text = std::slice::from_raw_parts(ptr as *const u16, count);
    if text.last() != Some(&0) || String::from_utf16(&text[..count - 1]).is_err() {
        return None;
    }
    Some(text[..count - 1].to_vec())
}

fn accepted(text: &[u16], room: usize) -> Vec<u16> {
    let Ok(text) = String::from_utf16(text) else {
        return Vec::new();
    };
    // Keep the native ASCII alphanumeric/uppercase contract. Do not admit
    // format markup, control characters, whitespace or partial surrogate pairs.
    if text.chars().any(|c| !c.is_alphanumeric()) {
        return Vec::new();
    }
    let mut out = Vec::new();
    for c in text.chars().map(|c| c.to_ascii_uppercase()) {
        if out.len() + c.len_utf16() > room.min(LIMIT - 1) {
            break;
        }
        let mut units = [0; 2];
        out.extend_from_slice(c.encode_utf16(&mut units));
    }
    out
}

unsafe fn refresh(p: usize) {
    let ptr = if word(p, 0x2b8).unwrap_or(0) == 0 {
        0x02d71740
    } else {
        word(p, 0x2b4).unwrap_or(0) as usize
    };
    let widget = word(p, 0x2ac).unwrap_or(0) as usize;
    let display: unsafe extern "thiscall" fn(usize, *const u16) -> u8 =
        std::mem::transmute(0x18bf320usize);
    let cursor: unsafe extern "thiscall" fn(usize) = std::mem::transmute(0x18f2e70usize);
    display(widget, ptr as *const u16);
    cursor(p);
}

pub(super) unsafe fn commit(p: usize, text: &[u16]) -> bool {
    if !eligible(p) {
        return false;
    }
    let Some(current) = current_text(p) else {
        return false;
    };
    let mut add = accepted(text, LIMIT.saturating_sub(current.len() + 1));
    if add.is_empty() {
        return false;
    }
    add.push(0);
    let append: unsafe extern "thiscall" fn(usize, *const u16) -> usize =
        std::mem::transmute(0x463f80usize);
    append(p + 0x2b4, add.as_ptr());
    refresh(p);
    true
}

pub(super) unsafe fn backspace(p: usize) -> bool {
    if !eligible(p) {
        return false;
    }
    let Some(mut text) = current_text(p) else {
        return false;
    };
    let Some(last) = text.pop() else {
        return true;
    };
    if (0xdc00..=0xdfff).contains(&last) {
        text.pop();
    }
    text.push(0);
    let set: unsafe extern "thiscall" fn(usize, *const u16) -> usize =
        std::mem::transmute(0x4637a0usize);
    set(p + 0x2b4, text.as_ptr());
    refresh(p);
    true
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn wide_admission_preserves_native_ascii_and_cjk_without_low_byte_aliasing() {
        assert_eq!(
            accepted(&[0x61, 0x39, 0x4e2d, 0x6587, 0x4e41], 13),
            vec![0x41, 0x39, 0x4e2d, 0x6587, 0x4e41]
        );
        assert_eq!(accepted(&[0x4e2d, 0x6587], 1), vec![0x4e2d]);
        for text in [vec![0], vec![13], vec![32], vec![b'<' as u16], vec![0xd800]] {
            assert!(accepted(&text, 13).is_empty());
        }
        let cjk: Vec<u16> = "𠀀".encode_utf16().collect();
        assert!(accepted(&cjk, 1).is_empty());
        assert_eq!(accepted(&cjk, 2), cjk);
        assert_eq!(accepted(&[65; 20], 100).len(), 13);
    }
}
