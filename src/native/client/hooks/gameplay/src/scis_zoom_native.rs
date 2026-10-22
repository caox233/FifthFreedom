//! SC-IS scene-view and wheel adapter; retail addresses are signature gated.
use crate::scis_diagnostics as diag;
use crate::scis_zoom::{project_fov, Session, Zoom};

// No native pointers are retained. Recheck the live possession/view/weapon chain
// synchronously on the render/input caller. A failed guard means retail behavior.
fn inspect(
    viewport: usize,
    world: usize,
    read: impl Fn(usize, usize) -> Option<u32>,
    name: impl Fn(u32) -> bool,
) -> Option<Session> {
    if world < 0x10000 || read(viewport, 0)? != 0x02c4ea4c {
        return None;
    }
    let actor = read(viewport, 0x30)? as usize;
    let pc = match read(actor, 0)? {
        0x02ac53ec => actor,
        0x02ac2064 => read(read(actor, 0x61c)? as usize, 0x20)? as usize,
        _ => return None,
    };
    let generation = read(pc, 0x20)?;
    let pawn = read(pc, 0x520)? as usize;
    let camera = read(pc, 0x6a4)? as usize;
    let core = read(camera, 0x61c)? as usize;
    let mode = read(core, 0x2c)? as usize;
    if generation == 0
        || read(pc, 0)? != 0x02ac53ec
        || read(pc, 0x60)? & 0x40000 == 0
        || read(pc, 0x4c)? & 0xff00 == 0x200
        || read(pc, 0x144)? as usize != world
        || read(pc, 0x354)? as usize != viewport
        || (actor != pc && actor != camera)
        || read(pc, 0x310)? as usize != pawn
        || read(pawn, 0)? != 0x02ad2abc
        || read(pawn, 0x938)? as usize != pc
        || read(pawn, 0x144)? as usize != world
        || read(pawn, 0x374)? & 0xe0000 != 0xa0000
        || read(camera, 0)? != 0x02ac2064
        || read(camera, 0x144)? as usize != world
        || read(core, 0x1c)? as usize != camera
        || read(core, 0x20)? as usize != pc
        || read(core, 0x24)? as usize != pawn
        || read(mode, 4)? as usize != core
        || read(mode, 0x20)? & 1 == 0
    {
        return None;
    }
    // D3B7D0 consumes this only in applyScopeFOVMultiplier mode. This is a
    // native scope-state gate, not a guessed screen-FOV cutoff or key poll.
    let scope = f32::from_bits(read(core, 0x14a8)?);
    if !scope.is_finite() || scope <= 0.0 || scope >= 1.0 {
        return None;
    }
    let input = read(pawn, 0x93c)? as usize;
    let selected = read(input, 0x324)? as usize;
    let weapon = if selected != 0 {
        selected
    } else {
        read(pawn, 0x4b8)? as usize
    };
    let inventory = read(pawn, 0x1410)? as usize;
    let props = read(weapon, 0x744)? as usize;
    if weapon == 0
        || read(inventory, 0x50)? as usize != weapon
        || read(props, 0)? != 0x02a176c4
        || read(props, 0x38)? >> 24 != 1
        || !name(read(weapon, 0x740)?)
    {
        return None;
    }
    Some(Session {
        world,
        controller: pc,
        generation,
    })
}

use crate::equipment_native::{signature, word};
use retour::static_detour;
use std::sync::{
    atomic::{AtomicBool, AtomicU32, Ordering},
    Mutex,
};
static READY: AtomicBool = AtomicBool::new(false);
static STATE: Mutex<Zoom> = Mutex::new(Zoom::new());
static LOGGED: AtomicU32 = AtomicU32::new(0);
static PROJECTION_LOGGED: AtomicU32 = AtomicU32::new(0);
static LAST_PROJECTION_FACTOR: AtomicU32 = AtomicU32::new(0);
type ViewFn =
    unsafe extern "thiscall" fn(usize, usize, usize, usize, usize, f32, f32, f32, f32) -> usize;
