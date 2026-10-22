//! Bounded per-instance audio identity. Numeric request IDs and source/asset
//! bytes are not ownership. No lock or native call is performed in this module.
use std::collections::BTreeMap;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) struct Token { pub generation: u64, pub request: u32 }
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) struct Processor { pub incarnation: u64, pub owner: Option<Token>, pub rejected: bool }
pub(super) struct Bank {
    next: u64,
    pub commands: BTreeMap<usize, Token>,
    pub contexts: BTreeMap<usize, Token>,
    pub delayed: BTreeMap<usize, Option<Token>>,
    pub emitters: BTreeMap<usize, Token>,
    pub processors: BTreeMap<usize, Processor>,
}
const CAP: usize = 1024;
impl Bank {
    pub const fn new() -> Self { Self { next: 1, commands: BTreeMap::new(), contexts: BTreeMap::new(), delayed: BTreeMap::new(), emitters: BTreeMap::new(), processors: BTreeMap::new() } }
    fn serial(&mut self) -> Option<u64> {
        let result = self.next;
        self.next = result.checked_add(1)?;
        Some(result)
    }
    pub fn clear(&mut self) {
        // Never reset the sequence: an in-flight callback may still hold a lease.
        self.commands.clear(); self.contexts.clear(); self.delayed.clear();
        self.emitters.clear(); self.processors.clear();
    }
    pub fn command(&mut self, p: usize, request: u32) -> Option<Token> {
        // A duplicate live address is ambiguous, not a reason to reuse its old
        // ownership. Drop enhancement for both interpretations, never replace it.
        if self.commands.remove(&p).is_some() { return None; }
        if p == 0 || request == u32::MAX || self.commands.len() >= CAP { return None; }
        let t = Token { generation: self.serial()?, request };
        self.commands.insert(p, t); Some(t)
    }
    pub fn put(map: &mut BTreeMap<usize, Token>, p: usize, t: Option<Token>) -> bool {
        // A constructor/copy owns this destination. Clear stale ownership even
        // for an unknown new source; never inherit an outer thread-local scope.
        let duplicate = map.remove(&p).is_some();
        if duplicate || p == 0 || map.len() >= CAP { return false; }
        if let Some(t) = t { map.insert(p, t); return true; }
        false
    }
    pub fn delayed_copy(&mut self, object: usize, source: usize) {
        let token = self.contexts.get(&source).copied();
        self.contexts.remove(&(object + 0x24));
        if object == 0 || (!self.delayed.contains_key(&object) && self.delayed.len() >= CAP) { return; }
        self.delayed.insert(object, token);
        Self::put(&mut self.contexts, object + 0x24, token);
    }
    pub fn retire_delayed(&mut self, object: usize) {
        if self.delayed.remove(&object).is_some() { self.contexts.remove(&(object + 0x24)); }
    }
    pub fn processor_created(&mut self, p: usize) {
        self.processors.remove(&p);
        if p == 0 || self.processors.len() >= CAP { return; }
        if let Some(incarnation) = self.serial() {
            self.processors.insert(p, Processor { incarnation, owner: None, rejected: false });
        }
    }
    pub fn bind(&mut self, p: usize, emitter: usize) {
        let token = self.emitters.get(&emitter).copied();
        let Some(record) = self.processors.get_mut(&p) else { return; };
        // Several independently eligible SC-IS shots may overlap on one native
        // processor. Their uniform gain is applied once to its completed block,
        // never once per request. Keep the first token as the incarnation lease;
        // every subsequent binding must independently prove exact eligibility.
        // Any unknown binding rejects the entire incarnation until destruction.
        if token.is_none() {
            record.rejected = true; record.owner = None;
        } else if !record.rejected && record.owner.is_none() { record.owner = token; }
    }
    pub fn lease(&self, p: usize) -> Option<Processor> {
        self.processors.get(&p).copied().filter(|r| !r.rejected && r.owner.is_some())
    }
    pub fn current(&self, p: usize, lease: Processor) -> bool { self.lease(p) == Some(lease) }
}

