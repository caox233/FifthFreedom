//! Optional gameplay-owned native idle motion. Camera/input publication is never
//! changed: only the paired aim deprojection consumes this angular delta.
use hooks_plugin_api::scope_motion::{self, v2::{Request, Sample, QueryFn, ABI, PREVIEW, POST_HANDS}};
use super::{memory::word, rotation_precision::Snapshot};
use std::sync::atomic::{AtomicUsize,Ordering};
static QUERY:AtomicUsize=AtomicUsize::new(0);
pub(crate) fn bind(query:Option<QueryFn>)->bool {
    QUERY.store(0,Ordering::Release);
    if let Some(f)=query {
        #[link(name="kernel32")] extern "system" {
            fn GetModuleHandleExW(flags:u32,address:*const u16,module:*mut *mut std::ffi::c_void)->i32;
        }
        let mut module=std::ptr::null_mut();
        if unsafe{GetModuleHandleExW(5,f as usize as *const u16,&mut module)}==0 || module.is_null(){return false;}
        QUERY.store(f as usize,Ordering::Release);
    }
    true
}
pub(super) unsafe fn delta(s:Snapshot,camera:usize,post:bool)->Option<[f64;2]> {
    let address=QUERY.load(Ordering::Acquire);if address==0{return None;}
    let input=scope_motion::Request{abi:scope_motion::ABI,size:std::mem::size_of::<scope_motion::Request>()as u32,world:s.owner.world as u32,
        controller:word(s.owner.core,0x20)?,pawn:s.owner.pawn as u32,camera:camera as u32,
        core:s.owner.core as u32,controller_generation:s.owner.generation,
        pawn_generation:word(s.owner.pawn,0x20)?,frame:[s.frame as u32,(s.frame>>32)as u32],mode:s.owner.mode as u32};
    let r=Request{abi:ABI,size:std::mem::size_of::<Request>()as u32,input,phase:if post{POST_HANDS}else{PREVIEW}};
    if !r.valid(){return None;}
    let mut out=Sample::default();let f:QueryFn=std::mem::transmute(address);
    if f(&r,&mut out,std::mem::size_of::<Sample>()as u32)!=1 || !out.valid_for(r)
        || QUERY.load(Ordering::Acquire)!=address {return None;}
    Some(out.delta.map(f64::from))
}
