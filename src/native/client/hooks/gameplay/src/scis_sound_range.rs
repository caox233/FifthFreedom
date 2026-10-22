//! Per-owned-instance range adaptation. Ownership and hook lifetime belong to
//! scis_sound_owner; this module never identifies a gun from a shared asset.
//! Private retail copies scale four distance *reads*, not emitter coordinates or
//! emitter+3f4. Normal/recursive/error paths therefore have no field to restore.
use crate::equipment_native::{readable, word, writable};
use sha2::{Digest, Sha256};
use std::{ffi::c_void, sync::atomic::{AtomicUsize, Ordering}};

const AGGREGATE: usize = 0x1e84bd0;
const PRIMARY: usize = 0x1e74980;
const SECONDARY: usize = 0x1e74a90;
const RELEVANCE: usize = 0x6b5500;
const PINS: [(usize, usize, &str); 4] = [
    (AGGREGATE, 0x9a, "f1882f6b50cd2e5e7e3c683d60b41e790d762c06f6092b6a1580170fc8c7dc58"),
    (PRIMARY, 0x108, "acda98abc83f1a683a9bb7e3f1189a55b75d6a07e701cab9f82c020ad4e880d2"),
    (SECONDARY, 0xf6, "e0b7f8a9b37a006c4af4c4565e5bd298706c8c43a45f6e6b46b4f61ad78bb90d"),
    (RELEVANCE, 0x375, "a15fbb6832eb6cd94e53e79171651f3a97ccbc4fd1989801c6951899b0126254"),
];
const OFFSETS: [usize; 4] = [0, 0x100, 0x240, 0x380];
const LOAD_HALF: usize = 0x350;
const HALF: usize = 0x700;
const RADIUS: usize = 0x708;
const BANK_SIZE: usize = 4096;
static BANK: AtomicUsize = AtomicUsize::new(0);

#[link(name = "kernel32")]
extern "system" {
    fn VirtualAlloc(p: *mut c_void, size: usize, kind: u32, protect: u32) -> *mut c_void;
    fn VirtualProtect(p: *mut c_void, size: usize, protect: u32, old: *mut u32) -> i32;
    fn VirtualFree(p: *mut c_void, size: usize, kind: u32) -> i32;
    fn FlushInstructionCache(handle: *mut c_void, p: *const c_void, size: usize) -> i32;
    fn GetCurrentProcess() -> *mut c_void;
}

fn relative_call(bytes: &mut [u8], at: usize, destination: usize, target: usize) -> Option<()> {
    if bytes.get(at) != Some(&0xe8) { return None; }
    bytes.get_mut(at + 1..at + 5)?.copy_from_slice(
        &(target as u32).wrapping_sub((destination + at + 5) as u32).to_le_bytes());
    Some(())
}

// Full-function signatures make every relocation and distance-read seam exact.
// All native branches, dirty bits, asset references and MAX aggregation remain.
fn prepare(original: [&[u8]; 4], destination: usize) -> Option<Vec<u8>> {
    if destination < 0x10000 || destination.checked_add(BANK_SIZE)? > u32::MAX as usize { return None; }
    for (bytes, (_, size, hash)) in original.iter().zip(PINS) {
        if bytes.len() != size || format!("{:x}", Sha256::digest(bytes)) != hash { return None; }
    }
    let mut bank = vec![0xcc; BANK_SIZE];
    for (bytes, offset) in original.into_iter().zip(OFFSETS) {
        bank[offset..offset + bytes.len()].copy_from_slice(bytes);
    }
    for (site, target) in [(0x3d, destination + OFFSETS[1]), (0x56, destination + OFFSETS[2])] {
        relative_call(&mut bank, site, destination, target)?;
    }
    for (site, target) in [(0x2b, 0x1e73d00), (0x58, 0x1e73d00), (0x85, 0x1dbdb50), (0xe5, 0x1dd19e0)] {
        relative_call(&mut bank, OFFSETS[1] + site, destination, target)?;
    }
    for (site, target) in [(0x51, 0x1e44990), (0x9c, 0x1f524f0), (0xbf, 0x1dd5430), (0xd9, 0x1e63240)] {
        relative_call(&mut bank, OFFSETS[2] + site, destination, target)?;
    }
    for (site, target) in [(0x20, 0x8e6e10), (0x45, 0x8b5ef0), (0x6c, 0x8b5ef0), (0x15c, 0x8b5ef0)] {
        relative_call(&mut bank, OFFSETS[3] + site, destination, target)?;
    }
    // Each original fld pushes one x87 value. The private helper does exactly
    // that and multiplies it by 0.5, preserving flags, GPRs and the source bits.
    let load = [0xd9, 0x86, 0xf4, 3, 0, 0];
    for site in [OFFSETS[1] + 0x1b, OFFSETS[1] + 0x48, OFFSETS[2] + 0x40, OFFSETS[2] + 0xcf] {
        if bank[site..site + 6] != load { return None; }
        bank[site] = 0xe8;
        relative_call(&mut bank, site, destination, destination + LOAD_HALF)?;
        bank[site + 5] = 0x90;
    }
    bank[LOAD_HALF..LOAD_HALF + 6].copy_from_slice(&load);
    bank[LOAD_HALF + 6..LOAD_HALF + 8].copy_from_slice(&[0xd8, 0x0d]);
    bank[LOAD_HALF + 8..LOAD_HALF + 12].copy_from_slice(&((destination + HALF) as u32).to_le_bytes());
    bank[LOAD_HALF + 12] = 0xc3;
    bank[HALF..HALF + 4].copy_from_slice(&0.5f32.to_le_bytes());
    // Retail relevance compares against a double containing 1.100000023841858.
    // Double only that private radius. Actor curve, location, zone and bypass
    // globals remain untouched, including when native relevance rejects a shot.
    bank[OFFSETS[3] + 0x147..OFFSETS[3] + 0x14b]
        .copy_from_slice(&((destination + RADIUS) as u32).to_le_bytes());
    bank[RADIUS..RADIUS + 8].copy_from_slice(&2.200000047683716f64.to_le_bytes());
    Some(bank)
}

