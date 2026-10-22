//! Synchronous SCIS scene ownership plus exact host-native fractional aim.
//! No polling, retained actor pointers, global latest ray, or projection writes.
use crate::scis_diagnostics as diag;
use hooks_plugin_api::scope_aim::{QueryFn, Request, Sample, ABI};
use std::sync::atomic::{AtomicUsize, AtomicU64, Ordering};
static QUERY: AtomicUsize = AtomicUsize::new(0);
static EPOCH: AtomicU64 = AtomicU64::new(1);

#[repr(C)]
#[derive(Clone,Copy,Debug,PartialEq,Eq)]
pub(crate) struct Scope {
    pub pawn: u32,
    pub pawn_generation: u32,
    pub actor: u32,
    pub generation: u32,
    pub node: u32,
    pub resource: u32,
}
#[repr(C)]
#[derive(Clone,Copy,Debug,PartialEq)]
pub(crate) struct Source { pub aim: Sample, pub scope: Scope, pub ads_fraction_bits: u32 }
impl Source {
    pub(crate) fn valid(self)->bool {
        f32::from_bits(self.ads_fraction_bits).is_finite()
            && (0. ..=1.).contains(&f32::from_bits(self.ads_fraction_bits))
            && self.aim.valid_for(self.aim.request) && self.scope.pawn==self.aim.pawn
            && self.scope.pawn_generation!=0 && self.scope.generation!=0
            && [self.scope.pawn,self.scope.actor,self.scope.node,self.scope.resource].into_iter().all(|p|p>=0x10000)
    }
}

/// Callback and bridge code must outlive native callsites and in-flight calls.
/// PIN|FROM_ADDRESS retains an existing module; it does not load another DLL.
pub(crate) fn retain_code(address: usize) -> bool {
    if address == 0 { return false; }
    #[link(name = "kernel32")]
    extern "system" {
        fn GetModuleHandleExW(flags: u32, address: *const u16, module: *mut *mut std::ffi::c_void) -> i32;
    }
    let mut module = std::ptr::null_mut();
    unsafe { GetModuleHandleExW(5, address as *const u16, &mut module) != 0 && !module.is_null() }
}
pub(crate) fn bind(abi:u32, query:Option<QueryFn>) -> bool {
    QUERY.store(0,Ordering::Release);
    EPOCH.fetch_add(1,Ordering::AcqRel);
    if abi!=ABI || query.is_some_and(|f|!retain_code(f as usize)) { return false; }
    QUERY.store(query.map_or(0,|f|f as usize),Ordering::Release);
    true
}
pub(crate) fn clear() { let _=bind(ABI,None); }
pub(crate) fn binding_epoch()->u64 { EPOCH.load(Ordering::Acquire) }

fn collect(scene:u32, mut inspect:impl FnMut(u32)->Option<(Request,Scope,u32)>,
    query:impl FnOnce(&Request,&mut Sample,u32)->i32) -> Option<Source> {
    let(request,scope,ads_fraction_bits)=(inspect(scene)).or_else(||{diag::hit(diag::SOURCE_INSPECT_REJECT);None})?;
    if !request.valid() || request.scene!=scene { return None; }
    let mut sample=Sample::default();
    if query(&request,&mut sample,std::mem::size_of::<Sample>()as u32)!=1 { diag::hit(diag::SOURCE_QUERY_REJECT); return None; }
    if !sample.valid_for(request) || sample.pawn!=scope.pawn { diag::hit(diag::SOURCE_SAMPLE_REJECT); return None; }
    if inspect(scene)!=Some((request,scope,ads_fraction_bits)) { diag::hit(diag::SOURCE_RECHECK_REJECT); return None; }
    diag::hit(diag::SOURCE_ACCEPTED);
    let source=Source{aim:sample,scope,ads_fraction_bits};source.valid().then_some(source)
}
pub(crate) unsafe fn capture(scene:u32)->Option<Source> {
    let epoch=EPOCH.load(Ordering::Acquire);
    let address=QUERY.load(Ordering::Acquire);
    if address==0 { diag::hit(diag::SOURCE_UNBOUND);return None; }
    let query:QueryFn=std::mem::transmute(address);
    let source=collect(scene,|s|crate::scis_zoom_native::scene_source(s),
        |r,out,bytes|query(r,out,bytes))?;
    if EPOCH.load(Ordering::Acquire)!=epoch || QUERY.load(Ordering::Acquire)!=address {return None;}
    #[cfg(feature="scis-runtime-diagnostics")] crate::scis_event_observer::scene(source);
    Some(source)
}

