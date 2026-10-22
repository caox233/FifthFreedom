//! Join the already-proven final camera publication and actual native aim carry.
//! No re-deprojection, integer BD8 approximation, new input, or scene mutation.
use super::scis_diagnostics as diag;
use super::{memory::word, rotation_precision::Snapshot, rotation_view::Published,
    rotation_aim_native::{self, SceneRay}, rotation_pipeline_native};
use hooks_plugin_api::scope_aim::{Request, Sample, ABI};

fn assemble(request: Request, published: Published, current: Snapshot, ray: SceneRay,
    read: impl Fn(usize, usize) -> Option<u32>) -> Option<Sample> {
    if !request.valid() || current != ray.frame.input || current != published.token
        || request.viewport as usize != current.owner.viewport
        || request.world as usize != current.owner.world || request.generation != current.owner.generation
        || read(current.owner.core, 0x20)? != request.controller
        || ray.camera != published.camera { return None; }
    published.scene_angles(current, request.scene as usize, &read)?;
    for axis in 0..3 {
        if read(current.owner.core, 0xbd8 + axis * 4)? as i32 != ray.frame.native_rot[axis]
            || read(current.owner.core, 0xe0 + axis * 4)? != ray.origin[axis].to_bits()
            || read(ray.camera, 0x62c + axis * 4)? != ray.target[axis].to_bits() { return None; }
    }
    if read(ray.camera, 0x628)? as usize != ray.hit_actor { return None; }
    let result = Sample {
        abi: ABI, size: std::mem::size_of::<Sample>() as u32, request,
        frame: [current.frame as u32, (current.frame >> 32) as u32],
        pawn: u32::try_from(current.owner.pawn).ok()?, camera: u32::try_from(ray.camera).ok()?,
        origin: ray.origin, direction: ray.frame.direction, target: ray.target,
        camera_origin: published.position.map(f32::from_bits),
    };
    result.valid_for(request).then_some(result)
}

pub(crate) unsafe extern "system" fn query(request: *const Request, out: *mut Sample, bytes: u32) -> i32 {
    if request.is_null() || out.is_null() || bytes as usize != std::mem::size_of::<Sample>() { return 0; }
    diag::hit(diag::QUERY_ENTER);
    let request = request.read_unaligned();
    if !request.valid() { return 0; }
    let Some((published, current)) = rotation_pipeline_native::scene_publication(request.scene as usize) else { diag::hit(diag::QUERY_PUBLICATION_REJECT);return 0; };
    let Some(ray) = rotation_aim_native::scene_ray(current.owner.core) else { diag::hit(diag::QUERY_RAY_REJECT);return 0; };
    let Some(sample) = assemble(request, published, current, ray, |p,o| word(p,o)) else { diag::hit(diag::QUERY_ASSEMBLE_REJECT);return 0; };
    // Re-read the publication token after the ray and native fields. A reset on
    // another thread must not let a partially observed frame escape the ABI.
    if rotation_pipeline_native::scene_publication(request.scene as usize) != Some((published,current)) { return 0; }
    out.write_unaligned(sample);diag::hit(diag::QUERY_ACCEPTED);
    1
}

