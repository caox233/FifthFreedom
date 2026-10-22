//! Retire only the admitted photographer's pawn through native DestroyActor.
//! Never write health, pawn/controller links or a network ownership flag.
use hooks_plugin_api::observer::InputFrame;
use crate::equipment_native::word;
use retour::static_detour;
use std::sync::{Mutex,atomic::{AtomicBool,Ordering}};

static_detour! { static LevelTick:unsafe extern "thiscall" fn(usize,u32,f32); }
static SESSION:Mutex<Session>=Mutex::new(Session{last_attempt:None});
static BUSY:AtomicBool=AtomicBool::new(false);
#[link(name="kernel32")]
extern "system" {fn GetTickCount64()->u64;}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
struct Key { world:u32, controller:u32, generation:u32, level:usize, pawn:usize, object_index:u32 }
#[derive(Clone, Copy)]
struct Request { key:Key, input:InputFrame }
#[derive(Debug, PartialEq, Eq)]
enum Outcome { Skipped, Refused, Detached }
#[derive(Default)]
struct Session { last_attempt:Option<(Key,u64)> }
impl Session {
    unsafe fn prepare(&mut self,level:usize,input:InputFrame,now:u64)->Option<Request> {
        let action=request(level,input,now)?;
        if self.last_attempt.is_some_and(|(key,time)|key==action.key && now>=time && now-time<500) {
            return None;
        }
        self.last_attempt=Some((action.key,now));Some(action)
    }
}

unsafe fn request(level:usize,input:InputFrame,now:u64)->Option<Request> {
    if now<input.now_ms || now-input.now_ms>=100 || !crate::observer_input::local_context(input) {
        return None;
    }
    let pc=input.controller as usize;
    let table=word(level,0)? as usize;
    if word(table,0x68)?!=0x0061c610 || word(table,0x94)?!=0x00619010
        || word(level,0x35c)?!=0 || word(pc,0x148)? as usize!=level {return None;}
    let pawn=word(pc,0x310)? as usize;
    if pawn<0x10000 || word(pc,0x520)? as usize!=pawn
        || !matches!(word(pawn,0)?,0x02ad3234|0x02ad2abc)
        || word(pawn,0x144)?!=input.world || word(pawn,0x148)? as usize!=level
        || word(pawn,0x358)?!=input.controller || word(pawn,0x938)?!=input.controller
        || word(pawn,0x4c)? & 0xff00==0x200 || word(pawn,0x5c)? & 0x8001!=0 {return None;}
    let object_index=word(pawn,0x20)?;
    if object_index==0 {return None;}
    Some(Request {key:Key{world:input.world,controller:input.controller,generation:input.generation,
        level,pawn,object_index},input})
}