#[derive(Clone,Copy)]
struct Bound { provider:u32, info:u32, source:Source }
#[derive(Default)]
struct Calls { epoch:u64, depth:u32, current:Option<Bound> }
thread_local! { static CALLS:std::cell::RefCell<Calls>=std::cell::RefCell::new(Calls::default()); }
fn calls<T>(f:impl FnOnce(&mut Calls)->T)->Option<T> {
    CALLS.try_with(|s|{
        let mut s=s.try_borrow_mut().ok()?;let epoch=EPOCH.load(Ordering::Acquire);
        if s.epoch!=epoch { *s=Calls{epoch,..Calls::default()}; }
        Some(f(&mut s))
    }).ok().flatten()
}
struct Guard { epoch:u64, saved:Option<(u32,Option<Bound>)> }
impl Drop for Guard {
    fn drop(&mut self) {
        if EPOCH.load(Ordering::Acquire)==self.epoch {
            if let Some((depth,current))=self.saved {let _=calls(|s|{s.depth=depth;s.current=current;});}
        }
    }
}
fn within<T>(bound:Option<Bound>,native:impl FnOnce()->T)->T {
    let epoch=EPOCH.load(Ordering::Acquire);
    let saved=calls(|s|{let old=(s.depth,s.current);s.depth=s.depth.saturating_add(1);s.current=if s.depth<=4{bound}else{None};old});
    let _guard=Guard{epoch,saved};
    native()
}
/// 667A2C observer consumes only the current original call's info once.
/// It receives an owned value; no stack pointer is stored past 667990 return.
pub(crate) fn take(provider:u32,info:u32)->Option<Source> {
    calls(|s|{let b=s.current?;if b.provider!=provider||b.info!=info{return None;}s.current=None;Some(b.source)}).flatten()
}
/// Exact 668BDF CALL wrapper. Original 667990 always executes, including reject.
pub(crate) fn publish_owned(bp:u32,kind:u32,callback:u32,provider:u32,info:u32,native_callback:u32,
    capture:impl FnOnce(u32)->Option<Source>,native:impl FnOnce(u32,u32,u32,u32)->u32)->u32 {
    diag::hit(diag::SOURCE_ENTRY);
    let bound=(||{
        if kind!=0||callback!=native_callback||provider<0x10000||bp.checked_sub(0x530)?!=info{return None;}
        Some(Bound{provider,info,source:capture(bp.checked_sub(0x8c0)?)?})
    })();
    if bound.is_some(){diag::hit(diag::SOURCE_BOUND);}else{diag::hit(diag::SOURCE_NOT_BOUND);}
    within(bound,||native(kind,callback,provider,info))
}
pub(crate) unsafe extern "C" fn publish_frame(bp:u32,kind:u32,callback:u32,provider:u32,info:u32)->u32 {
    let native:unsafe extern "C" fn(u32,u32,u32,u32)->u32=std::mem::transmute(0x667990usize);
    publish_owned(bp,kind,callback,provider,info,0x663ad0,|scene|capture(scene),|k,c,p,i|native(k,c,p,i))
}
pub(crate) const PUBLISH_SITE:u32=0x668bdf;
pub(crate) const PUBLISH_ORIGINAL:[u8;5]=[0xe8,0xac,0xed,0xff,0xff];
/// Entered by CALL with original four cdecl arguments and caller EBP intact.
pub(crate) fn publication_bridge(callback:u32)->Vec<u8> {
    let mut b=Vec::new();for _ in 0..4{b.extend([0xff,0x74,0x24,0x10]);}
    b.extend([0x55,0xb8]);b.extend(callback.to_le_bytes());b.extend([0xff,0xd0,0x83,0xc4,0x14,0xc3]);b
}

