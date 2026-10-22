//! Propagate native weapon-bone motion into the separate BasicScope actor.
//! No oscillator, input filtering, scene-camera or FOV mutation.
//! A monotonic clock is used only to retire the finite non-idle recovery correction.
#[derive(Clone,Copy,Debug,PartialEq)]
pub(super) struct Pose { pub p:[f64;3], pub q:[f64;4] }
fn add(a:[f64;3],b:[f64;3])->[f64;3]{std::array::from_fn(|i|a[i]+b[i])}
fn neg(a:[f64;3])->[f64;3]{a.map(|v|-v)}
fn cross(a:[f64;3],b:[f64;3])->[f64;3]{[a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0]]}
fn mul(a:[f64;4],b:[f64;4])->[f64;4]{
 [a[3]*b[0]+a[0]*b[3]+a[1]*b[2]-a[2]*b[1],a[3]*b[1]-a[0]*b[2]+a[1]*b[3]+a[2]*b[0],
  a[3]*b[2]+a[0]*b[1]-a[1]*b[0]+a[2]*b[3],a[3]*b[3]-a[0]*b[0]-a[1]*b[1]-a[2]*b[2]]
}
fn rotate(q:[f64;4],v:[f64;3])->[f64;3]{
 let a=[q[0],q[1],q[2]];let t=cross(a,v).map(|v|v*2.0);
 add(add(v,t.map(|v|v*q[3])),cross(a,t))
}
impl Pose {
 pub fn checked(p:[f64;3],q:[f64;4])->Option<Self>{
  let n=q.iter().map(|v|v*v).sum::<f64>();
  if p.iter().chain(q.iter()).any(|v|!v.is_finite())||!(0.9..=1.1).contains(&n){return None;}
  Some(Self{p,q:q.map(|v|v/n.sqrt())})
 }
 /// Retail AFA790 constructs view axes as (-head Z, head X, -head Y).
 /// Bone 14 is not a camera-basis quaternion. Normalize its fixed anatomical
 /// basis before a virtual optic lever is interpreted as forward/right/up.
 /// This is a rigid coordinate conversion, not a new sway waveform or gain.
 pub fn native_head_camera(self)->Self {
  self.compose(Self{p:[0.;3],q:[0.5,0.5,-0.5,0.5]})
 }
 pub fn compose(self,b:Self)->Self{Self{p:add(self.p,rotate(self.q,b.p)),q:mul(self.q,b.q)}}
 pub fn inverse(self)->Self{let q=[-self.q[0],-self.q[1],-self.q[2],self.q[3]];Self{p:rotate(q,neg(self.p)),q}}
 pub fn from_rotator(p:[f64;3],r:[i32;3])->Self{
  let k=std::f64::consts::PI/65536.0;
  let (sp,cp)=(r[0] as f64*k).sin_cos();let (sy,cy)=(r[1] as f64*k).sin_cos();let (sr,cr)=(r[2] as f64*k).sin_cos();
  Self{p,q:[cr*sp*sy-sr*cp*cy,-cr*sp*cy-sr*cp*sy,cr*cp*sy-sr*sp*cy,cr*cp*cy+sr*sp*sy]}
 }
 pub fn rotator(self)->[i32;3]{
  let [x,y,z,w]=self.q;let k=65536.0/std::f64::consts::TAU;
  [(2.0*(z*x-w*y)).clamp(-1.0,1.0).asin(),(2.0*(w*z+x*y)).atan2(1.0-2.0*(y*y+z*z)),
   (-2.0*(w*x+y*z)).atan2(1.0-2.0*(x*x+y*y))].map(|v|(v*k).round() as i32)
 }
}
#[derive(Clone,Copy,Debug,PartialEq,Eq)]
pub(super) struct Owner(pub [usize;10]);
// The optic is ~8 native units from the camera. UNIFIED3 applied captured
// FP-bone offsets exceeding 3 units at full strength, moving the rim out of view.
// Attenuate native motion with smooth bounds, not a mouse-input filter.
fn subtle(native:Pose, animated:Pose)->Pose {
 let d=std::array::from_fn::<_,3,_>(|i|(animated.p[i]-native.p[i])*0.15);
 let length=d.iter().map(|v|v*v).sum::<f64>().sqrt();
 let scale=1.0/(1.0+(length/0.06).powi(2)).sqrt();
 let mut q=mul(animated.q,[-native.q[0],-native.q[1],-native.q[2],native.q[3]]);
 if q[3]<0.0 {q=q.map(|v|-v);}
 let sine=q[..3].iter().map(|v|v*v).sum::<f64>().sqrt();
 if sine>1e-12 {
  let half=sine.atan2(q[3])*0.15;
  let cap=0.12f64.to_radians()*0.5;
  let bounded=half/(1.0+(half/cap).powi(2)).sqrt();
  let gain=bounded.sin()/sine;
  q=[q[0]*gain,q[1]*gain,q[2]*gain,bounded.cos()];
 } else {q=[0.,0.,0.,1.];}
 Pose{p:std::array::from_fn(|i|native.p[i]+d[i]*scale),q:mul(q,native.q)}
}
#[derive(Default,Clone)]
pub(super) struct Follow {
 anchor:Option<(Owner,Pose)>,
 // Unattenuated motion in native-scope coordinates, not a world-space pose.
 // Reapply to the current placement so moving the mouse never freezes the gun.
 held:Option<Pose>, paused:bool,
}
impl Follow {
 pub const fn new()->Self{Self{anchor:None,held:None,paused:false}}
 pub fn clear(&mut self){self.anchor=None;self.held=None;self.paused=false;}
 pub fn invalidate(&mut self,core:usize){if self.anchor.is_some_and(|(k,_)|k.0[9]==core){self.clear();}}
 pub fn update(&mut self,key:Owner,head:Pose,weapon:Pose,native:Pose,steady:bool)->Option<Pose>{
  if self.anchor.is_some_and(|(old,_)|old!=key){self.clear();}
  if !steady {
   self.paused=true;
   return self.held.map(|held|subtle(native,native.compose(held)));
  }
  let relative=head.inverse().compose(weapon);
  // Safety bounds reject unrelated/invalid poses, not a requested sway amplitude.
  if relative.p.iter().any(|v|v.abs()>200.0){self.clear();return None;}
  if self.paused {
   self.paused=false;
   if let Some(held)=self.held {
    // Resume from the last displayed offset, not the input-displaced bone.
    // The next true idle delta continues from here with no report-gap snap.
    let local=head.inverse().compose(native);
    let delta=local.compose(held).compose(local.inverse());
    self.anchor=Some((key,delta.inverse().compose(relative)));
   }
  }
  let baseline=match self.anchor {Some((old,b)) if old==key=>b,_=>{self.anchor=Some((key,relative));return None;}};
  let delta=relative.compose(baseline.inverse());
  // Recoil/reload/teleport must never be reinterpreted as idle breathing.
  if delta.p.iter().any(|v|v.abs()>4.0)||delta.q[3].abs()<0.9990482216 {self.clear();return None;}
  let result=head.compose(delta).compose(head.inverse().compose(native));
  self.held=Some(native.inverse().compose(result));
  let result=subtle(native,result);
  Pose::checked(result.p,result.q)
 }
}
#[cfg(test)] mod tests{
 use super::*;
 fn pose(p:[f64;3])->Pose{Pose{p,q:[0.,0.,0.,1.]}}
 fn key()->Owner{Owner([1,2,3,4,5,6,7,8,9,10])}
 fn close(a:Pose,b:Pose){for i in 0..3{assert!((a.p[i]-b.p[i]).abs()<1e-7,"{a:?} vs {b:?}");}let dot=a.q.iter().zip(b.q).map(|(a,b)|a*b).sum::<f64>().abs();assert!((dot-1.).abs()<1e-7);}
 #[test] fn scis_idle_propagates_native_bone_motion_not_elapsed_time(){
  let mut f=Follow::default();let h=pose([100.,20.,30.]);let w=pose([104.,22.,24.]);let s=pose([107.,20.,30.]);
  assert!(f.update(key(),h,w,s,true).is_none());let moved=pose([104.,22.,24.04]);
  let expected=subtle(s,pose([107.,20.,30.04]));
  close(f.update(key(),h,moved,s,true).unwrap(),expected);
  for _ in 0..500{close(f.update(key(),h,moved,s,true).unwrap(),expected);}
 }
 #[test] fn scis_idle_removes_whole_camera_motion_and_does_not_accumulate(){
  let mut f=Follow::default();let h=pose([10.,20.,30.]);let w=pose([13.,19.,22.]);let s=pose([17.,20.,30.]);f.update(key(),h,w,s,true);
  let global=Pose::from_rotator([2000.,-4000.,50.],[1000,27000,-300]);
  close(f.update(key(),global.compose(h),global.compose(w),global.compose(s),true).unwrap(),global.compose(s));
 }
 #[test] fn scis_idle_reset_on_every_identity_exit_and_explicit_invalidation(){
  for i in 0..10{let mut f=Follow::default();let h=pose([0.;3]);let w=pose([2.,1.,0.]);let s=pose([8.,0.,0.]);f.update(key(),h,w,s,true);
   let mut k=key();k.0[i]+=100;assert!(f.update(k,h,w,s,true).is_none());}
  let mut f=Follow::default();let p=pose([0.;3]);f.update(key(),p,p,p,true);f.clear();assert!(f.update(key(),p,p,p,true).is_none());
 }
 #[test] fn scis_idle_rejects_wrong_pose_jumps_not_clipping_them(){
  let mut f=Follow::default();let p=pose([0.;3]);f.update(key(),p,p,p,true);assert!(f.update(key(),p,pose([8.,0.,0.]),p,true).is_none());
  assert!(f.update(key(),p,p,p,true).is_none());assert!(Pose::checked([f64::NAN,0.,0.],[0.,0.,0.,1.]).is_none());
 }
 #[test] fn scis_idle_foreign_camera_failure_does_not_reset_local_animation(){
  let mut f=Follow::default();let h=pose([0.;3]);let w=pose([2.,0.,0.]);let s=pose([8.,0.,0.]);f.update(key(),h,w,s,true);
  f.invalidate(999);assert!(f.update(key(),h,pose([2.,0.,0.02]),s,true).is_some());
  f.invalidate(key().0[9]);assert!(f.update(key(),h,w,s,true).is_none());
 }
 #[test] fn scis_idle_report_gaps_hold_offset_in_current_scope_not_world(){
  let h=pose([0.;3]);let w=pose([3.,-2.,-17.]);let s=pose([8.,0.,0.]);
  let mut f=Follow::new();f.update(key(),h,w,s,true);
  let moved=pose([3.,-2.,-16.96]);let before=f.update(key(),h,moved,s,true).unwrap();
  let global=Pose::from_rotator([2000.,-1000.,30.],[200,9000,-50]);
  let polluted=pose([3.,-2.,-16.5]);
  close(f.update(key(),global.compose(h),global.compose(polluted),global.compose(s),false).unwrap(),global.compose(before));
  close(f.update(key(),global.compose(h),global.compose(polluted),global.compose(s),true).unwrap(),global.compose(before));
  f.invalidate(key().0[9]);assert!(f.update(key(),h,w,s,false).is_none());
 }
 #[test] fn scis_idle_rotator_basis_and_roundtrip(){
  for r in [[0,0,0],[500,15000,200],[-700,31000,-500],[12000,-30000,400]]{
   let p=Pose::from_rotator([0.;3],r);let out=p.rotator();for i in 0..3{assert!((out[i]-r[i]).abs()<=1,"{r:?} {out:?}");}
  }
  let pitch=Pose::from_rotator([0.;3],[16384,0,0]);let forward=rotate(pitch.q,[1.,0.,0.]);assert!((forward[2]-1.).abs()<1e-9);
  let yaw=Pose::from_rotator([0.;3],[0,16384,0]);assert!((rotate(yaw.q,[1.,0.,0.])[1]-1.).abs()<1e-9);
 }
 #[test] fn scis_idle_transfers_rotation_about_weapon_without_scene_or_fov(){
  let mut f=Follow::default();let h=pose([0.;3]);let w=pose([1.,0.,0.]);let s=pose([8.,0.,0.]);f.update(key(),h,w,s,true);
  let d=Pose::from_rotator([0.;3],[0,50,0]);let result=f.update(key(),h,d.compose(w),s,true).unwrap();close(result,subtle(s,d.compose(s)));
 }
 #[test] fn scis_idle_bounds_large_captured_delta_without_deadzone_or_accumulation(){
  let h=pose([0.;3]);let w=pose([3.,-2.,-17.]);let s=pose([8.,0.,0.]);
  let mut f=Follow::default();f.update(key(),h,w,s,true);
  for delta in [0.00001,0.01,0.1,1.0,3.5] {
   let moved=pose([w.p[0],w.p[1]+delta,w.p[2]]);
   let out=f.update(key(),h,moved,s,true).unwrap();
   let distance=out.p.iter().zip(s.p).map(|(a,b)|(a-b).powi(2)).sum::<f64>().sqrt();
   assert!(distance>0.0 && distance<0.06);
   close(f.update(key(),h,moved,s,true).unwrap(),out);
  }
 }
}

