use std::ffi::c_char;
use std::io::Write;
use std::os::windows::ffi::OsStringExt;
use std::path::PathBuf;
use std::sync::Mutex;
use std::sync::atomic::{AtomicBool,Ordering};

static GAMEPLAY_ENABLED:AtomicBool=AtomicBool::new(false);

use hooks_plugin_api::host_api_compatible;
use hooks_plugin_api::plugin_config_compatible;
use hooks_plugin_api::HostApiV1;
use hooks_plugin_api::PluginConfigV1;
use hooks_plugin_api::EVENT_NATIVE_SAVE_OPENED;
use hooks_plugin_api::LOG_INFO;
use tracing_subscriber::fmt::MakeWriter;
use tracing_subscriber::util::SubscriberInitExt;

mod aim_observation;
mod ats_counter;
mod ats_suppressor;
// Fixed-position suppressed-shot extension; original ATS entries stay native.
mod ats_marks;
mod ats_native_source;
mod sticky_camera_gesture;
mod sticky_camera_input;
mod sticky_camera_sound;
mod sticky_camera_runtime;
mod local_player;
mod ats_native_view;
// Opt-in peer exposure path; tests still compile the complete adapter.
#[cfg(any(test, feature = "goggle-feedback"))]
mod goggle_bms_source;
// Opt-in peer exposure path; tests still compile the complete adapter.
#[cfg(any(test, feature = "goggle-feedback"))]
mod goggle_ats_source;
// Opt-in peer exposure path; tests still compile the complete adapter.
#[cfg(any(test, feature = "goggle-feedback"))]
mod goggle_rfd_source;
// Offline peer ABI contract fixtures only; never a production transport.
#[cfg(test)]
mod native_feedback_contract_tests;
#[cfg(test)]
mod goggle_session_tests;
#[cfg(test)]
mod goggle_roster_tests;
#[cfg(test)]
mod combat_abi_tests;
mod combat_rules;
mod drone_emp;
mod drone_fall;
mod drone_hud;
// Retail DX11 native semantic control; no raw key polling.
mod drone_control;
mod drone_cooldown;
mod drone_health;
#[cfg(any(test, feature = "native-observation"))]
mod drone_observation;
#[cfg(any(test, feature = "native-observation"))]
mod r58_native_observation;
mod intel_disruptor;
mod drone_tags;
mod equipment_native;
mod equipment_rules;
mod extended_loadouts;
mod feature_contract;
mod feature_defaults;
mod feature_runtime;
mod gameplay_features;
// Opt-in HUD adapter: no positive without current validated peer feedback.
#[cfg(any(test, feature = "goggle-feedback"))]
mod goggle_exposure;
#[cfg(any(test, feature = "goggle-feedback"))]
mod goggle_warning_ui;
#[cfg(any(test, feature = "goggle-feedback"))]
mod goggle_warning_native;
#[cfg(any(test, feature = "goggle-feedback"))]
mod goggle_feedback;
#[cfg(any(test, feature = "goggle-feedback"))]
mod goggle_peer_frame;
#[cfg(any(test, feature = "goggle-feedback"))]
mod goggle_feedback_native;
mod chat_delivery_diag;
#[cfg(any(test, feature = "goggle-feedback"))]
mod goggle_feedback_wire;
mod head_emp;
mod overload_lights;
mod overload_particles;
mod overload_visibility;
mod overload_emission_lights;
mod overload_visual_policy;
mod overload_audio;
mod overload_viewer;
mod head_emp_rules;
mod intel_mark_lifetime;
mod intel_rfd_targets;
mod thermal_smoke;
mod thermal_targets;
mod vision_native_view;
mod vision_transparency;
mod spy_goggle_range;
#[cfg(any(test, feature = "goggle-feedback"))]
mod intel_rfd_wire;
mod live_rules;
mod mac11_catalog;
mod mac11_data;
mod scis_zoom;
mod scis_presentation;
mod scis_hud_policy;
mod scis_zoom_native;
mod scis_scene_source;
#[cfg(feature="scis-runtime-diagnostics")] mod scis_event_observer;
mod scis_frame_join;
mod scis_frame_projection;
mod scis_frame_preparation;
mod scis_frame_commit;
mod scis_frame_runtime;
mod scis_frame_native;
#[cfg(feature="scis-runtime-diagnostics")] mod scis_draw_diagnostics;
mod scis_frame_sites;
mod scis_frame_install;
#[path="../../src/input_mouse/rotation_patch_group.rs"]
mod scis_frame_patch_group;
mod scis_scene_transport;
mod scis_gpu_identity;
mod scis_gpu_device;
mod scis_gpu_pipeline;
mod scis_material_binding;
mod scis_shader_masks;
mod scis_normal_stage;
mod scis_reticle_content;
mod scis_frame_bridges;
mod melee_native;
mod player_capacity;
// Pure motion core is tested offline; native activation remains gated.
#[cfg(any(test, feature = "observer-native"))]
mod observer_camera;
#[cfg(any(test, feature = "observer-native"))]
mod observer_view;
#[cfg(any(test, feature = "observer-native"))]
mod observer_camera_runtime;
#[cfg(any(test, feature = "observer-native"))]
mod observer_input;
#[cfg(any(test, feature = "observer-native"))]
mod observer_pawn;
#[cfg(any(test, feature = "observer-native"))]
mod observer_hud;
#[cfg(feature = "observer-camera-probe")]
mod observer_camera_probe;
mod progression;
mod rank_presentation;
mod rfd_rules;
#[cfg(any(test, feature = "goggle-feedback"))]
mod rfd_interference;
mod rfd_exposure;
mod rfd_presentation;
mod rule_contract;
mod rule_defaults;
mod unlock_catalog;
mod mode_unlock;
mod mod_descriptions;
mod description_native;
mod weapon_probe;

