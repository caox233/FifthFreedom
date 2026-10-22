//! Pinned x86 publisher bridges. Pure builders: no allocation of executable memory,
//! installation, state, process reads, or assumed field predicates.
//! All callbacks are non-unwinding extern "stdcall"; begin/end(core), mark(core,event).
//! Caller owns thread-local nested scopes, frame invalidation, and owner validation.
//! A scope starts Unknown, not Current. Only Current + Published can yield Some;
//! Old, missing events, wrong scope/frame, alternate viewport, and faults yield None.
//! Never redirect `original` to the detoured entry: use entry_trampoline instead.

pub const ENTRY: u32 = 0x00d609b0;
pub const ENTRY_PIN: &[u8] = &[0x55,0x8b,0xec,0x83,0xec,0x78];

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(u32)]
pub enum Event { Current = 1, Old = 2, Published = 3 }

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Site { Current, Old, Published }
impl Site {
    pub const fn address(self) -> u32 { match self {
        Self::Current => 0x00d609bd, Self::Old => 0x00d60ade,
        Self::Published => 0x00d60ccc,
    }}
    pub const fn pin(self) -> &'static [u8] { match self {
        Self::Current => &[0xf3,0x0f,0x7e,0x46,0x6c,0x8b,0x4e,0x74],
        Self::Old => &[0x8b,0x86,0x80,0,0,0],
        Self::Published => &[0xf3,0x0f,0x7e,0x45,0xe8],
    }}
    pub const fn event(self) -> Event { match self {
        Self::Current => Event::Current, Self::Old => Event::Old,
        Self::Published => Event::Published,
    }}
}

fn rel(b: &mut Vec<u8>, base: u32, opcode: u8, target: u32) {
    b.push(opcode);
    let next = base.wrapping_add(b.len() as u32).wrapping_add(4);
    b.extend_from_slice(&target.wrapping_sub(next).to_le_bytes());
}

// Saved pushad frame: ESI +4, EAX +28, flags +32, outer saved core +36.
// Reserve 544 bytes then align down to 16; 512-byte FX state cannot overlap
// saved frame pointer at +512 or the native stack. Restore the exact native ESP.
fn save(b: &mut Vec<u8>) {
    b.extend_from_slice(&[0x9c,0x60,0x89,0xe0,0x81,0xec,0x20,0x02,0,0,
        0x83,0xe4,0xf0,0x89,0x84,0x24,0,2,0,0,0x0f,0xae,0x04,0x24,0xfc]);
}
fn restore(b: &mut Vec<u8>) {
    b.extend_from_slice(&[0x0f,0xae,0x0c,0x24,0x8b,0xa4,0x24,0,2,0,0,0x61,0x9d]);
}
fn callback(b: &mut Vec<u8>, address: u32, core_offset: u8, event: Option<Event>) {
    if let Some(e) = event { b.extend_from_slice(&[0x6a,e as u8]); }
    b.extend_from_slice(&[0xff,0x70,core_offset,0xb8]);
    b.extend_from_slice(&address.to_le_bytes());
    b.extend_from_slice(&[0xff,0xd0]);
}

/// Callable replacement for thiscall(core), no stack arguments.
/// Begin/end bracket every normal return, including nested invocation of same core.
/// Native exceptions cannot safely unwind generated code: owner must invalidate
/// outstanding scopes on frame reset; never consume an unfinished scope.
pub fn scope_bridge(base: u32, original: u32, begin: u32, end: u32) -> Vec<u8> {
    let mut b = vec![0x51]; // saved core survives original and both callbacks
    save(&mut b); callback(&mut b,begin,36,None); restore(&mut b);
    rel(&mut b,base,0xe8,original);
    save(&mut b); callback(&mut b,end,36,None); restore(&mut b);
    b.extend_from_slice(&[0x8d,0x64,0x24,4,0xc3]);
    b
}

/// Relocates the six entry bytes, all position-independent, then resumes +6.
pub fn entry_trampoline(base: u32, resume: u32) -> Vec<u8> {
    let mut b = ENTRY_PIN.to_vec(); rel(&mut b,base,0xe9,resume); b
}

/// Mid-function JMP target: preserves *entry* flags/GPR/FX, executes displaced
/// instructions exactly once, then resumes. Current/Old mark after real loads.
/// Published marks only after camera MoveActor returns nonzero EAX. Zero EAX
/// leaves publication absent; alternate path never reaches this site.
pub fn marker_bridge(base: u32, site: Site, mark: u32, resume: u32) -> Vec<u8> {
    let mut b = Vec::new();
    if site != Site::Published { b.extend_from_slice(site.pin()); }
    save(&mut b);
    let branch = if site == Site::Published {
        b.extend_from_slice(&[0x83,0x78,0x1c,0,0x74,0]); Some(b.len()-1)
    } else { None };
    callback(&mut b,mark,4,Some(site.event())); // real native ESI/core
    if let Some(index) = branch { b[index] = (b.len()-index-1) as u8; }
    restore(&mut b);
    if site == Site::Published { b.extend_from_slice(site.pin()); }
    rel(&mut b,base,0xe9,resume); b
}

/// Root must validate every native pin BEFORE any write; install all-or-none.
pub fn jump_patch(site: u32, target: u32, width: usize) -> Option<Vec<u8>> {
    if width < 5 { return None; }
    let mut b=Vec::new(); rel(&mut b,site,0xe9,target); b.resize(width,0x90); Some(b)
}

#[cfg(test)] mod tests {
    use super::*;
    #[test] fn all_sites_have_complete_pins() {
        assert_eq!(ENTRY_PIN.len(),6);
        for s in [Site::Current,Site::Old,Site::Published] {
            assert!(s.pin().len()>=5);
            let b=marker_bridge(0x10000000,s,0x12345678,s.address()+s.pin().len() as u32);
            assert_eq!(b[b.len()-5],0xe9);
            assert_eq!(u32::from_le_bytes(b[b.len()-4..].try_into().unwrap()).wrapping_add(0x10000000+b.len() as u32),s.address()+s.pin().len() as u32);
        }
    }
    #[test] fn scope_calls_begin_original_end_and_preserves_stack() {
        let b=scope_bridge(0xffffffe0,0x11111111,0x22222222,0x33333333);
        assert_eq!(b[0],0x51);assert_eq!(&b[b.len()-5..],&[0x8d,0x64,0x24,4,0xc3]);
        assert_eq!(b.windows(3).filter(|w|*w==[0xff,0x70,36]).count(),2);
    }
    #[test] fn patch_rejects_partial_instruction_space() {
        assert!(jump_patch(0,1,4).is_none());
        assert_eq!(jump_patch(0x1000,0x2000,6).unwrap(),[0xe9,0xfb,0x0f,0,0,0x90]);
    }
}
