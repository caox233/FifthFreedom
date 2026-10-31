//! MP deployed sticky-camera commands, on the existing semantic input/thread.
//! Original actors, replication, fuse and damage remain native. No key polling,
//! extra transport, actor deletion, health writes or timed background worker.
use crate::equipment_native::{word, float, readable};
use crate::sticky_camera_gesture::{Gestures, Owner, Edge, Command};
use retour::static_detour;
use sha2::{Digest,Sha256};
use std::{cell::{Cell,RefCell},sync::atomic::{AtomicBool,Ordering}};
const CAMERA:u32=0x02ab095c;
const SPY:u32=0x02ad3234;
static READY:AtomicBool=AtomicBool::new(false);
thread_local!{
    static GESTURE:RefCell<Gestures>=RefCell::new(Gestures::default());
    static INPUT:Cell<Option<u64>>=const{Cell::new(None)};
    static RECEIVING:Cell<bool>=const{Cell::new(false)};
    static BINDING:Cell<Option<(usize,u64)>>=const{Cell::new(None)};
    static RELEASE_TAIL:Cell<Option<ReleaseTail>>=const{Cell::new(None)};
}
#[link(name="kernel32")] extern "system"{fn GetTickCount64()->u64;}
static_detour!{
    static Whistle:unsafe extern "thiscall" fn(usize);
    static WhistleSend:unsafe extern "thiscall" fn(usize);
    static Receive:unsafe extern "thiscall" fn(usize,usize)->u32;
    static Tick:unsafe extern "thiscall" fn(usize,f32,u32)->u32;
    static Cleanup:unsafe extern "thiscall" fn(usize);
    static Disable:unsafe extern "thiscall" fn(usize);
}
/// The companion core refreshes this only from its foreground gameplay frame.
/// Opening chat/rename or losing focus revokes it immediately. Unknown/missing
/// or stale companion context fails closed without consuming native input.
pub(crate) fn input_scope(allowed:bool){
    INPUT.with(|s|s.set(allowed.then(||unsafe{GetTickCount64()})));
    if !allowed{GESTURE.with(|s|s.borrow_mut().cancel());BINDING.with(|s|s.set(None));RELEASE_TAIL.with(|s|s.set(None));}
}
fn input_allowed(now:u64)->bool{
    INPUT.with(|s|s.get().and_then(|t|now.checked_sub(t)).is_some_and(|d|d<=250))
}
unsafe fn identity(p:usize)->Option<[u32;2]>{
    let id=word(p,0x20).filter(|v|*v!=0&&*v!=u32::MAX)?;
    (p>=0x10000&&p<=u32::MAX as usize).then_some([p as u32,id])
}
/// +20 is an observed UObject identity/index, not a proven allocation generation.
/// A same-address/same-index replacement is not promised detectable.
unsafe fn device(camera:usize)->Option<Owner>{
    device_at(camera,word(0x339e5a0,0)?)
}
unsafe fn device_at(camera:usize,active_info:u32)->Option<Owner>{
    device_at_inner(camera,active_info,false)
}
unsafe fn device_at_inner(camera:usize,active_info:u32,allow_triggered:bool)->Option<Owner>{
    if word(camera,0)?!=CAMERA||word(camera,0x5c)?&1!=0
        ||word(camera,0x78)?&2!=0||(!allow_triggered&&word(camera,0x858)?&0x800!=0)
        ||word(camera,0x860)?&0xff!=2{return None;}
    let pawn=word(camera,0x8d4)? as usize;
    let world=word(camera,0x144)? as usize;
    if word(pawn,0)?!=SPY||word(pawn,0x144)? as usize!=world
        ||word(pawn,0x60)?&0x800!=0||word(pawn,0x5c)?&1!=0{return None;}
    let info=word(world,0x6d0)? as usize;
    // 339E5A0 is the active GameInfo, whereas world+6D0 is WorldInfo.
    // They are distinct live objects (confirmed by the selected-camera read).
    // Compare their owning world, not their object addresses. Keep the native
    // WorldInfo multiplayer mode and every camera/owner/lifetime gate intact.
    if word(active_info as usize,0x144)? as usize!=world||word(info,0x144)? as usize!=world
        ||!(6..=10).contains(&(word(info,0x37c)?&0xff)){return None;}
    Some(Owner{world:identity(world)?,pawn:identity(pawn)?,camera:identity(camera)?})
}
unsafe fn context(controller:usize,now:u64)->Option<Owner>{
    context_inner(controller,now,false)
}
unsafe fn context_inner(controller:usize,now:u64,allow_triggered:bool)->Option<Owner>{
    if !input_allowed(now)||word(controller,0)?!=0x02ac53ec{return None;}
    let pawn=word(controller,0x520)? as usize;
    let world=word(controller,0x144)? as usize;
    if crate::local_player::controller(world)?!=controller
        ||word(controller,0x310)? as usize!=pawn||word(pawn,0x938)? as usize!=controller
        ||word(pawn,0x4c)?&0xff00!=0x100||word(pawn,0x950)?&0x04000000!=0{return None;}
    let health=word(pawn,0x4dc)? as usize;
    if !float(health,0x6c)?.is_finite()||float(health,0x6c)?<=0.0{return None;}
    let camera=crate::sticky_camera_input::selected_camera(pawn)?;
    let owner=device_at_inner(camera,word(0x339e5a0,0)?,allow_triggered)?;
    (owner.pawn[0]as usize==pawn&&owner.world[0]as usize==world
        &&word(camera,0x4c)?&0xff00==0x100).then_some(owner)
}
fn bind(controller:usize)->bool{
    let key=crate::sticky_camera_input::binding_fingerprint(controller).map(|f|(controller,f));
    BINDING.with(|s|{
        if s.get()!=key{GESTURE.with(|g|g.borrow_mut().cancel());RELEASE_TAIL.with(|t|t.set(None));s.set(key);}
    });
    key.is_some()
}
unsafe fn execute(owner:Owner,command:Command)->bool{
    let camera=owner.camera[0]as usize;
    if device(camera)!=Some(owner){return false;}
    match command{
        Command::Shot=>{whistle(camera);false},
        Command::Detonate=>{
            let Some(vt)=word(camera,0).map(|v|v as usize)else{return false;};
            if word(vt,0x7b0)!=Some(0x00ad3ea0)||word(vt,0x7b4)!=Some(0x00aa5280){return false;}
            let available:unsafe extern "thiscall" fn(usize)->u32=std::mem::transmute(0x00ad3ea0usize);
            if available(camera)==0{return false;}
            if !READY.load(Ordering::Acquire)||device(camera)!=Some(owner){return false;}
            crate::sticky_camera_sound::cancel(camera);
            if !READY.load(Ordering::Acquire)||device(camera)!=Some(owner){return false;}
            let trigger:unsafe extern "thiscall" fn(usize)=std::mem::transmute(0x00aa5280usize);
            trigger(camera); // native fuse + NetStickyCam trigger; no camera-view exit call
            true
        }
    }
}
// A successfully consumed second Press owns exactly one matching Release.
// The native trigger sets +858/0x800 immediately, so this tail must validate
// the same still-live device without requiring its pre-trigger state. Never
// dereference a destroyed device to swallow a release: failed context passes.
const RELEASE_TAIL_MS:u64=1000;
#[derive(Clone,Copy,Debug,PartialEq,Eq)]
struct ReleaseTail{controller:[u32;2],owner:Owner,binding:u64,began:u64}
fn release_tail_step(controller:Option<[u32;2]>,owner:Option<Owner>,binding:Option<(usize,u64)>,edge:Option<Edge>,now:u64)->bool{
    RELEASE_TAIL.with(|s|{
        let Some(t)=s.get()else{return false;};
        if controller!=Some(t.controller)||owner!=Some(t.owner)
            ||binding!=Some((t.controller[0]as usize,t.binding))
            ||!now.checked_sub(t.began).is_some_and(|d|d<=RELEASE_TAIL_MS){s.set(None);return false;}
        if edge==Some(Edge::Release){s.set(None);GESTURE.with(|g|g.borrow_mut().cancel());true}else{false}
    })
}
unsafe fn release_tail_input(controller:usize,edge:Option<Edge>,now:u64)->bool{
    if RELEASE_TAIL.with(|s|s.get().is_none()){return false;}
    // Same foreground/IME/death/selection checks as actions; triggered is the
    // only relaxed field. A freed/unselected camera yields None and passes.
    release_tail_step(identity(controller),context_inner(controller,now,true),BINDING.with(Cell::get),edge,now)
}
unsafe fn command(controller:usize,owner:Owner,c:Command,now:u64){
    let tail=if c==Command::Detonate{
        let Some(id)=identity(controller)else{return;};
        let Some((pc,binding))=BINDING.with(Cell::get)else{return;};
        if pc!=controller{return;}
        Some(ReleaseTail{controller:id,owner,binding,began:now})
    }else{None};
    if execute(owner,c){
        if let Some(t)=tail{
            // Native callbacks may revoke input/rebind while triggering. Do
            // not restore a tail across that revocation; no actor read here.
            if input_allowed(now)&&BINDING.with(Cell::get)==Some((controller,t.binding)){
                RELEASE_TAIL.with(|s|s.set(Some(t)));
            }
        }
    }
}
pub(crate) unsafe fn input(controller:usize,delivered:u32)->bool{
    if !READY.load(Ordering::Acquire){return false;}
    let now=GetTickCount64();
    if !bind(controller){GESTURE.with(|s|s.borrow_mut().cancel());return false;}
    let edge=crate::sticky_camera_input::input_edge(controller,delivered);
    if release_tail_input(controller,edge,now){return true;}
    let owner=context_inner(controller,now,true);
    // The native fuse leaves the selected actor alive with +858/0x800 set.
    // A second/third deployment press must not fall back to EnterCameraView.
    // Revalidate the live selected device on EVERY input; no timer, retained
    // actor pointer or suppression carries over to a replacement camera.
    if owner.is_some_and(|o|word(o.camera[0]as usize,0x858).is_some_and(|f|f&0x800!=0)) {
        GESTURE.with(|s|s.borrow_mut().cancel());
        return edge.is_some(); // only this device's semantic deploy edges
    }
    if owner.is_none(){GESTURE.with(|s|s.borrow_mut().cancel());return false;}
    let Some(edge)=edge else{return false;};
    let decision=GESTURE.with(|s|s.borrow_mut().step(owner,edge,now));
    if let (Some(o),Some(c))=(owner,decision.command){command(controller,o,c,now);}
    decision.consume
}
fn whistle(camera:usize){unsafe{
    // Remote native WhistleMessage reaches this exact function too. No packet
    // contents or player-input state are reinterpreted on the receiving machine.
    let owner=if READY.load(Ordering::Acquire){device(camera)}else{None};
    use crate::sticky_camera_sound::WhistleEmission;
    let emission=if owner.is_some(){crate::sticky_camera_sound::whistle_emission(camera)}else{WhistleEmission::Unavailable};
    // The replacement calls native audio, which can reenter actor cleanup.
    // Neither fallback Whistle nor ATS/network publication may target a
    // destroyed/replaced actor merely because it was valid before that call.
    if owner.is_some()&&(!READY.load(Ordering::Acquire)||device(camera)!=owner){return;}
    // An inner Whistle already published the accepted replacement. The outer
    // callback owns neither a fallback sound nor another ATS/network emission.
    if emission==WhistleEmission::Superseded{return;}
    whistle_result(camera,emission==WhistleEmission::Accepted);
}}
unsafe fn whistle_result(camera:usize,accepted:bool){
    if !accepted{Whistle.call(camera);return;}
    crate::ats_native_source::observe_camera_shot(camera);
    if word(camera,0x4c).is_some_and(|v|v&0xff00==0x100){
        if let Some(net)=word(camera,0x2c0).map(|v|v as usize){
            if word(net,0)==Some(0x02b08c9c)&&word(net,0x88)==Some(camera as u32){
                whistle_send(net);
            }
        }
    }
}
// Guard the native sender itself, not only the replacement-sound success path.
// Original Whistle fallback also ends at FD17B0 on role 1.
fn whistle_send(net:usize){unsafe{
    if !RECEIVING.with(Cell::get){WhistleSend.call(net);}
}}
struct Receiving(bool);
impl Drop for Receiving{fn drop(&mut self){RECEIVING.with(|s|s.set(self.0));}}
fn receive(net:usize,message:usize)->u32{unsafe{
    let old=RECEIVING.with(|s|{let old=s.get();s.set(true);old});let _scope=Receiving(old);
    Receive.call(net,message)
}}
fn tick(camera:usize,dt:f32,kind:u32)->u32{unsafe{
    if READY.load(Ordering::Acquire){
        if let Some(world)=word(camera,0x144).map(|v|v as usize){
            if let Some(now)=float(world,0x5a0){crate::sticky_camera_sound::tick(camera,now);}
        }
        let tail=RELEASE_TAIL.with(Cell::get);
        if tail.is_some_and(|t|t.owner.camera[0]as usize==camera){
            let now=GetTickCount64();
            // Derive the controller from this live native Tick receiver, not
            // by dereferencing the retained tail controller address.
            let pc=word(camera,0x8d4).and_then(|p|word(p as usize,0x938)).map(|p|p as usize);
            if let Some(pc)=pc{if bind(pc){release_tail_input(pc,None,now);}}
            else{RELEASE_TAIL.with(|s|s.set(None));}
        }
        let pending=GESTURE.with(|s|s.borrow().owner());
        if pending.is_some_and(|o|o.camera[0]as usize==camera){
            let now=GetTickCount64();
            let owner=word(camera,0x8d4).and_then(|p|word(p as usize,0x938))
                .and_then(|pc|if bind(pc as usize){context(pc as usize,now)}else{None});
            let decision=GESTURE.with(|s|s.borrow_mut().step(owner,Edge::Tick,now));
            if let (Some(o),Some(c))=(owner,decision.command){let _=execute(o,c);}
        }
    }
    // Native Tick may explode/destroy this camera. Never touch it after return.
    Tick.call(camera,dt,kind)
}}
unsafe fn forget(camera:usize){
    crate::sticky_camera_sound::cancel(camera);
    RELEASE_TAIL.with(|s|{if s.get().is_some_and(|t|t.owner.camera[0]as usize==camera){s.set(None);}});
    GESTURE.with(|s|{let mut g=s.borrow_mut();if g.owner().is_some_and(|o|o.camera[0]as usize==camera){g.cancel();}});
}
fn cleanup(camera:usize){unsafe{forget(camera);Cleanup.call(camera);}}
fn disable(camera:usize){unsafe{forget(camera);Disable.call(camera);}}
unsafe fn signature(address:usize,size:usize,hash:&str)->bool{
    readable(address,size)&&format!("{:x}",Sha256::digest(std::slice::from_raw_parts(address as*const u8,size)))==hash
}
pub(crate) unsafe fn init(){
    if READY.load(Ordering::Acquire){return;}
    if !crate::sticky_camera_input::supported()||!PINS.iter().all(|&(a,n,h)|signature(a,n,h)){
        tracing::warn!("SCBL_STICKY_CAMERA enabled=false native_contract=false");return;
    }
    let installed=(||->Result<(),retour::Error>{
        WhistleSend.initialize(std::mem::transmute(0xfd17b0usize),whistle_send)?.enable()?;
        Whistle.initialize(std::mem::transmute(0xe2ee50usize),whistle)?.enable()?;
        Receive.initialize(std::mem::transmute(0x102b3f0usize),receive)?.enable()?;
        Tick.initialize(std::mem::transmute(0xb7ec40usize),tick)?.enable()?;
        Cleanup.initialize(std::mem::transmute(0xb52570usize),cleanup)?.enable()?;
        Disable.initialize(std::mem::transmute(0xb52610usize),disable)?.enable()?;
        Ok(())
    })();
    if installed.is_err(){deinit();return;}
    crate::sticky_camera_sound::init();
    if !crate::sticky_camera_sound::enabled(){deinit();return;}
    READY.store(true,Ordering::Release);
    tracing::info!("SCBL_STICKY_CAMERA enabled=true gesture=semantic-deploy double_ms=250 repeat_ms=70 native_replication=true");
}
pub(crate) unsafe fn deinit(){
    READY.store(false,Ordering::Release);input_scope(false);
    crate::sticky_camera_sound::deinit();
    let _=Disable.disable();let _=Cleanup.disable();let _=Tick.disable();let _=Receive.disable();let _=Whistle.disable();let _=WhistleSend.disable();
}
// Filled from the SHA-pinned retail image, before installation (not after hooks).
const PINS:&[(usize,usize,&str)]=&[
    (0xe2ee50,1262,"4b68d27f29389880e12c01ad54f15ce5a2a31b5b2ddac7d95f6f5a0e6d7640d4"),
    (0x102b3f0,668,"49c7255b4e31295a550828c2bb113d37c45bceb8191a8cd77e1403fa05038dd2"),
    (0xb7ec40,519,"24e79c57c5e6234113af351464e676e0f189ab8910c341fc55c79471161bd28a"),
    (0xb52570,153,"5a1084652f79c5434440ce6c6c3588a95dbd109efbc339466f534f8b22489f59"),
    (0xb52610,32,"57c35b730dd8b37d9b0e169704b430bcb817ac22a85f31eff54941bc0fa5c23f"),
    (0xad3ea0,33,"358547c7cc367a822fe945ef88ed1b9fb2886e1ae76042fa6c2df16ce7bdd8b0"),
    (0xaa5280,93,"4183418046bd9aa8c40a87a40b3f666b3af8325af54dddec1d894c757a62b7b1"),
    (0xfd17b0,32,"cf82ca9acdc3ecd1684532bcfe6e1a52f828076859979fa9099e0ec1e167fbbf"),
    (0xfd1890,32,"4098f1b7b6fb7ad2a301d478df1ca0fba402a245a3e975071d408ec78ae781dd"),
];