#[used]
#[no_mangle]
pub static SCBL_GAMEPLAY_RELEASE_IDENTITY: &str = server_api::release_catalog::GAMEPLAY_MARKER;

static HOST_LOG: Mutex<Option<hooks_plugin_api::HostLogFn>> = Mutex::new(None);
static LOADOUT_SIDECAR_PATH: Mutex<Option<PathBuf>> = Mutex::new(None);

pub(crate) fn host_log(level: u32, message: &str) {
    let callback = HOST_LOG.lock().ok().and_then(|guard| *guard);
    let Some(callback) = callback else {
        return;
    };
    let Ok(length) = u32::try_from(message.len()) else {
        return;
    };
    unsafe { callback(level, message.as_ptr().cast::<c_char>(), length) };
}

struct HostWriter;

impl Write for HostWriter {
    fn write(&mut self, buffer: &[u8]) -> std::io::Result<usize> {
        let message = String::from_utf8_lossy(buffer);
        let message = message.trim();
        if !message.is_empty() {
            host_log(LOG_INFO, message);
        }
        Ok(buffer.len())
    }

    fn flush(&mut self) -> std::io::Result<()> {
        Ok(())
    }
}

struct HostMakeWriter;

impl<'a> MakeWriter<'a> for HostMakeWriter {
    type Writer = HostWriter;

    fn make_writer(&'a self) -> Self::Writer {
        HostWriter
    }
}

fn init_tracing() {
    let _ = tracing_subscriber::fmt()
        .with_ansi(false)
        .without_time()
        .with_writer(HostMakeWriter)
        .finish()
        .try_init();
}

pub(crate) fn extended_loadout_sidecar_path() -> Option<PathBuf> {
    LOADOUT_SIDECAR_PATH
        .lock()
        .ok()
        .and_then(|path| path.clone())
}

unsafe fn init_observer(world_raycast:usize)->bool {
    #[cfg(feature="observer-native")]
    {
        observer_hud::init(world_raycast);
        observer_camera_runtime::init(world_raycast);
        observer_pawn::init(world_raycast);
        return observer_hud::ready() && observer_camera_runtime::ready() && observer_pawn::ready();
    }
    #[cfg(not(feature="observer-native"))]
    {let _=world_raycast;false}
}
unsafe fn stop_observer() {
    #[cfg(feature="observer-native")]
    {observer_pawn::deinit();observer_hud::deinit();observer_camera_runtime::deinit();}
}

