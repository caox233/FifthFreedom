//! The render camera is usable only after an observed native source and publish.
//! No pre-call effect flags, guessed last writer, or current/previous angle equality.
use super::rotation_precision::Snapshot;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Source { Unknown, Current, Old, Invalid }
#[derive(Clone, Copy)]
struct Scope { core: usize, input: Option<Snapshot>, source: Source, published: bool }
pub(super) struct Publication {
    scopes: [Option<Scope>; 8],
    depth: usize,
    overflow: usize,
}
impl Default for Publication {
    fn default()->Self { Self { scopes:[None;8], depth:0, overflow:0 } }
}
impl Publication {
    pub fn clear(&mut self) { *self=Self::default(); }
    pub fn busy(&self)->bool { self.depth!=0 || self.overflow!=0 }
    pub fn slot(&self)->Option<usize> {
        (self.overflow==0 && self.depth!=0).then(||self.depth-1)
    }
    pub fn current_input(&self)->Option<(usize,Snapshot)> {
        let i=self.slot()?;let s=self.scopes[i]?;
        if s.source!=Source::Current || s.published {return None;}
        let t=s.input?;(t.owner.core==s.core).then_some((i,t))
    }
    pub fn begin(&mut self, core:usize, input:Option<Snapshot>) {
        if self.depth==self.scopes.len() || self.overflow!=0 {
            self.overflow=self.overflow.saturating_add(1);
            if let Some(scope)=self.scopes.last_mut().and_then(Option::as_mut) { scope.source=Source::Invalid; }
            return;
        }
        self.scopes[self.depth]=Some(Scope {core,input,source:Source::Unknown,published:false});
        self.depth+=1;
    }
    pub fn mark(&mut self, core:usize, event:usize) {
        if self.overflow!=0 || self.depth==0 { return; }
        let Some(scope)=self.scopes[self.depth-1].as_mut() else { return; };
        if scope.core!=core || scope.published { scope.source=Source::Invalid;return; }
        match (event,scope.source) {
            (1,Source::Unknown)=>scope.source=Source::Current,
            (2,Source::Current)=>scope.source=Source::Old,
            (3,Source::Current|Source::Old)=>scope.published=true,
            _=>scope.source=Source::Invalid,
        }
    }
    pub fn end(&mut self, core:usize, current:Option<Snapshot>)->Option<Snapshot> {
        if self.overflow!=0 { self.overflow-=1; return None; }
        if self.depth==0 { return None; }
        self.depth-=1;
        let scope=self.scopes[self.depth].take()?;
        if scope.core!=core {
            self.clear();
            return None;
        }
        let input=scope.input?;
        (scope.source==Source::Current && scope.published && current==Some(input)
            && input.owner.core==core).then_some(input)
    }
}

#[cfg(test)] mod tests {
    use super::*;
    use super::super::rotation_precision::Owner;
    fn token()->Snapshot {Snapshot{owner:Owner{viewport:1,window:2,pawn:3,mode:4,core:5,world:6,generation:7},frame:8,targets:[9,10],fraction:[0.25,-0.25]}}
    #[test]fn composition_can_only_consume_top_current_unpublished_scope(){
        let t=token();let mut p=Publication::default();assert_eq!(p.current_input(),None);
        p.begin(5,Some(t));assert_eq!(p.slot(),Some(0));assert_eq!(p.current_input(),None);
        p.mark(5,1);assert_eq!(p.current_input(),Some((0,t)));
        p.begin(6,None);p.mark(6,1);assert_eq!(p.current_input(),None);p.mark(6,3);p.end(6,None);
        assert_eq!(p.current_input(),Some((0,t)));p.mark(5,3);assert_eq!(p.current_input(),None);
        assert_eq!(p.end(5,Some(t)),Some(t));assert_eq!(p.slot(),None);
        p.begin(5,Some(t));p.mark(5,1);p.mark(5,2);assert_eq!(p.current_input(),None);
        p.clear();for _ in 0..9{p.begin(5,Some(t));p.mark(5,1);}assert_eq!(p.slot(),None);assert_eq!(p.current_input(),None);
    }
    #[test]fn actual_current_publish_only(){
        let t=token();
        for (events,pass) in [(vec![1,3],true),(vec![],false),(vec![3],false),(vec![1],false),(vec![1,2,3],false),(vec![1,1,3],false),(vec![1,3,2],false),(vec![1,9,3],false)] {
            let mut p=Publication::default();p.begin(5,Some(t));for e in events{p.mark(5,e);}
            assert_eq!(p.end(5,Some(t)).is_some(),pass);assert!(!p.busy());
        }
    }
    #[test]fn stale_nested_and_overflow_are_not_global_last_writer(){
        let t=token();let mut p=Publication::default();p.begin(5,Some(t));p.mark(5,1);
        p.begin(6,None);p.mark(6,1);p.mark(6,3);assert_eq!(p.end(6,None),None);
        assert!(p.busy());p.mark(5,3);assert_eq!(p.end(5,Some(t)),Some(t));
        p.begin(5,Some(t));p.mark(5,1);p.mark(5,3);
        assert_eq!(p.end(5,Some(Snapshot{frame:9,..t})),None);
        for _ in 0..9 {p.begin(5,Some(t));p.mark(5,1);}
        p.mark(5,3);assert_eq!(p.end(5,Some(t)),None);
        p.mark(5,3);assert_eq!(p.end(5,Some(t)),None);
        for _ in 0..7 {p.mark(5,3);let _=p.end(5,Some(t));}
        assert!(!p.busy());
        p.begin(5,Some(t));p.mark(5,1);p.mark(99,3);assert_eq!(p.end(5,Some(t)),None);
        p.begin(5,Some(t));p.mark(5,1);p.mark(5,3);assert_eq!(p.end(99,Some(t)),None);assert!(!p.busy());
    }
}
