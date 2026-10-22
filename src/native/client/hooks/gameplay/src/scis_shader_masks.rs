//! Exact allocation extent for native173AD2F, never an enum or readable-region
//! guess. Registration is inert until the native allocation call is patched as
//! part of the complete scope projection group.
use std::sync::atomic::{AtomicU64, Ordering};

static ALLOCATION: AtomicU64 = AtomicU64::new(0);
pub(crate) const SITE: u32 = 0x173ad2f;
pub(crate) const ORIGINAL: [u8; 5] = [0xe8, 0x3c, 0x86, 0xf0, 0xff];

pub(crate) fn reset() {
    ALLOCATION.store(0, Ordering::Release);
}

/// Publish only an exact successful request. The retail multiplication uses
/// 0xffffffff on overflow; reject it, zero, malformed sizes and excessive counts.
fn encoded(bytes: u32, pointer: u32) -> u64 {
    if bytes == 0
        || bytes % 8 != 0
        || bytes / 8 > 65536
        || pointer < 0x10000
        || pointer % 4 != 0
        || pointer.checked_add(bytes).is_none()
    {
        return 0;
    }
    ((bytes / 8) as u64) << 32 | pointer as u64
}

pub(crate) fn allocate(args: [u32; 5], original: impl FnOnce([u32; 5]) -> u32) -> u32 {
    // No native work runs under a lock. A recursive/replaced allocation must
    // not retain a previous table while this call is incomplete.
    reset();
    let result = original(args);
    ALLOCATION.store(encoded(args[0], result), Ordering::Release);
    result
}

/// The caller reads current native global344A658 and rechecks it before using
/// the selection. A pre-publication registration or changed global is rejected.
pub(crate) fn count(current_pointer: u32) -> Option<u32> {
    let value = ALLOCATION.load(Ordering::Acquire);
    (value != 0 && value as u32 == current_pointer).then_some((value >> 32) as u32)
}

pub(crate) unsafe extern "C" fn capture(bytes: u32, tag: u32, source: u32, a: u32, b: u32) -> u32 {
    let native: unsafe extern "C" fn(u32, u32, u32, u32, u32) -> u32 =
        std::mem::transmute(0x1643370usize);
    allocate([bytes, tag, source, a, b], |v| {
        native(v[0], v[1], v[2], v[3], v[4])
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn scis_masks_accept_only_exact_native_allocation_extent() {
        assert_eq!(encoded(512 * 8, 0x10000), 512u64 << 32 | 0x10000);
        for (bytes, pointer) in [
            (0, 0x10000),
            (7, 0x10000),
            (0xffffffff, 0x10000),
            (65537 * 8, 0x10000),
            (8, 0),
            (8, 0xffff),
            (8, 0x10001),
            (16, 0xfffffff8),
        ] {
            assert_eq!(encoded(bytes, pointer), 0);
        }
    }
}
