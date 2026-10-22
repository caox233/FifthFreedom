//! Manual F5 only: follow native shot dispatch, not just SoundDefinition import.
//! Observers preserve calls, arguments and return values. No substitute audio.
use crate::equipment_native::{readable, signature, word};
use crate::scis_diagnostics as diag;
use retour::static_detour;
use std::{cell::Cell, sync::atomic::{AtomicBool, AtomicU32, Ordering}};

static READY: AtomicBool = AtomicBool::new(false);
static SEQUENCE: AtomicU32 = AtomicU32::new(0);
#[derive(Clone, Copy, Default)]
struct Context { logic: usize, weapon: usize, serial: u32, plays: u32, stops: u32 }
thread_local! { static CONTEXT: Cell<Context> = const { Cell::new(Context{logic:0,weapon:0,serial:0,plays:0,stops:0}) }; }
static_detour! {
    static Route: unsafe extern "thiscall" fn(usize);
    static Play: unsafe extern "thiscall" fn(usize,usize,u32,f32,u32,u32)->u32;
    static Stop: unsafe extern "thiscall" fn(usize,usize,f32,u32)->u32;
}
unsafe fn w(p:usize,o:usize)->u32 { word(p,o).unwrap_or(u32::MAX) }
unsafe fn byte(p:usize,o:usize)->u32 {
    p.checked_add(o).filter(|a|readable(*a,1)).map(|a|*(a as *const u8)as u32).unwrap_or(u32::MAX)
}
unsafe fn eligible(logic:usize)->bool {
    if !READY.load(Ordering::Acquire)||!diag::project_active(5) { return false; }
    let Some(world)=word(0x339e5a0,0).map(|v|v as usize) else{return false;};
    let Some(level)=word(world,0x144).filter(|v|*v>=0x10000) else{return false;};
    let Some(weapon)=word(logic,0x28).map(|v|v as usize) else{return false;};
    let Some(props)=word(logic,0x2c) else{return false;};
    word(world,0x330).is_some_and(|v|v&0x10!=0)
        && word(weapon,0)==Some(0x02a85b7c) && word(weapon,0x144)==Some(level)
        && word(weapon,0x744)==Some(props) && word(weapon,0x748)==Some(logic as u32)
        && word(props as usize,0)==Some(0x02a176c4)
        && word(weapon,0x740).is_some_and(|v|super::super::scis_name(v))
}
// Version, sequence, logic, weapon, flags, cached mode, effective mode, owner flags,
// network role, voice group, scope flags, unsuppressed/suppressed definition
// names, native accepted-shot timestamp, weapon flags, zone vtable.
unsafe fn snapshot(logic:usize,serial:u32)->[u32;16] {
    let weapon=w(logic,0x28)as usize;let pawn=w(weapon,0x8c)as usize;
    let props=w(logic,0x2c)as usize;
    // E00210 reads logic+1e4 in the ordinary branch, not props+3d. Observe
    // the exact selection without calling another native query or writing it.
    let effective=match (word(props,0x2c),word(pawn,0x5c),word(pawn,0x948)) {
        (Some(p),Some(owner),Some(scope)) if p&2!=0 && owner&0x800000!=0 && scope&0x400000!=0 => byte(props,0x41),
        (Some(_),Some(_),Some(_))=>byte(logic,0x1e4),
        _=>u32::MAX,
    };
    [1,serial,logic as u32,weapon as u32,w(logic,0xec),byte(logic,0x1e4),
     effective,w(pawn,0x5c),byte(pawn,0x4d),w(pawn,0x704),w(pawn,0x948),
     w(w(logic,0x50)as usize,0x28),w(w(logic,0x54)as usize,0x28),
     w(logic,0x198),w(weapon,0x5c),w(w(weapon,0xdc)as usize,0)]
}
pub(super) unsafe fn request(logic:usize,stage:u32) {
    if !eligible(logic){return;}
    diag::trace(diag::SOUND_REQUEST,||{
        let mut row=snapshot(logic,0);row[1]=stage;row
    });
}
fn route(logic:usize) { unsafe {
    if !eligible(logic){Route.call(logic);return;}
    let serial=SEQUENCE.fetch_add(1,Ordering::Relaxed).wrapping_add(1);
    let before=snapshot(logic,serial);
    let prior=CONTEXT.with(|c|c.replace(Context{logic,weapon:before[3]as usize,serial,..Context::default()}));
    Route.call(logic);
    let seen=CONTEXT.with(|c|c.replace(prior));
    diag::trace_bundle::<2>(diag::SOUND_ROUTE_BEFORE,||{
        let mut after=snapshot(logic,serial);after[6]=seen.plays;after[7]=seen.stops;
        [before,after]
    });
} }
unsafe fn event_row(c:Context,event:usize,flags:u32,value:f32,a:u32,b:u32,result:u32)->[u32;16] {
    let sound=w(c.logic,if w(c.logic,0xec)&1!=0{0x54}else{0x50})as usize;
    let field=if event==0{0}else{(0x58..=0x19c).step_by(4).find(|o|word(sound,*o)==Some(event as u32)).unwrap_or(0)as u32};
    [1,c.serial,c.logic as u32,c.weapon as u32,event as u32,w(event,0),w(event,0x28),
     w(event,0x40),field,flags,value.to_bits(),a,b,result,w(c.weapon,0x5c),sound as u32]
}
fn play(weapon:usize,event:usize,flags:u32,value:f32,a:u32,b:u32)->u32 { unsafe {
    let c=CONTEXT.with(Cell::get);
    let result=Play.call(weapon,event,flags,value,a,b);
    if c.logic!=0 && c.weapon==weapon && diag::project_active(5) {
        CONTEXT.with(|v|{let mut current=v.get();current.plays=current.plays.saturating_add(1);v.set(current)});
        diag::trace(diag::SOUND_EVENT,||event_row(c,event,flags,value,a,b,result));
    }
    result
} }
fn stop(weapon:usize,event:usize,value:f32,a:u32)->u32 { unsafe {
    let c=CONTEXT.with(Cell::get);
    let result=Stop.call(weapon,event,value,a);
    if c.logic!=0 && c.weapon==weapon && diag::project_active(5) {
        CONTEXT.with(|v|{let mut current=v.get();current.stops=current.stops.saturating_add(1);v.set(current)});
        diag::trace(diag::SOUND_STOP,||event_row(c,event,0,value,a,0,result));
    }
    result
} }
pub(super) unsafe fn init() {
    if READY.load(Ordering::Acquire){return;}
    if !PINS.iter().all(|&(p,n,h)|signature(p,n,h)) {
        tracing::error!("SCBL_SCIS_SOUND_TRACE enabled=false reason=signature");return;
    }
    if Route.initialize(std::mem::transmute::<usize,unsafe extern "thiscall" fn(usize)>(0xe19cd0),route).is_err()
      || Play.initialize(std::mem::transmute::<usize,unsafe extern "thiscall" fn(usize,usize,u32,f32,u32,u32)->u32>(0x544000),play).is_err()
      || Stop.initialize(std::mem::transmute::<usize,unsafe extern "thiscall" fn(usize,usize,f32,u32)->u32>(0x6843d0),stop).is_err()
      || Play.enable().is_err()||Stop.enable().is_err()||Route.enable().is_err() {
        deinit();tracing::error!("SCBL_SCIS_SOUND_TRACE enabled=false reason=install");return;
    }
    READY.store(true,Ordering::Release);
    tracing::info!("SCBL_SCIS_SOUND_TRACE enabled=true recording=manual_F5 playback=unverified");
}
pub(super) unsafe fn deinit() {
    READY.store(false,Ordering::Release);
    if Route.is_enabled(){let _=Route.disable();}
    if Stop.is_enabled(){let _=Stop.disable();}
    if Play.is_enabled(){let _=Play.disable();}
}
const PINS:&[(usize,usize,&str)]=&[
    (0xe19cd0,16,"77a822ce7346fa58886d1faf74887a93854c1b15219a035f51774434d37d173c"),
    (0x544000,256,"02f2a2320cd7a05723d0be4cc54c743f037b686a7b6b73926417db7fde932b70"),
    (0x6843d0,100,"748fa34f71ff1161e331d3f1aa7d80fe2ab7c3696546bcbe70bbc003da74ea0c"),
];