/// Angular native idle state, independent of zoom and world camera publication.
/// Both pre/post-hands aim calls may sample a frame: poses replace the result,
/// never integrate twice. A non-idle animation holds the last offset without
/// rebasing the idle calibration: per-shot anchors otherwise accumulate a DC
/// offset until the nonlinear safety bounds compress real breathing to zero.
#[derive(Default,Clone)]
pub(super) struct AimMotion {follow:Follow,owner:Option<Owner>,frame:u64,last:[f64;2],resume:Resume}
/// A finite return-to-idle correction, not an idle oscillator or mouse filter.
/// `shown` is the last authoritative post-query output, NOT a render receipt.
/// Fixed native calibration and phase continue independently in AimMotion.
#[derive(Default,Clone)]
struct Resume {
 shown:[f64;2], clock:Option<(u64,f64)>, pending:bool,
 correction:Option<(f64,[f64;2])>,
}
impl Resume {
 const SECONDS:f64=0.12;
 const FIRST_RATE:f64=45.;
 const fn new()->Self{Self{shown:[0.;2],clock:None,pending:false,correction:None}}
 fn apply(&mut self,frame:u64,now:f64,raw:[f64;2],idle:bool)->Option<[f64;2]>{
  if !now.is_finite()||now<0. {return None;}
  let (now,dt)=match self.clock {
   Some((old_frame,old)) if frame==old_frame=>(old,0.),
   Some((_,old)) if now<old=>return None,
   Some((_,old))=>(now,(now-old).min(1./30.)),
   None=>(now,0.),
  };
  // Include every authoritative non-idle query: a long shot is not the dt of
  // the first resumed frame. A low frame rate/long stall still cannot teleport.
  self.clock=Some((frame,now));
  if !idle {self.pending=true;return Some(self.shown);}
  if self.pending {
   self.pending=false;
   let delta=std::array::from_fn::<_,2,_>(|i|raw[i]-self.shown[i]);
   let length=delta.iter().map(|v|v*v).sum::<f64>().sqrt();
   let allowed=Self::FIRST_RATE*dt;
   if length<=allowed {self.shown=raw;self.correction=None;}
   else {
    self.shown=std::array::from_fn(|i|self.shown[i]+delta[i]*allowed/length);
    self.correction=Some((now,self.shown));
   }
   // Every gap, even one during an existing return, starts from current shown.
   // A finite nonzero first step prevents alternating idle/non-idle freezing.
   return Some(self.shown);
  }
  self.shown=if let Some((start,from))=self.correction {
   let u=((now-start)/Self::SECONDS).clamp(0.,1.);
   if u>=1. {self.correction=None;raw}
   else if u==0. {from}
   else {
    let remaining=1.-u*u*(3.-2.*u);
    // Convex output remains inside the existing native/protocol bounds.
    // Adding a fixed offset to an advancing raw phase can exceed 22 and be
    // rejected by the real V2 export, or accumulate during rapid shots.
    std::array::from_fn(|i|from[i]*remaining+raw[i]*(1.-remaining))
   }
  }else{raw};
  // Only the first resumed step has the 1.5-rotator bound. Subsequent samples
  // preserve native idle motion while removing the offset within 120ms. Do not
  // chase a moving target indefinitely or shrink the already-correct amplitude.
  Some(self.shown)
 }
}
impl AimMotion {
 pub fn preview_at(&self,key:Owner,frame:u64,bones:Option<(Pose,Pose)>,now:f64)->Option<[f64;2]>{
  self.clone().update_at(key,frame,bones,now)
 }
 pub fn update_at(&mut self,key:Owner,frame:u64,bones:Option<(Pose,Pose)>,now:f64)->Option<[f64;2]>{
  let raw=self.update(key,frame,bones)?;
  let output=self.resume.apply(frame,now,raw,bones.is_some());
  if output.is_none(){self.clear();}
  output
 }
    /// Evaluate an earlier/native query without establishing or invalidating the
    /// authoritative post-hands reference. The full delta is still returned.
    pub fn preview(&self,key:Owner,frame:u64,bones:Option<(Pose,Pose)>)->Option<[f64;2]>{
        self.clone().update(key,frame,bones)
    }

