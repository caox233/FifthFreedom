//! Value-only publication -> native provider insertion -> drained batch join.
//! No native pointer is dereferenced here and no camera is modified. The native
//! adapter must prove the owner and copy its ray before publishing the command.
const MAX_PENDING: usize = 64;
const MAX_CAMERAS: usize = 16;
const MAX_PROVIDERS: usize = 4;

use crate::scis_scene_source::Source;
#[derive(Clone, Copy)]
struct Pending {
    command: u32,
    provider: u32,
    nonce: u64,
    source: Source,
}
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Entry {
    pub nonce: u64,
    pub source: Source,
}
#[derive(Clone, Copy)]
struct Provider {
    address: u32,
    batch: u64,
    sealed: bool,
    poisoned: bool,
    entries: [Option<Entry>; MAX_CAMERAS],
}
impl Provider {
    fn new(address: u32) -> Self {
        Self {
            address,
            batch: 0,
            sealed: false,
            poisoned: false,
            entries: [None; MAX_CAMERAS],
        }
    }
}
pub struct Join {
    serial: u64,
    pending: [Option<Pending>; MAX_PENDING],
    providers: [Option<Provider>; MAX_PROVIDERS],
}
impl Default for Join {
    fn default() -> Self {
        Self {
            serial: 0,
            pending: [None; MAX_PENDING],
            providers: [None; MAX_PROVIDERS],
        }
    }
}
impl Join {
    fn next(&mut self) -> Option<u64> {
        self.serial = self.serial.checked_add(1)?;
        Some(self.serial)
    }
    /// Lifecycle reset invalidates data without reusing the old epoch numbers.
    pub fn reset(&mut self) {
        self.pending.fill(None);
        self.providers.fill(None);
    }
    pub fn publish(&mut self, command: u32, provider: u32, source: Source) -> bool {
        if command < 0x10000 || provider < 0x10000 || !source.valid() {
            self.reject(command);
            return false;
        }
        // Reusing a still-outstanding command address is ambiguous. Discard both.
        if let Some(slot) = self
            .pending
            .iter_mut()
            .find(|p| p.is_some_and(|p| p.command == command))
        {
            // Retain a tombstone until consumption. Otherwise a third publish
            // could occupy the cleared slot and be consumed by the first command.
            slot.as_mut().unwrap().nonce = 0;
            return false;
        }
        let Some(index) = self.pending.iter().position(Option::is_none) else {
            return false;
        };
        let Some(nonce) = self.next() else {
            return false;
        };
        self.pending[index] = Some(Pending {
            command,
            provider,
            nonce,
            source,
        });
        true
    }
    /// An observed native publication without valid ownership must invalidate
    /// an older unresolved publication at that address, not inherit its ray.
    pub fn reject(&mut self, command:u32) {
        if let Some(p)=self.pending.iter_mut().flatten().find(|p|p.command==command){p.nonce=0;}
    }
    /// Called after the actual native provider Add returns. No predicted index.
    pub fn admitted(
        &mut self,
        command: u32,
        provider: u32,
        before: u32,
        after: u32,
        index: u32,
    ) -> bool {
        let Some(slot) = self
            .pending
            .iter()
            .position(|p| p.is_some_and(|p| p.command == command))
        else {
            return false;
        };
        let p = self.pending[slot].take().unwrap();
        if p.nonce == 0
            || p.provider != provider
            || before >= 16
            || after != before + 1
            || index != before
        {
            return false;
        }
        let n = if let Some(n) = self
            .providers
            .iter()
            .position(|p| p.is_some_and(|p| p.address == provider))
        {
            n
        } else if let Some(n) = self.providers.iter().position(Option::is_none) {
            self.providers[n] = Some(Provider::new(provider));
            n
        } else {
            return false;
        };
        let state = self.providers[n].as_mut().unwrap();
        if state.sealed || state.entries[index as usize].is_some() {
            state.poisoned = true;
            return false;
        }
        if state.poisoned {
            return false;
        }
        state.entries[index as usize] = Some(Entry {
            nonce: p.nonce,
            source: p.source,
        });
        true
    }
    /// Native 161D0C0 has returned: all command types, not merely scene/type2.
    pub fn drained(&mut self, provider: u32) -> Option<u64> {
        let n = self
            .providers
            .iter()
            .position(|p| p.is_some_and(|p| p.address == provider))?;
        if self.providers[n].as_ref()?.sealed || self.providers[n].as_ref()?.poisoned {
            return None;
        }
        let batch = self.next()?;
        let p = self.providers[n].as_mut()?;
        p.batch = batch;
        p.sealed = true;
        Some(batch)
    }
    /// Use native retained info+8, not its post-sort physical array slot. The
    /// caller must also validate that info belongs to this live provider array.
    pub fn selected(&self, provider: u32, batch: u64, index: u32, flags: u32) -> Option<Entry> {
        if batch == 0 || index >= 16 || flags & (0x200000 | 0x20000000) != 0 {
            return None;
        }
        let p = self
            .providers
            .iter()
            .flatten()
            .find(|p| p.address == provider)?;
        if !p.sealed || p.poisoned || p.batch != batch {
            return None;
        }
        p.entries[index as usize]
    }
    /// After provider traversal/reset. Future commands remain in pending slots.
    pub fn finished(&mut self, provider: u32) {
        if let Some(p) = self
            .providers
            .iter_mut()
            .find(|p| p.is_some_and(|p| p.address == provider))
        {
            *p = None;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn source(generation:u32)->Source {
        use hooks_plugin_api::scope_aim::{Request,Sample,ABI};
        use crate::scis_scene_source::Scope;
        let request=Request{abi:ABI,size:28,scene:0x10000,viewport:0x20000,controller:0x30000,generation,world:0x40000};
        Source{ads_fraction_bits:1f32.to_bits(),aim:Sample{abi:ABI,size:100,request,frame:[generation,0],pawn:0x50000,camera:0x60000,direction:[0.,0.,1.],..Sample::default()},
            scope:Scope{pawn:0x50000,pawn_generation:generation,actor:0x70000,generation,node:0x80000,resource:0x90000}}
    }
    #[test]
    fn only_native_admission_and_full_drain_authorize_current_batch() {
        let mut j = Join::default();
        assert!(j.publish(0x50000, 0x60000, source(1)));
        assert!(j.selected(0x60000, 1, 0, 0).is_none());
        assert!(j.admitted(0x50000, 0x60000, 0, 1, 0));
        assert!(j.selected(0x60000, 1, 0, 0).is_none());
        let b = j.drained(0x60000).unwrap();
        assert_eq!(j.selected(0x60000, b, 0, 0).unwrap().source, source(1));
        assert!(j.selected(0x60000, b, 0, 0x200000).is_none());
        assert!(j.selected(0x60000, b, 0, 0x20000000).is_none());
        assert!(j.selected(0x61000, b, 0, 0).is_none());
        assert!(j.selected(0x60000, b + 1, 0, 0).is_none());
    }
    #[test]
    fn reused_pointer_index_and_lifecycle_never_reuse_identity() {
        let mut j = Join::default();
        j.publish(0x50000, 0x60000, source(1));
        j.admitted(0x50000, 0x60000, 0, 1, 0);
        let a = j.drained(0x60000).unwrap();
        // Native future slot already published while this old batch is consumed.
        j.publish(0x51000, 0x60000, source(2));
        j.finished(0x60000);
        assert!(j.selected(0x60000, a, 0, 0).is_none());
        assert!(j.admitted(0x51000, 0x60000, 0, 1, 0));
        let b = j.drained(0x60000).unwrap();
        assert_ne!(a, b);
        assert_eq!(j.selected(0x60000, b, 0, 0).unwrap().source.scope.generation, 2);
        assert!(j.selected(0x60000, a, 0, 0).is_none());
        j.reset();
        j.publish(0x50000, 0x60000, source(3));
        j.admitted(0x50000, 0x60000, 0, 1, 0);
        assert!(j.drained(0x60000).unwrap() > b);
    }
    #[test]
    fn ambiguous_or_rejected_native_input_does_not_borrow_another_record() {
        let mut j = Join::default();
        j.publish(0x50000, 0x60000, source(1));
        assert!(!j.publish(0x50000, 0x60000, source(2)));
        assert!(!j.publish(0x50000, 0x60000, source(3)));
        assert!(!j.admitted(0x50000, 0x60000, 0, 1, 0));
        for (before, after, index) in [(16, 16, 0), (0, 0, 0), (0, 2, 0), (0, 1, 1)] {
            j.publish(0x50000, 0x60000, source(1));
            assert!(!j.admitted(0x50000, 0x60000, before, after, index));
        }
        j.publish(0x50000, 0x60000, source(1));
        assert!(!j.admitted(0x50000, 0x61000, 0, 1, 0));
        assert!(j.drained(0x60000).is_none());
    }
    #[test]
    fn duplicate_admission_poisons_the_provider_instead_of_overwriting() {
        let mut j = Join::default();
        j.publish(0x50000, 0x60000, source(1));
        j.admitted(0x50000, 0x60000, 0, 1, 0);
        j.publish(0x51000, 0x60000, source(2));
        assert!(!j.admitted(0x51000, 0x60000, 0, 1, 0));
        assert!(j.drained(0x60000).is_none());
    }
    #[test]
    fn rejected_reuse_cannot_inherit_pending_ownership() {
        let mut j=Join::default();
        assert!(j.publish(0x50000,0x60000,source(1)));
        j.reject(0x50000);
        assert!(!j.admitted(0x50000,0x60000,0,1,0));
        assert!(j.publish(0x50000,0x60000,source(2)));
        let mut invalid=source(3);invalid.scope.generation=0;
        assert!(!j.publish(0x50000,0x60000,invalid));
        assert!(!j.admitted(0x50000,0x60000,0,1,0));
    }
}