#[cfg(test)] mod tests {
    use super::*;
    use super::super::{rotation_precision::Owner, fractional_aim_policy::AimFrame};
    use std::collections::HashMap;
    type Memory = HashMap<(usize,usize),u32>;
    fn fixture() -> (Request, Published, Snapshot, SceneRay, Memory) {
        let owner = Owner { viewport:0x10000, window:0x20000, pawn:0x30000, mode:0x40000, core:0x50000, world:0x60000, generation:7 };
        let token = Snapshot { owner, frame:0x100000009, targets:[1,2], fraction:[0.25,-0.25] };
        let pc=0x70000; let camera=0x80000; let scene=0x90000;
        let mut m = Memory::from([((owner.core,0x20),pc),((owner.core,0x1c),camera),((owner.core,0x1434),0),((owner.core,0x2c),owner.mode as u32),((owner.mode,4),owner.core as u32),((owner.core,0x24),owner.pawn as u32),((pc as usize,0x520),owner.pawn as u32),((pc as usize,0x6a4),camera),((camera as usize,0x61c),owner.core as u32),((pc as usize,0x354),owner.viewport as u32),((pc as usize,0x20),7),((pc as usize,0x60),0x40000),((pc as usize,0x144),owner.world as u32),((owner.pawn,0x144),owner.world as u32),((owner.pawn,0x938),pc),((camera as usize,0x144),owner.world as u32),((scene,0x10),owner.viewport as u32),((scene,0x2dc),owner.pawn as u32),((pc as usize,0x3d0),owner.pawn as u32),((owner.viewport,0x30),pc),((camera as usize,0x628),0)]);
        for axis in 0..3 {
            let pos=(axis as f32+10.).to_bits(); let rot=2000+axis as u32*4;
            for key in [(camera as usize,0x9c+axis*4),(scene,0x2a0+axis*4)] { m.insert(key,pos); }
            for key in [(camera as usize,0xa8+axis*4),(scene,0x2e8+axis*4),(owner.core,0xbd8+axis*4)] { m.insert(key,rot); }
            m.insert((owner.core,0xe0+axis*4),(axis as f32).to_bits());
            m.insert((camera as usize,0x62c+axis*4),(axis as f32+20.).to_bits());
        }
        let published=Published::capture(token,Some(true),|p,o|m.get(&(p,o)).copied()).unwrap();
        let ray=SceneRay { frame:AimFrame{input:token,native_rot:[2000,2004,2008],direction:[0.6,0.8,0.]},camera:camera as usize,hit_actor:0,origin:[0.,1.,2.],target:[20.,21.,22.] };
        (Request{abi:ABI,size:28,scene:scene as u32,viewport:owner.viewport as u32,controller:pc,generation:7,world:owner.world as u32},published,token,ray,m)
    }
    #[test] fn preserves_actual_native_float_ray_not_integer_reconstruction() {
        let(r,p,t,a,m)=fixture(); let q=assemble(r,p,t,a,|p,o|m.get(&(p,o)).copied()).unwrap();
        assert_eq!(q.direction,a.frame.direction); assert_eq!(q.target,a.target);
        assert_eq!(q.frame,[9,1]); assert_eq!(q.camera_origin,[10.,11.,12.]);
        assert!(q.valid_for(r)); assert_ne!(q.direction,super::super::rotation_view::forward([2000.,2004.,2008.]));
    }
    #[test] fn stale_owner_frame_native_camera_ray_or_target_is_rejected() {
        let(r,p,t,a,m)=fixture();
        for request in [Request{scene:r.scene+4,..r},Request{generation:8,..r},Request{viewport:r.viewport+4,..r},Request{controller:r.controller+4,..r},Request{world:r.world+4,..r}] {
            assert!(assemble(request,p,t,a,|p,o|m.get(&(p,o)).copied()).is_none());
        }
        assert!(assemble(r,p,Snapshot{frame:t.frame+1,..t},a,|p,o|m.get(&(p,o)).copied()).is_none());
        for (key,value) in [((t.owner.core,0xbd8),5),((t.owner.core,0xe0),2),((a.camera,0x62c),3),((a.camera,0x628),1),((r.scene as usize,0x2a0),4)] {
            let mut changed=m.clone(); changed.insert(key,value);
            assert!(assemble(r,p,t,a,|p,o|changed.get(&(p,o)).copied()).is_none());
        }
        for key in m.keys() { let mut missing=m.clone(); missing.remove(key);
            assert!(assemble(r,p,t,a,|p,o|missing.get(&(p,o)).copied()).is_none(),"{key:?}"); }
    }
    #[test] fn malformed_abi_leaves_output_untouched() {
        let(r,_,_,_,_)=fixture(); let mut s=Sample::default(); let before=s;
        unsafe { assert_eq!(query(std::ptr::null(),&mut s,100),0); assert_eq!(query(&r,&mut s,99),0); }
        assert_eq!(s,before);
    }
}
