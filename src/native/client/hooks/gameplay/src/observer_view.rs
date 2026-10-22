//! Final render-view boundary. Never writes a pawn, controller or camera actor.
use crate::observer_camera::Pose;
use crate::equipment_native::{float, word, writable};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) struct Identity {
    pub world: usize,
    pub controller: usize,
    pub generation: u32,
    pub viewport: usize,
    pub camera: usize,
}

pub(crate) struct View { pub identity: Identity, pub pose: Pose }
impl View {
    /// Revalidate identity on the render thread immediately before a bounded write.
    /// This is not admission: the caller must separately enforce OB authorization,
    /// HUD/input isolation and pawn lifecycle. Never call from a background worker.
    pub(crate) unsafe fn present(&self, frame: usize, pose: Pose) -> bool {
        if pose.position_cm.iter().any(|v| !v.is_finite() || v.abs() >= 1.0e8)
            || !pose.yaw_degrees.is_finite() || !pose.pitch_degrees.is_finite()
            || !(-89.0..=89.0).contains(&pose.pitch_degrees) { return false; }
        let Some(current) = inspect(self.identity.world, frame) else { return false; };
        if current.identity != self.identity || !writable(frame+8,24) { return false; }
        let values = [
            (pose.position_cm[0] as f32).to_bits(), (pose.position_cm[1] as f32).to_bits(),
            (pose.position_cm[2] as f32).to_bits(),
            (pose.pitch_degrees * (65536.0/360.0)).round() as i32 as u32,
            ((pose.yaw_degrees.rem_euclid(360.0) * (65536.0/360.0)).round() as u32) & 0xffff,
            0, // Level-horizon free flight, not a roll inherited from an animation.
        ];
        for (i,value) in values.into_iter().enumerate() {
            ((frame+8+i*4) as *mut u32).write_unaligned(value);
        }
        true
    }
}

/// Call synchronously inside 667A50, before 6688CC consumes this scratch frame.
/// 664A10 sets frame+0 to its owner viewport and frame+4 to owner+30.
/// frame+48 is a renderer/service pointer, NOT the viewport (664AB5..664AC5).
/// No pawn is required: the camera must remain bindable after unpossessing.
pub(crate) unsafe fn inspect(world: usize, frame: usize) -> Option<View> {
    let pc = crate::local_player::controller(world)?;
    if word(pc, 0)? != 0x02ac53ec { return None; }
    let generation = word(pc, 0x20)?;
    let viewport = word(pc, 0x354)? as usize;
    let camera = word(pc, 0x6a4)? as usize;
    let core = word(camera, 0x61c)? as usize;
    if generation == 0 || word(viewport, 0)? != 0x02c4ea4c
        || word(camera, 0)? != 0x02ac2064 || word(camera, 0x144)? as usize != world
        || word(core, 0x20)? as usize != pc || word(frame, 0)? as usize != viewport {
        return None;
    }
    let actor = word(frame, 4)? as usize;
    if (actor != pc && actor != camera) || word(viewport, 0x30)? as usize != actor {
        return None;
    }
    let position_cm = [float(frame,8)? as f64,float(frame,12)? as f64,float(frame,16)? as f64];
    if position_cm.iter().any(|v| !v.is_finite() || v.abs() >= 1.0e8) { return None; }
    // Retail FRotator: signed wrapped pitch, unsigned wrapped yaw; 65536/turn.
    let pitch_degrees = word(frame,0x14)? as u16 as i16 as f64 * (360.0/65536.0);
    let yaw_degrees = word(frame,0x18)? as u16 as f64 * (360.0/65536.0);
    Some(View { identity: Identity {world, controller:pc, generation, viewport, camera},
        pose: Pose {position_cm, pitch_degrees, yaw_degrees} })
}

