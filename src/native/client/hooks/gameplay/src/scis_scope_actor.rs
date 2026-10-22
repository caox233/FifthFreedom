//! Campaign BasicScope requires a native AScope1stPerson. Multiplayer skips
//! its creation (DA64B3), causing DA6B10 to return before setting the scoped bit.
//! DA6EF0 then retries and replays the enter sound. Supply the missing lifetime,
//! not a synthetic scope flag, replacement optic or audio suppression.
use crate::equipment_native::{patch, signature, word};
#[path = "scis_scope_policy.rs"]
mod policy;
#[path = "vss_scope_policy.rs"]
mod vss_scope_policy;
#[path = "scis_scope_ao.rs"]
mod ao;
#[path = "scis_scope_hud.rs"]
mod hud;
#[path = "scis_scope_sway.rs"]
mod sway;
use retour::static_detour;
use std::sync::{
    atomic::{AtomicBool, AtomicU32, Ordering},
    Mutex,
};

static READY: AtomicBool = AtomicBool::new(false);
static VISUAL_LOGS: AtomicU32 = AtomicU32::new(0);
#[derive(Clone, Copy)]
struct Lease {
    pawn: usize,
    pawn_name: u32,
    world: usize,
    actor: usize,
    actor_name: u32,
}
static LEASE: Mutex<Option<Lease>> = Mutex::new(None);
static_detour! {
    static Apply: unsafe extern "thiscall" fn(usize, u32);
    static DestroyPawn: unsafe extern "thiscall" fn(usize);
    static UpdateScope: unsafe extern "thiscall" fn(usize, f32);
}