static_detour! {static View:unsafe extern "thiscall" fn(usize,usize,usize,usize,usize,f32,f32,f32,f32)->usize;}
unsafe fn owner(viewport: usize) -> Option<Session> {
    let info = word(0x339e5a0, 0)? as usize;
    if word(info, 0x330)? & 0x10 == 0 {
        return None;
    }
    inspect(
        viewport,
        word(info, 0x144)? as usize,
        |p, o| word(p, o),
        |id| crate::mac11_catalog::scis_name(id),
    )
}

// These arguments belong to a single transient scene constructor call. Never
// modify actor FOV, shared camera state, scope assets or last frame's output.
#[derive(Clone, Copy, Debug, PartialEq)]
struct Args {
    out: usize,
    viewport: usize,
    actor: usize,
    position: usize,
    rotation: usize,
    fov: f32,
    secondary_fov: f32,
    near: f32,
    far: f32,
}
fn render_with(mut args: Args, factor: Option<f32>, original: impl FnOnce(Args) -> usize) -> usize {
    if let Some(f) = factor.and_then(|f| project_fov(args.fov, f)) {
        // The owned optic uses the native first-person projection group. Keep
        // its original scoped projection; wheel magnification affects only
        // the scene. Never feed our previous frame's FOV back into either.
        args.secondary_fov = args.fov;
        args.fov = f;
    }
    original(args)
}

fn projection_layer(flags: Option<u32>, owned: bool) -> Option<i32> {
    // Native D5DF90 / BBCCC0 use -1 for first-person meshes. 164AA60 maps
    // this to node+38 bits 18..19 AND rebuilds the node's renderer features.
    (owned && flags.is_some_and(|f| f & 0xc0000 != 0xc0000)).then_some(-1)
}

fn scope_target(
    owner: Session,
    read: impl Fn(usize, usize) -> Option<u32>,
) -> Option<(usize, u32)> {
    let pawn = read(owner.controller, 0x520)? as usize;
    let scope = read(pawn, 0x1508)? as usize;
    let node = read(scope, 0x30c)? as usize;
    if scope < 0x10000 || node < 0x10000
        || read(pawn, 0x938)? as usize != owner.controller
        || read(scope, 0)? != 0x02ab528c
        || read(scope, 0x144)? as usize != owner.world
        || read(scope, 0x1f4)? == 0
    {
        return None;
    }
    Some((scope, read(node, 0x38)?))
}

// Called on the native ApplyScope game-thread boundary, after mesh binding.
// Use the retail actor -> render command -> node setter; do not write render
// flags directly from the camera callback or retain render-node pointers.
pub(crate) unsafe fn prepare_scope(pawn: usize) {
    if !READY.load(Ordering::Acquire) { return; }
    let Some(pc) = word(pawn, 0x938) else { return; };
    let (Some(world), Some(generation)) = (word(pawn, 0x144), word(pc as usize, 0x20))
        else { return; };
    if generation == 0 || word(pc as usize, 0x520) != Some(pawn as u32) { return; }
    // Caller proves local_weapon after ApplyScope. Camera-core scope FOV may
    // not be updated until later in this tick: do not use the render owner()
    // gate here, or the one-time layer assignment can be missed on first ADS.
    let owner = Session { world:world as usize, controller:pc as usize, generation };
    let Some((scope, flags)) = scope_target(owner, |p,o| word(p,o)) else { return; };
    if let Some(layer) = projection_layer(Some(flags), true) {
        let set_layer: unsafe extern "thiscall" fn(usize, i32) =
            std::mem::transmute(0xdd2130usize);
        set_layer(scope, layer);
    }
}

// 664A10 first copies viewport+30 (controller), then native CalcView
// E5C460 replaces the scene actor with controller+3D0 (normally the pawn).
// Input ownership and the render view target are not pointer-identical.
fn render_owner(
    viewport: usize,
    actor: usize,
    owner: Option<Session>,
    read: impl Fn(usize, usize) -> Option<u32>,
) -> Option<Session> {
    let owner = owner?;
    if read(viewport, 0x30)? as usize == actor {
        return Some(owner);
    }
    let pc = owner.controller;
    if actor == 0 || read(pc, 0x3d0)? as usize != actor {
        return None;
    }
    // Do not widen this to an arbitrary spectator/remote view target.
    let pawn = read(pc, 0x520)? as usize;
    let camera = read(pc, 0x6a4)? as usize;
    (actor == pawn || actor == camera).then_some(owner)
}

