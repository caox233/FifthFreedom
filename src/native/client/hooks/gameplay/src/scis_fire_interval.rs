//! Bound both retail readiness consumers, not just the automatic-fire interval.
//! E557C0 commits level time to logic+198 only after native acceptance. Retain
//! native empty/reload/state checks and use that timestamp for MP SC-IS only.
use crate::equipment_native::{patch, readable, signature, word};
use std::sync::atomic::{AtomicBool, Ordering};
use std::cell::Cell;
#[path = "scis_fire_interval_policy.rs"] mod policy;
static READY: AtomicBool = AtomicBool::new(false);
const NATIVE: usize = 0xe094b0;
const SITES: [usize; 2] = [0xe5585f, 0xe57b70];
// E566D0 starts a firing state and then queues BB9F50 even when its inner
// E557C0 rejects the shot. Gate the request before any state/animation changes.
const START_NATIVE: usize = 0xe566d0;
const START_SITES: [usize; 4] = [0xe56df8, 0xe57845, 0xe5785a, 0xe57a22];
const ANIMATION_SITE: usize = 0xe56864;
const ANIMATION_NATIVE: usize = 0xbb9f50;
thread_local! {static SHOT:Cell<Option<policy::ShotCommit>>=const{Cell::new(None)};}
struct RestoreShot(Option<policy::ShotCommit>);
impl Drop for RestoreShot {fn drop(&mut self){SHOT.with(|s|s.set(self.0));}}
fn call(site: usize, target: usize) -> [u8; 5] {
    let mut b=[0xe8,0,0,0,0];
    b[1..].copy_from_slice(&(target as u32).wrapping_sub(site as u32+5).to_le_bytes());b
}
unsafe extern "thiscall" fn readiness(logic: usize) -> u8 {
    let native: unsafe extern "thiscall" fn(usize)->u8=std::mem::transmute(NATIVE);
    let original=native(logic);
    if !READY.load(Ordering::Acquire) { return original; }
    policy::readiness(original,logic,|p,o|word(p,o),|n|super::scis_name(n))
}
unsafe extern "thiscall" fn start(logic: usize, rounds: u32, flag: u32, reason: u32) {
    if READY.load(Ordering::Acquire)
        && policy::readiness(1, logic, |p,o|word(p,o), |n|super::scis_name(n)) == 0 {
        super::scis_sound::trace_request(logic,0);
        return;
    }
    super::scis_sound::trace_request(logic,1);
    // Scope the witness to this call, including nested starts. No persistent
    // ammo/time history, fake release, timer, or graph-completion event.
    let witness=if READY.load(Ordering::Acquire){
        policy::ShotCommit::capture(logic,|p,o|word(p,o),|n|super::scis_name(n))
    }else{None};
    let _restore=RestoreShot(SHOT.with(|s|s.replace(witness)));
    let native: unsafe extern "thiscall" fn(usize,u32,u32,u32) = std::mem::transmute(START_NATIVE);
    native(logic, rounds, flag, reason);
    super::scis_sound::trace_request(logic,2);
}
unsafe extern "thiscall" fn animation(hands:usize,weapon:usize) {
    if READY.load(Ordering::Acquire) {
        if let Some(witness)=SHOT.with(Cell::get).filter(|s|s.owns(hands,weapon)) {
            if !witness.committed(|p,o|word(p,o),|n|super::scis_name(n)){return;}
        }
    }
    let native:unsafe extern "thiscall" fn(usize,usize)=std::mem::transmute(ANIMATION_NATIVE);
    native(hands,weapon);
}
pub(super) unsafe fn init() {
    if READY.load(Ordering::Acquire) { return; }
    for &(p,n,h) in SIGNATURES {
        if !signature(p,n,h) { tracing::error!("SCBL_MOD_SCIS_INTERVAL enabled=false reason=signature");return; }
    }
    for site in SITES {
        if !patch(site,&call(site,NATIVE),&call(site,readiness as usize)) {
            deinit();tracing::error!("SCBL_MOD_SCIS_INTERVAL enabled=false reason=patch");return;
        }
    }
    for site in START_SITES {
        if !patch(site,&call(site,START_NATIVE),&call(site,start as usize)) {
            deinit();tracing::error!("SCBL_MOD_SCIS_INTERVAL enabled=false reason=start_patch");return;
        }
    }
    if !patch(ANIMATION_SITE,&call(ANIMATION_SITE,ANIMATION_NATIVE),&call(ANIMATION_SITE,animation as usize)) {
        deinit();tracing::error!("SCBL_MOD_SCIS_INTERVAL enabled=false reason=animation_commit_patch");return;
    }
    READY.store(true,Ordering::Release);
    tracing::info!("SCBL_MOD_SCIS_INTERVAL enabled=true seconds=1.45 clock=native_accepted_shot start_gate=true animation_commit_gate=true campaign=unchanged");
}
pub(super) unsafe fn deinit() {
    READY.store(false,Ordering::Release);
    let installed=call(ANIMATION_SITE,animation as usize);
    if readable(ANIMATION_SITE,5) && std::slice::from_raw_parts(ANIMATION_SITE as *const u8,5)==installed
        && !patch(ANIMATION_SITE,&installed,&call(ANIMATION_SITE,ANIMATION_NATIVE)) {
        tracing::error!("SCBL_MOD_SCIS_INTERVAL restore_failed site={:x}",ANIMATION_SITE);
    }
    for site in START_SITES.into_iter().rev() {
        let installed=call(site,start as usize);
        if readable(site,5) && std::slice::from_raw_parts(site as *const u8,5)==installed
            && !patch(site,&installed,&call(site,START_NATIVE)) {
            tracing::error!("SCBL_MOD_SCIS_INTERVAL restore_failed site={:x}",site);
        }
    }
    for site in SITES.into_iter().rev() {
        let installed=call(site,readiness as usize);
        if readable(site,5) && std::slice::from_raw_parts(site as *const u8,5)==installed
            && !patch(site,&installed,&call(site,NATIVE)) {
            tracing::error!("SCBL_MOD_SCIS_INTERVAL restore_failed site={:x}",site);
        }
    }
}
const SIGNATURES:&[(usize,usize,&str)]=&[
    (0xe566d0,535,"725cbdf70c0e811fd2aca375575c3212d107684cff455d45c80defba2bc621cd"),
    (0xe56dee,20,"b59faf34469553faf603908351baacaa751bda5215e897c1af30c9eb4ec53fe6"),
    (0xe5783b,20,"10f50cf841cf28a102045f9930bee3111b0866abb6210c946b39996a11d47b34"),
    (0xe57850,20,"eec7a5b906735b1335b0a2c06415c03f1b6ca7be556a1c152ce9bb0a115507bf"),
    (0xe57a18,20,"b5af1f2a0b69835a26056bd8221f4ee60ec2d9bee74593a7c1168467da6c927c"),
    (0xe094b0,260,"46c24738fdd4c2e5cd819ec15c56df13950655be815cbf7bed1602e1ee1d44dd"),
    (0xe5585d,7,"92a172398e5ef42b4058bd9406a2990a83039fe1be04601d8f8e5583187ac1f0"),
    (0xe57b6e,7,"5f360f8a145d5b324428c21f87cd416155fffd157943b0956ac8112ba7e34dc2"),
    (0xe55a1d,21,"ba82711bf15e35a5bcfc3e9330a514ab6b7014766057736d15d24f9f51c86ad9"),
];
