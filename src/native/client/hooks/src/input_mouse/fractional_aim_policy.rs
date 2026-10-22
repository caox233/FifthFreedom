//! Offline-tested aim-side policy. No patch installation and no global rotator hook.
//! Native adapter owns pointer validation, current-owner lookup and TLS scope.
use super::rotation_precision::{Owner, Snapshot};
pub(super) type Vec3 = [f32;3];
#[derive(Clone, Copy, Debug)]
pub(super) struct AimFrame { pub input:Snapshot, pub native_rot:[i32;3], pub direction:Vec3 }
#[derive(Clone, Copy, Debug)]
pub(super) struct CacheStamp { pub frame:AimFrame, pub camera:usize, pub actor:usize, pub point:Vec3 }
#[derive(Default)]
pub(super) struct AimCarry { frame:Option<AimFrame>, cache:Option<CacheStamp> }
fn same_vec(a:Vec3,b:Vec3)->bool {a.map(f32::to_bits)==b.map(f32::to_bits)}
impl AimCarry {
    pub fn clear(&mut self) {self.frame=None;self.cache=None;}
    /// Called after original 4F1300 returns; raw is its saved ECX input.
    /// scene_owner/frame must prove the deprojection used the same precise view.
    /// The adapter must never derive this vector by adding fraction to core+BD8.
    pub fn capture(&mut self, current:Option<Snapshot>, scene:Option<Snapshot>, raw:Vec3, native_rot:[i32;3]) -> bool {
        self.clear();
        let Some(input)=current else{return false};
        if scene!=Some(input) || !input.fraction.iter().all(|x|x.is_finite()) || !raw.iter().all(|x|x.is_finite()) {return false;}
        let len=raw.iter().map(|&x|f64::from(x).powi(2)).sum::<f64>().sqrt();
        if !len.is_finite() || len<1e-15 || len>1e15 {return false;}
        let direction=raw.map(|x|(f64::from(x)/len) as f32);
        self.frame=Some(AimFrame{input,native_rot,direction});true
    }
    /// Begin D8F02A -> D81180 once. Hold this immutable token in thread-local
    /// scope through both splices, preserving a nested caller's previous token.
    pub fn begin(&self, current:Option<Snapshot>, native_rot:[i32;3])->Option<AimFrame> {
        let f=self.frame?;if current!=Some(f.input)||native_rot!=f.native_rot{return None;}Some(f)
    }
    /// Publish only after both native producer splices and D81180 returned.
    pub fn finish(&mut self, frame:AimFrame, camera:usize, actor:usize, point:Vec3, both_splices:bool) {
        self.cache=if both_splices&&point.iter().all(|x|x.is_finite()) {Some(CacheStamp{frame,camera,actor,point})} else {None};
    }
    pub fn cache_matches(&self,current:Option<Snapshot>,rot:[i32;3],camera:usize,actor:usize,point:Vec3)->bool {
        let Some(f)=self.begin(current,rot)else{return false};let Some(c)=self.cache else{return false};
        c.frame.input==f.input&&c.frame.native_rot==rot&&c.camera==camera&&c.actor==actor&&same_vec(c.point,point)
    }
}
impl AimFrame {
    // D812F3 first executes original MOVSS [EBP-28],XMM0, then replaces only
    // local direction [-4C,-48,-44] and dot [-28]. Native origin selection stays.
    pub fn initial(&self,pawn:Vec3,origin:Vec3)->Option<(Vec3,f32)> {
        if !pawn.iter().chain(origin.iter()).all(|x|x.is_finite()){return None;}
        let delta=std::array::from_fn::<_,3,_>(|i|(f64::from(pawn[i])-f64::from(origin[i])) as f32);
        let dot=(f64::from(delta[0])*f64::from(self.direction[0])+f64::from(delta[1])*f64::from(self.direction[1])+f64::from(delta[2])*f64::from(self.direction[2])) as f32;
        dot.is_finite().then_some((self.direction,dot))
    }
    // D81530 first executes original MOVSS [EBP-44],XMM0, then replaces only
    // endpoint [-4C..-44], using native selected origin [-40..-38], range [-28].
    // D68AE0 no-hit fallback uses its ORIGINAL core+E0 origin, not producer BEC.
    pub fn endpoint(&self,origin:Vec3,range:f32)->Option<Vec3> {
        if !range.is_finite()||range<0.||!origin.iter().all(|x|x.is_finite()){return None;}
        let v=std::array::from_fn::<_,3,_>(|i|(f64::from(origin[i])+f64::from((f64::from(range)*f64::from(self.direction[i])) as f32)) as f32);
        v.iter().all(|x|x.is_finite()).then_some(v)
    }
}
// 5-byte call replacement at D4D0D2. Original ABI thiscall(vector ECX,out).
// EDI=core at this call. Bridge cdecl(core,raw,out) calls original 4F1300 and
// returns its EAX unchanged. Native D4D579/D4D585 remain the BD8 writers.
pub(super) fn capture_bridge(callback:u32)->Vec<u8>{let mut b=vec![0xff,0x74,0x24,0x04,0x51,0x57,0xb8];b.extend(callback.to_le_bytes());b.extend([0xff,0xd0,0x83,0xc4,0x0c,0xc2,0x04,0]);b}
// D6F62E only: native D68AE0 thiscall(camera ECX,out), ESI=owner pawn.
// Bridge cdecl(pawn,camera,out) calls original FIRST. Cached/special/native
// branches are not rewritten. Only validated ordinary no-hit fallback is rebuilt.
pub(super) fn fallback_bridge(callback:u32)->Vec<u8>{let mut b=capture_bridge(callback);b[5]=0x56;b}
pub(super) fn producer_bridge(callback_stdcall:u32,second:bool)->Vec<u8>{
    let mut b=vec![0xf3,0x0f,0x11,0x45,if second{0xbc}else{0xd8},0x9c,0x60,0x89,0xe0,0x81,0xec,0x20,0x02,0,0,0x83,0xe4,0xf0,0x89,0x84,0x24,0,0x02,0,0,0x0f,0xae,0x04,0x24,0xfc,0x55,0x57,0xb8];
    b.extend(callback_stdcall.to_le_bytes());b.extend([0xff,0xd0,0x0f,0xae,0x0c,0x24,0x8b,0xa4,0x24,0,0x02,0,0,0x61,0x9d,0xc3]);b
}
#[cfg(test)]mod tests{
 use super::*;
 fn s()->Snapshot{Snapshot{owner:Owner{core:1,mode:2,pawn:3,viewport:4,window:5,world:6,generation:7},frame:8,targets:[123,456],fraction:[0.25,-0.5]}}
 #[test]fn preserve_float_direction_and_native_rot_identity(){let mut c=AimCarry::default();let s=s();let r=[504,-698,0];assert!(c.capture(Some(s),Some(s),[10.,20.,30.],r));let f=c.begin(Some(s),r).unwrap();assert!((f.direction[1]/f.direction[0]-2.).abs()<1e-6);assert!(c.begin(Some(s),[503,-698,0]).is_none());assert!(c.begin(Some(Snapshot{frame:9,..s}),r).is_none());assert!(c.begin(None,r).is_none());assert!(c.begin(Some(Snapshot{targets:[124,456],..s}),r).is_none());}
 #[test]fn reject_scene_owner_generation_mode_and_nonfinite(){let s=s();for bad in [Owner{core:99,..s.owner},Owner{mode:99,..s.owner},Owner{pawn:99,..s.owner},Owner{world:99,..s.owner},Owner{generation:99,..s.owner},Owner{window:99,..s.owner},Owner{viewport:99,..s.owner}]{let mut c=AimCarry::default();assert!(!c.capture(Some(s),Some(Snapshot{owner:bad,..s}),[1.,0.,0.],[0;3]));}for raw in [[0.;3],[f32::NAN,1.,2.],[f32::INFINITY,0.,0.]]{let mut c=AimCarry::default();assert!(!c.capture(Some(s),Some(s),raw,[0;3]));}}
 #[test]fn native_origin_and_cache_stamps_are_preserved(){let mut c=AimCarry::default();let s=s();let r=[1,2,3];c.capture(Some(s),Some(s),[1.,2.,3.],r);let f=c.begin(Some(s),r).unwrap();let orig=[5.25,7.125,-11.5];let end=f.endpoint(orig,10000.).unwrap();c.finish(f,10,11,end,true);assert!(c.cache_matches(Some(s),r,10,11,end));assert!(!c.cache_matches(Some(s),r,10,12,end));let mut changed=end;changed[1]+=1.;assert!(!c.cache_matches(Some(s),r,10,11,changed));c.finish(f,10,11,end,false);assert!(!c.cache_matches(Some(s),r,10,11,end));assert_eq!(f.endpoint(orig,0.),Some(orig));assert!(f.endpoint(orig,f32::NAN).is_none());}
}


