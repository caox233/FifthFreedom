//! Optional paired SC-IS motion protocol. Values are sampled synchronously before
//! native aim deprojection; no render thread reads an actor through this ABI.
pub const ABI: u32 = 1;
pub const QUERY_EXPORT: &[u8] = b"scbl_plugin_scope_motion_v1\0";
pub const ENABLE_EXPORT: &[u8] = b"scbl_plugin_enable_scope_motion_v1\0";
pub type QueryFn = unsafe extern "system" fn(*const Request, *mut Sample, u32) -> i32;
pub type EnableFn = unsafe extern "system" fn(u32) -> i32;
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct Request {
    pub abi: u32, pub size: u32, pub world: u32, pub controller: u32,
    pub pawn: u32, pub camera: u32, pub core: u32,
    pub controller_generation: u32, pub pawn_generation: u32,
    pub frame: [u32; 2], pub mode: u32,
}
impl Request {
    pub fn valid(self) -> bool {
        self.abi == ABI && self.size as usize == core::mem::size_of::<Self>()
            && self.frame != [0; 2] && self.controller_generation != 0 && self.pawn_generation != 0
            && [self.world, self.controller, self.pawn, self.camera, self.core, self.mode]
                .into_iter().all(|p| p >= 0x10000 && p & 3 == 0)
    }
}
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct Sample {
    pub abi: u32, pub size: u32, pub request: Request,
    /// Native rotator units, not pixels, FOV or raw mouse counts.
    pub delta: [f32; 2],
}
impl Sample {
    pub fn valid_for(self, r: Request) -> bool {
        r.valid() && self.abi == ABI && self.size as usize == core::mem::size_of::<Self>()
            && self.request == r && self.delta.into_iter().all(|v| v.is_finite() && v.abs() <= 22.)
    }
}
#[cfg(test)] mod tests {
    use super::*;
    #[test] fn scope_motion_abi_is_fixed_width_and_rejects_wrong_frame_or_generation() {
        assert_eq!(core::mem::size_of::<Request>(),48);
        assert_eq!(core::mem::size_of::<Sample>(),64);
        let r=Request{abi:ABI,size:48,world:0x10000,controller:0x20000,pawn:0x30000,camera:0x40000,
            core:0x50000,controller_generation:1,pawn_generation:2,frame:[3,0],mode:0x60000};
        let s=Sample{abi:ABI,size:64,request:r,delta:[0.01,-4.]};
        assert!(s.valid_for(r));
        assert!(!s.valid_for(Request{frame:[4,0],..r}));
        assert!(!s.valid_for(Request{pawn_generation:3,..r}));
        for d in [f32::NAN,f32::INFINITY,23.] { assert!(!Sample{delta:[d,0.],..s}.valid_for(r)); }
    }
}

/// Explicit caller-owned phase protocol. V1 remains unchanged for older core
/// consumers; V2 is never negotiated through the V1 export names.
pub mod v2 {
    pub const ABI:u32=2;
    pub const PREVIEW:u32=0;
    pub const POST_HANDS:u32=1;
    pub const QUERY_EXPORT:&[u8]=b"scbl_plugin_scope_motion_v2\0";
    pub const ENABLE_EXPORT:&[u8]=b"scbl_plugin_enable_scope_motion_v2\0";
    pub type QueryFn=unsafe extern "system" fn(*const Request,*mut Sample,u32)->i32;
    pub type EnableFn=unsafe extern "system" fn(u32)->i32;
    #[repr(C)]
    #[derive(Clone,Copy,Debug,Default,PartialEq,Eq)]
    pub struct Request {pub abi:u32,pub size:u32,pub input:super::Request,pub phase:u32}
    impl Request {
        pub fn valid(self)->bool {
            self.abi==ABI && self.size as usize==core::mem::size_of::<Self>()
                && self.input.valid() && matches!(self.phase,PREVIEW|POST_HANDS)
        }
    }
    #[repr(C)]
    #[derive(Clone,Copy,Debug,Default,PartialEq)]
    pub struct Sample {pub abi:u32,pub size:u32,pub request:Request,pub delta:[f32;2]}
    impl Sample {
        pub fn valid_for(self,r:Request)->bool {
            r.valid() && self.abi==ABI && self.size as usize==core::mem::size_of::<Self>()
                && self.request==r && self.delta.into_iter().all(|v|v.is_finite()&&v.abs()<=22.)
        }
    }
    #[cfg(test)]mod tests {
        use super::*;
        #[test]fn phase_protocol_cannot_alias_v1_or_another_query(){
            assert_eq!(core::mem::size_of::<Request>(),60);assert_eq!(core::mem::size_of::<Sample>(),76);
            let input=super::super::Request{abi:1,size:48,world:0x10000,controller:0x20000,pawn:0x30000,camera:0x40000,core:0x50000,controller_generation:1,pawn_generation:2,frame:[3,0],mode:0x60000};
            let r=Request{abi:ABI,size:60,input,phase:POST_HANDS};let s=Sample{abi:ABI,size:76,request:r,delta:[1.,2.]};
            assert!(s.valid_for(r));
            for bad in [Request{abi:1,..r},Request{size:48,..r},Request{phase:2,..r},Request{input:super::super::Request{frame:[4,0],..input},..r},Request{phase:PREVIEW,..r}]{assert!(!s.valid_for(bad));}
            for bad in [Sample{abi:1,..s},Sample{size:64,..s},Sample{delta:[f32::NAN,0.],..s}]{assert!(!bad.valid_for(r));}
        }
    }
}