#[no_mangle]
pub unsafe extern "system" fn scbl_plugin_initialize(
    host: *const HostApiV1,
    config: *const PluginConfigV1,
) -> i32 {
    let Some(host) = host.as_ref() else {
        return -1;
    };
    let Some(config) = config.as_ref() else {
        return -2;
    };
    if !host_api_compatible(host) || !plugin_config_compatible(config) {
        return -3;
    }
    // LAN MOD is DX11-only. Reject before logging ownership, worker creation,
    // native memory access or player-capacity patches (which lacked this gate).
    if config.world_raycast != 0x0058_9860 {
        return -7;
    }
    let observer_required=config.feature_bits & hooks_plugin_api::observer::FEATURE_SESSION!=0;
    let gameplay_bits=config.feature_bits & !hooks_plugin_api::observer::FEATURE_SESSION;
    if (gameplay_bits!=hooks_plugin_api::GAMEPLAY_FEATURE_BITS_DEFAULT && gameplay_bits!=0)
        || (gameplay_bits==0 && !observer_required) {return -9;}
    if let Ok(mut callback) = HOST_LOG.lock() {
        *callback = host.log;
    } else {
        return -4;
    }
    init_tracing();
    if observer_required && !init_observer(config.world_raycast as usize) {
        stop_observer();host_log(hooks_plugin_api::LOG_ERROR,"SCBL_OB_BACKEND ready=false");return -8;
    }
    if gameplay_bits==0 {
        // No sidecar, workers, equipment/save hooks, stamina HUD or MOD rules.
        host_log(LOG_INFO,"SCBL_OB_BACKEND initialized scope=observer-only gameplay=false");
        return 0;
    }
    if config.loadout_sidecar_path_len > 0 && !config.loadout_sidecar_path.is_null() {
        let Ok(length) = usize::try_from(config.loadout_sidecar_path_len) else {
            return -5;
        };
        let path = std::slice::from_raw_parts(config.loadout_sidecar_path, length);
        if let Ok(mut stored) = LOADOUT_SIDECAR_PATH.lock() {
            *stored = Some(PathBuf::from(std::ffi::OsString::from_wide(path)));
        } else {
            return -6;
        }
    }
    host_log(
        LOG_INFO,
        &format!(
            "SCBL_GAMEPLAY_PLUGIN initialized abi={} feature_bits=0x{:016X}",
            config.abi_version, config.feature_bits
        ),
    );
    GAMEPLAY_ENABLED.store(true,Ordering::Release);
    live_rules::init();
    #[cfg(feature = "goggle-feedback")]
    goggle_warning_native::init(config.world_raycast as usize);
    #[cfg(feature = "goggle-feedback")]
    goggle_feedback_native::init(config.world_raycast as usize);
    chat_delivery_diag::init(config.world_raycast as usize);
    gameplay_features::init(config.world_raycast as usize);
    if let Err(reason) = player_capacity::init() {
        host_log(
            hooks_plugin_api::LOG_ERROR,
            &format!("SCBL_MOD_PLAYER_CAPACITY enabled=false reason={reason}"),
        );
    }
    extended_loadouts::init(config.world_raycast as usize);
    weapon_probe::init(config.world_raycast as usize);
    ats_counter::init(config.world_raycast as usize);
    ats_native_view::init(config.world_raycast as usize);
    sticky_camera_runtime::init();
    #[cfg(feature = "goggle-feedback")]
    goggle_bms_source::init(config.world_raycast as usize);
    #[cfg(feature = "goggle-feedback")]
    goggle_rfd_source::init(config.world_raycast as usize);
    #[cfg(feature = "native-observation")]
    r58_native_observation::init(config.world_raycast as usize);
    unsafe { ats_suppressor::init(config.world_raycast as usize); }
    equipment_native::init(config.world_raycast as usize);
    unsafe { rfd_exposure::init(config.world_raycast as usize); rfd_presentation::init(config.world_raycast as usize); }
    melee_native::init(config.world_raycast as usize);
    head_emp::init(config.world_raycast as usize);
    overload_lights::init(config.world_raycast as usize);
    // Validate the shared CB6CC0 parser while it is still pristine. The unlock
    // owner detours it below; validating afterward disables SC-IS preloading
    // and a saved SC-IS loadout can then reach the native null-resource crash.
    mac11_catalog::init(config.world_raycast as usize);
    unlock_catalog::init(config.world_raycast as usize);
    mode_unlock::init(config.world_raycast as usize);
    description_native::init(config.world_raycast as usize);
    if rank_presentation::init(config.world_raycast as usize) {
        progression::init(config.world_raycast as usize);
    }
    scis_zoom_native::init(config.world_raycast as usize);
    // Startup only: register creation identity before any native GPU resources exist.
    let frame_ready = scis_frame_install::init();
    host_log(if frame_ready { LOG_INFO } else { hooks_plugin_api::LOG_ERROR },
        &format!("SCBL_SCIS_FRAME ready={frame_ready} scope=current-native-frame"));
    drone_emp::init(config.world_raycast as usize);
    drone_cooldown::init(config.world_raycast as usize);
    drone_health::init(config.world_raycast as usize);
    drone_tags::init(config.world_raycast as usize);
    drone_fall::init(config.world_raycast as usize);
    drone_hud::init(config.world_raycast as usize);
    if !drone_control::init(config.world_raycast as usize) {
        #[cfg(feature = "native-observation")]
        drone_observation::init(config.world_raycast as usize);
    }
    weapon_probe::init_slide_cover();
    intel_mark_lifetime::init(config.world_raycast as usize);
    intel_rfd_targets::init(config.world_raycast as usize);
    thermal_smoke::init(config.world_raycast as usize);
    overload_particles::init(config.world_raycast as usize);
    thermal_targets::init(config.world_raycast as usize);
    vision_transparency::init(config.world_raycast as usize);
    vision_native_view::init(config.world_raycast as usize);
    spy_goggle_range::init(config.world_raycast as usize);
    // Shared audio-entry diagnostics must follow all gameplay signature checks.
    // Otherwise the F5 observer disables drone operator-loop cleanup at startup.
    mac11_catalog::start_sound_diagnostics();
    0
}

