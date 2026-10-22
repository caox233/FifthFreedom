use std::collections::HashMap;
use std::ffi::c_void;
use std::sync::Mutex;
use std::sync::OnceLock;

use retour::static_detour;
use tracing::error;
use tracing::info;

const DX11_WEAPON_TRACE_DISPATCH: usize = 0x00E2_B940;
// Refreshes the current weapon attachment position and returns weapon_state+0x220.
// The retail routine resolves the configured weapon bone through 0x006B5D20,
// then applies the weapon-specific muzzle offset. The same position is used by
// the projectile and firing-effect paths.
const DX11_WEAPON_MUZZLE_POSITION: usize = 0x00DF_FDA0;
// Synchronous, filtered world query used throughout the retail game. Unlike the
// lower-level world raycast, this wrapper removes the supplied actor hierarchy
// from the hit list and restores the engine's temporary query context.
const DX11_FILTERED_RAYCAST: usize = 0x005A_BC80;
// The weapon trace uses the sibling submit wrapper at 0x009243A0. That
// wrapper adds 0x04000000 to the caller's 0x09400004 mask, producing the
// 0x0D400004 value observed later on the deferred raycast worker thread.
const OBSTRUCTION_QUERY_MASK: usize = 0x0030_0400;
const OBSTRUCTION_QUERY_MATERIAL: usize = 0x029A_2AD0;
const DX11_ZERO_VECTOR: usize = 0x0337_F25C;
const QUERY_CONTEXT_MAX_AGE_MS: u64 = 2_500;
const MAX_QUERY_CONTEXTS: usize = 64;
// Retail UESEQJobMPIsPawnSpy / UESEQJobMPIsPawnMerc read these exact bits
// from the multiplayer pawn gameplay flags at +0x374.
const PAWN_GAMEPLAY_FLAGS_OFFSET: usize = 0x374;
const PAWN_IS_SPY: u32 = 1 << 18;
const PAWN_IS_MERC: u32 = 1 << 19;

#[repr(C)]
#[derive(Clone, Copy, Debug)]
struct FVector {
    x: f32,
    y: f32,
    z: f32,
}

#[derive(Clone, Copy, Debug)]
struct QueryContext {
    timestamp_ms: u64,
    query_owner: usize,
    query_filter: usize,
}

#[derive(Clone, Copy, Debug)]
struct ObstructionQueryResult {
    blocked: bool,
    hit_position: Option<FVector>,
}

static QUERY_CONTEXTS: OnceLock<Mutex<HashMap<usize, QueryContext>>> = OnceLock::new();

static_detour! {
    static WorldRaycastHook: unsafe extern "thiscall" fn(
        *mut c_void,
        usize,
        *const FVector,
        *const FVector,
        usize,
        usize,
        usize,
        usize,
        usize,
        usize,
        usize,
    ) -> *mut c_void;

    static WeaponTraceDispatchHook: unsafe extern "thiscall" fn(
        *mut c_void,
        *mut u8,
    );
}

#[link(name = "kernel32")]
extern "system" {
    fn GetTickCount64() -> u64;
}

fn query_contexts() -> &'static Mutex<HashMap<usize, QueryContext>> {
    QUERY_CONTEXTS.get_or_init(|| Mutex::new(HashMap::new()))
}

fn read_vector(vector: *const FVector) -> Option<FVector> {
    if vector.is_null() || !vector.is_aligned() {
        return None;
    }
    // These are the same start/end pointers dereferenced unconditionally by the
    // original retail routine. Reading them before forwarding does not broaden
    // the set of addresses touched by the game.
    Some(unsafe { vector.read() })
}

fn read_pointer(base: *const u8, offset: usize) -> Option<*const u8> {
    if base.is_null() {
        return None;
    }
    let value = unsafe { base.add(offset).cast::<usize>().read_unaligned() } as *const u8;
    (!value.is_null()).then_some(value)
}

