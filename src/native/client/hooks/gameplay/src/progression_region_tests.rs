//! Actual Windows virtual-memory tests of production project_with. The allocator
//! callback owns synthetic memory; this does not execute the retail allocator,
//! load a player, change a save, or establish the cause of a reported game run.
use super::*;
use std::ffi::c_void;

#[link(name = "kernel32")]
extern "system" {
    fn VirtualAlloc(p: *mut c_void, n: usize, t: u32, protect: u32) -> *mut c_void;
    fn VirtualFree(p: *mut c_void, n: usize, t: u32) -> i32;
    fn VirtualProtect(p: *mut c_void, n: usize, protect: u32, old: *mut u32) -> i32;
}

struct OwnedRegions { first: usize, second: Option<usize> }
impl OwnedRegions {
    unsafe fn one_allocation() -> Self {
        let first = VirtualAlloc(std::ptr::null_mut(), 0x10000, 0x3000, 4) as usize;
        assert_ne!(first, 0);
        Self { first, second: None }
    }
    unsafe fn unrelated_adjacent_allocations() -> Self {
        let reservation = VirtualAlloc(std::ptr::null_mut(), 0x20000, 0x2000, 4);
        assert!(!reservation.is_null());
        assert_ne!(VirtualFree(reservation, 0, 0x8000), 0);
        let first = VirtualAlloc(reservation, 0x10000, 0x3000, 4) as usize;
        assert_eq!(first, reservation as usize);
        let mut owned = Self { first, second: None };
        let second = VirtualAlloc((first + 0x10000) as _, 0x10000, 0x3000, 4) as usize;
        assert_eq!(second, first + 0x10000, "adjacent owned test reservation unavailable");
        owned.second = Some(second);
        owned
    }
    fn boundary(&self) -> usize { self.second.unwrap_or(self.first + 0x3000) }
    fn rows(&self) -> *mut Rank { (self.boundary() - 48) as *mut Rank }
    unsafe fn protect_tail(&self, protection: u32) {
        let mut old = 0;
        let size = if self.second.is_some() { 0x10000 } else { 0xd000 };
        assert_ne!(VirtualProtect(self.boundary() as _, size, protection, &mut old), 0);
    }
    unsafe fn decommit_tail(&self) {
        assert_ne!(VirtualFree(self.boundary() as _, 0xd000, 0x4000), 0);
    }
}
impl Drop for OwnedRegions {
    fn drop(&mut self) { unsafe {
        assert_ne!(VirtualFree(self.first as _, 0, 0x8000), 0);
        if let Some(second) = self.second { assert_ne!(VirtualFree(second as _, 0, 0x8000), 0); }
    } }
}

fn retail_rows() -> Vec<Rank> {
    let mut total = 0;
    (1..=56).map(|level| {
        let xp = if level == 1 { 0 } else if level == 56 { 20000 } else { 1500 };
        total += xp;
        Rank { level, xp, tokens: 0, flags: u32::from(level > 50), total_xp: total, total_tokens: 0 }
    }).collect()
}
unsafe fn object(rows: &mut [Rank]) -> [u32; 16] {
    let mut object = [0x5a5a5a5a; 16];
    object[0] = PROGRESSION_VTABLE;
    std::ptr::write((object.as_mut_ptr() as usize + 0x28) as *mut Array,
        Array { data: rows.as_mut_ptr(), len: rows.len() as i32, cap: rows.len() as i32 });
    object
}

#[test]
fn progression_region_single_owned_allocation_accepts_all_writable_regions() { unsafe {
    let owned = OwnedRegions::one_allocation();
    // Different protections in the SAME reservation create actual VirtualQuery
    // regions. No instruction is executed from this synthetic data allocation.
    owned.protect_tail(0x40);
    let mut original = retail_rows();
    let original_rows = original.clone();
    let mut live = object(&mut original);
    let before = live;
    let address = live.as_mut_ptr() as usize;
    let old_pointer = original.as_mut_ptr();
    let freed = std::cell::RefCell::new(Vec::new());
    assert!(!writable(owned.rows() as usize, MAX_LEVEL * std::mem::size_of::<Rank>()),
        "fixture must expose the old single-region false refusal");
    assert_eq!(project_with(address, |array, stride| {
        assert_eq!(stride, 24);
        assert_eq!((*array).cap, 999);
        assert_eq!((*array).len, 0);
        assert!((*array).data.is_null());
        (*array).data = owned.rows();
    }, |array| {
        freed.borrow_mut().push(((*array).data, (*array).len));
    }), Ok(true), "PROGRESSION_VALID_SAME_ALLOCATION_SPAN");
    assert_eq!(*freed.borrow(), vec![(old_pointer, 0)], "old allocation released exactly once after commit");
    assert_eq!(&live[..10], &before[..10]);
    assert_eq!(&live[13..], &before[13..]);
    assert_eq!(original, original_rows, "retail source rows must not change");
    let result = std::slice::from_raw_parts(owned.rows(), MAX_LEVEL);
    assert_eq!(result, extended(&original_rows).unwrap().unwrap());
    for level in [55, 56, 57, 999] {
        assert_eq!(result[level - 1].level, level as i32);
        if level > 56 {
            assert_eq!(result[level - 1].total_xp,
                original_rows[55].total_xp + (level as i32 - 56) * original_rows[55].xp);
        }
    }
    assert_eq!(project_with(address, |_, _| panic!("no second allocation"),
        |_| panic!("no second release")), Ok(false), "cross-region reload must recognize its own table");
    println!("PROGRESSION_REGIONS same_allocation=true actual_windows_query=true rows=999 stride=24 levels=55,56,57,999 source_rows_unchanged=true ownership_once=true retail_allocator_executed=false saves_touched=false");
} }

#[test]
fn progression_region_invalid_tail_or_unrelated_allocation_preserves_original() { unsafe {
    for variant in 0..8 {
        let owned = if variant == 4 { OwnedRegions::unrelated_adjacent_allocations() }
            else { OwnedRegions::one_allocation() };
        match variant {
            0 => owned.protect_tail(1),       // no access
            1 => owned.protect_tail(4 | 0x100), // guard
            2 => owned.protect_tail(2),       // readable but not writable
            3 => owned.decommit_tail(),
            _ => {}
        }
        let mut original = retail_rows();
        let old_rows = original.clone();
        let mut live = object(&mut original);
        let before = live;
        let freed = std::cell::RefCell::new(Vec::new());
        let prefix = std::slice::from_raw_parts(owned.rows() as *const u8, 48).to_vec();
        let replacement = match variant {
            6 => (owned.rows() as usize + 1) as *mut Rank,
            7 => (usize::MAX - 3) as *mut Rank,
            _ => owned.rows(),
        };
        assert_eq!(project_with(live.as_mut_ptr() as usize, |array, stride| {
            assert_eq!(stride, 24);
            assert_eq!((*array).cap, 999);
            (*array).data = replacement;
            if variant == 5 { (*array).cap = 998; }
        }, |array| {
            freed.borrow_mut().push(((*array).data, (*array).len));
        }), Err("allocation-range"), "PROGRESSION_INVALID_REPLACEMENT variant={variant}");
        assert_eq!(*freed.borrow(), vec![(replacement, 0)], "only rejected replacement is released once");
        assert_eq!(live, before);
        assert_eq!(original, old_rows);
        assert_eq!(std::slice::from_raw_parts(owned.rows() as *const u8, 48), prefix,
            "no partial copy before whole-range admission");
    }
} }