pub(crate) unsafe fn scene_source(scene:u32)->Option<(hooks_plugin_api::scope_aim::Request,crate::scis_scene_source::Scope,u32)> {
    if !READY.load(Ordering::Acquire) { return None; }
    let viewport=word(scene as usize,0x10)? as usize;
    let actor=word(scene as usize,0x2dc)? as usize;
    diag::sample(diag::SOURCE_SCENE_FIELDS,||{let pc=word(viewport,0x30).unwrap_or(0)as usize;let camera=word(pc,0x6a4).unwrap_or(0)as usize;let core=word(camera,0x61c).unwrap_or(0)as usize;[viewport as u32,actor as u32,pc as u32,word(pc,0x3d0).unwrap_or(0),word(pc,0x520).unwrap_or(0),camera as u32,core as u32,word(core,0x14a8).unwrap_or(0),word(pc,0x20).unwrap_or(0),word(pc,0x60).unwrap_or(0),0,0,0,0,0,0]});
    let owner=(render_owner(viewport,actor,owner(viewport),|p,o|word(p,o))).or_else(||{diag::hit(diag::SOURCE_OWNER_REJECT);None})?;
    let(scope,flags)=(scope_target(owner,|p,o|word(p,o))).or_else(||{diag::hit(diag::SOURCE_SCOPE_REJECT);None})?;
    if flags&0xc0000!=0xc0000 { diag::hit(diag::SOURCE_LAYER_REJECT);return None; }
    let pawn=word(owner.controller,0x520)?;
    if word(pawn as usize,0x948)?&0x400000==0 { diag::hit(diag::SOURCE_ADS_REJECT);return None; }
    // Snapshot the same camera core fraction before publication. collect() reads
    // this stamp again after the aim query; render preparation never polls it.
    let camera=word(owner.controller,0x6a4)? as usize;
    let core=word(camera,0x61c)? as usize;
    let ads_fraction_bits=word(core,0x46c)?;
    let fraction=f32::from_bits(ads_fraction_bits);
    if !fraction.is_finite() || !(0. ..=1.).contains(&fraction){return None;}
    let stamp=crate::scis_scene_source::Scope {
        pawn,pawn_generation:word(pawn as usize,0x20)?,actor:scope as u32,
        generation:word(scope,0x20)?,node:word(scope,0x30c)?,resource:word(scope,0x1f4)?,
    };
    if stamp.pawn_generation==0 || stamp.generation==0 || stamp.node<0x10000 || stamp.resource<0x10000 { return None; }
    let request=hooks_plugin_api::scope_aim::Request {
        abi:hooks_plugin_api::scope_aim::ABI,size:std::mem::size_of::<hooks_plugin_api::scope_aim::Request>()as u32,
        scene,viewport:viewport as u32,controller:owner.controller as u32,generation:owner.generation,world:owner.world as u32,
    };
    request.valid().then_some((request,stamp,ads_fraction_bits))
}

