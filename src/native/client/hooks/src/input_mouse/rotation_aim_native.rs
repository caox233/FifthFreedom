//! Paired native aim adapter. No global rotator hook, no FOV or weapon changes.
//! Site publication belongs to rotation_pipeline_native's all-or-none group.
use super::scis_diagnostics as diag;
use super::{memory::{word,float,readable}, rotation_precision::Snapshot,
    rotation_precision_native, rotation_pipeline_native,
    fractional_aim_policy::{self as policy,AimCarry,AimFrame,Vec3}};
use std::{cell::RefCell,sync::atomic::{AtomicU64,AtomicBool,Ordering}};
#[derive(Clone,Copy)]
struct SceneScope { post:bool, input:Snapshot, core:usize, rotation:usize, screen:usize, angles:[i32;3], scene:Option<usize>, mark:Option<Snapshot> }
#[derive(Clone,Copy)]
struct ProducerScope { frame:AimFrame, camera:usize, first:bool, second:bool }
#[derive(Default)]
struct State { post_pending:Option<Snapshot>, epoch:u64, depth:u32, scene:Option<SceneScope>, producer:Option<ProducerScope>, carry:AimCarry }
static ACTIVE:AtomicBool=AtomicBool::new(false);
static EPOCH:AtomicU64=AtomicU64::new(1);
thread_local! {static STATE:RefCell<State>=RefCell::new(State::default());}
fn state<R>(f:impl FnOnce(&mut State)->R)->Option<R> {
    STATE.try_with(|s|{let mut s=s.try_borrow_mut().ok()?;let e=EPOCH.load(Ordering::Acquire);
        if s.epoch!=e {*s=State{epoch:e,..State::default()};}Some(f(&mut s))}).ok().flatten()
}
/// Disable the shared pipeline first. Invalidates cached carry on every thread.
pub(super) fn set_active(value:bool){ACTIVE.store(value,Ordering::Release);if !value{invalidate();}}
pub(super) fn new_frame(){invalidate();}
pub(super) fn in_scope()->bool{state(|s|s.depth!=0).unwrap_or(true)}
pub(super) fn invalidate(){EPOCH.fetch_add(1,Ordering::AcqRel);let _=state(|_|());}
#[inline]unsafe fn native(va:usize)->usize {
    #[cfg(aim_fixture)] {return super::fixture_native::address(va);}
    #[cfg(not(aim_fixture))] {va}
}
unsafe fn rot(p:usize)->Option<[i32;3]>{Some([word(p,0)?as i32,word(p,4)?as i32,word(p,8)?as i32])}
unsafe fn vec(p:usize)->Option<Vec3>{Some([float(p,0)?,float(p,4)?,float(p,8)?])}
unsafe fn write_vec(p:usize,v:Vec3)->bool {
    if !readable(p,12){return false;}for (i,x)in v.into_iter().enumerate(){((p+i*4)as*mut f32).write_unaligned(x);}true
}
unsafe fn current(core:usize)->Option<(Snapshot,usize)> {
    if !ACTIVE.load(Ordering::Acquire)||!rotation_pipeline_native::active(){return None;}
    let s=rotation_precision_native::snapshot(core)?;let pc=word(core,0x20)?as usize;let camera=word(core,0x1c)?as usize;
    if s.owner.core!=core || word(core,0x2c)?as usize!=s.owner.mode || word(core,0x24)?as usize!=s.owner.pawn
        || word(core,0x1434)?!=0 || word(camera,0x61c)?as usize!=core
        || word(pc,0x6a4)?as usize!=camera || word(s.owner.pawn,0x938)?as usize!=pc
        || word(s.owner.pawn,0x144)?as usize!=s.owner.world || word(pc,0x354)?as usize!=s.owner.viewport
        || word(s.owner.viewport,0x30)?as usize!=pc
        || word(s.owner.mode,4)?as usize!=core || word(pc,0x520)?as usize!=s.owner.pawn
        || word(pc,0x144)?as usize!=s.owner.world || word(camera,0x144)?as usize!=s.owner.world
        || word(pc,0x20)?!=s.owner.generation || word(pc,0x60)?&0x40000==0
        || [word(core,0xb8)?as i32,word(core,0xbc)?as i32]!=s.targets {return None;}
    Some((s,camera))
}
unsafe fn active_frame(core:usize)->Option<(AimFrame,usize)> {
    let(s,camera)=current(core)?;let r=rot(core.checked_add(0xbd8)?)?;
    state(|x|x.carry.begin(Some(s),r)).flatten().map(|f|(f,camera))
}
#[derive(Clone,Copy)]
pub(super) struct SceneRay {
    pub frame:AimFrame, pub camera:usize, pub hit_actor:usize,
    pub origin:Vec3, pub target:Vec3,
}
pub(super) unsafe fn scene_ray(core:usize)->Option<SceneRay> {
    if in_scope() { diag::hit(diag::RAY_NESTED_REJECT);return None; }
    let epoch=EPOCH.load(Ordering::Acquire);
    let(frame,camera)=active_frame(core).or_else(||{diag::hit(diag::RAY_FRAME_REJECT);None})?;
    let hit_actor=word(camera,0x628)? as usize;
    let target=vec(camera.checked_add(0x62c)?)?;
    if !state(|s|s.carry.cache_matches(Some(frame.input),frame.native_rot,camera,hit_actor,target)).unwrap_or(false) { diag::hit(diag::RAY_CACHE_REJECT);return None; }
    let origin=vec(core.checked_add(0xe0)?)?;
    if EPOCH.load(Ordering::Acquire)!=epoch || current(core)?.0!=frame.input { return None; }
    Some(SceneRay{frame,camera,hit_actor,origin,target})
}
unsafe extern "thiscall" fn scoped_deproject(core:usize,out:usize,input:usize,screen:usize)->usize {
    let epoch=EPOCH.load(Ordering::Acquire);
    let pending=state(|x|x.post_pending.take()).flatten();
    let scope=(||{let(s,_)=current(core)?;if input!=core.checked_add(0x6c)?||screen!=core.checked_add(0xbcc)?{return None;}
        Some(SceneScope{post:pending==Some(s),input:s,core,rotation:input,screen,angles:rot(input)?,scene:None,mark:None})})();
    let saved=state(|x|{x.carry.clear();let old=(x.scene,x.depth);x.scene=scope;x.depth=x.depth.saturating_add(1);old});
    let f:unsafe extern "thiscall" fn(usize,usize,usize,usize)->usize=std::mem::transmute(native(0xd4ce90));
    let result=f(core,out,input,screen);
    if EPOCH.load(Ordering::Acquire)==epoch {if let Some(saved)=saved{let _=state(|x|{x.scene=saved.0;x.depth=saved.1;});}}
    result
}
unsafe extern "thiscall" fn bind_constructor(scene:usize,viewport:usize,target:usize,location:usize,rotation:usize,fov:f32)->usize {
    let scope=state(|x|x.scene).flatten();
    if let Some(s)=scope {
        let valid=current(s.core).is_some_and(|(now,_)|now==s.input)
            && viewport==s.input.owner.viewport && word(viewport,0x30).map(|x|x as usize)==Some(target)
            && rotation==s.rotation && rot(rotation)==Some(s.angles)
            && s.screen==s.core+0xbcc && vec(location).is_some_and(|p|p==[0.;3]);
        let _=state(|x|{if let Some(t)=x.scene.as_mut(){t.scene=valid.then_some(scene);t.mark=None;}});
    }
    let f:unsafe extern "thiscall" fn(usize,usize,usize,usize,usize,f32)->usize=std::mem::transmute(native(0x5acb60));
    f(scene,viewport,target,location,rotation,fov)
}
/// Shared 58FEB5 callback calls only after validating writable native output and
/// immediately before copying the precise matrix. Returning Some issues one mark.
/// Aim scene uses its own original core+6C angles and zero origin, not Published.
pub(super) unsafe fn view_angles(scene:usize)->Option<[f64;3]> {
    let s=state(|x|x.scene).flatten()?;let(now,camera)=current(s.core)?;
    if now!=s.input || s.scene!=Some(scene) || rot(scene.checked_add(0x2e8)?)?!=s.angles
        || rot(s.rotation)?!=s.angles || vec(scene.checked_add(0x2a0)?)?!=[0.;3]
        || word(scene,0x10)?as usize!=s.input.owner.viewport
        || word(scene,0x2dc)?!=word(s.core,0x20)? {return None;}
    // Only the shot-query scene receives native idle sway. The world scene keeps
    // its original precise mouse angles; AimCarry transports this exact ray.
    #[cfg(any(not(aim_fixture),aim_motion_fixture))]
    let motion=super::rotation_motion::delta(now,camera,s.post).unwrap_or([0.;2]);
    #[cfg(all(aim_fixture,not(aim_motion_fixture)))]
    let motion={let _=camera;[0.;2]};
    if current(s.core)?.0!=now {return None;}
    let angles=[s.angles[0]as f64+s.input.fraction[0]+motion[0],s.angles[1]as f64+s.input.fraction[1]+motion[1],s.angles[2]as f64];
    if !angles.iter().all(|v|v.is_finite()){return None;}
    state(|x|{let t=x.scene.as_mut()?;if t.scene!=Some(scene)||t.input!=now{return None;}t.mark=Some(now);Some(angles)}).flatten()
}
unsafe extern "C" fn capture(core:usize,raw:usize,out:usize)->usize {
    let epoch=EPOCH.load(Ordering::Acquire);let raw_saved=vec(raw);let scene=state(|x|x.scene.as_mut().and_then(|s|{let mark=s.mark.take();(s.core==core).then_some(mark).flatten()})).flatten();
    let f:unsafe extern "thiscall" fn(usize,usize)->usize=std::mem::transmute(native(0x4f1300));let result=f(raw,out);
    let now=if EPOCH.load(Ordering::Acquire)==epoch{current(core).map(|(s,_)|s)}else{None};let r=rot(out);
    let _=state(|x|{if let(Some(raw),Some(rot))=(raw_saved,r){x.carry.capture(now,scene,raw,rot);}else{x.carry.clear();}});result
}
#[allow(clippy::too_many_arguments)]
unsafe extern "thiscall" fn producer(core:usize,o1:usize,f1:usize,o2:usize,f2:usize,o3:usize,f3:usize,o4:usize,f4:usize,o5:usize,f5:usize) {
    let epoch=EPOCH.load(Ordering::Acquire);
    diag::hit(diag::AIM_PRODUCER_ENTER);
    let local=active_frame(core).and_then(|(frame,camera)|(camera.checked_add(0x624)==Some(o1)).then_some(ProducerScope{frame,camera,first:false,second:false}));
    if local.is_some(){diag::hit(diag::AIM_PRODUCER_OWNED);}else{diag::hit(diag::AIM_PRODUCER_REJECT);}
    let saved=state(|x|std::mem::replace(&mut x.producer,local));
    let f:unsafe extern "thiscall" fn(usize,usize,usize,usize,usize,usize,usize,usize,usize,usize,usize)=std::mem::transmute(native(0xd81180));
    f(core,o1,f1,o2,f2,o3,f3,o4,f4,o5,f5);
    let done=state(|x|x.producer).flatten();
    if let Some(done)=done {
        let matching=active_frame(core).is_some_and(|(now,camera)|now.input==done.frame.input&&now.native_rot==done.frame.native_rot&&camera==done.camera);
        diag::sample(diag::AIM_PRODUCER_COMPLETED,||[u32::from(matching),u32::from(done.first),u32::from(done.second),done.frame.input.frame as u32,done.frame.native_rot[0] as u32,done.frame.native_rot[1] as u32,done.frame.direction[0].to_bits(),done.frame.direction[1].to_bits(),done.frame.direction[2].to_bits(),0,0,0,0,0,0,0]);
        let actor=word(done.camera,0x628);let point=vec(done.camera+0x62c);
        let _=state(|x|{if let(Some(actor),Some(point))=(actor,point){x.carry.finish(done.frame,done.camera,actor as usize,point,matching&&done.first&&done.second);}});
    }
    if EPOCH.load(Ordering::Acquire)==epoch {if let Some(saved)=saved{let _=state(|x|x.producer=saved);}}
}
unsafe fn producer_frame(core:usize)->Option<ProducerScope> {
    let p=state(|x|x.producer).flatten()?;let(f,camera)=active_frame(core)?;
    (f.input==p.frame.input&&f.native_rot==p.frame.native_rot&&camera==p.camera).then_some(p)
}
unsafe extern "system" fn initial(core:usize,bp:usize) {
    let Some(p)=producer_frame(core)else{return;};let Some(stack)=bp.checked_sub(0x4c)else{return;};if !readable(stack,0x28){return;}
    let Some(pawn)=vec(p.frame.input.owner.pawn+0x9c)else{return;};let Some(origin)=vec(core+0xe0)else{return;};
    let Some((dir,dot))=p.frame.initial(pawn,origin)else{return;};if !write_vec(stack,dir){return;}( (bp-0x28)as*mut f32).write_unaligned(dot);
    let _=state(|x|{if let Some(p)=x.producer.as_mut(){p.first=true;}});
}
unsafe extern "system" fn endpoint(core:usize,bp:usize) {
    let Some(p)=producer_frame(core)else{return;};if !p.first{return;}let Some(stack)=bp.checked_sub(0x4c)else{return;};
    let Some(origin)=vec(bp-0x40)else{return;};let Some(range)=float(bp-0x28,0)else{return;};let Some(v)=p.frame.endpoint(origin,range)else{return;};
    if write_vec(stack,v){let _=state(|x|{if let Some(p)=x.producer.as_mut(){p.second=true;}});}
}
unsafe fn native_info()->Option<usize> {
    let preferred=word(0x330c4f8,0)?as usize;
    if word(0x330c198,0)?!=0 {return Some(preferred);}
    let fallback=word(0x330c4fc,0)?as usize;Some(if fallback!=0{fallback}else{preferred})
}
unsafe extern "C" fn fallback(pawn:usize,camera:usize,out:usize)->usize {
    let f:unsafe extern "thiscall" fn(usize,usize)->usize=std::mem::transmute(native(0xd68ae0));let result=f(camera,out);
    let work=(||{let core=word(camera,0x61c)?as usize;let(frame,owned_camera)=active_frame(core)?;
        if owned_camera!=camera||frame.input.owner.pawn!=pawn||word(camera,0x628)?!=0||word(native_info()?,0xec)?&0x10!=0{return None;}
        let point=vec(camera+0x62c)?;
        if !state(|s|s.carry.cache_matches(Some(frame.input),frame.native_rot,camera,0,point)).unwrap_or(false){return None;}
        let range:unsafe extern "thiscall" fn(usize)->f32=std::mem::transmute(native(0xd3a710));frame.endpoint(vec(core+0xe0)?,range(camera))})();
    if let Some(v)=work{let _=write_vec(out,v);}result
}
// DA3F14 is the post-hands deprojection, after D609B0 in both native
// camera paths. A one-use request marks only its immediate deprojection;
// nested/unrelated queries must not establish or invalidate the idle anchor.
struct PostRequest { epoch:u64, saved:Option<Option<Snapshot>> }
impl Drop for PostRequest {
    fn drop(&mut self) {
        if EPOCH.load(Ordering::Acquire)==self.epoch {
            if let Some(saved)=self.saved {let _=state(|s|s.post_pending=saved);}
        }
    }
}
unsafe extern "thiscall" fn post_hands(core:usize) {
    let epoch=EPOCH.load(Ordering::Acquire);
    let pending=current(core).map(|(s,_)|s);
    let saved=state(|s|std::mem::replace(&mut s.post_pending,pending));
    let _request=PostRequest{epoch,saved};
    let f:unsafe extern "thiscall" fn(usize)=std::mem::transmute(native(0xd4d540));
    f(core);
}