 pub const fn new()->Self{Self{follow:Follow::new(),owner:None,frame:0,last:[0.;2],resume:Resume::new()}}
 pub fn clear(&mut self){*self=Self::new();}
 pub fn invalidate(&mut self,core:usize){if self.owner.is_some_and(|o|o.0[9]==core){self.clear();}}
 pub fn update(&mut self,key:Owner,frame:u64,bones:Option<(Pose,Pose)>)->Option<[f64;2]>{
  if frame==0{return None;}
  if self.owner!=Some(key){self.clear();self.owner=Some(key);}
  if frame<self.frame {self.clear();return None;}self.frame=frame;
  // A shot/reload is not a change of the idle reference. Reusing Follow's
  // mouse-lag rebase here accumulates every resumed native phase into anchor.
  // Keep the last output while non-idle; then resume the actual native pose.
  let Some((head,weapon))=bones else{return Some(self.last);};
  // Eight native units is a fixed virtual optic lever, not the current zoom.
  // Removing the common head transform avoids interpreting mouse rotation as idle.
  let native=head.compose(Pose{p:[8.,0.,0.],q:[0.,0.,0.,1.]});
  let Some(animated)=self.follow.update(key,head,weapon,native,true)else{return Some(self.last);};
  let p=head.inverse().compose(animated).p;
  if !p.into_iter().all(f64::is_finite)||p[0]<=0.{self.clear();return None;}
  let radians=[p[2].atan2(p[0].hypot(p[1])),p[1].atan2(p[0])];
  let cap=0.12f64.to_radians();
  self.last=radians.map(|v|v/(1.+(v/cap).powi(2)).sqrt()*65536./std::f64::consts::TAU);
  Some(self.last)
 }
}
#[cfg(test)] mod aim_motion_tests {
 use super::*;
 fn p(z:f64)->Pose{Pose{p:[3.,-2.,z],q:[0.,0.,0.,1.]}}
 fn h()->Pose{Pose{p:[0.;3],q:[0.,0.,0.,1.]}}
 fn key()->Owner{Owner([1,2,3,4,5,6,7,8,9,10])}
 #[test] fn native_idle_reaches_shot_without_fov_or_duplicate_call_accumulation(){
  let mut m=AimMotion::new();assert_eq!(m.update(key(),1,Some((h(),p(-17.)))),Some([0.;2]));
  let a=m.update(key(),2,Some((h(),p(-16.96)))).unwrap();assert!(a[0]>0.01&&a[0]<22.);
  for _ in 0..20{assert_eq!(m.update(key(),2,Some((h(),p(-16.96)))),Some(a));}
  assert_eq!(m.update(key(),3,None),Some(a));
  let b=m.update(key(),4,Some((h(),p(-16.96)))).unwrap();
  for i in 0..2{assert!((a[i]-b[i]).abs()<1e-8);}
 }
 #[test] fn repeated_shots_preserve_idle_gain_center_and_native_phase(){
  let mut m=AimMotion::new();let mut frame=1;m.update(key(),frame,Some((h(),p(-17.))));
  let mut last=[0.;2];let mut first_span=0.;
  for shot in 0..25 {
   if shot>0 {frame+=1;assert_eq!(m.update(key(),frame,None),Some(last));}
   let mut low=f64::INFINITY;let mut high=f64::NEG_INFINITY;
   for z in [-17.04,-17.,-16.96,-17.,-17.04,-17.,-16.96] {
    frame+=1;last=m.update(key(),frame,Some((h(),p(z)))).unwrap();
    low=low.min(last[0]);high=high.max(last[0]);
    assert!(last.into_iter().all(|v|v.abs()<=0.12*65536./360.));
    assert_eq!(m.update(key(),frame,Some((h(),p(z)))),Some(last));
   }
   let span=high-low;if shot==0{first_span=span;assert!(span>14.&&span<15.);}
   assert!((span-first_span).abs()<1e-7,"shot={shot} span={span} first={first_span}");
   assert!((high+low).abs()<1e-7,"native idle center drifted after shot {shot}");
  }
 }
 #[test] fn motion_cannot_survive_rebirth_or_late_frame(){
  let mut m=AimMotion::new();m.update(key(),10,Some((h(),p(-17.))));m.update(key(),11,Some((h(),p(-16.96))));
  let mut new=key();new.0[2]+=1;assert_eq!(m.update(new,12,Some((h(),p(-16.96)))),Some([0.;2]));
  assert!(m.update(new,11,None).is_none());
 }
 #[test] fn fractional_camera_motion_does_not_become_breathing(){
  let mut m=AimMotion::new();m.update(key(),1,Some((h(),p(-17.))));
  for i in 2..100{let g=Pose::from_rotator([5000.,-6000.,200.],[i*3,i*5,0]);
   let a=m.update(key(),i as u64,Some((g.compose(h()),g.compose(p(-17.))))).unwrap();
   assert!(a.into_iter().all(|v|v.abs()<1e-6));}
 }
}

