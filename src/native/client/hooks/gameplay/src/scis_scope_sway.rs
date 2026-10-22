//! Same-frame native FP idle pose -> leased SCIS BasicScope only.
//! D609B0 refreshes hands before D60D40 in both camera update paths. Read the
//! native world bone palettes here, never an external/asynchronous gun sample.
use crate::scis_diagnostics as diag;
use crate::equipment_native::{signature,word,readable};
use retour::static_detour;
use std::sync::{Mutex,atomic::{AtomicBool,AtomicU32,Ordering}};
use std::{sync::OnceLock,time::Instant};
#[path="scis_scope_sway_policy.rs"] mod policy;
use policy::{Pose,Owner,Follow,AimMotion};
static PAIRED:AtomicBool=AtomicBool::new(false);
static MOTION:Mutex<AimMotion>=Mutex::new(AimMotion::new());
static MOTION_CLOCK:OnceLock<Instant>=OnceLock::new();
static READY:AtomicBool=AtomicBool::new(false);
static LOGS:AtomicU32=AtomicU32::new(0);
static FOLLOW:Mutex<Follow>=Mutex::new(Follow::new());
static_detour!{static Place:unsafe extern "thiscall" fn(usize);}
// The field gates are independently fixture tested. Render ownership additionally
// requires the parent module's exact scope lease (including actor generation).
fn inspect(core:usize,read:impl Fn(usize,usize)->Option<u32>)->Option<(Owner,usize,usize,usize)>{inspect_motion(core,true,read)}
fn idle_sequence(anim:usize,read:&impl Fn(usize,usize)->Option<u32>)->bool{
 let check=||->Option<bool>{
  if read(anim,0x30)?!=0x23c{return Some(false);}
  let flags=read(anim,0x34)?&3;let queued=read(anim,0x50)?;
  // BB9740 queues another native idle through B92BE0 while the hands update
  // is busy: current idle gets flag 2, queued idle retains flags 1.
  // This is still idle motion, not recoil. Pausing/rebasing here loses part of
  // every idle cycle and makes the result depend on the last shot's timing.
  Some((flags==1 && queued==0)
   ||(flags==3 && queued==0x23c && read(anim,0x54)?&3==1))
 };check()==Some(true)
}
fn inspect_motion(core:usize,idle:bool,read:impl Fn(usize,usize)->Option<u32>)->Option<(Owner,usize,usize,usize)>{
 let pawn=read(core,0x24)? as usize;let pc=read(core,0x20)? as usize;
 let camera=read(core,0x1c)? as usize;let hands=read(pawn,0x12ac)? as usize;
 let gun=read(pawn,0x4b8)? as usize;let scope=read(pawn,0x1508)? as usize;
 let world=read(pawn,0x144)? as usize;let anim=read(hands,0x31c)? as usize;
 let active_scope=read(pawn,0x1510)? as usize;
 // D60D40 returns without placing the scope outside ADS; do not add motion to
 // that stale pose or establish a hip-fire anchor after its early return.
 if read(pawn,0x948)?&0x400000==0 {return None;}
 diag::sample(diag::SWAY_GATES,||[read(core,0x470).unwrap_or(u32::MAX),read(core,0x474).unwrap_or(u32::MAX),read(pawn,0x1510).unwrap_or(u32::MAX),read(hands,0).unwrap_or(0),read(hands,0x318).unwrap_or(u32::MAX),read(gun,0).unwrap_or(0),read(anim,0).unwrap_or(0),read(anim,0x30).unwrap_or(u32::MAX),read(anim,0x34).unwrap_or(u32::MAX),read(anim,0x50).unwrap_or(u32::MAX),u32::from(read(pc,0x520)==Some(pawn as u32)),u32::from(read(pc,0x6a4)==Some(camera as u32)),u32::from(read(camera,0x61c)==Some(core as u32)),u32::from(read(hands,0x310)==Some(pawn as u32)),u32::from(read(hands,0x144)==Some(world as u32)),u32::from(read(gun,0x8c)==Some(pawn as u32))]);
 // D60EB4..D60F36 integrates ADS placement over 0.3 s and clamps 46C
 // to [0,1]. The idle descriptor is already present during that transition:
 // anchoring then turns its large offset into permanently saturated "sway".
 if read(core,0x46c)?!=1.0f32.to_bits(){return None;}
 // Input lag is not a lifecycle exit. Keep the existing bounded idle offset
 // while D60D40 handles lag; never feed that lag into breathing a second time.
 if !f32::from_bits(read(core,0x470)?).is_finite()
  ||!f32::from_bits(read(core,0x474)?).is_finite(){return None;}
 if read(pc,0x520)? as usize!=pawn||read(pc,0x6a4)? as usize!=camera
  ||read(camera,0x61c)? as usize!=core
  // DA6B10 caches the active scope at pawn+1510 on ADS entry (DA6D39).
  // A leased BasicScope is valid here; reject a different active optic.
  ||(active_scope!=0 && active_scope!=scope)
  ||read(hands,0)?!=0x02a23634||read(hands,0x310)? as usize!=pawn
  ||read(hands,0x144)? as usize!=world||read(hands,0x318)?&2!=0
  ||read(gun,0)?!=0x02a85b7c||read(gun,0x8c)? as usize!=pawn
  ||read(anim,0)?!=0x02a3ecfc
  ||(idle && !idle_sequence(anim,&read)) {return None;}
 let key=Owner([world,pawn,read(pawn,0x20)? as usize,gun,read(gun,0x20)? as usize,
  hands,read(hands,0x20)? as usize,scope,read(scope,0x20)? as usize,core]);
 Some((key,hands,scope,pawn))
}
unsafe fn bone_pair(hands:usize)->Option<(Pose,Pose)>{
 let instance=word(hands,0x1f0)? as usize;let component=word(instance,0xc0)? as usize;
 let map=word(hands,0x430)? as usize;
 let worker=word(0x3301820,0)? as usize;let phase=word(worker,8)?;
 // Retail 1CE0670 reads AC4 in phases 8/10; its AC8-valid bit (0x80)
 // is irrelevant there. Require it only in phase 9 to avoid worker lazy refresh.
 diag::sample(diag::SWAY_PALETTE,||[phase,word(component,0x14).unwrap_or(u32::MAX),0,0,0,0,0,0,0,0,0,0,0,0,0,0]);
 let required=if phase==9{0x00880000}else{0x00080000};
 if !(8..=10).contains(&phase)||word(component,0x14)?&required!=required{return None;}
 let index_gun=word(map,13*4)?;let index_head=word(map,14*4)?;
 if index_gun>=1024||index_head>=1024||index_gun==index_head{return None;}
 let buffer=word(component,if phase==9{0xac8}else{0xac4})? as usize;
 for i in [index_gun,index_head]{if !readable(buffer+i as usize*48,48){return None;}}
 let bone:unsafe extern "thiscall" fn(usize,u32)->usize=std::mem::transmute(0x1ce0670usize);
 let read_pose=|index|->Option<Pose>{
  let p=bone(component,index);if !readable(p,48){return None;}
  let values=std::slice::from_raw_parts(p as *const f32,7);
  Pose::checked([values[4] as f64,values[5] as f64,values[6] as f64],
   [values[0] as f64,values[1] as f64,values[2] as f64,values[3] as f64])
 };
 let gun=read_pose(index_gun)?;let head=read_pose(index_head)?.native_head_camera();Some((head,gun))
}
unsafe fn actor_pose(actor:usize)->Option<Pose>{
 let mut p=[0.;3];let mut r=[0;3];for i in 0..3{p[i]=f32::from_bits(word(actor,0x9c+i*4)?) as f64;r[i]=word(actor,0xa8+i*4)? as i32;}
 if p.iter().any(|v|!v.is_finite()){return None;}Some(Pose::from_rotator(p,r))
}
fn clear(){if let Ok(mut f)=FOLLOW.try_lock(){f.clear();}}
fn invalidate(core:usize){if let Ok(mut f)=FOLLOW.try_lock(){f.invalidate(core);}}
fn place(core:usize){unsafe{
 #[cfg(feature="scis-runtime-diagnostics")] let mut observer=crate::scis_event_observer::begin_ads(core);
 Place.call(core);
 #[cfg(feature="scis-runtime-diagnostics")] if let Some(ref mut observer)=observer{observer.native_placed();}
 // The paired core already sampled breathing before native deprojection.
 // Do not add a second late actor displacement after the shot ray is fixed.
 if scope_motion_enabled(){return;}
 diag::hit(diag::SWAY_ENTER);
 if !READY.load(Ordering::Acquire){return;}
 let Some((key,hands,scope,pawn))=inspect(core,|p,o|word(p,o))else{diag::hit(diag::SWAY_INSPECT_REJECT);invalidate(core);return;};
 let node=word(scope,0x30c).unwrap_or(0) as usize;
 if !super::owns_scope_node(node){diag::hit(diag::SWAY_LEASE_REJECT);invalidate(core);return;}
 let Some((head,weapon))=bone_pair(hands)else{diag::hit(diag::SWAY_BONES_REJECT);invalidate(core);return;};
 let Some(native)=actor_pose(scope)else{diag::hit(diag::SWAY_POSE_REJECT);invalidate(core);return;};
 // Native getters can refresh a palette; retain no pointers across that call.
 if inspect(core,|p,o|word(p,o)).map(|v|v.0)!=Some(key){diag::hit(diag::SWAY_RECHECK_REJECT);invalidate(core);return;}
 let steady=word(core,0x470).is_some_and(|v|v&0x7fffffff==0)
  &&word(core,0x474).is_some_and(|v|v&0x7fffffff==0);
 let result=FOLLOW.try_lock().ok().and_then(|mut f|f.update(key,head,weapon,native,steady));
 let Some(result)=result else{diag::hit(diag::SWAY_ANCHOR_OR_LOCK);return;};
 diag::sample(diag::SWAY_POSES,||[head.p[0] as f32,head.p[1] as f32,head.p[2] as f32,weapon.p[0] as f32,weapon.p[1] as f32,weapon.p[2] as f32,native.p[0] as f32,native.p[1] as f32,native.p[2] as f32,result.p[0] as f32,result.p[1] as f32,result.p[2] as f32,0.,0.,0.,0.].map(f32::to_bits));
 if result.p.iter().zip(native.p).all(|(a,b)|(a-b).abs()<0.00001)
  &&result.rotator()==native.rotator(){diag::hit(diag::SWAY_UNCHANGED);return;}
 let Some(world)=word(pawn,0x148).map(|v|v as usize)else{return;};
 let Some(table)=word(world,0).map(|v|v as usize)else{return;};
 let Some(entry)=word(table,0x88).filter(|v|*v>=0x400000&&*v<0x2990000)else{return;};
 let position=result.p.map(|v|v as f32);let rotation=result.rotator();
 let move_actor:unsafe extern "thiscall" fn(usize,usize,*const f32,*const i32,u32)->u32=std::mem::transmute(entry as usize);
 move_actor(world,scope,position.as_ptr(),rotation.as_ptr(),0);diag::hit(diag::SWAY_APPLIED);
 if LOGS.fetch_add(1,Ordering::Relaxed)<2{tracing::info!("SCBL_MOD_SCIS_IDLE native_bone=13 camera_bone=14 scope_only=true input_unchanged=true");}
}}
fn native_signatures_match(check:impl Fn(usize,usize,&str)->bool)->bool {
 SIGNATURES.iter().all(|&(p,n,h)|check(p,n,h))
}
pub(super) unsafe fn init()->bool{
 if !native_signatures_match(|p,n,h|signature(p,n,h)){return false;}
 if Place.initialize(std::mem::transmute::<usize,unsafe extern "thiscall" fn(usize)>(0xd60d40),place).is_err()
  ||Place.enable().is_err(){deinit();return false;}
 READY.store(true,Ordering::Release);true
}
pub(super) unsafe fn deinit(){scope_motion_enable(0);READY.store(false,Ordering::Release);if Place.is_enabled(){let _=Place.disable();}clear();}
const SIGNATURES:&[(usize,usize,&str)]=&[
    // Core owns the rel32 operands at AFA833/AFA8A9/AFA8F3. Their E8
    // opcodes and every bone-index/sign/axis instruction remain pinned.
    (0xafa790,163,"0e0dfa8c1e2875e4a06a761429b6e76b15f0a8cf196fd7f820819364201ab634"),
    (0xafa837,114,"a57cdc674bbff0b2a84ac8584a196e6be8c61422800ecd631a0c154444c021ef"),
    (0xafa8ad,70,"262f0fd65d683f2259a58257c82e650f2f93ef416d4ac0c124ee035c3408c36e"),
    (0xafa8f7,89,"5732c79c0c11219b62b0a64f90386f5676654408853fac4349eed94b9ceecae3"),
    (0xbb9740,481,"c7816706766cf02cac59a9ce07b7349753adbb922b1a5ea845deaf695fe7b710"),
    (0xb92be0,92,"d8fcdfd5bc6502bb720fac9d464c11406fc9d10080641fce91359cf2db3628e2"),
    (0xd60d40,1950,"11dd021c5ec5a3fc49074cbc50caaceb9999d0014f0edb8e5544097edd539047"),
    (0x1ce0670,88,"bb922658b5cfab5cf9f41b4e5688812bb61228e355f46fbf3b380072cfe7c738"),
    (0xad7af0,19,"a17cc550f22130bb259f02e1f9a48bc042e3596eed6b7bdb5f8a1c59a99a827a"),
    (0xb577d0,6,"0bec1e26f3d1a85d355f632d926209944d24832c42fe21c886c4a817fd6f8389"),
    (0x2a86234,4,"662bdbc21f8fea1fd2882cdf5d3b5bed8c1bf333afce772d18f688063617c34c"),
    (0xdc2d19,33,"db7d27bcdfd0cc44a775f083d6ab9d5dbd2aeadca1a49f410a41dd9ba5a229fe"),
    (0xdc4cab,33,"fe7c20010cb7cd9ac4dfa14596713a1e858387dad4dc37d163c5c1d89db6a524"),
];
#[cfg(test)] mod tests{
 use super::*;use std::collections::HashMap;
 #[test] fn scis_idle_requires_owned_camera_and_captured_steady_descriptor(){
  let (c,p,pc,cam,h,g,s,w,a)=(0x10000,0x20000,0x30000,0x40000,0x50000,0x60000,0x70000,0x80000,0x90000);
  let mut m=HashMap::from([((c,0x470),0),((c,0x474),0),((c,0x24),p as u32),((c,0x20),pc as u32),((c,0x1c),cam as u32),((p,0x12ac),h as u32),((p,0x4b8),g as u32),((p,0x1508),s as u32),((p,0x144),w as u32),((p,0x1510),0),((p,0x20),1),((pc,0x520),p as u32),((pc,0x6a4),cam as u32),((cam,0x61c),c as u32),((h,0),0x2a23634),((h,0x310),p as u32),((h,0x144),w as u32),((h,0x318),0),((h,0x31c),a as u32),((h,0x20),2),((g,0),0x2a85b7c),((g,0x8c),p as u32),((g,0x20),3),((s,0x20),4),((a,0),0x2a3ecfc),((a,0x30),0x23c),((a,0x34),1),((a,0x50),0)]);
  m.insert((p,0x948),0x400000);m.insert((c,0x46c),1.0f32.to_bits());
  let check=|m:&HashMap<_,_>|inspect(c,|p,o|m.get(&(p,o)).copied());assert!(check(&m).is_some());
  m.insert((p,0x948),0);assert!(check(&m).is_none());m.insert((p,0x948),0x400000);
  m.insert((p,0x1510),s as u32);assert!(check(&m).is_some(),"native ADS caches the owned scope");
  m.insert((a,0x34),3);m.insert((a,0x50),0x23c);m.insert((a,0x54),1);
  assert!(check(&m).is_some(),"native idle queued behind idle must not pause/rebase breathing");
  for (key,v) in [((a,0x30),0x23e),((a,0x50),0x23e),((a,0x54),3)] {
   let old=m.insert(key,v).unwrap();assert!(check(&m).is_none(),"shooting or non-idle queue must remain excluded");m.insert(key,old);
  }
  m.insert((a,0x34),1);m.insert((a,0x50),0);m.remove(&(a,0x54));
  for axis in [0x470,0x474]{m.insert((c,axis),0.0075f32.to_bits());assert!(check(&m).is_some(),"mouse reports retain the owned optic");m.insert((c,axis),0);}
  // Paired aim holds its last idle offset during native shot/reload states;
  // eligibility still requires a fully aimed, generation-matched owned scope.
  for (key,v) in [((a,0x30),0x23e),((a,0x50),0x23e),((a,0x34),3)] {
   let old=m.insert(key,v).unwrap();assert!(check(&m).is_none());
   assert!(inspect_motion(c,false,|p,o|m.get(&(p,o)).copied()).is_some());m.insert(key,old);
  }
  for (key,v) in [((c,0x46c),0.99f32.to_bits()),((c,0x470),f32::NAN.to_bits()),((pc,0x520),0),((p,0x1510),s as u32+4)] {
   let old=m.insert(key,v).unwrap();assert!(inspect_motion(c,false,|p,o|m.get(&(p,o)).copied()).is_none());m.insert(key,old);
  }
  for key in m.keys().copied().collect::<Vec<_>>(){let old=m.remove(&key).unwrap();assert!(check(&m).is_none(),"missing {key:?}");m.insert(key,old);}
  for (key,v) in [((c,0x46c),0.99f32.to_bits()),((c,0x470),f32::NAN.to_bits()),((c,0x474),f32::INFINITY.to_bits()),((a,0x30),0x23e),((a,0x50),0x23e),((a,0x34),3),((h,0x318),2),((pc,0x520),0),((p,0x1510),s as u32+4)]{let old=m.insert(key,v).unwrap();assert!(check(&m).is_none());m.insert(key,old);}
 }
}