pub(crate) fn ready()->bool {LevelTick.is_enabled()}
unsafe fn tick(level:usize,tick_type:u32,dt:f32) {
    LevelTick.call(level,tick_type,dt);
    let Some(input)=crate::observer_input::snapshot() else{return;};
    if BUSY.swap(true,Ordering::AcqRel){return;}
    struct Busy;
    impl Drop for Busy {fn drop(&mut self){BUSY.store(false,Ordering::Release);}}
    let _busy=Busy;
    let action={
        let Ok(mut session)=SESSION.try_lock() else{return;};
        session.prepare(level,input,GetTickCount64())
    };
    // Actor destruction runs only after native traversal has returned. No input,
    // plugin, camera or session mutex is held across this re-entrant engine call.
    let Some(action)=action else{return;};
    let result=action.run(GetTickCount64(),|level,pawn| {
        if !ready(){return false;}
        let destroy:unsafe extern "thiscall" fn(usize,usize,i32,i32)->i32=
            std::mem::transmute(0x00619010usize);
        // Exactly AActor::execDestroy's (actor, force=0, modify-level=1).
        destroy(level,pawn,0,1)!=0
    });
    tracing::info!("SCBL_OB_PAWN result={result:?} native_force=false controller_retained=true");
}
pub(crate) unsafe fn init(world_raycast:usize) {
    use crate::equipment_native::signature;
    if ready(){return;}
    if world_raycast!=0x00589860
        || !signature(0x0061c610,0x1848,"760ad23c8ed559b2bb3094cc53180ef0b6ad9a30d17158a16edd417098ac7114")
        || !signature(0x00619010,0x780,"70ef833ab890bba3b1fa9d8dc2ecaed17c97c952e0973a9f24d80cb4924fc595")
        || !signature(0x00544810,0x43,"b8c1c61e1a206429a953b13ff795d4ed92a1d4f56536b6443d87741b0c962d0c")
        || !signature(0x00ef2450,0x155,"809303610110666b4c17c73a4b5bc48c1f526f573a6e653ea726f7f792858e97")
        || !signature(0x006206a0,0x57,"b8a8673e259a66aaa01dcf09c09139de79c17772ee47b1cca657418481471080") {
        tracing::warn!("SCBL_OB_PAWN ready=false reason=native-contract");return;
    }
    let target:unsafe extern "thiscall" fn(usize,u32,f32)=std::mem::transmute(0x0061c610usize);
    if LevelTick.initialize(target,|level,kind,dt|unsafe{tick(level,kind,dt)}).is_ok()
        && LevelTick.enable().is_ok() {
        tracing::info!("SCBL_OB_PAWN ready=true boundary=post-level-tick native_force=false");
    }else{tracing::warn!("SCBL_OB_PAWN ready=false reason=hook-install");}
}
pub(crate) unsafe fn deinit() {
    let _=LevelTick.disable();
    if let Ok(mut session)=SESSION.lock(){*session=Session::default();}
}
impl Request {
    unsafe fn run(self,now:u64,destroy:impl FnOnce(usize,usize)->bool)->Outcome {
        if request(self.key.level,self.input,now).is_none_or(|current|current.key!=self.key) {
            return Outcome::Skipped;
        }
        if !destroy(self.key.level,self.key.pawn) {return Outcome::Refused;}
        // Native destruction may reclaim the pawn. Do not dereference it again.
        // A successful engine return alone is insufficient to activate the camera.
        if crate::observer_input::local_context(self.input)
            && word(self.input.controller as usize,0x310)==Some(0)
            && word(self.input.controller as usize,0x520)==Some(0) {Outcome::Detached}
        else {Outcome::Refused}
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use hooks_plugin_api::observer::{ADMITTED,CAPTURE};
    struct Scene {
        view:crate::observer_view::tests::Scene, level:Vec<u32>, table:Vec<u32>, pawn:Vec<u32>, input:InputFrame,
    }
    impl Scene {
        fn new()->Self {
            let view=crate::observer_view::tests::Scene::new();let id=view.inspect().unwrap().identity;
            let mut s=Self {view,level:vec![0;0x500/4],table:vec![0;0x100/4],pawn:vec![0;0x1000/4],
                input:InputFrame{world:id.world as u32,controller:id.controller as u32,generation:id.generation,
                    viewport:id.viewport as u32,flags:ADMITTED|CAPTURE,now_ms:1000,..InputFrame::default()}};
            s.table[0x68/4]=0x0061c610;s.table[0x94/4]=0x00619010;
            s.level[0]=s.table.as_ptr() as u32;s.pawn[0]=0x02ad3234;s.pawn[0x20/4]=99;
            s.pawn[0x144/4]=s.input.world;s.pawn[0x148/4]=s.level.as_ptr() as u32;
            s.pawn[0x358/4]=s.input.controller;s.pawn[0x938/4]=s.input.controller;
            unsafe {s.set(0x148,s.level.as_ptr() as u32);s.set(0x310,s.pawn.as_ptr() as u32);s.set(0x520,s.pawn.as_ptr() as u32);}
            s
        }
        unsafe fn set(&self,offset:usize,value:u32){((self.input.controller as usize+offset) as *mut u32).write(value);}
        fn level(&self)->usize {self.level.as_ptr() as usize}
    }
    #[test]
    fn observer_pawn_native_retirement_keeps_controller_and_session() {unsafe{
        let s=Scene::new();let before=s.pawn.clone();let mut calls=0;
        let action=request(s.level(),s.input,1000).expect("admitted owned pawn after native level tick");
        assert_eq!(action.run(1000,|level,pawn| {
            calls+=1;assert_eq!(level,s.level());assert_eq!(pawn,s.pawn.as_ptr() as usize);
            // Native engine seam: only its Destroyed -> UnPossess callback owns
            // these links. The adapter must not pre-clear them before this call.
            assert_eq!(word(s.input.controller as usize,0x310),Some(pawn as u32));
            s.set(0x310,0);s.set(0x520,0);true
        }),Outcome::Detached);
        assert_eq!(calls,1);assert_eq!(s.pawn,before);
        assert!(crate::observer_input::local_context(s.input));
        assert!(s.view.inspect().is_some());
        assert!(request(s.level(),s.input,1000).is_none());
    }}
    #[test]
    fn observer_pawn_refusal_does_not_repeatedly_destroy_and_respawn_gets_new_request() {unsafe{
        let mut s=Scene::new();let mut session=Session::default();
        let first=session.prepare(s.level(),s.input,1000).unwrap();
        assert_eq!(first.run(1000,|_,_|false),Outcome::Refused);
        s.input.now_ms=1050;
        assert!(session.prepare(s.level(),s.input,1050).is_none(),"native refusal is not retried every frame");
        s.input.now_ms=1500;
        assert!(session.prepare(s.level(),s.input,1500).is_some());
        // Allocation reuse must not suppress a new possession.
        s.pawn[0x20/4]=100;s.input.now_ms=1501;
        assert!(session.prepare(s.level(),s.input,1501).is_some());
    }}
    #[test]
    fn observer_pawn_rechecks_ownership_at_execution_and_never_forces_native_refusal() {unsafe{
        for fault in 0..14 {
            let mut s=Scene::new();let action=request(s.level(),s.input,1000).unwrap();
            match fault {
                0=>s.pawn[0x358/4]=0,1=>s.pawn[0x20/4]+=1,2=>s.pawn[0x144/4]=0,
                3=>s.pawn[0x148/4]=0,4=>s.pawn[0]=0,5=>s.level[0x35c/4]=1,
                6=>s.set(0x310,0),7=>s.set(0x520,0),8=>s.set(0x20,s.input.generation+1),
                9=>s.pawn[0x5c/4]=1,10=>s.pawn[0x5c/4]=0x8000,11=>s.table[0x94/4]=0,
                12=>s.pawn[0x938/4]=0,_=>s.pawn[0x4c/4]=0x200,
            }
            let before=s.pawn.clone();
            assert_eq!(action.run(1000,|_,_|panic!("fault {fault} reached native destroy")),Outcome::Skipped);
            assert_eq!(s.pawn,before);
        }
        let s=Scene::new();let action=request(s.level(),s.input,1000).unwrap();
        assert_eq!(action.run(1100,|_,_|panic!("expired request")),Outcome::Skipped);
        // Even a successful return must not activate OB if native links remain.
        assert_eq!(action.run(1000,|_,_|true),Outcome::Refused);
        assert_eq!(word(s.input.controller as usize,0x310),Some(s.pawn.as_ptr() as u32));
    }}
}