#[cfg(test)] mod recovery_tests {
use super::*;

fn h()->Pose{Pose{p:[0.;3],q:[0.,0.,0.,1.]}}
fn p(z:f64)->Pose{Pose{p:[3.,-2.,-17.+z],q:[0.,0.,0.,1.]}}
fn k()->Owner{Owner([1,2,3,4,5,6,7,8,9,10])}
fn step(m:&mut AimMotion,key:Owner,frame:u64,bones:Option<(Pose,Pose)>,now:f64)->Option<[f64;2]>{m.update_at(key,frame,bones,now)}
fn preview(m:&AimMotion,key:Owner,frame:u64,bones:Option<(Pose,Pose)>,now:f64)->Option<[f64;2]>{m.preview_at(key,frame,bones,now)}
fn distance(a:[f64;2],b:[f64;2])->f64{((a[0]-b[0]).powi(2)+(a[1]-b[1]).powi(2)).sqrt()}
fn recovery(advanced:f64)->([f64;2],[f64;2]){
 let mut m=AimMotion::new();step(&mut m,k(),1,Some((h(),p(0.))),0.);
 let held=step(&mut m,k(),2,Some((h(),p(0.04))),0.1).unwrap();
 for f in 3..33{assert_eq!(step(&mut m,k(),f,None,0.1+(f-2)as f64/60.),Some(held));}
 let resumed=step(&mut m,k(),33,Some((h(),p(advanced))),0.7).unwrap();
 println!("RECOVERY native_phase_z={advanced} held={held:?} first_idle={resumed:?} jump_rotator={}",resumed[0]-held[0]);
 (held,resumed)
}
#[test]fn every_gap_first_step_is_bounded_including_110_and_120ms(){
 let mut maximum=0f64;
 for millis in 8..=120 {
  for frequent in [false,true] {
   let mut m=AimMotion::new();step(&mut m,k(),1,Some((h(),p(0.))),0.);
   step(&mut m,k(),2,Some((h(),p(0.04))),0.1);step(&mut m,k(),3,None,0.2);
   step(&mut m,k(),4,Some((h(),p(-0.04))),0.9);
   let held=step(&mut m,k(),5,Some((h(),p(-0.04))),0.94).unwrap();
   let mut f=6;step(&mut m,k(),f,None,1.);
   if frequent{for ms in 1..millis{f+=1;assert_eq!(step(&mut m,k(),f,None,1.+ms as f64/1000.),Some(held));}}
   f+=1;let next=step(&mut m,k(),f,Some((h(),p(0.04))),1.+millis as f64/1000.).unwrap();
   let jump=distance(next,held);maximum=maximum.max(jump);
   let dt=if frequent{0.001}else{millis as f64/1000.};
   assert!(jump<=45.*dt.min(1./30.)+1e-9,"gap={millis}ms frequent={frequent} jump={jump}");
   if [110,120].contains(&millis){println!("GATE_BOUNDARY gap_ms={millis} frequent={frequent} first_step={jump}");}
  }
 }
 println!("GAP_SCAN 8..120ms sparse_and_each_ms max_first_step={maximum}");
}
#[test]fn subsequent_idle_reaches_bit_exact_native_after_120ms_at_all_fps(){
 for fps in [30u64,60,144,240,1000] {
  let mut m=AimMotion::new();let mut reference=AimMotion::new();
  step(&mut m,k(),1,Some((h(),p(0.))),0.);reference.update(k(),1,Some((h(),p(0.))));
  let before=step(&mut m,k(),2,Some((h(),p(0.04))),0.1).unwrap();
  step(&mut m,k(),3,None,0.2);step(&mut m,k(),4,None,0.5);
  let resume=step(&mut m,k(),5,Some((h(),p(-0.04))),0.6).unwrap();
  assert!(distance(resume,before)<=1.5+1e-9);
  let mut frame=5;
  for i in 1..=(fps as f64*0.12).floor()as u64 {
   frame+=1;let last=step(&mut m,k(),frame,Some((h(),p(-0.04))),0.6+i as f64/fps as f64).unwrap();
   assert!(last[0]>=-before[0]-1e-7&&last[0]<=before[0]+1e-7);
  }
  frame+=1;let end=step(&mut m,k(),frame,Some((h(),p(-0.04))),0.7200000001).unwrap();
  assert_eq!(end,reference.update(k(),frame,Some((h(),p(-0.04)))).unwrap());
  println!("RECOVERY_REALTIME fps={fps} full_native_at_ms=120 first_step={}",distance(resume,before));
 }
}
#[test]fn continuous_but_sampled_nonintersecting_raw_still_finishes_recovery(){
 for fps in [30u64,60,144,240,1000] {
  let mut m=AimMotion::new();let mut reference=AimMotion::new();
  step(&mut m,k(),1,Some((h(),p(0.))),0.);reference.update(k(),1,Some((h(),p(0.))));
  step(&mut m,k(),2,None,1./fps as f64);
  // Linear native interpolation can cross the held output between every pair
  // of sampled poses without any sampled target entering the pursuit radius.
  let mut exact=0;
  for i in 1..=fps {
   let z=if i%2==0{-0.04}else{0.04};let bones=Some((h(),p(z)));
   let got=step(&mut m,k(),i+2,bones,(i+1)as f64/fps as f64).unwrap();
   let raw=reference.update(k(),i+2,bones).unwrap();
   if (i-1)as f64/fps as f64>=0.120000001{assert_eq!(got,raw,"finite correction must expire without requiring target intersection");exact+=1;}
  }
  assert!(exact>fps/2);println!("NONINTERSECTING fps={fps} bit_exact_native_samples_after120ms={exact}");
 }
}
#[test]fn twenty_four_shots_retain_fixed_anchor_and_full_idle_after_correction(){
 for fps in [30u64,60,144,240,1000] {
  let mut m=AimMotion::new();let mut reference=AimMotion::new();let mut f=1;
  step(&mut m,k(),f,Some((h(),p(0.))),0.);reference.update(k(),f,Some((h(),p(0.))));
  let mut previous=[0.;2];let mut first_span=None;
  for shot in 0..24 {
   let start=shot as f64*2.;f+=1;
   assert_eq!(step(&mut m,k(),f,None,start+0.1),Some(previous));
   f+=1;step(&mut m,k(),f,None,start+0.35);
   let mut lo=f64::INFINITY;let mut hi=f64::NEG_INFINITY;
   for i in 0..=fps {
    let t=i as f64/fps as f64;let phase=(std::f64::consts::TAU*t*4.).sin()*0.04;
    f+=1;let bones=Some((h(),p(phase)));let before=previous;
    previous=step(&mut m,k(),f,bones,start+0.4+t).unwrap();
    let raw=reference.update(k(),f,bones).unwrap();
    if i==0{assert!(distance(previous,before)<=1.5+1e-9);}
    if t>=0.13{assert_eq!(previous,raw,"shot={shot} fixed phase/amplitude changed");lo=lo.min(previous[0]);hi=hi.max(previous[0]);}
   }
   let span=hi-lo;if let Some(first)=first_span{assert!((span-first as f64).abs()<1e-7);}else{first_span=Some(span);assert!(span>14.);}
  }
  println!("RECOVERY_24_SHOTS fps={fps} idle_span={first_span:?} drift=0 bit_exact_after120ms");
 }
}
#[test]fn alternating_short_idle_gates_make_progress_not_permanent_hold(){
 for fps in [30u64,60,144,240,1000] {
  let mut m=AimMotion::new();let mut reference=AimMotion::new();
  step(&mut m,k(),1,Some((h(),p(0.))),0.);reference.update(k(),1,Some((h(),p(0.))));
  let raw=reference.update(k(),2,Some((h(),p(0.04)))).unwrap();let mut held=[0.;2];let mut moved=false;
  for i in 1..=fps*2 {
   let now=i as f64/fps as f64;let f=i+1;
   if i%2==1{assert_eq!(step(&mut m,k(),f,None,now),Some(held));continue;}
   let got=step(&mut m,k(),f,Some((h(),p(0.04))),now).unwrap();
   assert!(distance(got,held)<=45./fps as f64+1e-9);
   if distance(got,held)>1e-5{moved=true;}held=got;
  }
  assert!(moved);assert_eq!(held,raw,"alternating gate cannot hold forever fps={fps}");
 }
}
#[test]fn preview_duplicate_frames_and_lifecycle_do_not_advance_or_retain_recovery(){
 let mut m=AimMotion::new();step(&mut m,k(),1,Some((h(),p(0.))),0.);
 step(&mut m,k(),2,Some((h(),p(0.04))),0.1);step(&mut m,k(),3,None,0.2);
 let mut control=m.clone();
 for i in 0..10{let _=preview(&m,k(),4,Some((h(),p(-0.04))),0.4+i as f64/100.);}
 let a=step(&mut m,k(),4,Some((h(),p(-0.04))),0.5);
 assert_eq!(a,step(&mut control,k(),4,Some((h(),p(-0.04))),0.5));
 assert_eq!(a,step(&mut m,k(),4,Some((h(),p(-0.04))),10.));
 for slot in 0..10{let mut copy=m.clone();let mut reborn=k();reborn.0[slot]+=1;
  assert_eq!(step(&mut copy,reborn,5,Some((h(),p(-0.04))),0.6),Some([0.;2]));}
 m.invalidate(k().0[9]);assert_eq!(step(&mut m,k(),6,Some((h(),p(0.04))),0.7),Some([0.;2]));
 m.clear();assert_eq!(step(&mut m,k(),7,Some((h(),p(-0.04))),0.8),Some([0.;2]));
 assert!(step(&mut m,k(),6,None,0.9).is_none());
}
#[test]fn long_pause_caps_first_step_not_the_whole_gap(){
 let mut m=AimMotion::new();step(&mut m,k(),1,Some((h(),p(0.))),0.);
 let held=step(&mut m,k(),2,Some((h(),p(0.04))),0.1).unwrap();
 step(&mut m,k(),3,None,0.2);
 let next=step(&mut m,k(),4,Some((h(),p(-0.04))),100.).unwrap();assert!(distance(next,held)<=1.5+1e-9);
}
#[test]fn ordinary_idle_is_bit_exact_native_not_low_pass_filtered(){
 let mut m=AimMotion::new();let mut reference=AimMotion::new();
 for f in 1..1000{let z=(f as f64*1.234).sin()*0.04;
  let bones=Some((h(),p(z)));assert_eq!(step(&mut m,k(),f,bones,f as f64/60.),reference.update(k(),f,bones));}
}
#[test]fn same_pose_recovery_is_insufficient_control(){let(a,b)=recovery(0.04);assert_eq!(a,b);}
#[test]fn resumed_advanced_native_phase_first_step_has_explicit_bound(){
 let(a,b)=recovery(-0.04);assert!(distance(a,b)<=1.5+1e-9,"hold -> resumed idle first step unbounded; actual source fixture is not captured live recovery");
}