fn read_vector_at(base: *const u8, offset: usize) -> Option<FVector> {
    if base.is_null() {
        return None;
    }
    read_vector(unsafe { base.add(offset).cast::<FVector>() })
}

fn read_u32_at(base: *const u8, offset: usize) -> Option<u32> {
    if base.is_null() {
        return None;
    }
    Some(unsafe { base.add(offset).cast::<u32>().read_unaligned() })
}

fn usable_vector(value: FVector) -> bool {
    value.x.is_finite() && value.y.is_finite() && value.z.is_finite()
}

fn distance_squared(left: FVector, right: FVector) -> f32 {
    let x = left.x - right.x;
    let y = left.y - right.y;
    let z = left.z - right.z;
    x * x + y * y + z * z
}

fn pawn_roles(flags: Option<u32>) -> (bool, bool) {
    let flags = flags.unwrap_or_default();
    (flags & PAWN_IS_SPY != 0, flags & PAWN_IS_MERC != 0)
}

fn reroute_trace_from_muzzle(trace: *mut u8, muzzle: FVector, target: FVector) -> Option<(FVector, FVector)> {
    if trace.is_null() || !usable_vector(muzzle) || !usable_vector(target) {
        return None;
    }
    let original_near = read_vector_at(trace, 4)?;
    let original_far = read_vector_at(trace, 0x10)?;
    unsafe {
        // 0x00E2B940 submits trace+0x10 as the ray start and trace+4 as
        // the ray end. Reversing those fields makes the retail async query
        // travel from the real muzzle toward the original crosshair target.
        trace.add(4).cast::<FVector>().write_unaligned(target);
        trace.add(0x10).cast::<FVector>().write_unaligned(muzzle);
    }
    Some((original_near, original_far))
}

fn restore_trace(trace: *mut u8, original_near: FVector, original_far: FVector) {
    unsafe {
        trace.add(4).cast::<FVector>().write_unaligned(original_near);
        trace.add(0x10).cast::<FVector>().write_unaligned(original_far);
    }
}

fn remember_query_context(actor: usize, context: QueryContext) {
    if actor == 0 || context.query_owner == 0 || context.query_filter == 0 {
        return;
    }
    if let Ok(mut contexts) = query_contexts().lock() {
        if !contexts.contains_key(&actor) && contexts.len() >= MAX_QUERY_CONTEXTS {
            if let Some(oldest) = contexts.iter().min_by_key(|(_, value)| value.timestamp_ms).map(|(actor, _)| *actor) {
                contexts.remove(&oldest);
            }
        }
        contexts.insert(actor, context);
    }
}

fn query_context_for(actor: usize, now_ms: u64) -> Option<(QueryContext, u64)> {
    let context = query_contexts().lock().ok()?.get(&actor).copied()?;
    let age_ms = now_ms.saturating_sub(context.timestamp_ms);
    (age_ms <= QUERY_CONTEXT_MAX_AGE_MS).then_some((context, age_ms))
}

fn should_apply_spy_obstruction_fix(spy_weapon_trace: bool, camera_query: Option<ObstructionQueryResult>, muzzle_query: Option<ObstructionQueryResult>) -> bool {
    spy_weapon_trace && camera_query.is_some_and(|result| !result.blocked) && muzzle_query.is_some_and(|result| result.blocked)
}

fn clipped_impact_target(muzzle: FVector, target: FVector, hit: FVector) -> Option<FVector> {
    if !usable_vector(muzzle) || !usable_vector(target) || !usable_vector(hit) {
        return None;
    }
    let direction = FVector {
        x: target.x - muzzle.x,
        y: target.y - muzzle.y,
        z: target.z - muzzle.z,
    };
    let length_squared = direction.x * direction.x + direction.y * direction.y + direction.z * direction.z;
    if length_squared < 1.0 {
        return None;
    }
    let to_hit = FVector {
        x: hit.x - muzzle.x,
        y: hit.y - muzzle.y,
        z: hit.z - muzzle.z,
    };
    let projection = to_hit.x * direction.x + to_hit.y * direction.y + to_hit.z * direction.z;
    if projection < 0.0 || projection > length_squared {
        return None;
    }

    let length = length_squared.sqrt();
    let remaining = distance_squared(hit, target).sqrt();
    let overshoot = 2.0_f32.min(remaining * 0.25);
    Some(FVector {
        x: hit.x + direction.x / length * overshoot,
        y: hit.y + direction.y / length * overshoot,
        z: hit.z + direction.z / length * overshoot,
    })
}