pub(super) enum Target { Call(usize), Bridge(Vec<u8>) }
pub(super) struct Descriptor {pub address:usize,pub original:[u8;5],pub target:Target}
/// One group, together with root-owned shared 58FEB5 view callback. Preflight all
/// original bytes, build+seal every thunk, publish while inactive; active only
/// after every patch succeeds. Reverse restoration uses replacement-byte ownership.
pub(super) fn descriptors()->Vec<Descriptor> {
    vec![
        Descriptor{address:0xda3f14,original:[0xe8,0x27,0x96,0xfa,0xff],target:Target::Call(post_hands as usize)},
        Descriptor{address:0xd4d574,original:[0xe8,0x17,0xf9,0xff,0xff],target:Target::Call(scoped_deproject as usize)},
        Descriptor{address:0xd4cf68,original:[0xe8,0xf3,0xfb,0x85,0xff],target:Target::Call(bind_constructor as usize)},
        Descriptor{address:0xd4d0d2,original:[0xe8,0x29,0x42,0x7a,0xff],target:Target::Bridge(policy::capture_bridge(capture as usize as u32))},
        Descriptor{address:0xd8f02a,original:[0xe8,0x51,0x21,0xff,0xff],target:Target::Call(producer as usize)},
        Descriptor{address:0xd812f3,original:[0xf3,0x0f,0x11,0x45,0xd8],target:Target::Bridge(policy::producer_bridge(initial as usize as u32,false))},
        Descriptor{address:0xd81530,original:[0xf3,0x0f,0x11,0x45,0xbc],target:Target::Bridge(policy::producer_bridge(endpoint as usize as u32,true))},
        Descriptor{address:0xd6f62e,original:[0xe8,0xad,0x94,0xff,0xff],target:Target::Bridge(policy::fallback_bridge(fallback as usize as u32))},
    ]
}

/// Allocates all bridges before root publishes any callsite.
pub(super) unsafe fn sites()->Option<Vec<rotation_pipeline_native::Site>> {
    let mut result=Vec::new();
    for d in descriptors(){let address=match d.target{Target::Call(p)=>p,Target::Bridge(b)=>rotation_pipeline_native::allocate_code(|_|b)?};
        result.push(rotation_pipeline_native::Site{address:d.address,original:d.original.to_vec(),replacement:rotation_pipeline_native::call(d.address,address)});}
    Some(result)
}