#[cfg(test)]
pub(crate) mod tests {
    use super::*;
    pub(crate) struct Scene { world: Vec<u32>, wi: Vec<u32>, list: Vec<u32>, pc: Vec<u32>,
        camera: Vec<u32>, core: Vec<u32>, viewport: Vec<u32>, frame: Vec<u32> }
    impl Scene {
        pub(crate) fn new() -> Self {
            let mut s = Self { world: vec![0;0x700/4], wi: vec![0;0x400/4], list:vec![0;1],
                pc:vec![0;0x800/4], camera:vec![0;0x800/4], core:vec![0;0x40/4],
                viewport:vec![0;0x940/4], frame:vec![0x77777777;0x50/4] };
            let world=s.world.as_ptr() as u32; let pc=s.pc.as_ptr() as u32;
            let camera=s.camera.as_ptr() as u32; let viewport=s.viewport.as_ptr() as u32;
            s.world[0x6d0/4]=s.wi.as_ptr() as u32;
            s.wi[0x36c/4]=s.list.as_ptr() as u32;s.wi[0x370/4]=1;s.wi[0x374/4]=1;s.list[0]=pc;
            s.pc[0]=0x02ac53ec;s.pc[0x144/4]=world;s.pc[0x60/4]=0x40000;s.pc[0x20/4]=17;
            s.pc[0x354/4]=viewport;s.pc[0x6a4/4]=camera;
            s.camera[0]=0x02ac2064;s.camera[0x144/4]=world;s.camera[0x61c/4]=s.core.as_ptr() as u32;
            s.core[0x20/4]=pc;s.viewport[0]=0x02c4ea4c;s.viewport[0x30/4]=camera;
            s.frame[0]=viewport;s.frame[1]=camera;
            for (i,f) in [100.0f32,200.,300.].into_iter().enumerate(){s.frame[2+i]=f.to_bits();}
            s.frame[5]=8192;s.frame[6]=16384;s.frame[7]=0;
            s
        }
        pub(crate) fn inspect(&self)->Option<View>{unsafe{inspect(self.world.as_ptr() as usize,self.frame.as_ptr() as usize)}}
        pub(crate) fn frame_address(&self)->usize{self.frame.as_ptr() as usize}
    }
    #[test]
    fn observer_view_reads_native_final_pose_without_a_possessed_pawn() {
        let s=Scene::new();let before=s.frame.clone();let view=s.inspect().expect("local view");
        assert_eq!(view.pose.position_cm,[100.,200.,300.]);
        assert_eq!(view.pose.pitch_degrees,45.);assert_eq!(view.pose.yaw_degrees,90.);
        assert_eq!(view.identity.viewport,s.viewport.as_ptr() as usize);
        assert_eq!(s.frame,before);assert_eq!(s.pc[0x520/4],0);
    }
    #[test]
    fn observer_view_presents_only_scratch_pose_not_authoritative_actors() {
        let s=Scene::new();let before=s.frame.clone();let pc=s.pc.clone();let camera=s.camera.clone();
        let view=s.inspect().unwrap();
        let pose=Pose {position_cm:[-500.,125.,800.],yaw_degrees:270.,pitch_degrees:-45.};
        assert!(unsafe{view.present(s.frame.as_ptr() as usize,pose)});
        let mut expected=before;
        expected[2]=(-500.0f32).to_bits();expected[3]=(125.0f32).to_bits();expected[4]=(800.0f32).to_bits();
        expected[5]=(-8192i32) as u32;expected[6]=49152;expected[7]=0;
        assert_eq!(s.frame,expected);assert_eq!(s.pc,pc);assert_eq!(s.camera,camera);
    }
    #[test]
    fn observer_view_rejects_foreign_stale_or_invalid_frames_without_writes() {
        for fault in 0..12 {
            let mut s=Scene::new();let view=s.inspect().unwrap();
            match fault {
                0=>s.pc[0x60/4]=0, 1=>s.pc[0x20/4]+=1, 2=>s.frame[0]=0,
                3=>s.frame[1]=s.core.as_ptr() as u32, 4=>s.viewport[0x30/4]=0,
                5=>s.pc[0x354/4]=0, 6=>s.core[0x20/4]=0, 7=>s.pc[0x144/4]=0,
                8=>s.viewport[0]=0, 9=>s.camera[0]=0, 10=>s.frame[2]=f32::NAN.to_bits(),
                _=>s.pc[0x4c/4]=2<<8,
            }
            let before=s.frame.clone();
            assert!(!unsafe{view.present(s.frame.as_ptr() as usize,view.pose)},"fault={fault}");
            assert_eq!(s.frame,before,"fault={fault}");
        }
    }
    #[test]
    fn observer_view_invalid_pose_is_not_partially_written() {
        let s=Scene::new();let view=s.inspect().unwrap();let before=s.frame.clone();
        for pose in [Pose{position_cm:[f64::NAN,0.,0.],..view.pose},
            Pose{pitch_degrees:90.,..view.pose},Pose{yaw_degrees:f64::INFINITY,..view.pose}] {
            assert!(!unsafe{view.present(s.frame.as_ptr() as usize,pose)});assert_eq!(s.frame,before);
        }
    }
}
