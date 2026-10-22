//! Exact native scene publication seam, before immediate mode can consume it.
pub const PUBLISH_CALL: u32 = 0x667a2c;
pub const PUBLISH_BYTES: [u8; 5] = [0xe8, 0xaf, 0x28, 0xfd, 0x00];
fn branch(b: &mut Vec<u8>, base: u32, target: u32, opcode: u8) {
    b.push(opcode);
    let end = base.wrapping_add(b.len() as u32 + 4);
    b.extend(target.wrapping_sub(end).to_le_bytes());
}
/// JMP replaces CALL, then invokes original exactly once. stdcall observer
/// receives command, provider, input info. No queued pointer is kept by bridge.
pub fn publish(base: u32, observer: u32, original: u32, resume: u32) -> Vec<u8> {
    let mut b = Vec::from([
        0x9c, 0x60, 0x89, 0xe0, 0x81, 0xec, 0x20, 0x02, 0, 0, 0x83, 0xe4, 0xf0, 0x89, 0x84, 0x24,
        0, 0x02, 0, 0, 0x0f, 0xae, 0x04, 0x24, 0xfc,
    ]);
    b.extend([0xff, 0x75, 0x14, 0xff, 0x75, 0x10, 0x56]); // info, provider, command
    b.push(0xb8);
    b.extend(observer.to_le_bytes());
    b.extend([
        0xff, 0xd0, 0x0f, 0xae, 0x0c, 0x24, 0x8b, 0xa4, 0x24, 0, 0x02, 0, 0, 0x61, 0x9d,
    ]);
    branch(&mut b, base, original, 0xe8);
    branch(&mut b, base, resume, 0xe9);
    b
}

/// Six bytes end exactly at the +6C/+70 shared continuation16DF0DE.
/// Starting at16DF0DC and stealing8bytes would corrupt the special-pass jump
/// into16DF0DE. Preserve that alternate entry by patching16DF0D8 instead.
pub const MAIN_SITE: u32 = 0x16df0d8;
pub const MAIN_BYTES: [u8; 6] = [0x8b, 0x40, 0x70, 0x52, 0xff, 0xd0];
/// Original EDI=provider, EBX=retained info, ECX=outer renderer,
/// EAX=renderer vtable, EDX=context. stdcall observer(provider,info,outer).
pub fn main_render(base: u32, observer: u32, finished: u32, resume: u32) -> Vec<u8> {
    let mut b = Vec::from([
        0x9c, 0x60, 0x89, 0xe0, 0x81, 0xec, 0x20, 0x02, 0, 0, 0x83, 0xe4, 0xf0, 0x89, 0x84, 0x24,
        0, 0x02, 0, 0, 0x0f, 0xae, 0x04, 0x24, 0xfc,
    ]);
    b.extend([0x51, 0x53, 0x57, 0xb8]);
    b.extend(observer.to_le_bytes());
    b.extend([
        0xff, 0xd0, 0x0f, 0xae, 0x0c, 0x24, 0x8b, 0xa4, 0x24, 0, 0x02, 0, 0, 0x61, 0x9d,
    ]);
    b.extend(MAIN_BYTES);
    // Pending provider/info belongs only to this ordinary +70 invocation.
    save(&mut b);
    observe(&mut b, finished);
    branch(&mut b, base, resume, 0xe9);
    b
}

// 16D80C0 invokes17462E0 to populate current collection+70/+74. Main entry
// precedes that operation; testing membership there rejects an empty list.
pub const CLASSIFIED_SITE: u32 = 0x16dc527;
pub const CLASSIFIED_BYTES: [u8; 5] = [0xe8, 0x94, 0xbb, 0xff, 0xff];
/// Original classifier runs once, then observer(outer=ESI), before17797E0.
pub fn classified(base: u32, observer: u32) -> Vec<u8> {
    let mut b=Vec::new();
    branch(&mut b,base,0x16d80c0,0xe8);
    save(&mut b);
    b.push(0x56);
    observe(&mut b,observer);
    branch(&mut b,base,CLASSIFIED_SITE+5,0xe9);
    b
}

fn save(b: &mut Vec<u8>) {
    b.extend([
        0x9c, 0x60, 0x89, 0xe0, 0x81, 0xec, 0x20, 0x02, 0, 0, 0x83, 0xe4, 0xf0, 0x89, 0x84, 0x24,
        0, 0x02, 0, 0, 0x0f, 0xae, 0x04, 0x24, 0xfc,
    ]);
}
fn observe(b: &mut Vec<u8>, callback: u32) {
    b.push(0xb8);
    b.extend(callback.to_le_bytes());
    b.extend([
        0xff, 0xd0, 0x0f, 0xae, 0x0c, 0x24, 0x8b, 0xa4, 0x24, 0, 0x02, 0, 0, 0x61, 0x9d,
    ]);
}
/// The original full queue drain runs before sealing this provider's batch.
pub fn after_drain(base: u32, observer: u32) -> Vec<u8> {
    let mut b = Vec::new();
    branch(&mut b, base, 0x161d0c0, 0xe8);
    save(&mut b);
    b.extend([0x53, 0xff, 0x75, 0x08]);
    observe(&mut b, observer);
    branch(&mut b, base, 0x161d27a, 0xe9);
    b
}
/// Original count reset precedes invalidation; preserve flags and all registers.
pub fn finish(base: u32, observer: u32) -> Vec<u8> {
    let mut b = vec![0xc7, 0x81, 0x30, 0x43, 0, 0, 0, 0, 0, 0];
    save(&mut b);
    b.push(0x51);
    observe(&mut b, observer);
    branch(&mut b, base, 0x16def9a, 0xe9);
    b
}

#[cfg(test)]
mod tests {
    use super::*;
    fn target(code:&[u8],at:usize,base:u32)->u32 {
        base.wrapping_add(at as u32+5).wrapping_add(u32::from_le_bytes(code[at+1..at+5].try_into().unwrap()))
    }
    #[test]
    fn classification_calls_native_once_then_observes_and_resumes() {
        let b=classified(0x10000000,0x20000000);
        assert_eq!(b[0],0xe8);
        assert_eq!(target(&b,0,0x10000000),0x16d80c0);
        assert_eq!(&b[5..7],&[0x9c,0x60]);
        assert!(b.windows(6).any(|v|v==[0x56,0xb8,0,0,0,0x20]));
        assert_eq!(target(&b,b.len()-5,0x10000000),CLASSIFIED_SITE+5);
    }
    #[test]
    fn main_keeps_alternate_entry_and_clears_pending_after_original() {
        let b=main_render(0x10000000,0x20000000,0x21000000,0x16df0de);
        let sites:Vec<_>=b.windows(MAIN_BYTES.len()).enumerate().filter(|(_,v)|*v==MAIN_BYTES).collect();
        assert_eq!(sites.len(),1);
        let i=sites[0].0+MAIN_BYTES.len();
        assert_eq!(&b[i..i+2],&[0x9c,0x60]);
        assert!(b[i..].windows(5).any(|v|v==[0xb8,0,0,0,0x21]));
        assert_eq!(target(&b,b.len()-5,0x10000000),0x16df0de);
    }
}
