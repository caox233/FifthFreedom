//! Opt-in per-project toggle observer. Diagnostic packages install checked callbacks at startup;
//! F1-F7 start/stop bounded local recording. No save access, injection or network.
#![allow(dead_code)]
#[cfg(feature="scis-runtime-diagnostics")] #[path="scis_diagnostics/enabled.rs"] mod enabled;
#[cfg(feature="scis-runtime-diagnostics")] pub(crate) use enabled::{hit,sample,trace,trace_bundle,trace_motion_bundle,poll_key,active,project_active};
#[cfg(not(feature="scis-runtime-diagnostics"))] #[inline] pub(crate) fn hit(_:usize){}
#[cfg(not(feature="scis-runtime-diagnostics"))] #[inline] pub(crate) fn sample(_:usize,_:impl FnOnce()->[u32;16]){}
#[cfg(not(feature="scis-runtime-diagnostics"))] #[inline] pub(crate) fn trace(_:usize,_:impl FnOnce()->[u32;16]){}
#[cfg(not(feature="scis-runtime-diagnostics"))] #[inline] pub(crate) fn trace_bundle<const N:usize>(_:usize,_:impl FnOnce()->[[u32;16];N]){}
#[cfg(not(feature="scis-runtime-diagnostics"))] #[inline] pub(crate) fn trace_motion_bundle(_:impl FnOnce()->[[u32;16];2],_:impl FnOnce()->[[u32;16];5]){}
#[cfg(not(feature="scis-runtime-diagnostics"))] #[inline] pub(crate) fn poll_key(){}
#[cfg(not(feature="scis-runtime-diagnostics"))] #[inline] pub(crate) fn active()->bool{false}
#[cfg(not(feature="scis-runtime-diagnostics"))] #[inline] pub(crate) fn project_active(_:u8)->bool{false}
#[cfg(all(test,feature="scis-runtime-diagnostics"))] pub(crate) use enabled::test_record;
pub(crate) const SECONDS:u32=120;
pub(crate) fn within_window(elapsed_ms:u64)->bool{elapsed_ms<SECONDS as u64*1000}
pub(crate) fn take_slot(second:u32,seen:&mut u32,count:&mut u32,limit:u32)->bool{
 if second==0||second>SECONDS{return false;} if *seen!=second{*seen=second;*count=0;}
 if *count>=limit{return false;}*count+=1;true
}
#[cfg(test)]mod tests{
 use super::*;
 #[test]fn exact_window_and_per_second_budget(){
  assert!(within_window(0));assert!(within_window(119999));assert!(!within_window(120000));assert!(!within_window(u64::MAX));
  let(mut seen,mut count)=(0,0);for second in 1..=SECONDS{for _ in 0..16{assert!(take_slot(second,&mut seen,&mut count,16));}assert!(!take_slot(second,&mut seen,&mut count,16));}
  assert!(!take_slot(121,&mut seen,&mut count,16));assert!(!take_slot(0,&mut seen,&mut count,16));
 }
 #[test]fn observer_never_changes_return_values(){
  let before=[7u32;16];hit(0);sample(0,||before);trace(0,||before);assert_eq!(before,[7;16]);
 }
 #[cfg(not(feature="scis-runtime-diagnostics"))] #[test]fn disabled_never_evaluates_read_closures(){
  sample(0,||panic!("disabled read"));trace(0,||panic!("disabled read"));
  trace_bundle::<6>(128,||panic!("disabled descriptor read"));
  trace_bundle::<10>(DRAW_CONTINUITY,||panic!("disabled continuity read"));
 }
}