pub(super) fn pcm_sample(value: i16) -> i16 {
    ((i32::from(value) * 3) / 2).clamp(i16::MIN as i32, i16::MAX as i32) as i16
}
pub(super) fn pcm_length(status: u32, requested: u32, produced: u32, channels: u32, stride: u32) -> Option<usize> {
    if !matches!(status, 0 | 2 | 4) || requested == 0 || requested > 65_536 || produced > requested
        || !matches!(channels, 1 | 2) || stride != channels * 2 { return None; }
    (produced as usize).checked_mul(channels as usize)
}

#[cfg(test)] mod tests {
    use super::*;
    #[test] fn instance_generation_survives_async_return_and_rejects_id_wrap() {
        let mut b = Bank::new(); let a = b.command(1, 77).unwrap();
        assert!(Bank::put(&mut b.contexts, 100, Some(a)));
        b.commands.remove(&1); let other = b.command(1, 77).unwrap();
        assert_ne!(a.generation, other.generation);
        assert_eq!(b.contexts[&100], a);
        b.clear(); assert!(b.command(1, 77).unwrap().generation > other.generation);
    }
    #[test] fn copied_contexts_clear_unknown_and_retire_without_source_lifetime() {
        let mut b = Bank::new(); let t = b.command(1, 2).unwrap();
        Bank::put(&mut b.contexts, 100, Some(t)); b.delayed_copy(200, 100);
        b.contexts.remove(&100); assert_eq!(b.contexts[&(200+0x24)], t);
        b.delayed_copy(200, 999); assert!(!b.contexts.contains_key(&(200+0x24)));
        Bank::put(&mut b.contexts, 100, Some(t)); b.delayed_copy(200, 100);
        b.retire_delayed(200); assert!(!b.contexts.contains_key(&(200+0x24)));
        b.retire_delayed(200); assert!(b.delayed.is_empty());
    }
    #[test] fn shared_unknown_or_reused_processor_can_never_inherit_boost() {
        let mut b = Bank::new(); let t = b.command(1, 4).unwrap();
        Bank::put(&mut b.emitters, 10, Some(t)); Bank::put(&mut b.emitters, 11, Some(t));
        b.processor_created(20); b.bind(20, 10); b.bind(20, 11); let old = b.lease(20).unwrap();
        b.bind(20, 99); assert!(b.lease(20).is_none()); b.bind(20, 10); assert!(b.lease(20).is_none());
        b.processor_created(20); b.bind(20, 10); assert!(!b.current(20, old));
        b.processors.remove(&20); assert!(b.lease(20).is_none());
        b.processor_created(20); b.bind(20, 99); b.bind(20, 10); assert!(b.lease(20).is_none());
    }
    #[test] fn capacity_and_exhaustion_fail_closed_without_live_eviction() {
        let mut b = Bank::new(); for p in 1..=CAP { assert!(b.command(p, 4).is_some()); }
        let first = b.commands[&1]; assert!(b.command(CAP+1, 4).is_none());
        assert_eq!(b.commands[&1], first);assert!(b.command(1, 4).is_none());assert!(!b.commands.contains_key(&1));
        b.clear(); b.next = u64::MAX; assert!(b.command(1, 4).is_none());
        assert!(b.command(1, u32::MAX).is_none());
    }
    #[test] fn signed_pcm_saturates_once_without_wrapping_or_unknown_format() {
        for v in i16::MIN..=i16::MAX {
            assert_eq!(i32::from(pcm_sample(v)), (i32::from(v)*3/2).clamp(-32768,32767));
        }
        assert_eq!(pcm_sample(8000), 12000); assert_eq!(pcm_sample(24000), 32767);
        assert_eq!(pcm_length(0, 8, 8, 2, 4), Some(16));
        for args in [(3,8,8,2,4),(0,8,9,2,4),(0,0,0,1,2),(0,8,8,3,6),(0,8,8,2,8)] {
            assert!(pcm_length(args.0,args.1,args.2,args.3,args.4).is_none());
        }
    }
}