#[cfg(test)]mod tests {
    use super::*;
    fn fixture()->(Request,Scope,Sample){
        let r=Request{abi:ABI,size:28,scene:0x10000,viewport:0x20000,controller:0x30000,generation:4,world:0x50000};
        let s=Scope{pawn:0x60000,pawn_generation:7,actor:0x70000,generation:8,node:0x80000,resource:0x90000};
        let a=Sample{abi:ABI,size:100,request:r,frame:[10,0],pawn:s.pawn,camera:0xa0000,direction:[1.,0.,0.],..Sample::default()};(r,s,a)
    }
    #[test]fn combines_same_call_scene_and_host_values(){
        let(r,s,a)=fixture();let got=collect(r.scene, |_|Some((r,s,1f32.to_bits())),|req,out,bytes|{assert_eq!(*req,r);assert_eq!(bytes,100);*out=a;1});
        assert_eq!(got,Some(Source{aim:a,scope:s,ads_fraction_bits:1f32.to_bits()}));
    }
    #[test]fn respawn_or_scope_replacement_during_query_cannot_escape(){
        let(r,s,a)=fixture();let mut n=0;
        assert!(collect(r.scene, |_|{n+=1;Some((r,if n==1{s}else{Scope{generation:9,..s}},1f32.to_bits()))},|_,out,_|{*out=a;1}).is_none());
        assert!(collect(r.scene, |_|Some((r,s,1f32.to_bits())),|_,out,_|{*out=Sample{pawn:s.pawn+4,..a};1}).is_none());
        assert!(collect(r.scene, |_|Some((r,s,1f32.to_bits())),|_,out,_|{*out=a;0}).is_none());
        assert!(collect(r.scene, |_|Some((r,s,1f32.to_bits())),|_,out,_|{*out=Sample{request:Request{scene:r.scene+4,..r},..a};1}).is_none());
    }
    #[test]fn callback_code_retains_its_module_and_rejects_invalid_addresses(){
        assert!(!retain_code(0));assert!(!retain_code(1));
        assert!(retain_code(callback_code_retains_its_module_and_rejects_invalid_addresses as usize));
    }
    #[test]fn absent_or_rejected_host_remains_unavailable(){
        clear();unsafe{assert!(capture(0x10000).is_none());}
        assert!(!bind(ABI+1,None));unsafe{assert!(capture(0x10000).is_none());}
    }
    #[test]fn nested_publications_are_scoped_consumed_once_and_invalidated(){
        let(_,s,a)=fixture();let outer=Bound{provider:0xb0000,info:0xc0000,source:Source{aim:a,scope:s,ads_fraction_bits:1f32.to_bits()}};
        let inner=Bound{provider:0xd0000,info:0xe0000,..outer};
        within(Some(outer),||{
            within(Some(inner),||{assert!(take(outer.provider,outer.info).is_none());assert_eq!(take(inner.provider,inner.info),Some(inner.source));assert!(take(inner.provider,inner.info).is_none());});
            within(None,||{assert!(take(outer.provider,outer.info).is_none());});
            assert_eq!(take(outer.provider,outer.info),Some(outer.source));
            assert!(take(outer.provider,outer.info).is_none());
        });
        assert!(take(outer.provider,outer.info).is_none());
        within(Some(outer),||{clear();assert!(take(outer.provider,outer.info).is_none());});
        assert!(take(outer.provider,outer.info).is_none());
    }
    #[test]fn scene_fraction_is_owned_and_rechecked_without_changing_scope_identity(){
        let(r,s,a)=fixture();let fraction=0.273352474f32.to_bits();
        let got=collect(r.scene,|_|Some((r,s,fraction)),|_,out,_|{*out=a;1}).unwrap();
        assert_eq!(got.scope,s);assert_eq!(got.ads_fraction_bits,fraction);
        let mut count=0;
        assert!(collect(r.scene,|_|{count+=1;Some((r,s,if count==1{fraction}else{1f32.to_bits()}))},|_,out,_|{*out=a;1}).is_none());
        assert_eq!(got.ads_fraction_bits,fraction); // A later frame cannot retime this value.
        for invalid in [f32::NAN,f32::INFINITY,-0.01,1.01] {
            assert!(collect(r.scene,|_|Some((r,s,invalid.to_bits())),|_,out,_|{*out=a;1}).is_none());
        }
    }

}