fn view(
    out: usize,
    viewport: usize,
    actor: usize,
    position: usize,
    rotation: usize,
    fov: f32,
    secondary_fov: f32,
    near: f32,
    far: f32,
) -> usize {
    diag::poll_key();
    let factor = unsafe {
        if READY.load(Ordering::Acquire) {
            let owner = render_owner(viewport, actor, owner(viewport), |p, o| word(p, o))
                .filter(|owner| scope_target(*owner, |p,o| word(p,o))
                    .is_some_and(|(_, flags)| flags & 0xc0000 == 0xc0000));
            STATE.try_lock().ok().and_then(|mut state| {
                state.bind(owner);
                owner.map(|_| state.factor())
            })
        } else {
            None
        }
    };
    diag::hit(diag::ZOOM_VIEW);
    diag::sample(diag::ZOOM_VALUES,||[fov.to_bits(),secondary_fov.to_bits(),factor.unwrap_or(0.).to_bits(),project_fov(fov,factor.unwrap_or(1.)).unwrap_or(0.).to_bits(),0,0,0,0,0,0,0,0,0,0,0,0]);
    // Bounded evidence at the actual projection consumer, not just wheel state.
    if let Some(factor) = factor {
        if LAST_PROJECTION_FACTOR.swap(factor.to_bits(), Ordering::Relaxed) != factor.to_bits()
            && PROJECTION_LOGGED.fetch_add(1, Ordering::Relaxed) < 12
        {
            if let Some(applied) = project_fov(fov, factor) {
                tracing::info!(
                    "SCBL_MOD_SCIS_PROJECTION relative={} native_fov={} applied_fov={} scene_actor={:x}",
                    factor, fov, applied, actor
                );
            }
        }
    }
    render_with(
        Args {
            out,
            viewport,
            actor,
            position,
            rotation,
            fov,
            secondary_fov,
            near,
            far,
        },
        factor,
        |a| unsafe {
            View.call(
                a.out,
                a.viewport,
                a.actor,
                a.position,
                a.rotation,
                a.fov,
                a.secondary_fov,
                a.near,
                a.far,
            )
        },
    )
}
pub(crate) fn wheel(viewport: u32, delta: i32) -> bool {
    if !READY.load(Ordering::Acquire) || !hooks_plugin_api::scope::valid(viewport, delta) {
        return false;
    }
    let owner = unsafe { owner(viewport as usize) };
    let Ok(mut state) = STATE.try_lock() else {
        return false;
    };
    state.bind(owner);
    let accepted = state.wheel(delta);
    if accepted && LOGGED.fetch_add(1, Ordering::Relaxed) < 12 {
        tracing::info!(
            "SCBL_MOD_SCIS_ZOOM scoped=true relative_magnification={} max=1.5",
            state.factor()
        );
    }
    accepted
}
pub(crate) unsafe fn init(world_raycast: usize) {
    if world_raycast != 0x589860 || READY.load(Ordering::Acquire) {
        return;
    }
    for &(address, size, hash) in SIGNATURES {
        if !signature(address, size, hash) {
            tracing::error!(
                "SCBL_MOD_SCIS_ZOOM enabled=false reason=native-signature address={:x}",
                address
            );
            return;
        }
    }
    if View
        .initialize(std::mem::transmute::<usize, ViewFn>(0x5acfe0), view)
        .is_err()
        || View.enable().is_err()
    {
        deinit();
        tracing::error!("SCBL_MOD_SCIS_ZOOM enabled=false reason=detour");
        return;
    }
    READY.store(true, Ordering::Release);
    tracing::info!("SCBL_MOD_SCIS_ZOOM enabled=true max_relative=1.5 input=existing-window-owner scope=local-merc-scis");
}
pub(crate) unsafe fn deinit() {
    READY.store(false, Ordering::Release);
    if View.is_enabled() {
        let _ = View.disable();
    }
    if let Ok(mut state) = STATE.try_lock() {
        *state = Zoom::new();
    }
}
const SIGNATURES: &[(usize, usize, &str)] = &[
    (0xdd2130,20,"2e453d851b224136910bbc8480fa14b5e26d995cf67316f4f70f39482109e3ea"),
    (0xa37bf0,57,"a8a5afae7311fc52cee8b2fb3c3249e14a274d2808f34dfb12a6deb0dea9ee69"),
    (0x164aa60,86,"0e07d9dd53ede3dfb9d88e60d9fdcb54a2ffcc42f11cad852981385fc737e6d5"),
    (
        0x664a10,
        244,
        "7daea70ac5566a484fab64d99ab07a66ffb6a55df61b9c268358d645f964e828",
    ),
    (
        0xe5c460,
        131,
        "465ac89a4556a416d600ec6ad3319486bfd415c512e2b02da012ec3dfe1e5d45",
    ),

    (
        0x5acfe0,
        557,
        "e805f640122e7eeea80cfce26237e6358185389b8d6bd1815a5c6fb254cb334f",
    ),
    (
        0x668888,
        80,
        "eca812197ef4ab881ab002c866d3dffea10a87a724ae0c3a43d5178f36b8a963",
    ),
    (
        0xd3b7d0,
        132,
        "7d8698348526080281cf126782d12e32b6a3e4660af0910169d62196207f3bd4",
    ),
    (
        0xd6e610,
        22,
        "2342e082d4644069bc94a5ca58e9f0d0312daf3d9ec3fe4327b5dbad7a15c701",
    ),
    (
        0xd5f680,
        102,
        "0cc2566ccca92579bf153f0a350e77265f8e92d530d50059590d59bfcb5c094b",
    ),
];

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::HashMap;
    #[test]
    fn scis_optic_group_is_native_first_person_once_and_requires_live_bound_scope() {
        assert_eq!(projection_layer(Some(0x080010e0),true),Some(-1));
        assert_eq!(projection_layer(Some(0x080410e0),true),Some(-1));
        assert_eq!(projection_layer(Some(0x080c10e0),true),None);
        assert_eq!(projection_layer(Some(0x080010e0),false),None);
        assert_eq!(projection_layer(None,true),None);
        let owner=Session{world:0x30000,controller:0x20000,generation:9};
        let mut m=HashMap::from([
            ((0x20000,0x520),0x40000),((0x40000,0x938),0x20000),
            ((0x40000,0x1508),0xc0000),((0xc0000,0),0x02ab528c),
            ((0xc0000,0x144),0x30000),((0xc0000,0x1f4),0xd0000),
            ((0xc0000,0x30c),0xe0000),((0xe0000,0x38),0x080c10e0),
        ]);
        assert_eq!(scope_target(owner,|p,o|m.get(&(p,o)).copied()),Some((0xc0000,0x080c10e0)));
        for key in [(0x40000,0x938),(0xc0000,0),(0xc0000,0x144),(0xc0000,0x1f4),(0xc0000,0x30c)] {
            let old=m.insert(key,0).unwrap();
            assert_eq!(scope_target(owner,|p,o|m.get(&(p,o)).copied()),None);
            m.insert(key,old);
        }
    }
    fn scene() -> HashMap<(usize, usize), u32> {
        let mut m = HashMap::new();
        for (p, o, v) in [
            (0x10000, 0, 0x02c4ea4c),
            (0x10000, 0x30, 0x20000),
            (0x20000, 0, 0x02ac53ec),
            (0x20000, 0x144, 0x30000),
            (0x20000, 0x20, 9),
            (0x20000, 0x60, 0x40000),
            (0x20000, 0x4c, 0),
            (0x20000, 0x354, 0x10000),
            (0x20000, 0x310, 0x40000),
            (0x20000, 0x520, 0x40000),
            (0x20000, 0x6a4, 0x50000),
            (0x50000, 0, 0x02ac2064),
            (0x50000, 0x144, 0x30000),
            (0x50000, 0x61c, 0x60000),
            (0x60000, 0x1c, 0x50000),
            (0x60000, 0x20, 0x20000),
            (0x60000, 0x24, 0x40000),
            (0x60000, 0x2c, 0x70000),
            (0x60000, 0x14a8, 0.25f32.to_bits()),
            (0x70000, 4, 0x60000),
            (0x70000, 0x20, 1),
            (0x40000, 0, 0x02ad2abc),
            (0x40000, 0x144, 0x30000),
            (0x40000, 0x938, 0x20000),
            (0x40000, 0x374, 0xa0000),
            (0x40000, 0x93c, 0x80000),
            (0x80000, 0x324, 0x90000),
            (0x40000, 0x4b8, 0x90000),
            (0x40000, 0x1410, 0xa0000),
            (0xa0000, 0x50, 0x90000),
            (0x90000, 0x740, 123),
            (0x90000, 0x744, 0xb0000),
            (0xb0000, 0, 0x02a176c4),
            (0xb0000, 0x38, 1 << 24),
        ] {
            m.insert((p, o), v);
        }
        m
    }
    #[test]
    fn scis_render_separates_optic_projection_and_preserves_native_call_and_return() {
        let a = Args {
            out: 1,
            viewport: 2,
            actor: 3,
            position: 4,
            rotation: 5,
            fov: 24.,
            secondary_fov: 70.,
            near: 1.,
            far: 10000.,
        };
        for factor in [None, Some(1.), Some(1.5), Some(f32::NAN)] {
            let calls = std::cell::Cell::new(0);
            let result = render_with(a, factor, |out| {
                calls.set(calls.get() + 1);
                let expected_fov = if factor == Some(1.5) { 16.130728 } else { 24. };
                assert!((out.fov - expected_fov).abs() < 0.0001);
                let valid = factor.is_some_and(|f| project_fov(a.fov, f).is_some());
                assert_eq!(out.secondary_fov, if valid { 24. } else { 70. });
                assert_eq!(Args { fov: 24., secondary_fov:70., ..out }, a);
                0x12345678
            });
            assert_eq!(calls.get(), 1);
            assert_eq!(result, 0x12345678);
        }
        println!("SCIS_RENDER scene_native24_to16.130728 optic_fov=24 native_calls=1 native_return=preserved");
    }
    #[test]
    fn scis_native_scope_is_local_merc_exact_weapon_and_native_scope_not_fov_heuristic() {
        let mut m = scene();
        let check = |m: &HashMap<(usize, usize), u32>| {
            inspect(
                0x10000,
                0x30000,
                |p, o| m.get(&(p, o)).copied(),
                |n| n == 123,
            )
        };
        let owner = Session {
            world: 0x30000,
            controller: 0x20000,
            generation: 9,
        };
        assert_eq!(check(&m), Some(owner));
        // Camera and controller scene owners are both native view forms.
        m.insert((0x10000, 0x30), 0x50000);
        assert_eq!(check(&m), Some(owner));
        // Native active-weapon fallback, not merely presence in secondary slot.
        m.insert((0x80000, 0x324), 0);
        assert_eq!(check(&m), Some(owner));
        for (p, o, bad) in [
            (0x70000, 0x20, 0),
            (0x60000, 0x14a8, 1f32.to_bits()),
            (0x40000, 0x374, 0x60000),
            (0x20000, 0x520, 0),
            (0x40000, 0x4b8, 0),
            (0xa0000, 0x50, 0),
            (0x90000, 0x740, 124),
            (0x20000, 0x60, 0),
            (0x20000, 0x144, 0x31000),
            (0x20000, 0x20, 0),
            (0x20000, 0x4c, 0x200),
            (0x70000, 4, 0),
            (0x50000, 0, 0),
            (0xb0000, 0x38, 2 << 24),
        ] {
            let old = m.insert((p, o), bad);
            assert_eq!(check(&m), None, "gate {p:x}+{o:x}");
            if let Some(old) = old {
                m.insert((p, o), old);
            } else {
                m.remove(&(p, o));
            }
        }
        println!("SCIS_NATIVE_SCOPE scoped=accepted hipfire_spy_ob_foreign_weapon_proxy_stale_world=rejected");
    }
}