pub(crate) const SWAY_ENTER:usize=0;
pub(crate) const SWAY_INSPECT_REJECT:usize=1;
pub(crate) const SWAY_LEASE_REJECT:usize=2;
pub(crate) const SWAY_BONES_REJECT:usize=3;
pub(crate) const SWAY_POSE_REJECT:usize=4;
pub(crate) const SWAY_RECHECK_REJECT:usize=5;
pub(crate) const SWAY_ANCHOR_OR_LOCK:usize=6;
pub(crate) const SWAY_POSES:usize=7;
pub(crate) const SWAY_UNCHANGED:usize=8;
pub(crate) const SWAY_APPLIED:usize=9;
pub(crate) const SWAY_PALETTE:usize=10;
pub(crate) const SWAY_GATES:usize=11;
pub(crate) const ZOOM_VIEW:usize=12;
pub(crate) const ZOOM_VALUES:usize=13;
pub(crate) const SOURCE_OWNER_REJECT:usize=14;
pub(crate) const SOURCE_SCOPE_REJECT:usize=15;
pub(crate) const SOURCE_LAYER_REJECT:usize=16;
pub(crate) const SOURCE_ADS_REJECT:usize=17;
pub(crate) const SOURCE_INSPECT_REJECT:usize=18;
pub(crate) const SOURCE_QUERY_REJECT:usize=19;
pub(crate) const SOURCE_SAMPLE_REJECT:usize=20;
pub(crate) const SOURCE_RECHECK_REJECT:usize=21;
pub(crate) const SOURCE_ACCEPTED:usize=22;
pub(crate) const SOURCE_UNBOUND:usize=23;
pub(crate) const SOURCE_ENTRY:usize=24;
pub(crate) const SOURCE_BOUND:usize=25;
pub(crate) const SOURCE_NOT_BOUND:usize=26;
pub(crate) const QUEUE_PUBLISHED:usize=27;
pub(crate) const QUEUE_PUBLISH_REJECT:usize=28;
pub(crate) const QUEUE_DRAINED:usize=29;
pub(crate) const QUEUE_DRAIN_REJECT:usize=30;
pub(crate) const QUEUE_ADMITTED:usize=31;
pub(crate) const QUEUE_ADMIT_REJECT:usize=32;
pub(crate) const RENDER_ENTER:usize=33;
pub(crate) const RENDER_POINTER_REJECT:usize=34;
pub(crate) const RENDER_BATCH_MISSING:usize=35;
pub(crate) const RENDER_SELECTION_REJECT:usize=36;
pub(crate) const RENDER_JOINED:usize=37;
pub(crate) const RENDER_COMMITTED:usize=38;
pub(crate) const RENDER_COMMIT_REJECT:usize=39;
pub(crate) const PREPARE_SETTINGS_REJECT:usize=40;
pub(crate) const PREPARE_RETICLE_REJECT:usize=41;
pub(crate) const PREPARE_POSE_REJECT:usize=42;
pub(crate) const PREPARE_SLOT_REJECT:usize=43;
pub(crate) const PREPARE_CAMERA:usize=44;
pub(crate) const PREPARE_CAMERA_REJECT:usize=45;
pub(crate) const PREPARE_PROJECTION_REJECT:usize=46;
pub(crate) const PREPARE_SUCCESS:usize=47;
pub(crate) const MAIN_RENDER_ENTER:usize=48;
pub(crate) const MAIN_RENDER_VTABLE_REJECT:usize=49;
pub(crate) const IDENTIFY_ENTER:usize=50;
pub(crate) const IDENTIFY_ALPHA_REJECT:usize=51;
pub(crate) const IDENTIFY_DRAW_REJECT:usize=52;
pub(crate) const IDENTIFY_DRAW_SELECTED:usize=53;
pub(crate) const IDENTIFY_RETICLE_FOUND:usize=54;
pub(crate) const INPUT_FRAME:usize=55;
pub(crate) const PUBLISHER_BEGIN_TOKEN:usize=56;
pub(crate) const PUBLISHER_BEGIN_MISSING:usize=57;
pub(crate) const PUBLISHER_END_TOKEN:usize=58;
pub(crate) const PUBLISHER_END_MISSING:usize=59;
pub(crate) const PUBLISHER_CAPTURED:usize=60;
pub(crate) const PUBLISHER_CAPTURE_REJECT:usize=61;
pub(crate) const PUBLICATION_INACTIVE_OR_AIM:usize=62;
pub(crate) const PUBLICATION_MISSING:usize=63;
pub(crate) const PUBLICATION_SNAPSHOT_REJECT:usize=64;
pub(crate) const VIEW_ACCEPTED:usize=65;
pub(crate) const VIEW_FRAME:usize=66;
pub(crate) const VIEW_REJECTED:usize=67;
pub(crate) const VIEW_TOKEN_STALE:usize=68;
pub(crate) const VIEW_CAPTURE_ABSENT:usize=69;
pub(crate) const VIEW_CAPTURE_CHANGED:usize=70;
pub(crate) const VIEW_SCENE_OWNER_REJECT:usize=71;
pub(crate) const VIEW_SCENE_POSE_REJECT:usize=72;
pub(crate) const VIEW_POSE_MISMATCH:usize=73;
pub(crate) const QUERY_ENTER:usize=74;
pub(crate) const QUERY_PUBLICATION_REJECT:usize=75;
pub(crate) const QUERY_RAY_REJECT:usize=76;
pub(crate) const QUERY_ASSEMBLE_REJECT:usize=77;
pub(crate) const QUERY_ACCEPTED:usize=78;
pub(crate) const PRECISION_CONVERT:usize=79;
pub(crate) const PRECISION_ROUNDTRIP:usize=80;
pub(crate) const RAY_NESTED_REJECT:usize=81;
pub(crate) const RAY_FRAME_REJECT:usize=82;
pub(crate) const RAY_CACHE_REJECT:usize=83;
pub(crate) const AIM_PRODUCER_ENTER:usize=84;
pub(crate) const AIM_PRODUCER_OWNED:usize=85;
pub(crate) const AIM_PRODUCER_REJECT:usize=86;
pub(crate) const AIM_PRODUCER_COMPLETED:usize=87;
pub(crate) const SOURCE_SCENE_FIELDS:usize=88;
pub(crate) const RAW_INPUT_FRAME:usize=89;
pub(crate) const NAMES:&[&str]=&["sway_enter", "sway_inspect_reject", "sway_lease_reject", "sway_bones_reject", "sway_pose_reject", "sway_recheck_reject", "sway_anchor_or_lock", "sway_poses", "sway_unchanged", "sway_applied", "sway_palette", "sway_gates", "zoom_view", "zoom_values", "source_owner_reject", "source_scope_reject", "source_layer_reject", "source_ads_reject", "source_inspect_reject", "source_query_reject", "source_sample_reject", "source_recheck_reject", "source_accepted", "source_unbound", "source_entry", "source_bound", "source_not_bound", "queue_published", "queue_publish_reject", "queue_drained", "queue_drain_reject", "queue_admitted", "queue_admit_reject", "render_enter", "render_pointer_reject", "render_batch_missing", "render_selection_reject", "render_joined", "render_committed", "render_commit_reject", "prepare_settings_reject", "prepare_reticle_reject", "prepare_pose_reject", "prepare_slot_reject", "prepare_camera", "prepare_camera_reject", "prepare_projection_reject", "prepare_success", "main_render_enter", "main_render_vtable_reject", "identify_enter", "identify_alpha_reject", "identify_draw_reject", "identify_draw_selected", "identify_reticle_found", "input_frame", "publisher_begin_token", "publisher_begin_missing", "publisher_end_token", "publisher_end_missing", "publisher_captured", "publisher_capture_reject", "publication_inactive_or_aim", "publication_missing", "publication_snapshot_reject", "view_accepted", "view_frame", "view_rejected", "view_token_stale", "view_capture_absent", "view_capture_changed", "view_scene_owner_reject", "view_scene_pose_reject", "view_pose_mismatch", "query_enter", "query_publication_reject", "query_ray_reject", "query_assemble_reject", "query_accepted", "precision_convert", "precision_roundtrip", "ray_nested_reject", "ray_frame_reject", "ray_cache_reject", "aim_producer_enter", "aim_producer_owned", "aim_producer_reject", "aim_producer_completed", "source_scene_fields", "raw_input_frame", "alpha_reads_0", "alpha_reads_1", "alpha_reads_2", "alpha_reads_3", "alpha_reads_4", "alpha_reads_5", "alpha_reads_6", "alpha_reads_7", "view_render_frame", "view_owner_mismatch", "draw_native_0", "draw_native_1", "draw_native_2", "draw_native_3", "draw_native_4", "draw_native_5", "draw_native_6", "draw_native_7", "draw_native_8", "draw_native_9", "draw_native_10", "draw_native_11", "draw_native_12", "draw_native_13", "draw_native_14", "draw_native_15", "final_render_basis", "final_render_context", "final_aim_basis", "identify_entry_context", "draw_camera", "projection_result", "stage_guard_reads", "stage_membership", "draw_view_basis", "draw_record_basis", "draw_view_context", "reticle_gpu_proof", "gpu_detail_status", "gpu_detail_draw", "gpu_detail_vb", "gpu_detail_ib", "gpu_detail_srv", "gpu_detail_texture", "chat_send", "chat_gate", "chat_receive", "sound_request", "sound_route_before", "sound_route_after", "sound_event", "sound_stop", "animation_event_before", "animation_event_after", "animation_state", "animation_graph", "animation_reject", "ads_place_before", "ads_place_native", "ads_place_final", "ads_scene", "draw_continuity_header", "draw_continuity_selection", "draw_continuity_node", "draw_continuity_object_cache", "draw_continuity_secondary", "draw_continuity_view", "draw_continuity_primary_cache", "draw_continuity_secondary_cache", "draw_continuity_relative", "draw_continuity_extra", "ads_lifecycle_before", "ads_lifecycle_native", "ads_motion", "ads_motion_state", "ads_partial_palette", "ads_partial_raw", "ads_partial_head", "ads_partial_gun", "ads_partial_placement"];

