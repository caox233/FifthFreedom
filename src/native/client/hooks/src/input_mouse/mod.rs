//! One process-lifetime owner of the accepted C71/C72 mouse fixes.
//! The optional gameplay DLL must not install these hooks. Ordinary menu,
//! background windows, controllers and unsupported builds keep native input.
pub(crate) mod memory;
#[path="../scis_diagnostics.rs"] mod scis_diagnostics;
mod merc_mouse_sensitivity;
mod raw_mouse;
mod raw_mouse_native;
mod gameplay_input_context;
mod suicide_policy;
mod suicide_native;
pub(crate) use gameplay_input_context::current as shortcut_context;
mod rotation_precision;
mod rotation_precision_native;
mod rotation_view;
mod rotation_camera;
mod rotation_publication;
mod rotation_publisher_bridge;
mod rotation_patch_group;
mod rotation_pipeline_native;
mod fractional_aim_policy;
mod rotation_aim_native;
mod rotation_motion;
pub(crate) use rotation_motion::bind as bind_scope_motion;
mod rotation_scene_query;
pub(crate) use rotation_scene_query::query as scope_aim_query;

#[used]
#[no_mangle]
pub static SCBL_CORE_MOUSE_PROTOCOL: [u8; 23] = *b"SCBL_CORE_MOUSE=C71C72\0";

pub(super) unsafe fn init(world: usize) {
    merc_mouse_sensitivity::init(world);
    suicide_native::init();
    raw_mouse_native::init(world);
    rotation_precision_native::init(world);
    rotation_pipeline_native::init(world);
}

pub(super) unsafe fn deinit() {
    rotation_motion::bind(None);
    rotation_pipeline_native::deinit();
    rotation_precision_native::deinit();
    suicide_native::deinit();
    raw_mouse_native::deinit();
    merc_mouse_sensitivity::deinit();
}
