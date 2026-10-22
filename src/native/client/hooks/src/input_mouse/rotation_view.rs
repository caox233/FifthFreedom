//! Local frame publication and full precision scene basis. Never infer a view
//! from a global angle: native camera effects and the actual scene arguments win.
use super::scis_diagnostics as diag;
use super::rotation_precision::{Owner, Snapshot};

#[derive(Clone, Copy, Debug, PartialEq)]
pub(super) struct Published {
    pub token: Snapshot,
    pub camera: usize,
    pub position: [u32; 3],
    pub rotation: [i32; 3],
    pub composed: Option<[f64;3]>,
}
impl Published {
    /// Call only AFTER native D609B0 has published this core. The alternate
    /// source_current must be observed at the native source-selection branch;
    /// a pre-call guess is not enough because native effects may update state.
    /// The alternate viewport branch never publishes the ordinary camera and must be rejected.
    pub fn capture(token: Snapshot, source_current: Option<bool>, read: impl Fn(usize, usize)->Option<u32>)->Option<Self> {
        if source_current!=Some(true) { return None; }
        let Owner { core, mode, viewport, pawn, world, generation, .. }=token.owner;
        let pc=read(core,0x20)? as usize;
        let camera=read(core,0x1c)? as usize;
        if pc<0x10000 || camera<0x10000 || read(core,0x1434)?!=0
            || read(core,0x2c)? as usize!=mode || read(mode,4)? as usize!=core
            || read(core,0x24)? as usize!=pawn || read(pc,0x520)? as usize!=pawn
            || read(pc,0x6a4)? as usize!=camera || read(camera,0x61c)? as usize!=core
            || read(pc,0x354)? as usize!=viewport || read(pc,0x20)?!=generation
            || read(pc,0x60)?&0x40000==0 || read(pc,0x144)? as usize!=world
            || read(pawn,0x144)? as usize!=world || read(pawn,0x938)? as usize!=pc
            || read(camera,0x144)? as usize!=world { return None; }
        let position=[read(camera,0x9c)?,read(camera,0xa0)?,read(camera,0xa4)?];
        if position.iter().any(|v|!f32::from_bits(*v).is_finite()) { return None; }
        Some(Self { token, camera, position, composed:None,
            rotation:[read(camera,0xa8)? as i32,read(camera,0xac)? as i32,read(camera,0xb0)? as i32] })
    }
    pub fn scene_angles(&self,current:Snapshot,scene:usize,read:impl Fn(usize,usize)->Option<u32>)->Option<[f64;3]> {
        self.angles_at(current,scene,false,read)
    }
    /// 58FEB5 builds the view matrix before native scene publication. CHECK4
    /// observes the local controller as scene+2DC here, not its later view target.
    /// Accept that constructor identity only at this seam; keep the published
    /// query strict, and retain all frame, viewport, target and pose checks.
    pub fn construction_angles(&self,current:Snapshot,scene:usize,read:impl Fn(usize,usize)->Option<u32>)->Option<[f64;3]> {
        self.angles_at(current,scene,true,read)
    }
    fn angles_at(&self,current:Snapshot,scene:usize,constructing:bool,read:impl Fn(usize,usize)->Option<u32>)->Option<[f64;3]> {
        if current!=self.token {diag::hit(diag::VIEW_TOKEN_STALE);return None;}
        if (Self::capture(current,Some(true),&read)).or_else(||{diag::hit(diag::VIEW_CAPTURE_ABSENT);None})?!=(Self{composed:None,..*self}) {diag::hit(diag::VIEW_CAPTURE_CHANGED);return None;}
        let o=current.owner;let pc=read(o.core,0x20)? as usize;
        let actor=read(scene,0x2dc)? as usize;
        let viewport_actor=read(o.viewport,0x30)? as usize;
        let target=read(pc,0x3d0)? as usize;
        let owned_target=target==o.pawn || target==self.camera;
        let owned_actor=actor==target || (constructing && actor==pc && viewport_actor==pc);
        if read(scene,0x10)? as usize!=o.viewport || !owned_target || !owned_actor
            || (viewport_actor!=pc && viewport_actor!=self.camera) {
            diag::hit(diag::VIEW_SCENE_OWNER_REJECT);
            diag::trace(diag::VIEW_OWNER_MISMATCH,||[scene as u32,
                read(scene,0x10).unwrap_or(0),o.viewport as u32,actor as u32,
                read(pc,0x3d0).unwrap_or(0),o.pawn as u32,self.camera as u32,
                viewport_actor as u32,pc as u32,current.frame as u32,
                read(scene,0x2e8).unwrap_or(0),read(scene,0x2ec).unwrap_or(0),
                self.rotation[0] as u32,self.rotation[1] as u32,
                (current.fraction[0]as f32).to_bits(),(current.fraction[1]as f32).to_bits()]);
            return None;
        }
        for axis in 0..3 {
            if read(scene,0x2a0+axis*4)?!=self.position[axis]
                || read(scene,0x2e8+axis*4)? as i32!=self.rotation[axis] { diag::hit(diag::VIEW_SCENE_POSE_REJECT);diag::sample(diag::VIEW_POSE_MISMATCH,||[axis as u32,read(scene,0x2a0+axis*4).unwrap_or(0),self.position[axis],read(scene,0x2e8+axis*4).unwrap_or(0),self.rotation[axis] as u32,current.frame as u32,self.token.frame as u32,0,0,0,0,0,0,0,0,0]);return None; }
        }
        Some(self.composed.unwrap_or([self.rotation[0] as f64+current.fraction[0],
            self.rotation[1] as f64+current.fraction[1],self.rotation[2] as f64]))
    }
}