#[cfg(test)]
mod abi_tests {
    use super::*;
    use std::ffi::c_void;
    #[link(name = "kernel32")]
    extern "system" {
        fn LoadLibraryW(p: *const u16) -> *mut c_void;
        fn GetProcAddress(m: *mut c_void, n: *const u8) -> *mut c_void;
    }
    static_detour! {static Fixture:unsafe extern "thiscall" fn(usize,usize,usize,usize,usize,f32,f32,f32,f32)->usize;}
    fn replacement(
        out: usize,
        viewport: usize,
        actor: usize,
        position: usize,
        rotation: usize,
        fov: f32,
        secondary_fov: f32,
        near: f32,
        far: f32,
    ) -> usize {
        render_with(
            Args {
                out,
                viewport,
                actor,
                position,
                rotation,
                fov,
                secondary_fov,
                near,
                far,
            },
            Some(1.5),
            |a| unsafe {
                Fixture.call(
                    a.out,
                    a.viewport,
                    a.actor,
                    a.position,
                    a.rotation,
                    a.fov,
                    a.secondary_fov,
                    a.near,
                    a.far,
                )
            },
        )
    }
    #[test]
    #[ignore = "explicit MSVC x86 ret32 scene constructor fixture; not a live game"]
    fn scis_scene_constructor_thiscall_ret32_detour_preserves_arguments_and_stack() {
        let path = std::env::var("SCBL_SCIS_VIEW_ABI_FIXTURE").unwrap();
        let path: Vec<u16> = path.encode_utf16().chain(Some(0)).collect();
        unsafe {
            let dll = LoadLibraryW(path.as_ptr());
            assert!(!dll.is_null());
            let raw = GetProcAddress(dll, b"scope_view_fixture\0".as_ptr());
            assert!(!raw.is_null());
            let call: ViewFn = std::mem::transmute(raw);
            Fixture.initialize(call, replacement).unwrap();
            Fixture.enable().unwrap();
            let mut data = [0u32; 9];
            let out = data.as_mut_ptr() as usize;
            for _ in 0..4096 {
                assert_eq!(
                    std::hint::black_box(call)(out, 11, 22, 33, 44, 24., 70., 1., 10000.),
                    out
                );
                assert_eq!(&data[..5], &[out as u32, 11, 22, 33, 44]);
                assert!((f32::from_bits(data[5]) - 16.130728).abs() < 0.0001);
                assert_eq!(
                    &data[6..],
                    &[24f32.to_bits(), 1f32.to_bits(), 10000f32.to_bits()]
                );
            }
            Fixture.disable().unwrap();
            assert_eq!(call(out, 11, 22, 33, 44, 24., 70., 1., 10000.), out);
            assert_eq!(data[5], 24f32.to_bits());
            println!("SCIS_SCENE_ABI thiscall_ret32=pass detour_calls=4096 native_return=preserved disabled_fov=24");
        }
    }
}