fn read_muzzle_position(weapon_state: *mut c_void) -> Option<FVector> {
    if weapon_state.is_null() {
        return None;
    }
    let getter: unsafe extern "thiscall" fn(*mut c_void) -> *const FVector = unsafe { std::mem::transmute(DX11_WEAPON_MUZZLE_POSITION) };
    let value = read_vector(unsafe { getter(weapon_state) })?;
    usable_vector(value).then_some(value)
}

fn query_obstruction(context: QueryContext, origin: FVector, target: FVector) -> Option<ObstructionQueryResult> {
    if context.query_owner == 0 || !usable_vector(origin) || !usable_vector(target) || distance_squared(origin, target) < 1.0 {
        return None;
    }

    type FilteredRaycast = unsafe extern "thiscall" fn(*mut c_void, *mut u8, usize, *const FVector, *const FVector, usize, usize, usize) -> u8;

    let query: FilteredRaycast = unsafe { std::mem::transmute(DX11_FILTERED_RAYCAST) };
    let mut hit = [0_u8; 0x48];
    let clear = unsafe {
        query(
            context.query_owner as *mut c_void,
            hit.as_mut_ptr(),
            context.query_filter,
            &origin,
            &target,
            OBSTRUCTION_QUERY_MASK,
            OBSTRUCTION_QUERY_MATERIAL,
            DX11_ZERO_VECTOR,
        )
    };
    let blocked = clear == 0;
    let hit_position = blocked.then(|| read_vector_at(hit.as_ptr(), 8)).flatten().filter(|value| usable_vector(*value));
    Some(ObstructionQueryResult { blocked, hit_position })
}

fn weapon_trace_dispatch(weapon_state: *mut c_void, trace: *mut u8) {
    let now_ms = unsafe { GetTickCount64() };
    if trace.is_null() {
        unsafe { WeaponTraceDispatchHook.call(weapon_state, trace) };
        return;
    }

    let state = weapon_state.cast::<u8>();
    let owner = read_pointer(state, 0x28);
    let pawn = owner.and_then(|value| read_pointer(value, 0x8c));
    let trace_near = read_vector_at(trace, 4);
    let trace_far = read_vector_at(trace, 0x10);
    let trace_actor = read_pointer(trace, 0).map(|value| value as usize).unwrap_or_default();
    let pawn_flags = pawn
        .filter(|value| *value as usize == trace_actor)
        .and_then(|value| read_u32_at(value, PAWN_GAMEPLAY_FLAGS_OFFSET));
    let (is_spy, is_merc) = pawn_roles(pawn_flags);
    let spy_weapon_trace = is_spy && !is_merc;

    // World queries continuously provide the retail scene-query owner and the
    // actor hierarchy that must be filtered. Cache those values by actor so
    // this path does not depend on a particular body/cover query being emitted
    // by the current map or stance. The authoritative result is a synchronous
    // pair of queries from the camera and the refreshed weapon muzzle to the
    // aim target. We intervene only when the camera ray is confirmed clear and
    // the muzzle ray is blocked. A wall hit by both rays is ordinary wall
    // aiming and must retain the retail trace, impact decal, and crosshair
    // alignment. Missing or stale context fails open and preserves retail
    // behavior.
    let query_context = spy_weapon_trace.then(|| query_context_for(trace_actor, now_ms)).flatten();
    let muzzle = query_context.and_then(|_| read_muzzle_position(weapon_state));
    let muzzle_query = match (query_context, muzzle, trace_far) {
        (Some((context, _)), Some(muzzle), Some(target)) => query_obstruction(context, muzzle, target),
        _ => None,
    };
    let camera_query = match (query_context, trace_near, trace_far) {
        (Some((context, _)), Some(camera), Some(target)) => query_obstruction(context, camera, target),
        _ => None,
    };
    let blocked = should_apply_spy_obstruction_fix(spy_weapon_trace, camera_query, muzzle_query);
    let obstruction_hit = muzzle_query.and_then(|result| result.hit_position);
    let clipped_target = match (blocked, muzzle, trace_far, obstruction_hit) {
        (true, Some(muzzle), Some(target), Some(hit)) => clipped_impact_target(muzzle, target, hit),
        _ => None,
    };

    if blocked {
        if let (Some(muzzle), Some(clipped_target)) = (muzzle, clipped_target) {
            if let Some((original_near, original_far)) = reroute_trace_from_muzzle(trace, muzzle, clipped_target) {
                unsafe { WeaponTraceDispatchHook.call(weapon_state, trace) };
                restore_trace(trace, original_near, original_far);
                return;
            }
        }
        // A confirmed obstruction without a trustworthy hit position must not
        // fall back to the retail camera trace and damage the player behind it.
        return;
    }
    unsafe { WeaponTraceDispatchHook.call(weapon_state, trace) };
}