pub(super) fn matrix(angles:[f64;3])->[f32;16] {
    let k=-std::f64::consts::TAU/65536.;
    let(sp,cp)=(angles[0].rem_euclid(65536.)*k).sin_cos();
    let(sy,cy)=(angles[1].rem_euclid(65536.)*k).sin_cos();
    let(sr,cr)=(angles[2].rem_euclid(65536.)*k).sin_cos();
    let yaw=[cy,sy,0.,0.,-sy,cy,0.,0.,0.,0.,1.,0.,0.,0.,0.,1.];
    let pitch=[cp,0.,sp,0.,0.,1.,0.,0.,-sp,0.,cp,0.,0.,0.,0.,1.];
    let roll=[1.,0.,0.,0.,0.,cr,-sr,0.,0.,sr,cr,0.,0.,0.,0.,1.];
    fn mul(a:[f64;16],b:[f64;16])->[f64;16] {
        std::array::from_fn(|n|(0..4).map(|k|a[n/4*4+k]*b[k*4+n%4]).sum())
    }
    mul(mul(yaw,pitch),roll).map(|v|v as f32)
}
pub(super) fn forward(angles:[f64;3])->[f32;3] {
    let k=std::f64::consts::TAU/65536.;
    let(sp,cp)=(angles[0].rem_euclid(65536.)*k).sin_cos();
    let(sy,cy)=(angles[1].rem_euclid(65536.)*k).sin_cos();
    [(cp*cy) as f32,(cp*sy) as f32,sp as f32]
}

#[cfg(test)]mod tests {
    use super::*;use std::collections::HashMap;
    #[test]fn accepts_only_same_published_camera_and_scene_without_losing_native_effects(){
        let o=Owner{viewport:0x10000,window:0x20000,pawn:0x30000,mode:0x40000,core:0x50000,world:0x60000,generation:7};
        let token=Snapshot{owner:o,frame:9,targets:[1,2],fraction:[0.25,-0.25]};
        let pc=0x70000;let camera=0x80000;let scene=0x90000;
        let mut m=HashMap::from([((o.core,0x20),pc as u32),((o.core,0x1c),camera as u32),((o.core,0x1434),0),((o.core,0x2c),o.mode as u32),((o.mode,4),o.core as u32),((o.core,0x24),o.pawn as u32),((pc,0x520),o.pawn as u32),((pc,0x6a4),camera as u32),((camera,0x61c),o.core as u32),((pc,0x354),o.viewport as u32),((pc,0x20),7),((pc,0x60),0x40000),((pc,0x144),o.world as u32),((o.pawn,0x144),o.world as u32),((o.pawn,0x938),pc as u32),((camera,0x144),o.world as u32),((scene,0x10),o.viewport as u32),((scene,0x2dc),o.pawn as u32),((pc,0x3d0),o.pawn as u32),((o.viewport,0x30),pc as u32)]);
        for axis in 0..3 {let p=(axis as f32+10.).to_bits();let r=(axis as u32+500)*4;
            m.insert((camera,0x9c+axis*4),p);m.insert((camera,0xa8+axis*4),r);
            m.insert((scene,0x2a0+axis*4),p);m.insert((scene,0x2e8+axis*4),r);}
        let read=|p,o|m.get(&(p,o)).copied();let published=Published::capture(token,Some(true),read).unwrap();
        assert!(Published::capture(token,Some(false),read).is_none());
        assert!(Published::capture(token,None,read).is_none());
        assert_eq!(published.scene_angles(token,scene,read),Some([2000.25,2003.75,2008.]));
        let exact=Published{composed:Some([2001.125,2004.5,2008.25]),..published};
        assert_eq!(exact.scene_angles(token,scene,read),exact.composed);
        assert_eq!(exact.construction_angles(token,scene,read),exact.composed);
        assert!(exact.scene_angles(Snapshot{frame:10,..token},scene,read).is_none());
        assert_eq!(published.construction_angles(token,scene,read),Some([2000.25,2003.75,2008.]));
        assert!(published.scene_angles(Snapshot{frame:10,..token},scene,read).is_none());
        assert!(published.scene_angles(Snapshot{targets:[2,2],..token},scene,read).is_none());
        for key in m.keys(){let mut missing=m.clone();missing.remove(key);let read=|p,o|missing.get(&(p,o)).copied();assert!(Published::capture(token,Some(true),read).and_then(|p|p.scene_angles(token,scene,read)).is_none(),"{key:?}");}
        for (key,value) in [((o.core,0x1434),o.viewport as u32),((camera,0xa8),8),((scene,0x2a0),0),((pc,0x20),8),((scene,0x2dc),pc as u32),((camera,0x9c),f32::NAN.to_bits())]{let mut changed=m.clone();changed.insert(key,value);assert!(published.scene_angles(token,scene,|p,o|changed.get(&(p,o)).copied()).is_none());}
        m.insert((scene,0x2dc),pc as u32);
        assert_eq!(published.construction_angles(token,scene,|p,o|m.get(&(p,o)).copied()),Some([2000.25,2003.75,2008.]));
        assert!(published.scene_angles(token,scene,|p,o|m.get(&(p,o)).copied()).is_none());
        for (key,value) in [((scene,0x10),0x12345678),((scene,0x2dc),0x12345678),((pc,0x3d0),0x12345678),((o.viewport,0x30),camera as u32),((scene,0x2e8),0),((scene,0x2a0),0),((pc,0x20),8)] {
            let mut changed=m.clone();changed.insert(key,value);
            assert!(published.construction_angles(token,scene,|p,o|changed.get(&(p,o)).copied()).is_none());
        }
    }
}