pub(crate) fn scope_motion_enabled()->bool{PAIRED.load(Ordering::Acquire)&&READY.load(Ordering::Acquire)}
pub(crate) fn scope_motion_enable(abi:u32)->bool{
 let enabled=abi==hooks_plugin_api::scope_motion::ABI&&READY.load(Ordering::Acquire);
 PAIRED.store(false,Ordering::Release);clear();if let Ok(mut f)=MOTION.lock(){f.clear();}
 PAIRED.store(enabled,Ordering::Release);enabled
}
pub(crate) unsafe fn scope_motion_query(r:hooks_plugin_api::scope_motion::Request)->Option<[f32;2]>{scope_motion_query_phase(r,true)}
// Preview queries keep the last authoritative anchor, including on rejection.
// Only the pinned post-hands caller may commit lifecycle or idle-pose state.
pub(crate) unsafe fn scope_motion_query_phase(r:hooks_plugin_api::scope_motion::Request,post:bool)->Option<[f32;2]>{
 #[cfg(feature="scis-runtime-diagnostics")] let mut observed=crate::scis_event_observer::Motion::new(r,post);
 if !scope_motion_enabled()||!r.valid(){return None;}
 let core=r.core as usize;
 let invalidate=||{if post{if let Ok(mut f)=MOTION.try_lock(){f.invalidate(core);}}None};
 #[cfg(feature="scis-runtime-diagnostics")] observed.stage(1);
 let Some((key,hands,scope,pawn))=inspect_motion(core,false,|p,o|word(p,o))else{return invalidate();};
 #[cfg(feature="scis-runtime-diagnostics")] observed.stage(2);
 if key.0[0]!=r.world as usize||pawn!=r.pawn as usize||key.0[2]!=r.pawn_generation as usize
  ||word(core,0x20)!=Some(r.controller)||word(core,0x1c)!=Some(r.camera)||word(core,0x2c)!=Some(r.mode)
  ||word(r.controller as usize,0x20)!=Some(r.controller_generation)
  ||!super::owns_scope_node(word(scope,0x30c)? as usize){return invalidate();}
 #[cfg(feature="scis-runtime-diagnostics")] observed.stage(3);
 let idle=inspect(core,|p,o|word(p,o)).is_some();
 let bones=if idle{bone_pair(hands)}else{None};
 #[cfg(feature="scis-runtime-diagnostics")] observed.bones(idle,bones.is_some());
 // Copy only already-consumed poses; no additional getter or palette refresh.
 #[cfg(feature="scis-runtime-diagnostics")] observed.consumed(bones.map(|(head,gun)|{
  [head.q[0],head.q[1],head.q[2],head.q[3],head.p[0],head.p[1],head.p[2],
   gun.q[0],gun.q[1],gun.q[2],gun.q[3],gun.p[0],gun.p[1],gun.p[2]]
 }));
 #[cfg(feature="scis-runtime-diagnostics")] observed.stage(4);
 // A palette refresh must not carry an old pawn's state into its successor.
 if inspect_motion(core,false,|p,o|word(p,o)).map(|v|v.0)!=Some(key){return invalidate();}
 let frame=r.frame[0]as u64|((r.frame[1]as u64)<<32);
 #[cfg(feature="scis-runtime-diagnostics")] observed.stage(5);
 let mut motion=MOTION.try_lock().ok()?;
 let now=MOTION_CLOCK.get_or_init(Instant::now).elapsed().as_secs_f64();
 #[cfg(feature="scis-runtime-diagnostics")] observed.stage(6);
 let result=if post{motion.update_at(key,frame,bones,now)}else{motion.preview_at(key,frame,bones,now)}?;
 let result=result.map(|v|v as f32);
 #[cfg(feature="scis-runtime-diagnostics")] observed.accepted(result);
 Some(result)
}