#[no_mangle]
pub extern "system" fn scbl_plugin_shutdown() {
    // Explicit unload is not a render-thread fence. Disable without rewriting
    // live code or releasing GPU objects under the loader lock.
    scis_frame_install::disable();
    scis_scene_source::clear();
    unsafe {stop_observer();}
    if !GAMEPLAY_ENABLED.swap(false,Ordering::AcqRel) {
        host_log(LOG_INFO,"SCBL_OB_BACKEND shutdown scope=observer-only");
        if let Ok(mut callback)=HOST_LOG.lock(){*callback=None;}
        return;
    }
    live_rules::stop();
    unsafe {
        intel_rfd_targets::deinit();
        thermal_smoke::deinit();
        vision_native_view::deinit();
        thermal_targets::deinit();
        vision_transparency::deinit();
        spy_goggle_range::deinit();
        intel_mark_lifetime::deinit();
        weapon_probe::deinit_slide_cover();
        drone_control::deinit();
        #[cfg(feature = "native-observation")]
        drone_observation::deinit();
        drone_hud::deinit();
        drone_fall::deinit();
        drone_tags::deinit();
        scis_zoom_native::deinit();
        mac11_catalog::deinit();
        progression::deinit();
        rank_presentation::deinit();
        chat_delivery_diag::deinit();
        #[cfg(feature = "goggle-feedback")]
        goggle_warning_native::deinit();
        #[cfg(feature = "goggle-feedback")]
        goggle_feedback_native::deinit();
        description_native::deinit();
        mode_unlock::deinit();
        unlock_catalog::deinit();
        overload_lights::deinit();
        head_emp::deinit();
        drone_emp::deinit();
        drone_cooldown::deinit();
        drone_health::deinit();
        melee_native::deinit();
        rfd_presentation::deinit();
        #[cfg(feature = "goggle-feedback")]
        goggle_rfd_source::deinit();
        rfd_exposure::deinit();
        equipment_native::deinit();
        #[cfg(feature = "native-observation")]
        r58_native_observation::deinit();
        #[cfg(feature = "goggle-feedback")]
        goggle_bms_source::deinit();
        ats_suppressor::deinit();
        sticky_camera_runtime::deinit();
        ats_native_view::deinit();
        ats_counter::deinit();
        player_capacity::deinit();
        extended_loadouts::deinit();
        weapon_probe::deinit();
        gameplay_features::deinit();
    }
    host_log(LOG_INFO, "SCBL_GAMEPLAY_PLUGIN shutdown");
    if let Ok(mut callback) = HOST_LOG.lock() {
        *callback = None;
    }
    if let Ok(mut path) = LOADOUT_SIDECAR_PATH.lock() {
        *path = None;
    }
}

