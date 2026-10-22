//! Optional synchronous, value-only camera/aim query. No retained game pointers.
//! A caller owns readable Request and writable Sample for the duration of QueryFn.
pub const ABI: u32 = 1;
pub const BIND_EXPORT: &[u8] = b"scbl_plugin_bind_scope_aim_v1\0";
pub type QueryFn = unsafe extern "system" fn(*const Request, *mut Sample, u32) -> i32;
pub type BindFn = unsafe extern "system" fn(u32, Option<QueryFn>) -> i32;

#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct Request {
    pub abi: u32,
    pub size: u32,
    pub scene: u32,
    pub viewport: u32,
    pub controller: u32,
    pub generation: u32,
    pub world: u32,
}
impl Request {
    pub fn valid(self) -> bool {
        self.abi == ABI && self.size as usize == core::mem::size_of::<Self>()
            && self.generation != 0
            && [self.scene, self.viewport, self.controller, self.world].into_iter().all(|p| p >= 0x10000)
    }
}
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct Sample {
    pub abi: u32,
    pub size: u32,
    pub request: Request,
    pub frame: [u32; 2],
    pub pawn: u32,
    pub camera: u32,
    pub origin: [f32; 3],
    pub direction: [f32; 3],
    pub target: [f32; 3],
    pub camera_origin: [f32; 3],
}
impl Sample {
    pub fn valid_for(self, request: Request) -> bool {
        let len = self.direction.into_iter().map(|v| v * v).sum::<f32>();
        self.abi == ABI && self.size as usize == core::mem::size_of::<Self>()
            && request.valid() && self.request == request && self.frame != [0; 2]
            && self.pawn >= 0x10000 && self.camera >= 0x10000
            && self.origin.into_iter().chain(self.direction).chain(self.target).chain(self.camera_origin).all(f32::is_finite)
            && (0.9999..=1.0001).contains(&len)
    }
}

#[cfg(test)] mod tests {
    use super::*;
    #[test] fn fixed_width_value_abi() {
        assert_eq!(core::mem::size_of::<Request>(), 28);
        assert_eq!(core::mem::size_of::<Sample>(), 100);
        assert_eq!(core::mem::align_of::<Sample>(), 4);
        assert_eq!(core::mem::offset_of!(Sample, direction), 64);
    }
    #[test] fn exact_request_and_finite_unit_ray_required() {
        let r = Request { abi: ABI, size: 28, scene: 0x10000, viewport: 0x20000, controller: 0x30000, generation: 4, world: 0x50000 };
        let s = Sample { abi: ABI, size: 100, request: r, frame: [8, 0], pawn: 0x60000, camera: 0x70000, direction: [1., 0., 0.], ..Sample::default() };
        assert!(s.valid_for(r));
        assert!(!s.valid_for(Request { scene: r.scene + 4, ..r }));
        for bad in [Sample { frame: [0,0], ..s }, Sample { size: 99, ..s }, Sample { abi: 2, ..s }, Sample { target: [f32::NAN,0.,0.], ..s }, Sample { direction: [0.;3], ..s }] {
            assert!(!bad.valid_for(r));
        }
    }
}
