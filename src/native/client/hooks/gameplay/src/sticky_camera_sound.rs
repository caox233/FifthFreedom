//! Sticky-camera decoy audio. Add PFS-12 suppressed dependencies to the native
//! MP loadout request, then import once at its completion boundary. Never load
//! a sound from Whistle/Tick. Actor and engine keep event ownership.
use crate::equipment_native::{float, patch, readable, signature, word, writable};
use std::sync::{Mutex, atomic::{AtomicBool, Ordering}};
const CAMERA: u32 = 0x02ab095c;
const SOUND: u32 = 0x02a0a5ec;
const EVENT: u32 = 0x029d5cb4;
const NAME: &[u8] = b"SMG_Vector_Suppressed";
const PFS: &[u8] = b"ADV_Spy_SMG_Scorpion";
const COLLECT: usize = 0xf216f2;
const COMPLETE: usize = 0xf1de2e;
static ENABLED: AtomicBool = AtomicBool::new(false);
static BANK: Mutex<Bank> = Mutex::new(Bank { requests: Vec::new(), ready: None, shots: Vec::new(), next_shot: 0 });
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
struct Object { address: usize, key: u32 }
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
struct Request { manager: Object, world: Object, id: u32 }
#[derive(Clone, Copy)]
struct Ready { world: Object, manager: Object, voices: [Voice;4] }
#[derive(Clone, Copy)]
struct Voice { start: Object, end: Object, delay: f32 }
#[derive(Clone, Copy)]
struct Shot { camera: Object, owner: Object, world: Object, start: Object, end: Object, flags: u32, began: f32, due: f32, token: u64, accepted: bool }
struct Bank { requests: Vec<Request>, ready: Option<Ready>, shots: Vec<Shot>, next_shot: u64 }
unsafe fn object(address: usize) -> Option<Object> {
    let key = word(address, 0x20)?;
    (key != u32::MAX && readable(address, 0x24)).then_some(Object { address, key })
}
unsafe fn same(o: Object) -> bool { object(o.address) == Some(o) }
unsafe fn world() -> Option<Object> {
    let info = word(0x339e5a0, 0)? as usize;
    if word(info, 0x330)? & 0x10 == 0 { return None; }
    object(word(info, 0x144)? as usize)
}
unsafe fn name_is(id: u32, expected: &[u8]) -> bool {
    if id == 0 || id >> 19 != 0 || expected.len() >= 96 { return false; }
    let Some(table) = word(0x333cd60, 0) else { return false; };
    let Some(entry) = word(table as usize, ((id & 0x3ffff) as usize) * 4) else { return false; };
    let Some(p) = (entry as usize).checked_add(8) else { return false; };
    readable(p, expected.len()+1)
        && std::slice::from_raw_parts(p as *const u8, expected.len()) == expected
        && *(p as *const u8).add(expected.len()) == 0
}
unsafe fn array(p: usize, stride: usize, max: usize) -> Option<(usize,usize)> {
    let n = word(p,4)? as usize; let cap = word(p,8)? as usize;
    if n > max || cap < n || cap > max*4 { return None; }
    let data = word(p,0)? as usize;
    (n == 0 || readable(data,n.checked_mul(stride)?)).then_some((data,n))
}
// F1DE40/F18D00 have already built one 0x64-byte dependency record for
// ADV_Spy_SMG_Scorpion. Do not call its collector again or alter its lists.
unsafe fn dependencies(manager: usize) -> Option<Vec<u32>> {
    let (data,n) = array(manager+0x334,0x64,512)?;
    let mut found = None;
    for i in 0..n {
        let p = data+i*0x64;
        if name_is(word(p,0)?,PFS) {
            if found.replace(p).is_some() { return None; }
        }
    }
    let p=found?; let mut names=Vec::new();
    for lane in [0x4c,0x58] {
        let (data,n)=array(p+lane,4,256)?;
        if n==0 { return None; }
        for i in 0..n {
            let name=word(data,i*4)?;
            if name==0 { return None; }
            if !names.contains(&name) { names.push(name); }
        }
    }
    Some(names)
}
fn call(site: usize,target: usize)->[u8;5] {
    let mut b=[0xe8,0,0,0,0]; b[1..].copy_from_slice(&(target as u32).wrapping_sub(site as u32+5).to_le_bytes()); b
}
// This is the same native queue and requestID used for selected-weapon assets.
// Loading this small sound definition for all native MP loadout requests avoids
// pretending that an unobserved gadget-class spelling is a proven class-list ID.
unsafe extern "thiscall" fn collect(manager: usize,id:u32,classes:usize,pending:usize) {
    let original: unsafe extern "thiscall" fn(usize,u32,usize,usize)=std::mem::transmute(0xf1e0c0usize);
    original(manager,id,classes,pending);
    if !ENABLED.load(Ordering::Acquire) { return; }
    let (Some(world),Some(manager_id),Some(names),Some(_)) =
        (world(),object(manager),dependencies(manager),array(pending,4,4096)) else { return; };
    if word(manager,0x5c).is_none_or(|v|v&1!=0) { return; }
    let request=Request{manager:manager_id,world,id};
    {
        let Ok(mut b)=BANK.lock() else { return; };
        b.requests.retain(|r|r.world==world && r.manager==manager_id);
        if b.ready.is_some_and(|r|r.world!=world) { b.ready=None; }
        if b.requests.len()>=64 || b.requests.contains(&request) { return; }
        b.requests.push(request);
    }
    if word(0x337cf84,0)==Some(0) {
        let init:unsafe extern "C" fn(usize)->usize=std::mem::transmute(0x4cc410usize);
        init(b"Core\0".as_ptr() as usize);
    }
    let Some(class)=word(0x337cf84,0).filter(|x|*x>=0x10000) else { forget(request); return; };
    let queue:unsafe extern "thiscall" fn(usize,u32,u32,usize,usize)=std::mem::transmute(0xf19850usize);
    for name in names { queue(manager,id,name,class as usize,pending); }
    // No pending names means the native queue confirmed every dependency was
    // already present. Otherwise wait for F1DE10's native completion callback.
    if word(pending,4)==Some(0) { completed(manager,id); }
}
fn forget(request: Request) {
    if let Ok(mut b)=BANK.lock() { b.requests.retain(|r|*r!=request); }
}
unsafe extern "thiscall" fn complete(manager:usize,loaded:usize,pawn:usize,id:u32) {
    let original:unsafe extern "thiscall" fn(usize,usize,usize,u32)=std::mem::transmute(0xf19a50usize);
    original(manager,loaded,pawn,id);
    if ENABLED.load(Ordering::Acquire) { completed(manager,id); }
}
unsafe fn completed(manager:usize,id:u32) {
    let Some(w)=world() else{return;}; let Some(m)=object(manager) else{return;};
    let r=Request{manager:m,world:w,id};
    let take=if let Ok(mut b)=BANK.lock() { b.requests.iter().position(|x|*x==r).map(|i|b.requests.remove(i)).is_some() }else{false};
    if !take || word(manager,0x5c).is_none_or(|v|v&1!=0) { return; }
    // F19A50 has completed native ownership/finalization before entering here.
    // Collector globals must be inactive; listener exclusions are scoped exactly
    // as the existing weapon importer, then restored before leaving this call.
    if word(0x330c498,0)!=Some(0) || word(0x330c4b0,0)!=Some(0) { return; }
    let Some(root)=word(0x339e5a4,0) else{return;};
    let Some(sound_manager)=word(root as usize,0x20).filter(|x|*x>=0x10000) else{return;};
    let flags=0x330c4b4usize;
    let prior=[word(flags,0),word(flags,4),word(flags,8)];
    if !writable(flags,12) || !matches!(prior[0],Some(0|1)) || !prior[1..].iter().all(|v|matches!(v,Some(0|1))) {return;}
    struct Restore(usize,[u32;3]);
    impl Drop for Restore {fn drop(&mut self){unsafe{for(i,v)in self.1.iter().enumerate(){((self.0+i*4)as*mut u32).write_unaligned(*v);}}}}
    let restore=Restore(flags,prior.map(Option::unwrap));
    ((flags+4)as*mut u32).write_unaligned(0); ((flags+8)as*mut u32).write_unaligned(0);
    let mut name=0u32;
    let make:unsafe extern "thiscall" fn(usize,usize,u32,u32)->usize=std::mem::transmute(0x4c7d80usize);
    make(&mut name as*mut u32 as usize,b"SMG_Vector_Suppressed\0".as_ptr()as usize,1,0);
    if !name_is(name,NAME){return;}
    let import:unsafe extern "thiscall" fn(usize,u32)->usize=std::mem::transmute(0xca3e00usize);
    let sound=import(sound_manager as usize,name);
    // A partial definition may still lack completed assets. Refresh only
    // here, after this request's assets complete, never in Whistle or Tick.
    if word(sound,0)==Some(SOUND) && word(sound,0x28)==Some(name) && !events_ready(sound) {
        let load:unsafe extern "thiscall" fn(usize,u32,usize,usize,usize)=std::mem::transmute(0x4d43e0usize);
        let section=b"Echelon.ESoundDefinition.SMG_Vector_Suppressed\0".as_ptr()as usize;
        load(sound,0,0,b"WeaponConfiguration.ini\0".as_ptr()as usize,section);
        if word(sound_manager as usize,0x28).is_some_and(|f|f&1!=0){load(sound,0,0,b"ConfigOverrides.ini\0".as_ptr()as usize,section);}
    }
    drop(restore);
    if world()==Some(w) {
        if let (Some(voices),Ok(mut b))=(snapshot(sound),BANK.lock()) {
            b.ready=Some(Ready{world:w,manager:m,voices});
        }
    }
}
unsafe fn event(p:usize)->Option<Object>{
    if word(p,0)!=Some(EVENT) || word(p,0x1c)? & 0x8000 != 0 || word(p,4)? & 0x100000 != 0 {return None;}
    object(p)
}
unsafe fn events_ready(sound:usize)->bool {
    if word(sound,0)!=Some(SOUND) || word(sound,0x1c).is_none_or(|f|f&0x8000!=0) || word(sound,4).is_none_or(|f|f&0x100000!=0) || !word(sound,0x28).is_some_and(|n|name_is(n,NAME)){return false;}
    [0x58,0xc0,0xf8,0x130,0x168].iter().all(|o|word(sound,*o).is_some_and(|p|event(p as usize).is_some()))
        && [0x98,0xd4,0x10c,0x144,0x17c].iter().all(|o|(0..9).all(|i|word(sound,*o+i*4).is_some_and(|p|event(p as usize).is_some())))
        && (0..5).all(|i|float(sound,0x3c+i*4).is_some_and(|d|d.is_finite()&&(0.001..=0.15).contains(&d)))
}
// CA3E00's MP definition is per-weapon, not rooted by the global cache.
// It can disappear after completion; Rust statics are NOT engine GC roots.
// F19A50/F18C50 retain/serialize the requested native event assets. Copy only
// their identities and scalar timing while the definition is valid. Never
// retain or reread the transient definition from Whistle/Tick.
unsafe fn snapshot(sound:usize)->Option<[Voice;4]> {
    if !events_ready(sound){return None;}
    let voice=|i:usize|->Option<Voice>{
        let start=event(word(sound,0xc0+i*0x38)? as usize)?;
        let base=0xd4+i*0x38;
        let end=event(word(sound,base)? as usize)?;
        // Every environment in this PFS definition must select the same End.
        if !(1..9).all(|j|word(sound,base+j*4)==Some(end.address as u32)){return None;}
        Some(Voice{start,end,delay:float(sound,0x40+i*4)?})
    };
    Some([voice(0)?,voice(1)?,voice(2)?,voice(3)?])
}
unsafe fn cached_voice(r:Ready,variant:usize)->Option<Voice>{
    if world()!=Some(r.world)||!same(r.manager){return None;}
    let v=*r.voices.get(variant)?;
    // Native unload/respawn/world changes still fail shut. Recycled event
    // pointers may have a valid vtable: compare their captured object keys too.
    (event(v.start.address)==Some(v.start)&&event(v.end.address)==Some(v.end)).then_some(v)
}
unsafe fn identity(camera:usize)->Option<(Object,Object,Object,f32)> {
    if word(camera,0)!=Some(CAMERA)||word(camera,0x5c)?&1!=0{return None;}
    let w=world()?; if word(camera,0x144)?as usize!=w.address{return None;}
    let owner=word(camera,0x8d4)?as usize;
    if word(owner,0)!=Some(0x02ad3234) || word(owner,0x144)?as usize!=w.address || word(owner,0x5c)?&1!=0{return None;}
    if word(owner,0x374)?&(0x40000|0x80000)!=0x40000{return None;}
    let now=float(w.address,0x5a0)?;if !now.is_finite()||now<0.0{return None;}
    Some((object(camera)?,object(owner)?,w,now))
}
unsafe fn play(camera:usize,event:usize,flags:u32)->bool {
    let native:unsafe extern "thiscall" fn(usize,usize,u32,f32,u32,u32)->u32=std::mem::transmute(0x544000usize);
    native(camera,event,flags,0.0,0,0)!=0
}
/// World emitter, not a fictitious first-person weapon. Native NPC variant and
/// its paired Start/End events are used; Sam Sweetener alone is not a shot.
#[derive(Clone,Copy,Debug,PartialEq,Eq)]
pub(crate) enum WhistleEmission { Unavailable, Accepted, Superseded }
pub(crate) unsafe fn whistle_emission(camera:usize)->WhistleEmission {
    if !ENABLED.load(Ordering::Acquire){return WhistleEmission::Unavailable;}
    let Some((camera_id,owner,w,_now))=identity(camera)else{return WhistleEmission::Unavailable;};
    if word(camera,0x858).is_none_or(|f|f&0x800!=0){return WhistleEmission::Unavailable;}
    let ready=if let Ok(b)=BANK.lock(){b.ready}else{None};let Some(ready)=ready else{return WhistleEmission::Unavailable;};
    let Some(variant)=word(owner.address,0x704).filter(|v|*v<=3).map(|v|v as usize)else{return WhistleEmission::Unavailable;};
    let Some(Voice{start,end,delay})=cached_voice(ready,variant)else{return WhistleEmission::Unavailable;};
    cancel(camera); // close an earlier pair before the next 70ms pulse, never mix in Whistle.
    // End/Stop are native calls, not inert bookkeeping. A nested cleanup,
    // world change or asset unload must not publish a new Start from the
    // identities captured before cancel. Re-read only the existing fields.
    let Some((current_camera,current_owner,current_world,now))=identity(camera)else{return WhistleEmission::Unavailable;};
    if !ENABLED.load(Ordering::Acquire)||(current_camera,current_owner,current_world)!=(camera_id,owner,w)
        ||word(camera,0x858).is_none_or(|f|f&0x800!=0)||cached_voice(ready,variant).is_none(){return WhistleEmission::Unavailable;}
    let role=owner.address+0x4d;if !readable(role,1){return WhistleEmission::Unavailable;}
    let flags=if *(role as*const u8)==2{7}else{0};
    let token={
        let Ok(mut b)=BANK.lock()else{return WhistleEmission::Unavailable;};
        if let Some(existing)=b.shots.iter().find(|s|s.camera==camera_id) {
            // End may synchronously run a new Whistle before cancel returns.
            // That accepted replacement already owns publication as well.
            return if existing.accepted {WhistleEmission::Superseded}else{WhistleEmission::Unavailable};
        }
        if b.shots.len()>=32{return WhistleEmission::Unavailable;}
        let Some(token)=b.next_shot.checked_add(1)else{return WhistleEmission::Unavailable;};
        b.next_shot=token;
        b.shots.push(Shot{camera:camera_id,owner,world:w,start,end,flags,began:now,due:now+delay,token,accepted:false});
        token
    };
    // Never hold BANK across native audio publication.
    let accepted=play(camera,start.address,flags);
    let current=identity(camera).is_some_and(|(c,o,w,_)|(c,o,w)==(camera_id,owner,current_world));
    let nested_accepted=if let Ok(mut b)=BANK.lock(){
        // An outer failed publication cannot erase a nested replacement's
        // record, even when the engine reuses the same object identity.
        if !accepted||!current {b.shots.retain(|s|s.token!=token);}
        else if let Some(s)=b.shots.iter_mut().find(|s|s.token==token){s.accepted=true;}
        current&&b.shots.iter().any(|s|s.camera==camera_id&&s.token!=token&&s.accepted)
    }else{false};
    // A nested accepted pair already owns this emission. Returning false to
    // Whistle would mix the original sound into that surviving replacement.
    if nested_accepted {WhistleEmission::Superseded}
    else if accepted {WhistleEmission::Accepted}
    else {WhistleEmission::Unavailable}
}
// Compatibility helper for the fixed four-role fixture: its boolean assertions
// compare the old production entry with the new production outcome projection.
#[cfg(test)]
unsafe fn try_whistle(camera:usize)->bool {whistle_emission(camera)!=WhistleEmission::Unavailable}
/// Parent calls before native camera Tick, and cancel before Disable/Cleanup.
/// No raw actor/event is used after identity changes or native destruction.
pub(crate) unsafe fn tick(camera:usize,now:f32){finish(camera,Some(now));}
pub(crate) unsafe fn cancel(camera:usize){finish(camera,None);}
unsafe fn finish(camera:usize,now:Option<f32>){
    let candidate={let Ok(b)=BANK.lock()else{return;};b.shots.iter().find(|s|s.camera.address==camera).copied()};
    let Some(s)=candidate else{return;};
    let valid=identity(camera).is_some_and(|(c,o,w,_)|c==s.camera&&o==s.owner&&w==s.world)&&same(s.end)&&event(s.end.address)==Some(s.end);
    let due=now.is_none_or(|n|!n.is_finite()||n<s.began||n>=s.due);
    if valid&&!due{return;}
    let removed=if let Ok(mut b)=BANK.lock(){
        b.shots.iter().position(|x|x.token==s.token).map(|i|b.shots.remove(i)).is_some()
    }else{false};
    if !removed{return;}
    if valid && !play(camera,s.end.address,s.flags)
        &&identity(camera).is_some_and(|(c,o,w,_)|(c,o,w)==(s.camera,s.owner,s.world))
        &&BANK.lock().is_ok_and(|b|!b.shots.iter().any(|new|new.camera==s.camera))
        &&same(s.start)&&event(s.start.address)==Some(s.start) {
        let stop:unsafe extern "thiscall" fn(usize,usize,f32,u32)=std::mem::transmute(0x6843d0usize);
        stop(camera,s.start.address,0.0,0);
    }
}
pub(crate) fn enabled()->bool { ENABLED.load(Ordering::Acquire) }
pub(crate) unsafe fn init(){
    if ENABLED.load(Ordering::Acquire){return;}
    if !PINS.iter().all(|&(a,n,h)|signature(a,n,h)){return;}
    if !patch(COLLECT,&call(COLLECT,0xf1e0c0),&call(COLLECT,collect as usize))
        ||!patch(COMPLETE,&call(COMPLETE,0xf19a50),&call(COMPLETE,complete as usize)){deinit();return;}
    ENABLED.store(true,Ordering::Release);
}
pub(crate) unsafe fn deinit(){
    let actors=if let Ok(b)=BANK.lock(){b.shots.iter().map(|s|s.camera.address).collect::<Vec<_>>()}else{Vec::new()};
    for a in actors{cancel(a);}
    ENABLED.store(false,Ordering::Release);
    let _=patch(COLLECT,&call(COLLECT,collect as usize),&call(COLLECT,0xf1e0c0));
    let _=patch(COMPLETE,&call(COMPLETE,complete as usize),&call(COMPLETE,0xf19a50));
    if let Ok(mut b)=BANK.lock(){b.requests.clear();b.ready=None;b.shots.clear();}
}
const PINS:&[(usize,usize,&str)]=&[
(0xf216e1,22,"c18ba3707dc9686e22dddc2147a4c33e1c77fc7808a4e338be1937a755842bac"),
(0xf1e0c0,185,"2901cc75be0994982bcb53e0d76c2de5edf2ca72dc90b36a09fa81f8740a88bd"),
(0xf19850,499,"815e12b85938278e770163bd4d4f39a34f35aa65307ee23e019195562cced453"),
(0xf1de10,39,"9a83893a7c446530f22e9daccbd5056415249b6fd975a496c8775dcd21f4280b"),
(0xf19a50,762,"e632639f5e1c10691feb86e5ee1e46593d6dfc951b2dd21320616efa5370a06d"),
(0xca3e00,693,"ca7e4322013a4f77bd2e6b70b6b84160552f99ff3f2f4db4374910ba59d86867"),
(0xca36d0,72,"561bb8f793d8309efa0397774fe6cfbafeef355237ff0bae2459aea1bd3df58b"),
(0xc7c7d0,86,"05630af41bd1be48c3e8e06e57c98225eb74b64c56bab92d7488f783b51b8bbd"),
(0xde69e0,236,"ac87f99554305a5a0eb7877fa1d832cbc066e2f7f9be1d68f5bbfa62390bd880"),
(0xde8030,2475,"2c29f8174d8f1722e7811cc7be750eaa3ec1ed1841da3f4810a4d5ce50c10553"),
(0x544000,158,"69835d0e1888fe3358b9e523986e8082311bc28c2486909d324610a0f2766682"),
(0x6843d0,69,"527da20cdf40125b88957e612467965fad929bc45122b210ac1c18bfbb6be1f0"),
];

#[cfg(test)]
#[path="sticky_camera_lifecycle_tests.rs"]
mod lifecycle_replay;
