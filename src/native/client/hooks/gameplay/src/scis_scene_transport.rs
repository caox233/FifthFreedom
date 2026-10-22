//! Owned scene transport. The caller serializes this small value-only state;
//! no native function, GPU API, or actor memory read runs under that lock.
use crate::{scis_frame_join::{Entry,Join},scis_scene_source};

#[derive(Default)]
pub(crate) struct Transport { epoch:u64, join:Join }
impl Transport {
    fn synchronize(&mut self) {
        let current=scis_scene_source::binding_epoch();
        if self.epoch!=current { self.join.reset();self.epoch=current; }
    }
    /// Called at667A2C before the original immediate-or-deferred consumer.
    pub(crate) fn publish(&mut self,command:u32,provider:u32,input:u32)->bool {
        self.synchronize();
        let Some(source)=scis_scene_source::take(provider,input) else {self.join.reject(command);return false;};
        if scis_scene_source::binding_epoch()!=self.epoch {self.synchronize();return false;}
        self.join.publish(command,provider,source)
    }
    pub(crate) fn admitted(&mut self,command:u32,provider:u32,before:u32,after:u32,index:u32)->bool {
        self.synchronize();self.join.admitted(command,provider,before,after,index)
    }
    pub(crate) fn drained(&mut self,provider:u32)->Option<u64> {
        self.synchronize();self.join.drained(provider)
    }
    pub(crate) fn selected(&mut self,provider:u32,batch:u64,info:u32,count:u32,index:u32,flags:u32)->Option<Entry> {
        self.synchronize();
        // Sort changes physical position, not info+8 insertion index. Check
        // both independently; a stale stack/copy cannot be selected by index.
        if provider<0x10000||count==0||count>16||index>=count {return None;}
        let offset=info.checked_sub(provider.checked_add(0x30)?)?;
        if offset%0x430!=0||offset/0x430>=count {return None;}
        let result=self.join.selected(provider,batch,index,flags)?;
        (self.epoch==scis_scene_source::binding_epoch()).then_some(result)
    }
    pub(crate) fn finished(&mut self,provider:u32) {self.synchronize();self.join.finished(provider);}
    pub(crate) fn reset(&mut self) {self.synchronize();self.join.reset();}
}

#[cfg(test)]mod tests {
    use super::*;
    use hooks_plugin_api::scope_aim::{Request,Sample,ABI};
    use crate::scis_scene_source::{Source,Scope,publish_owned};
    fn source()->Source {
        let request=Request{abi:ABI,size:28,scene:0x10740,viewport:0x20000,controller:0x30000,generation:1,world:0x40000};
        Source{ads_fraction_bits:1f32.to_bits(),aim:Sample{abi:ABI,size:100,request,frame:[1,0],pawn:0x50000,camera:0x60000,direction:[0.,0.,1.],..Sample::default()},
            scope:Scope{pawn:0x50000,pawn_generation:1,actor:0x70000,generation:1,node:0x80000,resource:0x90000}}
    }
    fn publish(t:&mut Transport,command:u32){
        let bp=0x11000;let provider=0xa0000;let info=bp-0x530;
        let r=publish_owned(bp,0,0x663ad0,provider,info,0x663ad0,|_|Some(source()),|_,_,p,i|{assert!(t.publish(command,p,i));17});
        assert_eq!(r,17);assert!(scis_scene_source::take(provider,info).is_none());
    }
    #[test]fn selects_only_retained_native_array_and_keeps_original_return(){
        let mut t=Transport::default();publish(&mut t,0xb0000);assert!(t.admitted(0xb0000,0xa0000,0,1,0));let b=t.drained(0xa0000).unwrap();
        assert_eq!(t.selected(0xa0000,b,0xa0030,1,0,0).unwrap().source,source());
        for(info,count,index,flags)in[(0xa0031,1,0,0),(0xa0460,1,0,0),(0xa0030,17,0,0),(0xa0030,1,1,0),(0xa0030,1,0,0x200000),(0x10ad0,1,0,0)]{
            assert!(t.selected(0xa0000,b,info,count,index,flags).is_none());
        }
    }
    #[test]fn binding_epoch_invalidates_pending_and_sealed_batches(){
        let mut t=Transport::default();publish(&mut t,0xb0000);scis_scene_source::clear();assert!(!t.admitted(0xb0000,0xa0000,0,1,0));
        publish(&mut t,0xb0000);assert!(t.admitted(0xb0000,0xa0000,0,1,0));let b=t.drained(0xa0000).unwrap();
        scis_scene_source::clear();assert!(t.selected(0xa0000,b,0xa0030,1,0,0).is_none());
        publish(&mut t,0xb0000);assert!(t.admitted(0xb0000,0xa0000,0,1,0));assert!(t.drained(0xa0000).unwrap()>b);
    }
    #[test]fn unrelated_call_at_pending_address_cannot_borrow_prior_scope(){
        let mut t=Transport::default();publish(&mut t,0xb0000);
        assert!(!t.publish(0xb0000,0xa0000,0x10ad0));
        assert!(!t.admitted(0xb0000,0xa0000,0,1,0));assert!(t.drained(0xa0000).is_none());
    }
}