use crate::scis_presentation::local_weapon;
fn eligible(
    pawn: usize,
    read: impl Fn(usize, usize) -> Option<u32>,
    named: impl Fn(u32) -> bool,
) -> bool {
    local_weapon(pawn,&read,named)
        && read(pawn,0x1508)==Some(0)
        && read(pawn,0x1510)==Some(0)
}
// Both campaign BasicScopes use the same native actor constructor and owner slot.
// Keep the separate SC-IS-only guard for sway and zoom. Both local copied
// BasicScopes preserve ordinary HUD components via the scoped HUD adapter.
fn eligible_for_scope(
    pawn: usize,
    read: impl Fn(usize, usize) -> Option<u32>,
    scis: impl Fn(u32) -> bool,
    vss: impl Fn(u32) -> bool,
) -> bool {
    eligible(pawn, &read, scis)
        || (vss_scope_policy::local_weapon(pawn, &read, vss)
            && read(pawn, 0x1508) == Some(0)
            && read(pawn, 0x1510) == Some(0))
}
fn update_scope(pawn:usize,delta:f32) {
    unsafe {
        // Both copied BasicScopes need the native local controller and viewport.
        // A premature VSS entry latches scoped even when AA6120 cannot bind.
        // Active scopes still tick normally so readiness loss never blocks exit.
        let ours=READY.load(Ordering::Acquire)
            && (local_weapon(pawn,|p,o|word(p,o),|n|super::super::scis_name(n))
                || vss_scope_policy::local_weapon(pawn,|p,o|word(p,o),
                    |n|super::super::native_name_is(n,super::super::vss::RETAIL_NAME)));
        if policy::defer_entry(ours,pawn,|p,o|word(p,o)) { return; }
        UpdateScope.call(pawn,delta);
    }
}
fn prepare(pawn: usize, allowed: bool, create: impl FnOnce(usize)) {
    if allowed {
        create(pawn);
    }
}
fn owns(l: Lease, pawn: usize, read: impl Fn(usize, usize) -> Option<u32>) -> bool {
    l.pawn == pawn
        && read(pawn, 0x20) == Some(l.pawn_name)
        && read(pawn, 0x144) == Some(l.world as u32)
        && read(pawn, 0x1508) == Some(l.actor as u32)
        && read(l.actor, 0) == Some(0x02ab528c)
        && read(l.actor, 0x20) == Some(l.actor_name)
        && read(l.actor, 0x144) == Some(l.world as u32)
}
fn owns_material_node(
    l: Lease,
    node: usize,
    read: impl Fn(usize,usize)->Option<u32>,
) -> bool {
    node >= 0x10000
        && read(l.actor,0x30c)==Some(node as u32)
        && read(l.actor,0x1f4).is_some_and(|p|p>=0x10000)
        && owns(l,l.pawn,&read)
        && read(l.pawn,0x948).is_some_and(|b|b&0x400000!=0)
}
fn owns_node(
    l: Lease,
    node: usize,
    read: impl Fn(usize,usize)->Option<u32>,
    named: impl Fn(u32)->bool,
) -> bool {
    owns_material_node(l,node,&read) && local_weapon(l.pawn,&read,named)
}
// This guard is deliberately VSS-only: the EMF glass exception must never
// include SC-IS, remote pawns, a stale scope lease or an ordinary gun attachment.
fn owns_vss_node(
    l: Lease, node: usize,
    read: impl Fn(usize,usize)->Option<u32>, named: impl Fn(u32)->bool,
) -> bool {
    owns_material_node(l,node,&read) && vss_scope_policy::local_weapon(l.pawn,&read,named)
}
pub(super) fn owns_vss_scope_node(node: usize) -> bool {
    if !READY.load(Ordering::Acquire) { return false; }
    let lease=LEASE.try_lock().ok().and_then(|l|*l);
    lease.is_some_and(|l| unsafe {
        owns_vss_node(l,node,|p,o|word(p,o),
            |n|super::super::native_name_is(n,super::super::vss::RETAIL_NAME))
    })
}
// SelfAO readiness is shared by the two copied BasicScopes, not their
// projection or animation policy. Never enroll VSS in SC-IS zoom/sway.
fn owns_ao_node(
    l: Lease,
    node: usize,
    read: impl Fn(usize,usize)->Option<u32>,
    scis: impl Fn(u32)->bool,
    vss: impl Fn(u32)->bool,
) -> bool {
    owns_material_node(l,node,&read)
        && (local_weapon(l.pawn,&read,scis)
            || vss_scope_policy::local_weapon(l.pawn,&read,vss))
}
fn owns_ao_scope_node(node: usize) -> bool {
    if !READY.load(Ordering::Acquire) { return false; }
    let lease=LEASE.try_lock().ok().and_then(|l|*l);
    lease.is_some_and(|l| unsafe {
        owns_ao_node(l,node,|p,o|word(p,o),|n|super::super::scis_name(n),
            |n|super::super::native_name_is(n,super::super::vss::RETAIL_NAME))
    })
}
fn owns_scope_node(node: usize) -> bool {
    if !READY.load(Ordering::Acquire) { return false; }
    let lease=LEASE.try_lock().ok().and_then(|l|*l);
    lease.is_some_and(|l| unsafe {
        owns_node(l,node,|p,o|word(p,o),|n|super::super::scis_name(n))
    })
}
unsafe fn ensure(pawn: usize) {
    if !READY.load(Ordering::Acquire)
        || !eligible_for_scope(pawn, |p, o| word(p, o), |n| super::super::scis_name(n),
            |n| super::super::native_name_is(n, super::super::vss::RETAIL_NAME))
    {
        return;
    }
    let (Some(pawn_name), Some(world)) = (word(pawn, 0x20), word(pawn, 0x144)) else {
        return;
    };
    let Ok(mut lease) = LEASE.try_lock() else {
        return;
    };
    let current = policy::Owner { pawn, generation: pawn_name, world: world as usize };
    let old_owner = lease.map(|l| policy::Owner { pawn:l.pawn, generation:l.pawn_name, world:l.world });
    if policy::blocks_creation(old_owner, current,
        lease.is_some_and(|l| owns(l, pawn, |p,o| word(p,o)))) {
        return;
    }
    // Respawn can precede corpse destruction. Retire only the exact old actor
    // we own, and only after possession has demonstrably moved to this pawn.
    // Reused identities/worlds fail owns(); never destroy their replacement.
    if let Some(old) = *lease {
        if old.world == world as usize && old.pawn != pawn {
            let pc = word(pawn,0x938).unwrap_or(0) as usize;
            if word(pc,0x520) == Some(pawn as u32) {
                destroy_owned(old);
            }
        }
    }
    *lease = None;
    let create: unsafe extern "thiscall" fn(usize) = std::mem::transmute(0xd5f360usize);
    prepare(pawn, true, |p| create(p));
    let Some(actor) = word(pawn, 0x1508).map(|a| a as usize) else {
        return;
    };
    if word(actor, 0) != Some(0x02ab528c) || word(actor, 0x144) != Some(world) {
        return;
    }
    let Some(actor_name) = word(actor, 0x20) else {
        return;
    };
    *lease = Some(Lease {
        pawn,
        pawn_name,
        world: world as usize,
        actor,
        actor_name,
    });
    tracing::info!("SCBL_MOD_SCIS_SCOPE actor=created native_lifetime=true audio=unchanged");
}
fn apply(pawn: usize, enabled: u32) {
    unsafe {
        if enabled != 0 {
            ensure(pawn);
        }
        let owned = READY.load(Ordering::Acquire)
            && (crate::scis_presentation::owned(pawn) || crate::scis_presentation::vss_owned(pawn));
        crate::scis_hud_policy::during(owned, || Apply.call(pawn, enabled));
        // A remote pawn's scope update must not clear this client's node hint.
        if LEASE.try_lock().ok().and_then(|l|*l).is_some_and(|l|l.pawn==pawn) {
            ao::hint(0);
        }
        if enabled != 0 {
            if READY.load(Ordering::Acquire)
                && local_weapon(pawn, |p,o| word(p,o), |n| super::super::scis_name(n)) {
                crate::scis_zoom_native::prepare_scope(pawn);
            }
            let scope=word(pawn,0x1508).unwrap_or(0) as usize;
            let node=word(scope,0x30c).unwrap_or(0) as usize;
            if owns_ao_scope_node(node) { ao::hint(node); }
            observe_binding(pawn);
        }
    }
}
// One bounded record on scope entry, not a per-frame probe. It distinguishes
// an allocated scope actor from an actual bound mesh; no state is changed.
unsafe fn observe_binding(pawn: usize) {
    let weapon = word(pawn, 0x4b8).unwrap_or(0) as usize;
    if READY.load(Ordering::Acquire)
        && vss_scope_policy::local_weapon(pawn, |p,o| word(p,o),
            |n| super::super::native_name_is(n, super::super::vss::RETAIL_NAME))
        && VISUAL_LOGS.fetch_add(1,Ordering::Relaxed) < 12 {
        let scope = word(pawn,0x1508).unwrap_or(0) as usize;
        let gun_node = word(weapon,0x30c).unwrap_or(0) as usize;
        let scope_node = word(scope,0x30c).unwrap_or(0) as usize;
        let gun_resource = word(gun_node,0xf8).unwrap_or(0) as usize;
        let scope_resource = word(scope_node,0xf8).unwrap_or(0) as usize;
        tracing::info!("SCBL_MOD_VSS_VISUAL stage=scope-entry scope_actor={} gun_key={:08x} gun_sections={} scope_key={:08x} scope_sections={} native_scope_active={}",
            word(scope,0)==Some(0x02ab528c), word(gun_resource,0x14).unwrap_or(0),
            word(gun_resource,0x64).unwrap_or(0), word(scope_resource,0x14).unwrap_or(0),
            word(scope_resource,0x64).unwrap_or(0),word(pawn,0x948).is_some_and(|v|v&0x400000!=0));
        return;
    }
    let pc = word(pawn, 0x938).unwrap_or(0) as usize;
    let info = word(0x339e5a0, 0).unwrap_or(0) as usize;
    if !READY.load(Ordering::Acquire)
        || word(pawn,0) != Some(0x02ad2abc)
        || word(info,0x330).map_or(true,|v|v&0x10==0)
        || word(pc,0x60).map_or(true,|v|v&0x40000==0)
        || word(pc,0x520) != Some(pawn as u32)
        || !word(weapon,0x740).is_some_and(|n|super::super::scis_name(n))
        || VISUAL_LOGS.fetch_add(1,Ordering::Relaxed) >= 12
    { return; }
    let scope = word(pawn,0x1508).unwrap_or(0) as usize;
    let upgrade = word(scope,0x6fc).unwrap_or(0) as usize;
    let props = word(upgrade,0x700).unwrap_or(0) as usize;
    tracing::info!("SCBL_MOD_SCIS_VISUAL stage=scope-entry gun_mesh_bound={} scope_actor={} scope_upgrade={} scope_resource={} scope_mesh_bound={} native_scope_active={}",
        word(weapon,0x1ec).is_some_and(|p|p!=0),word(scope,0)==Some(0x02ab528c),
        upgrade!=0,word(props,0x5c).is_some_and(|p|p!=0),word(scope,0x1f4).is_some_and(|p|p!=0),
        word(pawn,0x948).is_some_and(|v|v&0x400000!=0));
}
unsafe fn release(pawn: usize) {
    let Ok(mut slot) = LEASE.try_lock() else {
        return;
    };
    let Some(l) = *slot else {
        return;
    };
    if l.pawn != pawn {
        return;
    }
    ao::hint(0);
    *slot = None;
    destroy_owned(l);
}
unsafe fn destroy_owned(l: Lease) {
    let pawn=l.pawn;
    if !owns(l, pawn, |p,o| word(p,o)) || !matches!(word(pawn,0),Some(0x02ad2abc | 0x02ad3234))
        || word(l.world,0x148).map_or(true,|p|p<0x10000) { return; }
    let destroy: unsafe extern "thiscall" fn(usize) = std::mem::transmute(0xd32e20usize);
    destroy(pawn);
    if word(pawn,0x1508) == Some(l.actor as u32)
        && crate::equipment_native::writable(pawn+0x1508,4) {
        ((pawn+0x1508) as *mut u32).write_unaligned(0);
    }
    tracing::info!("SCBL_MOD_SCIS_SCOPE actor=released native_lifetime=true");
}
// DA6EF0 already decides that ADS ended before this call. Keep D2D430's
// native exit sound, then remove only this optic's delayed visual handoff.
// The remaining native DA6EF0 tick calls Apply(false), restoring weapon and
// attachment visibility together and hiding the optic. No render-list filter.
const EXIT_SITE:usize=0xda6f2f;
fn exit_bytes(target:usize)->[u8;5] {
    let mut b=[0xe8,0,0,0,0];
    b[1..].copy_from_slice(&(target as u32).wrapping_sub(EXIT_SITE as u32+5).to_le_bytes());b
}
unsafe extern "thiscall" fn finish_exit(pawn:usize,enabled:u32) {
    let original:unsafe extern "thiscall" fn(usize,u32)=std::mem::transmute(0xd2d430usize);
    original(pawn,enabled);
    if !READY.load(Ordering::Acquire) || enabled != 0 {return;}
    let ours=LEASE.try_lock().ok().and_then(|l|*l).is_some_and(|l| {
        let pc=word(pawn,0x938).unwrap_or(0) as usize;
        let weapon=word(pawn,0x4b8).unwrap_or(0) as usize;
        let info=word(0x339e5a0,0).unwrap_or(0) as usize;
        owns(l,pawn,|p,o|word(p,o))
            && word(pawn,0)==Some(0x02ad2abc)
            && word(pawn,0x374).is_some_and(|v|v&0xe0000==0xa0000)
            && word(info,0x144)==Some(l.world as u32)
            && word(info,0x330).is_some_and(|v|v&0x10!=0)
            && word(pc,0)==Some(0x02ac53ec)
            && word(pc,0x144)==Some(l.world as u32)
            && word(pc,0x60).is_some_and(|v|v&0x40000!=0)
            && word(pc,0x520)==Some(pawn as u32)
            && word(weapon,0x740).is_some_and(|n|super::super::scis_name(n))
    });
    if !ours {return;}
    let Some(bits)=word(pawn,0x150c) else {return;};
    let delay=f32::from_bits(bits);let adjusted=policy::exit_delay(ours,enabled,delay);
    if adjusted.to_bits()!=bits && crate::equipment_native::writable(pawn+0x150c,4) {
        ((pawn+0x150c) as *mut f32).write_unaligned(adjusted);
    }
}

