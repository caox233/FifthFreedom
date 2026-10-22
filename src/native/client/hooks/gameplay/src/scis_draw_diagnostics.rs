//! Manual F2 only: value snapshots at existing owned render seams. CPU CB cache
//! bytes are recorded verbatim, not presented as GPU pixels or velocity history.
use crate::{scis_diagnostics as diag, scis_material_binding::Read};
use std::{cell::RefCell,sync::atomic::{AtomicU64,Ordering}};

#[derive(Clone,Copy,Debug,PartialEq)]
pub(crate) struct Context {
    pub provider:u32,pub info:u32,pub renderer:u32,pub frame:[u32;2],
    pub node:u32,pub generation:u32,pub epoch:u64,pub nonce:u64,
    pub local:[f32;3],pub target:[f32;3],
}
#[derive(Default)]struct State{main:Option<Main>}
static MAIN_SERIAL:AtomicU64=AtomicU64::new(1);
#[derive(Clone,Copy)]struct Main{serial:u64,provider:u32,info:u32,outer:u32,event:u32,context:Option<Context>,commit:u32}
thread_local!{static STATE:RefCell<State>=RefCell::new(State::default());}
fn enabled()->bool{diag::project_active(2)}
pub(crate) fn begin(provider:u32,info:u32,outer:u32){
    if !enabled(){return;}
    let _=STATE.try_with(|s|{if let Ok(mut s)=s.try_borrow_mut(){s.main=Some(Main{serial:MAIN_SERIAL.fetch_add(1,Ordering::Relaxed),provider,info,outer,event:0,context:None,commit:0});}});
}
fn event(context:Context,bind:bool,commit:Option<bool>)->Option<Main>{
    STATE.try_with(|s|{
        let mut s=s.try_borrow_mut().ok()?;let m=s.main.as_mut()?;
        if m.provider!=context.provider || m.info!=context.info || m.outer.checked_add(0x2c)!=Some(context.renderer){return None;}
        if bind{m.context=Some(context);}else if m.context!=Some(context){return None;}
        if let Some(ok)=commit{m.commit=if ok{1}else{2};}
        m.event=m.event.wrapping_add(1);Some(*m)
    }).ok().flatten()
}
pub(crate) fn prepared(r:&impl Read,context:Context){
    if !enabled(){return;}let Some(m)=event(context,true,None)else{return;};
    diag::trace_bundle::<10>(diag::DRAW_CONTINUITY,||snapshot(r,m,1,0));
}
pub(crate) fn committed(r:&impl Read,renderer:u32,nonce:u64,ok:bool){
    if !enabled(){return;}
    let context=STATE.try_with(|s|s.try_borrow().ok().and_then(|s|s.main?.context)).ok().flatten();
    let Some(c)=context.filter(|c|c.renderer==renderer&&c.nonce==nonce)else{return;};
    let Some(m)=event(c,false,Some(ok))else{return;};
    diag::trace_bundle::<10>(diag::DRAW_CONTINUITY,||snapshot(r,m,2,0));
}
/// Called only inside observe_emissive's current SOURCE/packet/submesh join.
/// The closure is not evaluated when inactive, another project, or over budget.
pub(crate) fn draw(r:&impl Read,renderer:u32,node:u32,generation:u32,epoch:u64,sub:u32){
    if !enabled(){return;}
    let context=STATE.try_with(|s|s.try_borrow().ok().and_then(|s|s.main?.context)).ok().flatten();
    let Some(c)=context.filter(|c|c.renderer==renderer&&c.node==node&&c.generation==generation&&c.epoch==epoch)else{return;};
    let Some(m)=event(c,false,None)else{return;};
    diag::trace_bundle::<10>(diag::DRAW_CONTINUITY,||snapshot(r,m,3,sub));
}
/// Receives the one original restore call's result. Do not dereference an object
/// after native main has returned, or execute restore a second time for a trace.
pub(crate) fn end(restored:bool){
    let m=STATE.try_with(|s|s.try_borrow_mut().ok().and_then(|mut s|s.main.take())).ok().flatten();
    if !enabled(){return;}let Some(mut m)=m.filter(|m|m.context.is_some())else{return;};
    m.event=m.event.wrapping_add(1);
    diag::trace_bundle::<10>(diag::DRAW_CONTINUITY,||{
        let mut rows=[[u32::MAX;16];10];rows[0]=header(m,4,0);rows[0][14]=u32::from(restored);rows[0][15]=0;rows
    });
}
fn header(m:Main,phase:u32,sub:u32)->[u32;16]{
    let c=m.context.unwrap();
    [1,m.serial as u32,(m.serial>>32)as u32,m.event,phase,c.frame[0],c.frame[1],c.renderer,c.node,c.generation,sub,c.nonce as u32,(c.nonce>>32)as u32,m.commit,0,0]
}
fn words<const N:usize>(r:&impl Read,p:u32,o:u32)->Option<[u32;N]>{
    let mut v=[0;N];for(i,x)in v.iter_mut().enumerate(){*x=r.at(p,o.checked_add(i as u32*4)?)?;}Some(v)
}
fn camera_cache(r:&impl Read,cb:u32)->Option<[u32;16]>{
    let data=r.pointer(cb,0x6a8)?;let offset=r.at(cb,0x254)?&0xffff;
    if offset==0xffff || offset%4!=0{return None;}words(r,data,offset)
}
fn snapshot(r:&impl Read,m:Main,phase:u32,sub:u32)->[[u32;16];10]{
    let c=m.context.unwrap();let mut rows=[[u32::MAX;16];10];rows[0]=header(m,phase,sub);
    let mut valid=0u32;let mut scalar_valid=0u32;
    let settings=r.pointer(c.renderer,0x20d0);let record=r.pointer(c.renderer,0x1c);
    let mode=r.at(c.renderer,0x2334);let object=mode.and_then(|v|r.pointer(c.node,if v==4{0x218}else{0x214}));
    let primary=r.pointer(c.renderer,0);let secondary=r.pointer(c.renderer,4);
    let cache=object.and_then(|p|r.pointer(p,0x6a8));let packed=object.and_then(|p|r.at(p,0x24c));
    let values=[settings,record,c.renderer.checked_sub(0x2c).and_then(|p|r.at(p,0x66c4)),mode,
        r.at(c.node,0x34),r.at(c.node,0x38),r.at(c.node,0x2c),r.at(c.node,0xfc),
        object,object.and_then(|p|r.byte(p,0x6a4)).map(u32::from),cache,packed,
        primary,secondary,secondary.and_then(|p|r.pointer(p,0x6a8)),secondary.and_then(|p|r.byte(p,0x6a4)).map(u32::from)];
    for(i,v)in values.into_iter().enumerate(){if let Some(v)=v{rows[1][i]=v;scalar_valid|=1<<i;}}
    if scalar_valid==0xffff{valid|=1<<1;}
    let object_values=(||{
        let data=cache?;let packed=packed?;let a=packed>>16;let b=packed&0xffff;
        if a==0xffff||b==0xffff||a%4!=0||b%4!=0||(a<b+16&&b<a+48){return None;}
        let matrix=words::<12>(r,data,a)?;let scale=words::<4>(r,data,b)?;
        let mut v=[0;16];v[..12].copy_from_slice(&matrix);v[12..].copy_from_slice(&scale);Some(v)
    })();
    let data=[words(r,c.node,0x40),object_values,settings.and_then(|p|words(r,p,0x160)),
        settings.and_then(|p|words(r,p,0x50)),primary.and_then(|p|camera_cache(r,p)),
        secondary.and_then(|p|camera_cache(r,p)),settings.and_then(|p|words(r,p,0x110))];
    for(i,v)in data.into_iter().enumerate(){if let Some(v)=v{rows[i+2]=v;valid|=1<<(i+2);}}
    let scale=words::<4>(r,c.node,0xc0);let eye=settings.and_then(|p|words::<3>(r,p,0x150));
    if let Some(v)=scale{rows[9][..4].copy_from_slice(&v);}
    if let Some(v)=eye{rows[9][4..7].copy_from_slice(&v);}
    rows[9][7..10].copy_from_slice(&c.local.map(f32::to_bits));rows[9][10..13].copy_from_slice(&c.target.map(f32::to_bits));
    rows[9][13]=scalar_valid;rows[9][14]=u32::from(scale.is_some());rows[9][15]=u32::from(eye.is_some());
    if scale.is_some()&&eye.is_some(){valid|=1<<9;}rows[0][15]=valid;rows
}

