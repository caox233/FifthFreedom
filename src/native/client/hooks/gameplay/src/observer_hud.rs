//! Presentation-only HUD alpha. Does not destroy UI scenes, change visibility
//! properties, or hide menus. Native alpha is recalculated when the lease ends.
use crate::equipment_native::signature;
use retour::static_detour;
static_detour! {static Alpha:unsafe extern "thiscall" fn(usize)->f32;}
const ALPHA:usize=0x01a528f0;

fn presented_alpha(native:f32,observer:bool)->f32 {if observer{0.}else{native}}
unsafe fn alpha(scene:usize)->f32 {
    // Preserve native property evaluation and its native return value when not
    // admitted. Every RTTI reference to this getter is a HudScene/Hud derivative.
    let original=Alpha.call(scene);
    presented_alpha(original,crate::observer_input::active())
}
pub(crate) fn ready()->bool {Alpha.is_enabled()}
pub(crate) unsafe fn init(world_raycast:usize) {
    if ready(){return;}
    if world_raycast!=0x00589860
        || !signature(ALPHA,0x99,"06766dbd248612e269a1c07aadf8b81e001c92a3e6ceaa37dc140f444ce35cb6")
        || !signature(0x01a42100,0x3c,"6ef9beae58550cc0273261e2d9e68eaf8ab345e206faa375cf1bf91cf363aac6")
        || !signature(0x018a2290,0x6c,"ddd423ec3b4594925d54736f5c6089c1d294d6c337fc871893eebc92a3d19433") {
        tracing::warn!("SCBL_OB_HUD ready=false reason=native-contract");return;
    }
    let native:unsafe extern "thiscall" fn(usize)->f32=std::mem::transmute(ALPHA);
    if Alpha.initialize(native,|scene|unsafe{alpha(scene)}).is_ok() && Alpha.enable().is_ok() {
        tracing::info!("SCBL_OB_HUD ready=true presentation=alpha preserve_native_visibility=true");
    }else{tracing::warn!("SCBL_OB_HUD ready=false reason=hook-install");}
}
pub(crate) unsafe fn deinit(){crate::observer_input::clear();let _=Alpha.disable();}

#[cfg(test)]
mod tests {
    use super::*;
    #[test] fn observer_hud_preserves_original_alpha_bit_for_bit_without_admission() {
        for value in [0.,25.,100.,f32::NEG_INFINITY,f32::from_bits(0x7fc00011)] {
            assert_eq!(presented_alpha(value,false).to_bits(),value.to_bits());
            assert_eq!(presented_alpha(value,true),0.);
        }
        assert_eq!(presented_alpha(60.,false),60.); // Restore current native value, not a saved stale 100%.
    }
}
