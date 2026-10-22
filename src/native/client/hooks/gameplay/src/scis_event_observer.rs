//! SCBL_SCIS_DIAG manual F6/F7 only. No forced completion, native state writes,
//! fake input, timer recovery, graph evaluation, or retained native resources.
use crate::{equipment_native::{word,signature},scis_diagnostics as diag};
use retour::static_detour;
use std::sync::atomic::{AtomicBool,AtomicU32,Ordering};
#[path="scis_event_policy.rs"]mod policy;
#[path="scis_ads_observer.rs"]mod ads;
pub(crate) use ads::Motion;
static READY:AtomicBool=AtomicBool::new(false);
static SEQUENCE:AtomicU32=AtomicU32::new(0);
static_detour!{
    static Tick:unsafe extern "thiscall" fn(usize);
    static Event:unsafe extern "thiscall" fn(usize,usize,usize)->u8;
}
unsafe fn w(p:usize,o:usize)->u32{word(p,o).unwrap_or(u32::MAX)}
fn seq()->u32{SEQUENCE.fetch_add(1,Ordering::Relaxed).wrapping_add(1)}
unsafe fn owned_hands(hands:usize)->bool{
    if !READY.load(Ordering::Acquire)||!diag::project_active(6){return false;}
    let pawn=w(hands,0x310)as usize;
    let ok=word(hands,0)==Some(0x02a23634)&&word(pawn,0x12ac)==Some(hands as u32)
        &&crate::scis_presentation::owned(pawn);
    if !ok{diag::hit(diag::ANIMATION_REJECT);}ok
}
unsafe fn state(hands:usize,serial:u32)->[u32;16]{
    let pawn=w(hands,0x310)as usize;let anim=w(hands,0x31c)as usize;
    let gun=w(pawn,0x4b8)as usize;let logic=w(gun,0x748)as usize;
    [1,serial,hands as u32,w(hands,0x20),pawn as u32,w(pawn,0x20),anim as u32,
     w(anim,0x28),w(anim,0x2c),w(anim,0x30),w(anim,0x34),w(anim,0x50),w(anim,0x54),
     policy::phase(anim,0x40,|p,o|word(p,o)).unwrap_or(u32::MAX),
     policy::phase(anim,0x60,|p,o|word(p,o)).unwrap_or(u32::MAX),w(logic,0xec)]
}
unsafe fn graph(hands:usize,serial:u32)->[u32;16]{
    let c=policy::component(hands,|p,o|word(p,o)).unwrap_or(0);let pawn=w(hands,0x310)as usize;
    [1,serial,hands as u32,w(hands,0x20),pawn as u32,w(pawn,0x20),c as u32,
     w(c,0x19c),w(c,0x380),w(c,0x384),w(c,0x388),w(c,0x38c),w(c,0x390),w(hands,0x318),0,0]
}
fn tick(hands:usize){unsafe{
    Tick.call(hands);
    if owned_hands(hands){diag::trace_bundle::<2>(diag::ANIMATION_STATE,||{let s=seq();[state(hands,s),graph(hands,s)]});}
}}
fn event(hands:usize,source:usize,notification:usize)->u8{unsafe{
    policy::around(||{
        if !owned_hands(hands){return None;}
        let s=seq();let before=state(hands,s);
        let event=[w(notification,0),w(notification,4),w(notification,8)];
        Some((s,before,event))
    },||Event.call(hands,source,notification),|(s,mut before,event)|{
        // Same event identity copied before the original call, never reread after.
        // Event rows replace state slots 3/5/15 with kind/enum/handle.
        diag::trace_bundle::<2>(diag::ANIMATION_EVENT_BEFORE,||{
            let mut after=state(hands,s);
            for row in [&mut before,&mut after]{row[3]=event[0];row[5]=event[1];row[15]=event[2];}
            [before,after]
        });
    })
}}
unsafe fn ads_owned(core:usize)->bool{
    let pawn=w(core,0x24)as usize;let pc=w(core,0x20)as usize;let camera=w(core,0x1c)as usize;
    crate::scis_presentation::owned(pawn)&&word(pc,0x520)==Some(pawn as u32)
        &&word(pc,0x6a4)==Some(camera as u32)&&word(camera,0x61c)==Some(core as u32)
}
unsafe fn ads_row(core:usize,s:u32)->[u32;16]{
    let pawn=w(core,0x24)as usize;let scope=w(pawn,0x1508)as usize;
    [1,s,core as u32,pawn as u32,w(pawn,0x20),scope as u32,w(scope,0x20),w(core,0x46c),
     w(core,0x470),w(core,0x474),w(scope,0x9c),w(scope,0xa0),w(scope,0xa4),
     w(scope,0xa8),w(scope,0xac),w(scope,0xb0)]
}
pub(crate) struct Ads{core:usize,s:u32,before:[u32;16],native:[u32;16]}
pub(crate) unsafe fn begin_ads(core:usize)->Option<Ads>{
    if !diag::project_active(7)||!ads_owned(core){return None;}
    let s=seq();
    diag::trace(diag::ADS_LIFECYCLE_BEFORE,||ads::lifecycle(core,s,|p,o|word(p,o)));
    Some(Ads{core,s,before:ads_row(core,s),native:[u32::MAX;16]})
}
impl Ads{pub(crate) unsafe fn native_placed(&mut self){self.native=ads_row(self.core,self.s);diag::trace(diag::ADS_LIFECYCLE_NATIVE,||ads::lifecycle(self.core,self.s,|p,o|word(p,o)));}}
impl Drop for Ads{fn drop(&mut self){unsafe{
    diag::trace_bundle::<3>(diag::ADS_PLACE_BEFORE,||[self.before,self.native,ads_row(self.core,self.s)]);
}}}
pub(crate) unsafe fn scene(source:crate::scis_scene_source::Source){
    diag::trace(diag::ADS_SCENE,||{
        let a=source.aim;let scope=source.scope;
        let camera=w(a.request.controller as usize,0x6a4)as usize;let core=w(camera,0x61c)as usize;
        let fraction=if word(core,0x24)==Some(scope.pawn)&&ads_owned(core){w(core,0x46c)}else{u32::MAX};
        [1,a.frame[0],a.frame[1],a.request.scene,scope.pawn,scope.pawn_generation,
         scope.actor,scope.generation,scope.node,core as u32,fraction,a.camera,
         a.direction[0].to_bits(),a.direction[1].to_bits(),a.direction[2].to_bits(),a.request.controller]
    });
}
pub(crate) unsafe fn init(){
    if READY.load(Ordering::Acquire){return;}
    if !PINS.iter().all(|&(p,n,h)|signature(p,n,h)){
        tracing::error!("SCBL_SCIS_EVENT_OBSERVER enabled=false reason=signature");return;
    }
    if Tick.initialize(std::mem::transmute::<usize,unsafe extern "thiscall" fn(usize)>(0xafa5d0),tick).is_err()
      ||Event.initialize(std::mem::transmute::<usize,unsafe extern "thiscall" fn(usize,usize,usize)->u8>(0xbbc7f0),event).is_err()
      ||Tick.enable().is_err()||Event.enable().is_err(){deinit();tracing::error!("SCBL_SCIS_EVENT_OBSERVER enabled=false reason=install");return;}
    READY.store(true,Ordering::Release);
    tracing::info!("SCBL_SCIS_EVENT_OBSERVER enabled=true recording=manual_F6_F7 visual_fix=unverified");
}
pub(crate) unsafe fn deinit(){
    READY.store(false,Ordering::Release);
    if Event.is_enabled(){let _=Event.disable();}if Tick.is_enabled(){let _=Tick.disable();}
}
const PINS:&[(usize,usize,&str)]=&[
    (0xafa5d0,88,"6f43f37fb955980da5862e5aa1d5c1ddcb753943cd4b6ecb08e16afa56a7e046"),
    (0xbbc7f0,200,"789ed5265c67758099de37a3b4f0590b21a1122930dfb5b4ac8e27372411aae8"),
];