#[allow(clippy::too_many_arguments)]
fn world_raycast(
    owner: *mut c_void,
    arg1: usize,
    start: *const FVector,
    end: *const FVector,
    arg4: usize,
    arg5: usize,
    arg6: usize,
    arg7: usize,
    arg8: usize,
    arg9: usize,
    arg10: usize,
) -> *mut c_void {
    let now_ms = unsafe { GetTickCount64() };
    let result = unsafe { WorldRaycastHook.call(owner, arg1, start, end, arg4, arg5, arg6, arg7, arg8, arg9, arg10) };

    if !owner.is_null() && arg7 == OBSTRUCTION_QUERY_MATERIAL && arg8 != 0 {
        remember_query_context(
            arg8,
            QueryContext {
                timestamp_ms: now_ms,
                query_owner: owner as usize,
                query_filter: arg8,
            },
        );
    }

    result
}

pub unsafe fn init(world_raycast_address: usize) {
    if world_raycast_address == 0 {
        error!("SCBL_WEAPON_OBSTRUCTION_FIX unavailable for this executable");
        return;
    }
    let address = world_raycast_address;
    if address != 0x0058_9860 {
        error!("SCBL_WEAPON_OBSTRUCTION_FIX unsupported world_raycast={address:08X}");
        return;
    }

    let target = std::mem::transmute(address);
    if let Err(err) = WorldRaycastHook.initialize(target, world_raycast).and_then(|hook| hook.enable()) {
        error!("SCBL_WEAPON_OBSTRUCTION_FIX failed stage=world-raycast address={address:08X} error={err:?}");
        return;
    }

    let dispatch_target = std::mem::transmute(DX11_WEAPON_TRACE_DISPATCH);
    if let Err(err) = WeaponTraceDispatchHook.initialize(dispatch_target, weapon_trace_dispatch).and_then(|hook| hook.enable()) {
        error!("SCBL_WEAPON_OBSTRUCTION_FIX failed stage=weapon-dispatch address={DX11_WEAPON_TRACE_DISPATCH:08X} error={err:?}");
        let _ = WorldRaycastHook.disable();
        return;
    }

    info!(
        "SCBL_WEAPON_OBSTRUCTION_FIX enabled=true world_raycast={address:08X} weapon_dispatch={DX11_WEAPON_TRACE_DISPATCH:08X} query_context_max_age_ms={QUERY_CONTEXT_MAX_AGE_MS}"
    );
}