fn destroy_pawn(pawn: usize) {
    unsafe {
        release(pawn);
        DestroyPawn.call(pawn);
    }
}
pub(super) unsafe fn init() -> bool {
    for &(a, n, h) in SIGNATURES {
        if !signature(a, n, h) {
            return false;
        }
    }
    if Apply
        .initialize(
            std::mem::transmute::<usize, unsafe extern "thiscall" fn(usize, u32)>(entry()),
            apply,
        )
        .is_err()
        || DestroyPawn
            .initialize(
                std::mem::transmute::<usize, unsafe extern "thiscall" fn(usize)>(0xd99d80),
                destroy_pawn,
            )
            .is_err()
        || UpdateScope
            .initialize(
                std::mem::transmute::<usize, unsafe extern "thiscall" fn(usize, f32)>(0xda6ef0),
                update_scope,
            )
            .is_err()
        || UpdateScope.enable().is_err()
        || DestroyPawn.enable().is_err()
        || Apply.enable().is_err()
        || !ao::init()
        || !hud::init()
        || !sway::init()
    {
        deinit();
        return false;
    }
    if !patch(EXIT_SITE,&exit_bytes(0xd2d430),&exit_bytes(finish_exit as usize)) {
        deinit();return false;
    }
    READY.store(true, Ordering::Release);
    true
}
pub(super) fn entry() -> usize {
    0xda6b10
}
pub(super) unsafe fn deinit() {
    READY.store(false, Ordering::Release);
    sway::deinit();
    hud::deinit();
    ao::deinit();
    if crate::equipment_native::readable(EXIT_SITE,5)
        && std::slice::from_raw_parts(EXIT_SITE as *const u8,5)==exit_bytes(finish_exit as usize) {
        if !patch(EXIT_SITE,&exit_bytes(finish_exit as usize),&exit_bytes(0xd2d430)) {
            tracing::error!("SCBL_MOD_SCIS_SCOPE restore_failed site=da6f2f");
        }
    }
    if Apply.is_enabled() {
        let _ = Apply.disable();
    }
    if DestroyPawn.is_enabled() {
        let _ = DestroyPawn.disable();
    }
    if UpdateScope.is_enabled() {
        let _ = UpdateScope.disable();
    }
    // Do not call game actor functions from a DLL-unload thread. World teardown
    // owns any remaining actor; normal pawn destruction uses release above.
    if let Ok(mut slot) = LEASE.lock() {
        *slot = None;
    }
}
const SIGNATURES: &[(usize, usize, &str)] = &[
    (0xaa6120,239,"9c30ee2bf291611b23753094da3f313e808160c32a27367345c58a622dedaaa3"),
    (0xc94fc0,22,"ae73613dd4600d74aef0b211701d1cc84231e8553d7b738db07d27670db48ee3"),
    (0xe93df0,25,"6707943c22f0b056c541d52fd83e276a0bbaffbcf9a649b8f5fd3fbe58411f0d"),
    (0xd2d430,139,"48ea0e0f6292eca838180e1d0967d47a1bbea230e91fd8a4959a1944427f093e"),

    (
        0xda6b10,
        978,
        "de5a40dade7104e305950b2d94f4077d59cd2fe3119159b794487c8742ca5dab",
    ),
    (
        0xd5f360,
        202,
        "750dc62283570dea4017c711c83fb1a844e734a6e2b3cc512d66aebfcfd9f4e4",
    ),
    (
        0xd99d80,
        616,
        "6a26510e71c060bf448ae00e46490d6c134015d22dc13fbb6814ea3e33b91472",
    ),
    (
        0xd32e20,
        38,
        "0a9e45f5882ec1ffcfe30918aca6adcd3cd815765a6ee9f3e87e0db9a3e0a61a",
    ),
    (
        0xda6ef0,
        301,
        "a25866b2b54cbd34bf9bd55ef7eabfd7cb5a0b4eaca88df46e0967668c3f6207",
    ),
];

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn vss_ao_scope_is_owned_without_scis_projection_or_sway() {
        let l=Lease { pawn:0x30000,pawn_name:12,world:0x20000,actor:0x80000,actor_name:13 };
        let mut m=std::collections::HashMap::from([
            ((0x339e5a0,0),0x10000),((0x10000,0x144),0x20000),((0x10000,0x330),0x10),
            ((0x30000,0),0x02ad3234),((0x30000,0x20),12),((0x30000,0x144),0x20000),
            ((0x30000,0x148),0x70000),((0x30000,0x4c),0),
            ((0x30000,0x938),0x40000),((0x30000,0x1410),0x50000),((0x30000,0x4b8),0x60000),
            ((0x30000,0x1508),0x80000),((0x30000,0x948),0x400000),
            ((0x40000,0),0x02ac53ec),((0x40000,0x60),0x40000),((0x40000,0x520),0x30000),
            ((0x40000,0x144),0x20000),((0x50000,0x50),0x60000),((0x60000,0x740),124),
            ((0x80000,0),0x02ab528c),((0x80000,0x20),13),((0x80000,0x144),0x20000),
            ((0x80000,0x30c),0x90000),((0x80000,0x1f4),0xa0000),
        ]);
        let check=|m:&std::collections::HashMap<_,_>,node| {
            let read=|p,o|m.get(&(p,o)).copied();
            let exact=owns_vss_node(l,node,read,|n|n==124);
            assert_eq!(exact,owns_ao_node(l,node,read,|n|n==123,|n|n==124));
            exact
        };
        assert!(check(&m,0x90000));
        assert!(!owns_node(l,0x90000,|p,o|m.get(&(p,o)).copied(),|n|n==123));
        assert!(!check(&m,0x90004));
        for key in m.keys().copied().collect::<Vec<_>>() {
            let old=m.remove(&key).unwrap();
            assert!(!check(&m,0x90000),"missing {key:?}");
            m.insert(key,old);
        }
        for (key,v) in [
            ((0x10000,0x330),0),((0x30000,0x4c),0x200),
            ((0x40000,0x60),0),((0x40000,0x520),0xb0000),((0x60000,0x740),123),
            ((0x30000,0x948),0),((0x80000,0x20),14),((0x30000,0x20),14),
            ((0x80000,0x30c),0xb0000),((0x80000,0x144),0xb0000),((0x80000,0x1f4),0),
        ] {
            let old=m.insert(key,v).unwrap();
            assert!(!check(&m,0x90000),"changed {key:?}");
            m.insert(key,old);
        }
    }
    #[test]
    fn scis_ao_node_requires_current_owned_local_scope() {
        let l=Lease { pawn:0x30000,pawn_name:12,world:0x20000,actor:0x80000,actor_name:13 };
        let mut m=std::collections::HashMap::from([
            ((0x339e5a0,0),0x10000),((0x10000,0x144),0x20000),((0x10000,0x330),0x10),
            ((0x30000,0),0x02ad2abc),((0x30000,0x20),12),((0x30000,0x144),0x20000),
            ((0x30000,0x148),0x70000),((0x30000,0x374),0xa0000),((0x30000,0x4c),0),
            ((0x30000,0x938),0x40000),((0x30000,0x1410),0x50000),((0x30000,0x4b8),0x60000),
            ((0x30000,0x1508),0x80000),((0x30000,0x948),0x400000),
            ((0x40000,0),0x02ac53ec),((0x40000,0x60),0x40000),((0x40000,0x520),0x30000),
            ((0x40000,0x144),0x20000),((0x50000,0x50),0x60000),((0x60000,0x740),123),
            ((0x80000,0),0x02ab528c),((0x80000,0x20),13),((0x80000,0x144),0x20000),
            ((0x80000,0x30c),0x90000),((0x80000,0x1f4),0xa0000),
        ]);
        let check=|m:&std::collections::HashMap<_,_>,node|owns_node(l,node,|p,o|m.get(&(p,o)).copied(),|n|n==123);
        assert!(check(&m,0x90000));
        assert!(!check(&m,0x90004));
        for key in m.keys().copied().collect::<Vec<_>>() {
            let old=m.remove(&key).unwrap();assert!(!check(&m,0x90000),"missing {key:?}");m.insert(key,old);
        }
        for (key,v) in [((0x10000,0x330),0),((0x30000,0x374),0x60000),
            ((0x40000,0x60),0),((0x40000,0x520),0xb0000),((0x60000,0x740),124),
            ((0x30000,0x948),0),((0x80000,0x20),14),((0x30000,0x20),14),
            ((0x80000,0x30c),0xb0000),((0x80000,0x144),0xb0000),((0x80000,0x1f4),0)] {
            let old=m.insert(key,v).unwrap();assert!(!check(&m,0x90000),"changed {key:?}");m.insert(key,old);
        }
    }
    #[test]
    fn scis_scope_cleanup_rejects_reused_pawn_actor_or_world() {
        let l = Lease {
            pawn: 1,
            pawn_name: 2,
            world: 3,
            actor: 4,
            actor_name: 5,
        };
        let mut m = std::collections::HashMap::from([
            ((1, 0x20), 2),
            ((1, 0x144), 3),
            ((1, 0x1508), 4),
            ((4, 0), 0x02ab528c),
            ((4, 0x20), 5),
            ((4, 0x144), 3),
        ]);
        assert!(owns(l, 1, |p, o| m.get(&(p, o)).copied()));
        assert!(!owns(l, 6, |p, o| m.get(&(p, o)).copied()));
        for key in m.keys().copied().collect::<Vec<_>>() {
            let old = m.insert(key, 0).unwrap();
            assert!(!owns(l, 1, |p, o| m.get(&(p, o)).copied()));
            m.insert(key, old);
        }
    }
    #[test]
    fn scis_scope_creation_is_only_for_missing_local_merc_scope() {
        let mut m = std::collections::HashMap::new();
        for (p, o, v) in [
            (0x339e5a0, 0, 0x10000),
            (0x10000, 0x144, 0x20000),
            (0x10000, 0x330, 0x10),
            (0x30000, 0, 0x02ad2abc),
            (0x30000, 0x144, 0x20000),
            (0x30000, 0x148, 0x70000),
            (0x30000, 0x374, 0xa0000),
            (0x30000, 0x4c, 0),
            (0x30000, 0x938, 0x40000),
            (0x30000, 0x1410, 0x50000),
            (0x30000, 0x4b8, 0x60000),
            (0x30000, 0x1508, 0),
            (0x30000, 0x1510, 0),
            (0x40000, 0, 0x02ac53ec),
            (0x40000, 0x60, 0x40000),
            (0x40000, 0x520, 0x30000),
            (0x40000, 0x144, 0x20000),
            (0x50000, 0x50, 0x60000),
            (0x60000, 0x740, 123),
        ] {
            m.insert((p, o), v);
        }
        let check = |m: &std::collections::HashMap<_, _>| {
            eligible(0x30000, |p, o| m.get(&(p, o)).copied(), |n| n == 123)
        };
        assert!(check(&m));
        for (p, o, v) in [
            (0x10000, 0x330, 0),
            (0x30000, 0x374, 0x60000),
            (0x30000, 0x4c, 0x200),
            (0x40000, 0x60, 0),
            (0x40000, 0x520, 0),
            (0x30000, 0x1508, 0x80000),
            (0x30000, 0x1510, 0x80000),
            (0x60000, 0x740, 124),
            (0x50000, 0x50, 0),
            (0x30000, 0x148, 0),
        ] {
            let old = m.insert((p, o), v).unwrap();
            assert!(!check(&m), "gate {p:x}+{o:x}");
            m.insert((p, o), old);
        }
        let mut calls = 0;
        prepare(1, false, |_| calls += 1);
        assert_eq!(calls, 0);
        prepare(1, true, |p| {
            assert_eq!(p, 1);
            calls += 1;
        });
        assert_eq!(calls, 1);
    }
    #[test]
    #[ignore = "explicit retail scope retry-loop replay; fixture actor/audio, not live rendering"]
    fn scis_scope_native_retry_replay() {
        use std::ffi::c_void;
        #[link(name = "kernel32")]
        extern "system" {
            fn LoadLibraryW(p: *const u16) -> *mut c_void;
            fn GetProcAddress(h: *mut c_void, n: *const u8) -> *mut c_void;
        }
        unsafe extern "C" fn repair(p: usize) {
            prepare(p, true, |p| {
                ((p + 0x1508) as *mut u32).write_unaligned(0x12345678)
            });
        }
        let path = std::env::var("SCBL_SCIS_SCOPE_REPLAY").unwrap();
        let wide: Vec<u16> = path.encode_utf16().chain([0]).collect();
        unsafe {
            let h = LoadLibraryW(wide.as_ptr());
            assert!(!h.is_null());
            let f = GetProcAddress(h, b"scis_scope_replay\0".as_ptr());
            assert!(!f.is_null());
            let run: unsafe extern "C" fn(usize, *mut u32) -> u32 = std::mem::transmute(f);
            let mut result = [0; 4];
            let enabled = super::super::scope_installed_in_source();
            assert_eq!(
                run(
                    if enabled { repair as usize } else { 0 },
                    result.as_mut_ptr()
                ),
                0
            );
            println!("SCIS_SCOPE frames=600 enter_sounds={} scoped_frames={} exit_sounds={} final_scoped={} lifecycle_hook={enabled}",result[0],result[1],result[2],result[3]);
            assert_eq!(
                result[0], 1,
                "missing scope actor makes native entry retry and repeat audio"
            );
            assert!(result[1] > 500);
            assert_eq!(result[2], 1);
            assert_eq!(result[3], 0);
        }
    }
}

pub(crate) fn scope_motion_enabled()->bool{sway::scope_motion_enabled()}
pub(crate) fn scope_motion_enable(abi:u32)->bool{sway::scope_motion_enable(abi)}
pub(crate) unsafe fn scope_motion_query(r:hooks_plugin_api::scope_motion::Request)->Option<[f32;2]>{sway::scope_motion_query(r)}

pub(crate) unsafe fn scope_motion_query_phase(r:hooks_plugin_api::scope_motion::Request,post:bool)->Option<[f32;2]>{sway::scope_motion_query_phase(r,post)}