pub(super) unsafe fn init() -> bool {
    if BANK.load(Ordering::Acquire) != 0 { return true; }
    if !PINS.iter().all(|(at, len, _)| readable(*at, *len)) { return false; }
    let original = PINS.map(|(at, len, _)| std::slice::from_raw_parts(at as *const u8, len));
    let memory = VirtualAlloc(std::ptr::null_mut(), BANK_SIZE, 0x3000, 4) as usize;
    if memory == 0 { return false; }
    let Some(bank) = prepare(original, memory) else {
        VirtualFree(memory as _, 0, 0x8000); return false;
    };
    std::ptr::copy_nonoverlapping(bank.as_ptr(), memory as *mut u8, bank.len());
    let mut old = 0;
    if VirtualProtect(memory as _, BANK_SIZE, 0x20, &mut old) == 0
        || FlushInstructionCache(GetCurrentProcess(), memory as _, BANK_SIZE) == 0
    {
        VirtualFree(memory as _, 0, 0x8000); return false;
    }
    if BANK.compare_exchange(0, memory, Ordering::Release, Ordering::Acquire).is_err() {
        VirtualFree(memory as _, 0, 0x8000);
    }
    true
}

fn eligible(generation: u64, distance: Option<u32>) -> bool {
    generation != 0 && distance.is_some_and(|bits| {
        let value = f32::from_bits(bits); value.is_finite() && value >= 0.0
    })
}

// The caller has checked this exact emitter's registered generation. Like the
// original method, evaluation requires the native object to stay alive for the
// duration of its call; the token is not a registry lock or a lifetime pin.
// This is not an independent ownership lookup and never installs a detour.
pub(super) unsafe fn evaluate(
    subobject: usize, output: usize, generation: u64, native: impl FnOnce() -> usize,
) -> usize {
    let bank = BANK.load(Ordering::Acquire);
    let Some(emitter) = subobject.checked_sub(4) else { return native(); };
    if bank == 0 || !eligible(generation, word(emitter, 0x3f4))
        || word(emitter, 0) != Some(0x02ccf05c)
        || word(subobject, 0) != Some(0x02ccf054) || !writable(output, 16)
    { return native(); }
    let original_copy: unsafe extern "thiscall" fn(usize, usize) -> usize = std::mem::transmute(bank);
    original_copy(subobject, output)
}

// Called only from the owner's exact synchronous MP SC-IS shot scope, before
// enqueue can create a generation. No ordinary SVU or same-actor event qualifies.
pub(super) unsafe fn relevance(
    actor: usize, event: usize, position: usize, exact_shot: bool, native: impl FnOnce() -> u32,
) -> u32 {
    let bank = BANK.load(Ordering::Acquire);
    if bank == 0 || !exact_shot { return native(); }
    let original_copy: unsafe extern "thiscall" fn(usize, usize, usize) -> u32
        = std::mem::transmute(bank + OFFSETS[3]);
    original_copy(actor, event, position)
}

// RX is retained: an in-flight native call may still return through the copy.
// The single owner disables its hooks/ownership records during teardown instead.
#[cfg(test)]
#[path = "scis_sound_range_tests.rs"]
mod tests;