pub unsafe fn deinit() {
    if let Ok(mut contexts) = query_contexts().lock() {
        contexts.clear();
    }
    if WeaponTraceDispatchHook.is_enabled() {
        let _ = WeaponTraceDispatchHook.disable();
    }
    if WorldRaycastHook.is_enabled() {
        let _ = WorldRaycastHook.disable();
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn vector(x: f32, y: f32, z: f32) -> FVector {
        FVector { x, y, z }
    }

    #[test]
    fn context_is_matched_by_actor_and_expires() {
        let actor = 0x1234;
        remember_query_context(
            actor,
            QueryContext {
                timestamp_ms: 1_000,
                query_owner: 1,
                query_filter: actor,
            },
        );
        assert!(query_context_for(actor, 1_100).is_some());
        assert!(query_context_for(actor + 1, 1_100).is_none());
        assert!(query_context_for(actor, 4_000).is_none());
    }

    #[test]
    fn only_camera_clear_and_muzzle_blocked_spy_shots_are_fixed() {
        let clear = Some(ObstructionQueryResult {
            blocked: false,
            hit_position: None,
        });
        let blocked = Some(ObstructionQueryResult {
            blocked: true,
            hit_position: None,
        });

        assert!(should_apply_spy_obstruction_fix(true, clear, blocked));
        assert!(!should_apply_spy_obstruction_fix(true, blocked, blocked));
        assert!(!should_apply_spy_obstruction_fix(true, clear, clear));
        assert!(!should_apply_spy_obstruction_fix(false, clear, blocked));
        assert!(!should_apply_spy_obstruction_fix(true, None, blocked));
        assert!(!should_apply_spy_obstruction_fix(true, clear, None));
        assert_eq!(distance_squared(vector(0.0, 0.0, 0.0), vector(3.0, 4.0, 0.0)), 25.0);
    }

    #[test]
    fn clipped_target_stops_just_beyond_the_wall_hit() {
        let muzzle = vector(0.0, 0.0, 0.0);
        let target = vector(100.0, 0.0, 0.0);
        let clipped = clipped_impact_target(muzzle, target, vector(25.0, 0.0, 0.0)).unwrap();
        assert_eq!(clipped.x, 27.0);
        assert_eq!(clipped.y, 0.0);
        assert_eq!(clipped.z, 0.0);
        assert!(clipped_impact_target(muzzle, target, vector(-1.0, 0.0, 0.0)).is_none());
        assert!(clipped_impact_target(muzzle, target, vector(101.0, 0.0, 0.0)).is_none());
    }

    #[test]
    fn retail_pawn_role_bits_scope_the_fix_to_spies() {
        assert_eq!(pawn_roles(Some(PAWN_IS_SPY)), (true, false));
        assert_eq!(pawn_roles(Some(PAWN_IS_MERC)), (false, true));
        assert_eq!(pawn_roles(Some(0)), (false, false));
        assert_eq!(pawn_roles(None), (false, false));
    }

    #[test]
    fn blocked_trace_is_rerouted_from_muzzle_and_restored() {
        let mut trace = [0_u8; 0x1c];
        let near = vector(1.0, 2.0, 3.0);
        let far = vector(10.0, 20.0, 30.0);
        let muzzle = vector(4.0, 5.0, 6.0);
        unsafe {
            trace.as_mut_ptr().add(4).cast::<FVector>().write_unaligned(near);
            trace.as_mut_ptr().add(0x10).cast::<FVector>().write_unaligned(far);
        }

        let originals = reroute_trace_from_muzzle(trace.as_mut_ptr(), muzzle, far).unwrap();
        assert_eq!(distance_squared(read_vector_at(trace.as_ptr(), 0x10).unwrap(), muzzle), 0.0);
        assert_eq!(distance_squared(read_vector_at(trace.as_ptr(), 4).unwrap(), far), 0.0);

        restore_trace(trace.as_mut_ptr(), originals.0, originals.1);
        assert_eq!(distance_squared(read_vector_at(trace.as_ptr(), 4).unwrap(), near), 0.0);
        assert_eq!(distance_squared(read_vector_at(trace.as_ptr(), 0x10).unwrap(), far), 0.0);
    }
}