#[no_mangle]
pub extern "system" fn scbl_plugin_handle_event(event: u32) {
    match event {
        hooks_plugin_api::EVENT_GAMEPLAY_INPUT_ALLOWED => sticky_camera_runtime::input_scope(true),
        hooks_plugin_api::EVENT_GAMEPLAY_INPUT_BLOCKED => sticky_camera_runtime::input_scope(false),
        _ => {}
    }
    if event == EVENT_NATIVE_SAVE_OPENED {
        // An open attempt (even a successful one) is not a native deserialize.
        // Do not discard unsaved shadow edits; serialize-read reloads after retail.
        return;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn unknown_or_dx9_engine_is_rejected_before_any_native_side_effect() {
        let host = HostApiV1 {
            abi_version: hooks_plugin_api::ABI_VERSION,
            struct_size: std::mem::size_of::<HostApiV1>() as u32,
            log: None,
        };
        let mut config = PluginConfigV1 {
            abi_version: hooks_plugin_api::ABI_VERSION,
            struct_size: std::mem::size_of::<PluginConfigV1>() as u32,
            feature_bits: 0,
            world_raycast: 0,
            fov_factor_bits: 1.2f32.to_bits(),
            loadout_sidecar_path: std::ptr::null(),
            loadout_sidecar_path_len: 0,
            reserved2: 0,
        };
        for unsupported in [0, 1, 0x0058_9861, u32::MAX] {
            config.world_raycast = unsupported;
            assert_eq!(unsafe { scbl_plugin_initialize(&host, &config) }, -7);
        }
    }
    #[test]
    fn rejects_missing_host_and_config() {
        assert_eq!(
            unsafe { scbl_plugin_initialize(std::ptr::null(), std::ptr::null()) },
            -1
        );
    }
}

#[cfg(test)]
mod balance_native_tests;
#[cfg(test)]
mod balance_native_adapter {
    pub const ENABLED: bool = true;
    pub unsafe fn patch(relocation:usize, active:bool) { crate::intel_disruptor::fixture_patch(relocation,active); }
    pub unsafe fn recharge(a:usize,dt:f32,n:unsafe extern "thiscall" fn(usize,f32)) {crate::drone_cooldown::fixture_recharge(a,dt,n);}
    pub fn health_emp()->bool {crate::drone_emp::fixture_health_policy()}
}

#[cfg(test)]
mod native_peer_origin_tests;

#[no_mangle]
pub extern "system" fn scbl_plugin_scope_wheel(viewport:u32,delta:i32)->i32 {
    (GAMEPLAY_ENABLED.load(Ordering::Acquire) && scis_zoom_native::wheel(viewport,delta)) as i32
}

#[no_mangle]
pub extern "system" fn scbl_plugin_bind_scope_aim_v1(abi:u32,query:Option<hooks_plugin_api::scope_aim::QueryFn>)->i32 {
    if !GAMEPLAY_ENABLED.load(Ordering::Acquire) { scis_scene_source::clear(); return 0; }
    scis_scene_source::bind(abi,query) as i32
}

#[path="../../src/scis_diagnostics.rs"] mod scis_diagnostics;

#[no_mangle]
pub extern "system" fn scbl_plugin_enable_scope_motion_v1(abi:u32)->i32 {
    mac11_catalog::scope_motion_enable(if GAMEPLAY_ENABLED.load(Ordering::Acquire){abi}else{0}) as i32
}
#[no_mangle]
pub unsafe extern "system" fn scbl_plugin_scope_motion_v1(r:*const hooks_plugin_api::scope_motion::Request,out:*mut hooks_plugin_api::scope_motion::Sample,bytes:u32)->i32 {
    use hooks_plugin_api::scope_motion::{Sample,ABI};
    if !GAMEPLAY_ENABLED.load(Ordering::Acquire)||r.is_null()||out.is_null()||bytes as usize!=std::mem::size_of::<Sample>() {return 0;}
    let request=r.read_unaligned();if !request.valid(){return 0;}
    let Some(delta)=mac11_catalog::scope_motion_query(request)else{return 0;};
    let sample=Sample{abi:ABI,size:bytes,request,delta};
    if !sample.valid_for(request){return 0;}out.write_unaligned(sample);1
}

#[no_mangle]
pub extern "system" fn scbl_plugin_enable_scope_motion_v2(abi:u32)->i32 {
    let compatible=GAMEPLAY_ENABLED.load(Ordering::Acquire)&&abi==hooks_plugin_api::scope_motion::v2::ABI;
    mac11_catalog::scope_motion_enable(if compatible{hooks_plugin_api::scope_motion::ABI}else{0}) as i32
}
#[no_mangle]
pub unsafe extern "system" fn scbl_plugin_scope_motion_v2(r:*const hooks_plugin_api::scope_motion::v2::Request,out:*mut hooks_plugin_api::scope_motion::v2::Sample,bytes:u32)->i32 {
    use hooks_plugin_api::scope_motion::v2::{Sample,ABI,POST_HANDS};
    if !GAMEPLAY_ENABLED.load(Ordering::Acquire)||r.is_null()||out.is_null()||bytes as usize!=std::mem::size_of::<Sample>(){return 0;}
    let request=r.read_unaligned();if !request.valid(){return 0;}
    let Some(delta)=mac11_catalog::scope_motion_query_phase(request.input,request.phase==POST_HANDS)else{return 0;};
    let sample=Sample{abi:ABI,size:bytes,request,delta};
    if !sample.valid_for(request){return 0;}out.write_unaligned(sample);1
}