pub(crate) const ALPHA_READS_BASE:usize=90;

pub(crate) const VIEW_RENDER_FRAME:usize=98;
pub(crate) const VIEW_OWNER_MISMATCH:usize=99;
pub(crate) const DRAW_NATIVE_BASE:usize=100;

pub(crate) const FINAL_RENDER_BASIS:usize=116;
pub(crate) const FINAL_RENDER_CONTEXT:usize=117;
pub(crate) const FINAL_AIM_BASIS:usize=118;
pub(crate) const IDENTIFY_ENTRY_CONTEXT:usize=119;
pub(crate) const DRAW_CAMERA:usize=120;
pub(crate) const PROJECTION_RESULT:usize=121;

pub(crate) const STAGE_GUARD_READS:usize=122;
pub(crate) const STAGE_MEMBERSHIP:usize=123;
pub(crate) const DRAW_VIEW_BASIS:usize=124;
pub(crate) const DRAW_RECORD_BASIS:usize=125;
pub(crate) const DRAW_VIEW_CONTEXT:usize=126;

// State1=shader/layout,2=device,3=unknown content,4=mismatch,5=content accepted.
pub(crate) const RETICLE_GPU_PROOF:usize=127;
// Six adjacent rows form one atomic observation, sharing version/sequence.
pub(crate) const RETICLE_GPU_DETAIL:usize=128;