#[cfg(test)]
mod scene_actor_regression {
    use super::*;
    #[test]
    fn scis_view_target_reaches_projection_after_native_calc_view() {
        // Read-only SC-IS session, 2026-10-16. E5C4B3..E5C4BC copies
        // controller+3D0 to the scene actor before 6688D2 calls View.
        let viewport = 0x3916aed0usize;
        let pc = 0x52e0a818usize;
        let pawn = 0x50a00370usize;
        let camera = 0x4ce7ab68usize;
        let owner = Session {world: 0x10000, controller: pc, generation: 1};
        let read = |p: usize, o: usize| match (p, o) {
            (p, 0x30) if p == viewport => Some(pc as u32),
            (p, 0x3d0 | 0x520) if p == pc => Some(pawn as u32),
            (p, 0x6a4) if p == pc => Some(camera as u32),
            _ => None,
        };
        let mut z = Zoom::new(); z.bind(Some(owner)); assert!(z.wheel(480));
        let accepted = render_owner(viewport, pawn, Some(owner), read);
        let input = Args {out:1,viewport,actor:pawn,position:2,rotation:3,
            fov:19.5,secondary_fov:75.,near:1.,far:10000.};
        let mut calls=0;
        render_with(input, accepted.map(|_| z.factor()), |output| {
            calls+=1;
            println!("SCIS_SCENE actor=local_pawn controller_differs=true input_factor={} native_fov={} applied_fov={} secondary_fov={}",z.factor(),input.fov,output.fov,output.secondary_fov);
            assert!(output.fov < input.fov, "wheel state changed but projection unchanged");
            let ratio=(input.fov.to_radians()/2.).tan()/(output.fov.to_radians()/2.).tan();
            assert!((ratio-1.5).abs()<0.00001);
            assert_eq!(output.secondary_fov,input.fov);
            assert_eq!(Args {fov:input.fov,secondary_fov:input.secondary_fov,..output},input);
            1
        });
        assert_eq!(calls,1);
        assert_eq!(render_owner(viewport, pc, Some(owner), read),Some(owner));
        assert_eq!(render_owner(viewport, 0x60000, Some(owner), read),None);
        assert_eq!(render_owner(viewport, pawn, None, read),None);
        // Even a controller's foreign view target is not a local SC-IS view.
        let foreign=|p,o| if p==pc && o==0x3d0 {Some(0x60000)} else {read(p,o)};
        assert_eq!(render_owner(viewport, 0x60000, Some(owner), foreign),None);
    }
}