#[test]fn rapid_cross_phase_recoveries_stay_inside_existing_protocol_bound(){
 let mut m=AimMotion::new();let mut f=1;let mut now=0.;step(&mut m,k(),f,Some((h(),p(0.))),now);
 for shot in 0..50 {
  f+=1;now+=0.001;step(&mut m,k(),f,None,now);
  for i in [0,1,2,1,0,1,2] {
   f+=1;now+=0.001;let z=[-0.04,0.,0.04][i];let v=step(&mut m,k(),f,Some((h(),p(z))),now).unwrap();
   assert!(v.into_iter().all(|v|v.is_finite()&&v.abs()<=22.),"existing v2 export rejects this output shot={shot} {v:?}");
  }
 }
}

}

#[cfg(test)] mod native_camera_basis_tests {
 use super::*;
 fn close(a:[f64;3],b:[f64;3]){for i in 0..3{assert!((a[i]-b[i]).abs()<1e-9,"{a:?} {b:?}");}}
 #[test] fn native_head_view_axes_are_minus_z_x_minus_y_not_bone_xyz(){
  // AFA790 emits -Rotate(Z), Rotate(X), -Rotate(Y) into its view basis.
  for r in [[0,0,0],[-10000,19000,5000],[10000,-19000,-5000],[0,32760,0]] {
   let head=Pose::from_rotator([8000.,-9000.,600.],r);let camera=head.native_head_camera();
   for (input,expected) in [([1.,0.,0.],[0.,0.,-1.]),([0.,1.,0.],[1.,0.,0.]),([0.,0.,1.],[0.,-1.,0.])] {
    close(rotate(camera.q,input),rotate(head.q,expected));
   }
   assert_eq!(camera.p,head.p);assert!(Pose::checked(camera.p,camera.q).is_some());
  }
 }
 #[test] fn head_conversion_is_rigid_and_commutes_with_world_motion(){
  let head=Pose::from_rotator([1.,2.,3.],[100,15000,300]);
  let world=Pose::from_rotator([-9000.,8000.,600.],[-5000,-21000,9000]);
  let a=world.compose(head.native_head_camera());let b=world.compose(head).native_head_camera();
  close(a.p,b.p);for i in 0..4{assert!((a.q[i]-b.q[i]).abs()<1e-12);}
 }
}