#[cfg(test)] mod tests{
    use super::*;
    struct Mem(Vec<u32>);
    impl Mem{
        fn new(n:usize)->Self{Self(vec![0;n/4+1])}
        fn p(&self)->usize{self.0.as_ptr()as usize}
        fn w(&mut self,o:usize,v:u32){self.0[o/4]=v;}
    }
    struct Fixture{world:Mem,info:Mem,pawn:Mem,camera:Mem}
    impl Fixture{
        fn new()->Self{
            let mut f=Self{world:Mem::new(0x6d4),info:Mem::new(0x380),pawn:Mem::new(0x950),camera:Mem::new(0x980)};
            f.world.w(0x20,1);f.world.w(0x6d0,f.info.p()as u32);
            f.info.w(0x144,f.world.p()as u32);f.info.w(0x37c,6);
            f.pawn.w(0,SPY);f.pawn.w(0x20,2);f.pawn.w(0x144,f.world.p()as u32);
            f.camera.w(0,CAMERA);f.camera.w(0x20,3);f.camera.w(0x144,f.world.p()as u32);
            f.camera.w(0x8d4,f.pawn.p()as u32);f.camera.w(0x860,2);f
        }
        unsafe fn got(&self)->Option<Owner>{device_at(self.camera.p(),self.info.p()as u32)}
    }
    #[test] fn scoped_device_is_a_deployed_mp_spy_camera_not_a_campaign_object(){unsafe{
        let mut f=Fixture::new();let old=f.got().unwrap();
        for (offset,bad)in[(0,0x02ab0194),(0x5c,1),(0x78,2),(0x858,0x800),(0x860,0),(0x860,1),(0x860,3),(0x20,0),(0x20,u32::MAX)]{
            let saved=f.camera.0[offset/4];f.camera.w(offset,bad);assert_eq!(f.got(),None,"{offset:x}");f.camera.w(offset,saved);
        }
        for mode in [0,1,5,11,255]{f.info.w(0x37c,mode);assert_eq!(f.got(),None);}
        for mode in 6..=10{f.info.w(0x37c,mode);assert_eq!(f.got(),Some(old));}
        f.pawn.w(0,0x02ad2abc);assert_eq!(f.got(),None);f.pawn.w(0,SPY);
        f.pawn.w(0x60,0x800);assert_eq!(f.got(),None);f.pawn.w(0x60,0);
        f.pawn.w(0x5c,1);assert_eq!(f.got(),None);f.pawn.w(0x5c,0);
        f.pawn.w(0x144,0);assert_eq!(f.got(),None);f.pawn.w(0x144,f.world.p()as u32);
        assert_eq!(device_at(f.camera.p(),0),None);
        assert_eq!(f.got(),Some(old));
    }}
    #[test] fn identity_change_and_input_revocation_cancel_pending_gesture(){unsafe{
        let mut f=Fixture::new();let old=f.got().unwrap();let mut g=Gestures::default();
        g.step(Some(old),Edge::Press,1);f.camera.w(0x20,4);
        assert_eq!(g.step(f.got(),Edge::Tick,500).command,None);
        assert_eq!(g.step(f.got(),Edge::Release,501).command,None);
        INPUT.with(|s|s.set(Some(100)));assert!(input_allowed(100));assert!(input_allowed(350));
        assert!(!input_allowed(99));assert!(!input_allowed(351));
        GESTURE.with(|s|{s.borrow_mut().step(f.got(),Edge::Press,100);});
        input_scope(false);assert!(!input_allowed(101));assert!(GESTURE.with(|s|s.borrow().owner().is_none()));
    }}
}

#[cfg(test)]
#[path="sticky_camera_world_tests.rs"]
mod camera_world_regression;

#[cfg(test)]
#[path="sticky_camera_lifecycle_runtime_fixture.rs"]
pub(crate) mod lifecycle_fixture;
