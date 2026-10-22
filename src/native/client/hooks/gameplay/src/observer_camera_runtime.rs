//! Camera ownership/lifecycle at the final native render-view boundary.
use crate::observer_camera::{Camera,Binding,Input,Pose};
use crate::observer_view::{View,Identity};
use hooks_plugin_api::observer as ob;
use crate::equipment_native::word;
use retour::static_detour;
use std::sync::{Mutex,atomic::{AtomicBool,Ordering}};

static STATE:Mutex<Runtime>=Mutex::new(Runtime::new());
static NEEDS_PAUSE:AtomicBool=AtomicBool::new(false);
static_detour! {static Update:unsafe extern "C" fn(usize);}
#[link(name="kernel32")]
extern "system" {fn GetTickCount64()->u64;}

struct Runtime {
    identity:Option<Identity>,camera:Option<Camera>,pose:Option<Pose>,last_input:Option<u64>,last_present:u64,
}
impl Runtime {
    const fn new()->Self {Self{identity:None,camera:None,pose:None,last_input:None,last_present:0}}
    fn pause(&mut self) {
        if let (Some(id),Some(camera))=(self.identity,self.camera.as_mut()) {
            camera.step(binding(id),false,0,Input::default());
        }
        self.last_input=None; self.last_present=0;
    }
    unsafe fn present(&mut self,view:View,frame:usize,input:ob::InputFrame,now:u64)->bool {
        let id=view.identity;
        if !matches_input(id,input) || now<input.now_ms || now-input.now_ms>=100
            || word(id.controller,0x310)!=Some(0) || word(id.controller,0x520)!=Some(0) {
            self.pause();return false;
        }
        if self.identity!=Some(id) {
            self.camera=Camera::new(binding(id),view.pose);
            self.identity=Some(id);self.pose=None;self.last_input=None;self.last_present=0;
        } else if self.last_present!=0 && (now<self.last_present || now-self.last_present>=100) {
            self.pause();
        }
        let Some(camera)=self.camera.as_mut() else{return false;};
        if self.last_input.is_none_or(|last|input.now_ms>last) {
            let axis=|positive,negative| f64::from((input.keys&positive!=0) as u8)
                - f64::from((input.keys&negative!=0) as u8);
            let controls=Input {movement:[axis(ob::FORWARD,ob::BACK),axis(ob::RIGHT,ob::LEFT),axis(ob::UP,ob::DOWN)],
                mouse:[input.mouse_x as f64,input.mouse_y as f64],fast:input.keys&ob::FAST!=0,slow:input.keys&ob::SLOW!=0};
            let Some(pose)=camera.step(binding(id),true,input.now_ms,controls) else {return false;};
            self.pose=Some(pose);self.last_input=Some(input.now_ms);
        }
        let Some(pose)=self.pose else{return false;};
        if !view.present(frame,pose) {self.pause();return false;}
        self.last_present=now;true
    }
}
fn binding(id:Identity)->Binding {Binding{world:id.world,controller:id.controller,generation:id.generation}}
fn matches_input(id:Identity,f:ob::InputFrame)->bool {
    f.valid() && f.flags==ob::ADMITTED|ob::CAPTURE && f.world as usize==id.world
        && f.controller as usize==id.controller && f.generation==id.generation && f.viewport as usize==id.viewport
}
pub(crate) fn ready()->bool {Update.is_enabled()}
pub(crate) fn pause() {
    if !NEEDS_PAUSE.swap(false,Ordering::AcqRel){return;}
    if let Ok(mut state)=STATE.try_lock(){state.pause();}
    else{NEEDS_PAUSE.store(true,Ordering::Release);}
}
pub(crate) fn is_presenting(f:ob::InputFrame,now:u64)->bool {
    let Ok(s)=STATE.try_lock() else {return false;};
    s.identity.is_some_and(|id|matches_input(id,f)) && s.last_present!=0
        && now>=s.last_present && now-s.last_present<100
}
unsafe fn update(frame:usize) {
    Update.call(frame);
    #[cfg(feature="observer-camera-probe")]
    crate::observer_camera_probe::observe(frame);
    let Some(f)=crate::observer_input::snapshot() else {pause();return;};
    // Secondary/reflection frames must neither consume controls nor retire the
    // primary camera. Only a validated final local viewport reaches this owner.
    let Some(view)=crate::observer_view::inspect(f.world as usize,frame) else {return;};
    if !matches_input(view.identity,f) {return;}
    let Ok(mut state)=STATE.try_lock() else {return;};
    let now=GetTickCount64();
    let after=if state.identity==Some(view.identity){state.last_input}else{None};
    let Some(input)=crate::observer_input::take(now,f,after) else {state.pause();return;};
    NEEDS_PAUSE.store(true,Ordering::Release);
    state.present(view,frame,input,now);
}
pub(crate) unsafe fn init(world_raycast:usize) {
    use crate::equipment_native::signature;
    if ready(){return;}
    if world_raycast!=0x00589860
        || !signature(0x00667a50,0xe0,"e744df7c84f212e2efdeb22f164d65febde95fe63afd9cda65f0b8b266f9ff33")
        || !signature(0x00664a10,0xf4,"7daea70ac5566a484fab64d99ab07a66ffb6a55df61b9c268358d645f964e828")
        || !signature(0x006687d0,0x535,"ee2f6d30205d9c4775a1e196e8201c932664cd9688657cca170746e7ca6bbfbf") {
        tracing::warn!("SCBL_OB_CAMERA ready=false reason=native-contract");return;
    }
    let target:unsafe extern "C" fn(usize)=std::mem::transmute(0x00667a50usize);
    if Update.initialize(target,|frame|unsafe{update(frame)}).is_ok() && Update.enable().is_ok() {
        tracing::info!("SCBL_OB_CAMERA ready=true owner=final-view requires=admission-detached-local-pawn");
    }else{tracing::warn!("SCBL_OB_CAMERA ready=false reason=hook-install");}
}
pub(crate) unsafe fn deinit() {
    crate::observer_input::clear();let _=Update.disable();
    if let Ok(mut state)=STATE.lock(){*state=Runtime::new();}
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn observer_runtime_updates_real_final_frame_and_retains_pose_across_render_passes() {
        let s=crate::observer_view::tests::Scene::new();let mut runtime=Runtime::new();
        let view=s.inspect().unwrap();let id=view.identity;
        let mut f=ob::InputFrame{world:id.world as u32,controller:id.controller as u32,generation:id.generation,
            viewport:id.viewport as u32,flags:ob::ADMITTED|ob::CAPTURE,now_ms:1000,..ob::InputFrame::default()};
        assert!(unsafe{runtime.present(view,s.frame_address(),f,1000)});
        f.now_ms=1050;f.keys=ob::UP;f.mouse_x=100.;
        assert!(unsafe{runtime.present(s.inspect().unwrap(),s.frame_address(),f,1050)});
        let p=s.inspect().unwrap().pose;
        assert_eq!(p.position_cm,[100.,200.,325.]);assert!((p.yaw_degrees-97.).abs()<0.01);
        assert!(unsafe{runtime.present(s.inspect().unwrap(),s.frame_address(),f,1050)});
        assert_eq!(s.inspect().unwrap().pose,p);
    }
    fn controls(s:&crate::observer_view::tests::Scene,now:u64)->ob::InputFrame {
        let id=s.inspect().unwrap().identity;
        ob::InputFrame{world:id.world as u32,controller:id.controller as u32,generation:id.generation,
            viewport:id.viewport as u32,flags:ob::ADMITTED|ob::CAPTURE,now_ms:now,..ob::InputFrame::default()}
    }
    #[test] fn observer_runtime_expiry_and_resume_do_not_teleport_or_replay_mouse() {unsafe{
        let s=crate::observer_view::tests::Scene::new();let mut r=Runtime::new();let mut f=controls(&s,1000);
        assert!(r.present(s.inspect().unwrap(),s.frame_address(),f,1000));
        f.now_ms=1050;f.keys=ob::UP;assert!(r.present(s.inspect().unwrap(),s.frame_address(),f,1050));
        let held=s.inspect().unwrap().pose;
        assert!(!r.present(s.inspect().unwrap(),s.frame_address(),f,1150));
        assert_eq!(s.inspect().unwrap().pose,held);
        f.now_ms=2000;f.mouse_x=1000.;
        assert!(r.present(s.inspect().unwrap(),s.frame_address(),f,2000));
        assert_eq!(s.inspect().unwrap().pose,held);
        f.now_ms=2050;f.mouse_x=0.;
        assert!(r.present(s.inspect().unwrap(),s.frame_address(),f,2050));
        assert_eq!(s.inspect().unwrap().pose.position_cm,[100.,200.,350.]);
    }}
    #[test] fn observer_runtime_requires_detachment_and_retires_reused_controller_generation() {unsafe{
        let s=crate::observer_view::tests::Scene::new();let mut r=Runtime::new();let mut f=controls(&s,1000);
        let before=s.inspect().unwrap().pose;
        for offset in [0x310,0x520] {
            ((f.controller as usize+offset) as *mut u32).write(0x12345);
            assert!(!r.present(s.inspect().unwrap(),s.frame_address(),f,1000));
            assert_eq!(s.inspect().unwrap().pose,before);
            ((f.controller as usize+offset) as *mut u32).write(0);
        }
        assert!(r.present(s.inspect().unwrap(),s.frame_address(),f,1000));
        f.now_ms=1050;f.keys=ob::UP;assert!(r.present(s.inspect().unwrap(),s.frame_address(),f,1050));
        ((f.controller as usize+0x20) as *mut u32).write(f.generation+1);
        ((s.frame_address()+16) as *mut f32).write(900.);
        assert!(!r.present(s.inspect().unwrap(),s.frame_address(),f,1050));
        f=controls(&s,1100);assert!(r.present(s.inspect().unwrap(),s.frame_address(),f,1100));
        assert_eq!(s.inspect().unwrap().pose.position_cm,[100.,200.,900.]);
    }}
}
