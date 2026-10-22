//! Diagnostic-only, bounded observation of the selector's actual reads.
//! No second traversal, synthetic memory values, writes or retained native pointers.
use crate::scis_material_binding::Read;
use std::cell::{Cell, RefCell};

pub(super) struct Observed<'a, R> {
    read: &'a R,
    tail: RefCell<([(u32, Option<u32>); 5], usize)>,
    membership: Cell<(u32, u32, u32, u32, Option<bool>)>,
}
impl<'a, R: Read> Observed<'a, R> {
    pub(super) fn new(read: &'a R) -> Self {
        Self { read, tail: RefCell::new(([(0, None); 5], 0)),
            membership: Cell::new((0, 0, 0, 0, None)) }
    }
    /// Header: renderer, pass, node, submesh, total reads, validity bitmap.
    /// Then the last five (address,value) pairs, oldest first. Bit 8 = selected.
    /// A missing read is distinct from a successfully read zero.
    pub(super) fn snapshot(&self, context: [u32; 4], selected: bool) -> [u32; 16] {
        let (tail, count) = *self.tail.borrow();
        let mut out = [0; 16];
        out[..4].copy_from_slice(&context);
        out[4] = count.min(u32::MAX as usize) as u32;
        out[5] = (selected as u32) << 8;
        for i in 0..count.min(5) {
            let (address, value) = tail[(count.saturating_sub(5) + i) % 5];
            out[5] |= (value.is_some() as u32) << i;
            out[6 + 2 * i] = address;
            out[7 + 2 * i] = value.unwrap_or(0);
        }
        out
    }
    /// No extra traversal. State 0=unreadable, 1=not unique, 2=unique;
    /// count=0 distinguishes an uncalled method from an unreadable array.
    pub(super) fn membership_snapshot(&self, context: [u32; 4]) -> [u32; 16] {
        let (calls, first, count, value, result) = self.membership.get();
        let mut out = [0; 16];
        out[..4].copy_from_slice(&context);
        out[4..9].copy_from_slice(&[calls, first, count, value,
            result.map_or(0, |unique| 1 + unique as u32)]);
        out
    }
}
impl<R: Read> Read for Observed<'_, R> {
    fn unique_member(&self, first: u32, count: u32, value: u32) -> Option<bool> {
        // Preserve Native's single-region readability check and bounded scan;
        // the trait's default per-word implementation is not equivalent.
        let result = self.read.unique_member(first, count, value);
        self.membership.set((self.membership.get().0.saturating_add(1), first, count, value, result));
        result
    }
    fn word(&self, address: u32) -> Option<u32> {
        let value = self.read.word(address);
        let mut tail = self.tail.borrow_mut();
        let slot = tail.1 % 5;
        tail.0[slot] = (address, value);
        tail.1 += 1;
        value
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    struct NativeLike(Cell<u32>);
    impl Read for NativeLike {
        fn word(&self, address: u32) -> Option<u32> { (address != 7).then_some(0) }
        fn unique_member(&self, _: u32, _: u32, _: u32) -> Option<bool> {
            self.0.set(self.0.get() + 1);
            None // unreadable whole region, unlike default word-wise scan
        }
    }
    #[test]
    fn observation_preserves_native_membership_and_missing_vs_zero() {
        let native = NativeLike(Cell::new(0));
        let observed = Observed::new(&native);
        assert_eq!(observed.word(6), Some(0));
        assert_eq!(observed.word(7), None);
        assert_eq!(observed.unique_member(0x10000, 1, 0), None);
        assert_eq!(native.0.get(), 1);
        let words = observed.snapshot([1, 2, 3, 4], false);
        assert_eq!(&words[4..10], &[2, 1, 6, 0, 7, 0]);
        assert_eq!(&observed.membership_snapshot([1, 2, 3, 4])[4..9], &[1, 0x10000, 1, 0, 0]);
        assert_eq!(native.0.get(), 1); // serializing never re-reads live state
    }
}