#[cfg(test)]mod tests{
    use super::*;use std::{cell::Cell,collections::BTreeMap};
    struct Memory{v:BTreeMap<u32,u32>,reads:Cell<usize>}
    impl Read for Memory{fn word(&self,a:u32)->Option<u32>{self.reads.set(self.reads.get()+1);self.v.get(&a).copied()}}
    fn context()->Context{Context{provider:0x10000,info:0x11000,renderer:0x2002c,frame:[9,0],node:0x30000,generation:7,epoch:1,nonce:42,local:[1.,2.,3.],target:[4.,5.,6.]}}
    #[test]fn inactive_observer_performs_no_object_reads(){
        let r=Memory{v:BTreeMap::new(),reads:Cell::new(0)};let c=context();
        let check=||{begin(c.provider,c.info,c.renderer-0x2c);prepared(&r,c);committed(&r,c.renderer,c.nonce,true);draw(&r,c.renderer,c.node,c.generation,c.epoch,0x50000);end(true);};
        #[cfg(feature="scis-runtime-diagnostics")]let _=diag::test_record(0,check);
        #[cfg(not(feature="scis-runtime-diagnostics"))]check();
        assert_eq!(r.reads.get(),0);
    }
    #[cfg(feature="scis-runtime-diagnostics")]
    #[test]fn separate_native_threads_have_unique_main_ids_and_nested_borrow_is_inert(){
        let _=diag::test_record(2,||{
            let ids:Vec<_>=(0..2).map(|_|std::thread::spawn(||{
                let c=context();begin(c.provider,c.info,c.renderer-0x2c);
                STATE.with(|s|{let held=s.borrow_mut();let id=held.main.unwrap().serial;
                    let r=Memory{v:BTreeMap::new(),reads:Cell::new(0)};prepared(&r,c);draw(&r,c.renderer,c.node,c.generation,c.epoch,0x50000);end(false);
                    assert_eq!(r.reads.get(),0);id})
            })).collect::<Vec<_>>().into_iter().map(|t|t.join().unwrap()).collect();
            assert_ne!(ids[0],ids[1]);
        });
    }
    #[cfg(feature="scis-runtime-diagnostics")]
    #[test]fn owned_same_main_snapshots_join_and_record_raw_cache_changes(){
        let c=context();let mut r=Memory{v:BTreeMap::new(),reads:Cell::new(0)};
        let settings=0x40000;let obj=0x50000;let cache=0x60000;let primary=0x70000;let secondary=0x71000;
        for(p,o,v)in [(c.renderer,0x20d0,settings),(c.renderer,0x1c,0x21000),(c.renderer,0x2334,0),(c.node,0x214,obj),(obj,0x6a8,cache),(obj,0x24c,48),(obj,0x6a4,0),(c.renderer,0,primary),(c.renderer,4,secondary),(primary,0x6a8,0x72000),(secondary,0x6a8,0x73000),(primary,0x254,0),(secondary,0x254,0)]{r.v.insert(p+o,v);}
        for(p,n)in [(c.node+0x40,16),(c.node+0xc0,4),(cache,16),(settings+0x50,16),(settings+0x110,16),(settings+0x150,3),(settings+0x160,16),(0x72000,16),(0x73000,16)]{for i in 0..n{r.v.insert(p+i*4,p+i);}}
        let rows=diag::test_record(2,||{
            begin(c.provider,c.info,c.renderer-0x2c);prepared(&r,c);committed(&r,c.renderer,c.nonce,true);
            let reads=r.reads.get();draw(&r,c.renderer,c.node+4,c.generation,c.epoch,0x50000);assert_eq!(reads,r.reads.get()); // foreign must not read
            draw(&r,c.renderer,c.node,c.generation,c.epoch,0x50000);
            r.v.insert(cache,0x12345678);draw(&r,c.renderer,c.node,c.generation,c.epoch,0x50000);end(true);
        });
        assert_eq!(rows.len(),50);let headers:Vec<_>=rows.iter().filter(|v|v.0==diag::DRAW_CONTINUITY).map(|v|v.2).collect();
        assert_eq!(headers.iter().map(|r|r[4]).collect::<Vec<_>>(),vec![1,2,3,3,4]);
        assert!(headers.iter().all(|r|r[1..3]==headers[0][1..3]&&r[11]==42));
        assert_eq!(rows[33].2[0],0x12345678);assert_eq!(headers[4][14],1);assert_eq!(headers[4][15],0);
    }
}
