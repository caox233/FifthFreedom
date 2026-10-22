//! Optional synchronous OB input extension. The main DLL owns Raw Input;
//! callbacks receive copied POD values and must not retain native pointers.
pub const INPUT_EXPORT: &[u8] = b"scbl_observer_input_v1\0";
/// Per-process observer backend; never included in ordinary MOD defaults.
pub const FEATURE_SESSION:u64=1<<5;
pub fn backend_features(is_mod:bool,observer:bool)->u64 {
    (if is_mod {crate::GAMEPLAY_FEATURE_BITS_DEFAULT}else{0})
        | if observer {FEATURE_SESSION}else{0}
}
pub const ADMITTED: u32 = 1;
pub const CAPTURE: u32 = 2;
pub const FORWARD: u32 = 1;
pub const BACK: u32 = 2;
pub const LEFT: u32 = 4;
pub const RIGHT: u32 = 8;
pub const UP: u32 = 16;
pub const DOWN: u32 = 32;
pub const FAST: u32 = 64;
pub const SLOW: u32 = 128;

#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct InputFrame {
    pub abi: u32, pub size: u32, pub now_ms: u64,
    pub world: u32, pub controller: u32, pub generation: u32, pub viewport: u32,
    pub mouse_x: f32, pub mouse_y: f32, pub keys: u32, pub flags: u32,
}
impl Default for InputFrame {
    fn default()->Self { Self {abi:1,size:core::mem::size_of::<Self>() as u32,now_ms:0,
        world:0,controller:0,generation:0,viewport:0,mouse_x:0.,mouse_y:0.,keys:0,flags:0} }
}
impl InputFrame {
    pub fn valid(&self)->bool {
        self.abi==1 && self.size==core::mem::size_of::<Self>() as u32
        && self.flags & !(ADMITTED|CAPTURE)==0 && self.keys & !255==0
        && self.mouse_x.is_finite() && self.mouse_y.is_finite()
        && self.mouse_x.abs()<=8_000_000. && self.mouse_y.abs()<=8_000_000.
        && (self.flags & CAPTURE==0 || (self.flags & ADMITTED!=0 && self.world>=0x10000
            && self.controller>=0x10000 && self.viewport>=0x10000 && self.generation!=0))
    }
}
// 0 = inactive, 1 = accepted but still isolating, 2 = camera active; negative error.
pub type InputFn = unsafe extern "system" fn(*const InputFrame,u32)->i32;

#[cfg(test)]
mod tests {
    use super::*;
    #[test] fn observer_backend_selection_does_not_enable_mod_rules_in_official_mode() {
        assert_eq!(backend_features(false,false),0);
        assert_eq!(backend_features(true,false),31);
        assert_eq!(backend_features(false,true),FEATURE_SESSION);
        assert_eq!(backend_features(true,true),31|FEATURE_SESSION);
        assert_eq!(crate::GAMEPLAY_FEATURE_BITS_DEFAULT,31);
    }
    #[test] fn observer_input_is_bounded_and_capture_requires_admission() {
        let mut f=InputFrame::default();assert_eq!(core::mem::size_of::<InputFrame>(),48);assert!(f.valid());
        f.flags=CAPTURE;assert!(!f.valid());f.flags|=ADMITTED;assert!(!f.valid());
        f.world=0x10000;f.controller=0x20000;f.viewport=0x30000;f.generation=1;assert!(f.valid());
        f.mouse_x=f32::NAN;assert!(!f.valid());f.mouse_x=0.;f.keys=256;assert!(!f.valid());
    }
}
