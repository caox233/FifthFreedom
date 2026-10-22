//! Bridge a campaign single-shot animation's queued recovery into merc FP hands.
//! E0A0E0 has already accepted the native stop request before this one callsite.
//! Keep the native stop once; advance only the exact start->stop descriptor pair.
//! A matched primary-clip wrap also consumes that pair for a local SC-IS single
//! shot: the campaign descriptor has no loop callback in merc first-person hands.
use crate::equipment_native::{patch,signature,word};
use std::sync::atomic::{AtomicBool,AtomicU32,Ordering};
#[path="scis_fire_policy.rs"] mod policy;
static READY:AtomicBool=AtomicBool::new(false);
static LOGS:AtomicU32=AtomicU32::new(0);
static ADVANCE_LOGS:AtomicU32=AtomicU32::new(0);
static LOOP_LOGS:AtomicU32=AtomicU32::new(0);
static CUT_LOGS:AtomicU32=AtomicU32::new(0);
const SITE:usize=0xe0a1ca;
const LOOP_SITE:usize=0xbbc879;
const CLASSIFY_SITE:usize=0x9c2e07;
const CUT_SITE:usize=0xbbc83d;
fn call_bytes(site:usize,target:usize)->[u8;5]{let mut b=[0xe8,0,0,0,0];b[1..].copy_from_slice(&(target as u32).wrapping_sub(site as u32+5).to_le_bytes());b}
fn bytes(target:usize)->[u8;5]{call_bytes(SITE,target)}
unsafe extern "C" fn classify(path:usize,model:usize)->u8{
    let original:unsafe extern "C" fn(usize)->u8=std::mem::transmute(0x1cfaed0usize);
    let finite=original(path);
    if finite==0 && READY.load(Ordering::Acquire)
        && policy::authored_hip_cut(model,path,|p,o|word(p,o),|n|super::super::scis_name(n)){1}else{finite}
}
// This single pinned caller owns the EBP frame: model at -2c, metadata on the
// stack. Preserve its cdecl argument and callee-saved registers; only AL changes.
#[unsafe(naked)]
unsafe extern "C" fn classify_callsite(){
    core::arch::naked_asm!(
        "push dword ptr [ebp-0x2c]",
        "push dword ptr [esp+8]",
        "call {classify}",
        "add esp,8",
        "ret",
        classify=sym classify,
    );
}
unsafe extern "thiscall" fn cut(hands:usize,event:usize){
    let pending=READY.load(Ordering::Acquire).then(||
        policy::pending_cut(hands,event,|p,o|word(p,o),|n|super::super::scis_name(n))).flatten();
    let original:unsafe extern "thiscall" fn(usize,usize)=std::mem::transmute(0xad8420usize);
    original(hands,event);
    if pending.is_none() || !READY.load(Ordering::Acquire)
        || policy::pending_cut(hands,event,|p,o|word(p,o),|n|super::super::scis_name(n))!=pending{return;}
    let advance:unsafe extern "thiscall" fn(usize)=std::mem::transmute(0xbbc2d0usize);
    advance(hands);
    if CUT_LOGS.fetch_add(1,Ordering::Relaxed)<8{
        tracing::info!("SCBL_MOD_SCIS_FIRE_RECOVERY authored_hip_cut=true single_shot=true queued_phase=2 advanced=true");
    }
}
unsafe extern "thiscall" fn loop_wrap(hands:usize,event:usize){
    let pending=READY.load(Ordering::Acquire).then(||
        policy::pending_loop(hands,event,|p,o|word(p,o),|n|super::super::scis_name(n))).flatten();
    let original:unsafe extern "thiscall" fn(usize,usize)=std::mem::transmute(0xad84d0usize);
    original(hands,event);
    if pending.is_none() || !READY.load(Ordering::Acquire)
        || policy::pending_loop(hands,event,|p,o|word(p,o),|n|super::super::scis_name(n))!=pending{return;}
    // No synthetic end/release event and no timer. The engine produced the wrap
    // for this exact clip; consume the owned recovery through native copy/release.
    let advance:unsafe extern "thiscall" fn(usize)=std::mem::transmute(0xbbc2d0usize);
    advance(hands);
    if LOOP_LOGS.fetch_add(1,Ordering::Relaxed)<8{
        tracing::info!("SCBL_MOD_SCIS_FIRE_RECOVERY native_loop=true single_shot=true queued_phase=2 advanced=true");
    }
}
unsafe extern "thiscall" fn stop(hands:usize,weapon:usize,reason:u32){
    let original:unsafe extern "thiscall" fn(usize,usize,u32)=std::mem::transmute(0xbba250usize);
    original(hands,weapon,reason);
    if !READY.load(Ordering::Acquire){return;}
    let pending=policy::pending_stop(hands,weapon,|p,o|word(p,o),|n|super::super::scis_name(n));
    // Stop-call evidence is bounded and contains no retained native pointers.
    // Do not mistake an absent success line for a disabled or unexecuted hook.
    if word(weapon,0x740).is_some_and(|n|super::super::scis_name(n))
        && LOGS.fetch_add(1,Ordering::Relaxed)<8 {
        let anim=word(hands,0x31c).unwrap_or(0) as usize;
        tracing::info!("SCBL_MOD_SCIS_FIRE_STOP hook_entered=true reason={} pending_stop={} current_enum={:?} current_flags={:?} next_enum={:?} next_flags={:?}",
            reason,pending,word(anim,0x30),word(anim,0x34),word(anim,0x50),word(anim,0x54));
    }
    if !pending{return;}
    // Consume the already-owned queued descriptor via native copying/release.
    // No forged completion notification, direct animation-pointer write or timer.
    let advance:unsafe extern "thiscall" fn(usize)=std::mem::transmute(0xbbc2d0usize);
    advance(hands);
    if ADVANCE_LOGS.fetch_add(1,Ordering::Relaxed)<8{tracing::info!("SCBL_MOD_SCIS_FIRE_RECOVERY native_stop=true queued_phase=2 advanced=true");}
}
pub(super) unsafe fn init()->bool{
    if READY.load(Ordering::Acquire){return true;}
    for &(p,n,h) in SIGNATURES{if !signature(p,n,h){return false;}}
    if !patch(SITE,&bytes(0xbba250),&bytes(stop as usize)){return false;}
    // The diagnostic observer pins BBC7F0 before this interior call is patched.
    // Its entry trampoline and this case-19 callsite do not overlap.
    #[cfg(feature="scis-runtime-diagnostics")] crate::scis_event_observer::init();
    if !patch(LOOP_SITE,&call_bytes(LOOP_SITE,0xad84d0),&call_bytes(LOOP_SITE,loop_wrap as usize)){
        #[cfg(feature="scis-runtime-diagnostics")] crate::scis_event_observer::deinit();
        if !patch(SITE,&bytes(stop as usize),&bytes(0xbba250)){
            tracing::error!("SCBL_MOD_SCIS_FIRE_RECOVERY init_rollback_failed=true");
        }
        return false;
    }
    if !patch(CUT_SITE,&call_bytes(CUT_SITE,0xad8420),&call_bytes(CUT_SITE,cut as usize))
        || !patch(CLASSIFY_SITE,&call_bytes(CLASSIFY_SITE,0x1cfaed0),&call_bytes(CLASSIFY_SITE,classify_callsite as usize)){
        deinit();return false;
    }
    READY.store(true,Ordering::Release);
    true
}
pub(super) unsafe fn deinit(){
    READY.store(false,Ordering::Release);
    for(site,original,ours)in[(CLASSIFY_SITE,0x1cfaed0,classify_callsite as usize),(CUT_SITE,0xad8420,cut as usize)]{
        if crate::equipment_native::readable(site,5)&&std::slice::from_raw_parts(site as *const u8,5)==call_bytes(site,ours)
            &&!patch(site,&call_bytes(site,ours),&call_bytes(site,original)){
            tracing::error!("SCBL_MOD_SCIS_FIRE_RECOVERY cut_restore_failed=true site={:#x}",site);
        }
    }
    if crate::equipment_native::readable(LOOP_SITE,5)&&std::slice::from_raw_parts(LOOP_SITE as *const u8,5)==call_bytes(LOOP_SITE,loop_wrap as usize)
        &&!patch(LOOP_SITE,&call_bytes(LOOP_SITE,loop_wrap as usize),&call_bytes(LOOP_SITE,0xad84d0)){
        tracing::error!("SCBL_MOD_SCIS_FIRE_RECOVERY loop_restore_failed=true");
    }
    #[cfg(feature="scis-runtime-diagnostics")] crate::scis_event_observer::deinit();
    if crate::equipment_native::readable(SITE,5)&&std::slice::from_raw_parts(SITE as *const u8,5)==bytes(stop as usize)
        &&!patch(SITE,&bytes(stop as usize),&bytes(0xbba250)){tracing::error!("SCBL_MOD_SCIS_FIRE_RECOVERY restore_failed=true");}
}
const SIGNATURES:&[(usize,usize,&str)]=&[
    (0x9c2e03,18,"d3af041b531979cf9e904589a52d9eeec2ef9e280c3fd633445ace0b8cf3599f"),
    (0x1cfaed0,134,"1ae3548dffdd7b2f44daf5e4244e418fc90f14e117b58e0060a5a718726185ce"),
    (0xad8420,101,"ed950bff8a4a66ad606c56273a539958973b25574b6fda965b4c6445be3ec54c"),
    (0xbba250,381,"060e3040bf89555269ed212b322f6c44d3ef6a6edb5a982725fefdf736e4f12e"),
    (0xbbc2d0,313,"2f45fd5778f0d2c30a6aa79c169f655bbe1f246eee4bf711f764eb6d5c5da947"),
    (0xbbc7f0,200,"789ed5265c67758099de37a3b4f0590b21a1122930dfb5b4ac8e27372411aae8"),
];