// Categorical transport boundaries only: no message content or identity.
pub(crate) const CHAT_SEND:usize=134;
pub(crate) const CHAT_GATE:usize=135;
pub(crate) const CHAT_RECEIVE:usize=136;

// Only F5 admits these rows; no automatic sampling.
pub(crate) const SOUND_REQUEST:usize=137;
pub(crate) const SOUND_ROUTE_BEFORE:usize=138;
pub(crate) const SOUND_ROUTE_AFTER:usize=139;
pub(crate) const SOUND_EVENT:usize=140;
pub(crate) const SOUND_STOP:usize=141;

// Manual F6/F7, native state only. Missing values are u32::MAX, not zero.
pub(crate) const ANIMATION_EVENT_BEFORE:usize=142;
pub(crate) const ANIMATION_EVENT_AFTER:usize=143;
pub(crate) const ANIMATION_STATE:usize=144;
pub(crate) const ANIMATION_GRAPH:usize=145;
pub(crate) const ANIMATION_REJECT:usize=146;
pub(crate) const ADS_PLACE_BEFORE:usize=147;
pub(crate) const ADS_PLACE_NATIVE:usize=148;
pub(crate) const ADS_PLACE_FINAL:usize=149;
pub(crate) const ADS_SCENE:usize=150;

// Dedicated manual F2 ten-row atomic CPU-cache observation; not GPU readback.
pub(crate) const DRAW_CONTINUITY:usize=151;
pub(crate) const DRAW_CONTINUITY_END:usize=161;
pub(crate) const ADS_LIFECYCLE_BEFORE:usize=161;
pub(crate) const ADS_LIFECYCLE_NATIVE:usize=162;
pub(crate) const ADS_MOTION:usize=163;
pub(crate) const ADS_MOTION_STATE:usize=164;
pub(crate) const ADS_PARTIAL:usize=165;
pub(crate) const ADS_PARTIAL_END:usize=170;
pub(crate) fn bundle_shape(base:usize,n:usize)->bool{
    if n==0||base.checked_add(n).is_none_or(|end|end>NAMES.len()){return false;}
    if base<ADS_PARTIAL&&base+n>ADS_PARTIAL{return false;}
    if base>=ADS_PARTIAL{return base==ADS_PARTIAL&&n==5;}
    if base>=DRAW_CONTINUITY&&base<DRAW_CONTINUITY_END{return base==DRAW_CONTINUITY&&n==10;}
    n<=8 && (base+n<=DRAW_CONTINUITY||base>=DRAW_CONTINUITY_END)
}
