//! Native single-shot first-person recovery, without touching weapon firing rules.
fn phase(anim:usize, offset:usize, wanted:u32, read:&impl Fn(usize,usize)->Option<u32>)->bool {
    let Some(p)=read(anim,offset).filter(|&p|p>=0x10000) else{return false;};
    let (Some(n),Some(cap))=(read(anim,offset+4),read(anim,offset+8)) else{return false;};
    if !(1..=8).contains(&n)||cap<n||cap>32{return false;}
    // Native BB9F50 stores the parameter tag as a WORD; the upper half is padding.
    (0..n as usize).any(|i|read(p as usize,i*24).is_some_and(|tag| tag & 0xffff == 0x563)&&read(p as usize,i*24+4)==Some(wanted))
}
pub(super) fn pending_stop(hands:usize, weapon:usize, read:impl Fn(usize,usize)->Option<u32>, named:impl Fn(u32)->bool)->bool {
    let check=||->Option<bool>{
        let pawn=read(hands,0x310)? as usize;let pc=read(pawn,0x938)? as usize;
        let world=read(pawn,0x144)?;let anim=read(hands,0x31c)? as usize;
        Some(read(hands,0)?==0x02a23634 && read(hands,0x318)?&2==0
            && read(pawn,0)?==0x02ad2abc && read(pawn,0x374)?&0xe0000==0xa0000
            && read(pawn,0x4c)?&0xff00!=0x200 && world>=0x10000
            && read(pawn,0x12ac)? as usize==hands && read(pawn,0x4b8)? as usize==weapon
            && read(weapon,0x8c)? as usize==pawn && named(read(weapon,0x740)?)
            && read(pc,0)?==0x02ac53ec && read(pc,0x60)?&0x40000!=0
            && read(pc,0x520)? as usize==pawn && read(pc,0x144)?==world
            && read(anim,0)?==0x02a3ecfc && read(anim,0x28)?!=0 && read(anim,0x2c)?&3==1
            && read(anim,0x30)?==0x23e && read(anim,0x34)?&3==3
            && read(anim,0x50)?==0x23e && read(anim,0x54)?&3==3
            && phase(anim,0x40,1,&read) && phase(anim,0x60,2,&read))
    }; check()==Some(true)
}

// Native kind 19 is emitted when the primary clip's time wraps, not when it
// ends. A campaign single-shot descriptor has no kind-19 callback, so the
// merc looping clip otherwise never consumes its already-queued phase 2.
// Return an identity token so the caller can revalidate after the original.
pub(super) fn pending_loop(hands:usize, event:usize, read:impl Fn(usize,usize)->Option<u32>, named:impl Fn(u32)->bool)->Option<(usize,u32)> {
    let pawn=read(hands,0x310)? as usize;
    let weapon=read(pawn,0x4b8)? as usize;
    if !pending_stop(hands,weapon,&read,named){return None;}
    let anim=read(hands,0x31c)? as usize;
    let logic=read(weapon,0x748)? as usize;
    let handle=read(anim,0x28)?;
    (read(event,0)?==19 && read(event,4)?==0x23e && read(event,8)?==handle
        && read(anim,0x4c)?==0 && read(logic,0x1e4)?&0xff==1)
        .then_some((anim,handle))
}

// The AK47 hip clip inherited by SC-IS is loop-classified although its graph
// provides an authored cut point. Only this exact queued single-shot recovery
// may use that point. Never alter a shared clip, an ADS clip or a firing input.
const HIP_SHOT:u32=0x09bb6023;
fn hand_model(hands:usize, read:&impl Fn(usize,usize)->Option<u32>)->Option<usize>{
    let primary=read(hands,0x1f0)? as usize;
    let model=if primary!=0{read(primary,0xc4)?}else{
        let fallback=read(hands,0x1f8)? as usize;
        if fallback<0x10000{return None;}read(fallback,0x64)?
    };
    (model>=0x10000).then_some(model as usize)
}
fn owned_hip(hands:usize, model:usize, read:&impl Fn(usize,usize)->Option<u32>,
    named:&impl Fn(u32)->bool)->Option<(usize,usize,u32)>{
    let pawn=read(hands,0x310)? as usize;
    let weapon=read(pawn,0x4b8)? as usize;
    if !pending_stop(hands,weapon,read,named)||hand_model(hands,read)?!=model{return None;}
    let anim=read(hands,0x31c)? as usize;
    let logic=read(weapon,0x748)? as usize;
    let clip=read(model,0x42c)? as usize;
    let handle=read(anim,0x28)?;
    (read(logic,0x1e4)?&0xff==1 && read(anim,0x4c)?==0
        && read(model,0x418)?==0x23e && read(clip,0x28)?==handle)
        .then_some((hands,anim,handle))
}
pub(super) fn authored_hip_cut(model:usize,path:usize,
    read:impl Fn(usize,usize)->Option<u32>,named:impl Fn(u32)->bool)->bool{
    let check=||->Option<bool>{
        // Read an already-resolved resource only; no lazy load on the hot path.
        if read(path,0x54)?&0x01000000!=0 || read(path,0x46)?&0xffff!=0x2e66{return None;}
        let resource=read(path,0x2c)? as usize;
        // +30 is the temporary resource-access depth, not a loaded flag.
        // Native 9C25D0/9B9720 copy/release that access around classification;
        // a resolved path normally has depth zero between calls.
        if (read(path,0x30)? as i32)<0 || read(resource,0x1a)?&0xffff!=1{return None;}
        let payload=read(resource,8)? as usize;
        if read(payload,0)?!=0x1a446e41 || read(payload,8)?!=HIP_SHOT
            || read(payload,0x12)?&4==0{return None;}
        let list=read(model,0x132c)? as usize;
        let count=read(model,0x1330)?;let cap=read(model,0x1334)?;
        if list<0x10000 || !(1..=8).contains(&count)||cap<count||cap>32{return None;}
        let mut owner=None;
        for i in 0..count as usize{
            let listener=read(list,i*4)? as usize;
            // 9730E0 increments this signed dispatch depth before invoking
            // listeners, then decrements it. Zero is a registered idle listener;
            // -1 is skipped after the native increment, not zero.
            if (read(listener,4)? as i32)<0{continue;}
            let hands=read(listener,0xc)? as usize;
            if let Some(key)=owned_hip(hands,model,&read,&named){
                // Duplicate/ambiguous ownership is not a reason to alter events.
                if owner.is_some(){return None;}owner=Some(key);
            }
        }
        Some(owner.is_some())
    };check()==Some(true)
}
pub(super) fn pending_cut(hands:usize,event:usize,
    read:impl Fn(usize,usize)->Option<u32>,named:impl Fn(u32)->bool)->Option<(usize,usize,u32)>{
    let model=hand_model(hands,&read)?;
    let key=owned_hip(hands,model,&read,&named)?;
    (read(event,0)?==1 && read(event,4)?==0x23e && read(event,8)?==key.2
        && read(event,0x18)?==HIP_SHOT).then_some((model,key.1,key.2))
}

#[cfg(test)] mod tests {
 use super::*;use std::collections::HashMap;
 fn fixture()->HashMap<(usize,usize),u32>{
 let h=0x10000;let p=0x20000;let c=0x30000;let w=0x40000;let a=0x50000;
 HashMap::from([((h,0),0x2a23634),((h,0x310),p as u32),((h,0x318),0),((h,0x31c),a as u32),
 ((p,0),0x2ad2abc),((p,0x374),0xa0000),((p,0x4c),0),((p,0x144),0x90000),((p,0x938),c as u32),((p,0x4b8),w as u32),((p,0x12ac),h as u32),
 ((c,0),0x2ac53ec),((c,0x60),0x40000),((c,0x520),p as u32),((c,0x144),0x90000),((w,0x8c),p as u32),((w,0x740),123),
 ((a,0),0x2a3ecfc),((a,0x28),0x8f),((a,0x2c),1),((a,0x30),0x23e),((a,0x34),3),((a,0x50),0x23e),((a,0x54),3),
 ((a,0x40),0x60000),((a,0x44),1),((a,0x48),1),((a,0x60),0x70000),((a,0x64),1),((a,0x68),1),
 ((0x60000,0),0x563),((0x60000,4),1),((0x70000,0),0x563),((0x70000,4),2)])
 }
 #[test] fn scis_fire_recovery_requires_every_identity_and_descriptor_field(){
 let m=fixture();let check=|m:&HashMap<_,_>|pending_stop(0x10000,0x40000,|p,o|m.get(&(p,o)).copied(),|n|n==123);
 assert!(check(&m));for key in m.keys(){let mut missing=m.clone();missing.remove(key);assert!(!check(&missing),"missing {key:?}");}
 for (key,value) in [((0x10000,0x318),2),((0x20000,0x4c),0x200),((0x50000,0x2c),3),((0x50000,0x64),9),((0x50000,0x68),64),((0x70000,4),1)]{let mut bad=m.clone();bad.insert(key,value);assert!(!check(&bad));}
 }
 #[test] fn scis_fire_recovery_reads_native_word_tag_not_padding(){
 let mut m=fixture();
 m.insert((0x60000,0),0xa5a5_0563);m.insert((0x70000,0),0xb6b6_0563);
 let check=|m:&HashMap<_,_>|pending_stop(0x10000,0x40000,|p,o|m.get(&(p,o)).copied(),|n|n==123);
 assert!(check(&m),"native WORD tag must ignore adjacent padding");
 m.insert((0x70000,0),0xb6b6_0564);
 assert!(!check(&m),"different native tag must remain rejected");
 }

 #[test] fn scis_loop_requires_owned_single_shot_matching_wrap_and_no_callback(){
 let mut m=fixture();
 for (k,v) in [((0x40000,0x748),0x80000),((0x80000,0x1e4),1),((0x50000,0x4c),0),
     ((0xa0000,0),19),((0xa0000,4),0x23e),((0xa0000,8),0x8f)]{m.insert(k,v);}
 let check=|m:&HashMap<_,_>|pending_loop(0x10000,0xa0000,|p,o|m.get(&(p,o)).copied(),|n|n==123);
 assert_eq!(check(&m),Some((0x50000,0x8f)));
 for key in m.keys(){let mut missing=m.clone();missing.remove(key);assert_eq!(check(&missing),None,"missing {key:?}");}
 for (key,value) in [((0x80000,0x1e4),2),((0x50000,0x4c),0xb0000),((0xa0000,0),2),
     ((0xa0000,0),20),((0xa0000,4),0x242),((0xa0000,8),0x90),((0x60000,4),2),
     ((0x40000,0x740),124),((0x30000,0x60),0)]{
     let mut bad=m.clone();bad.insert(key,value);assert_eq!(check(&bad),None,"invalid {key:?}");
 }
 // Mode is a byte, not the adjacent padding/flags. Holding fire is not a veto:
 // single-shot recovery follows the completed clip cycle, never a fake release.
 m.insert((0x80000,0x1e4),0xa5a50001);assert!(check(&m).is_some());
 m.insert((0x50000,0x28),0x90);assert!(check(&m).is_none());
 m.insert((0xa0000,8),0x90);assert_eq!(check(&m),Some((0x50000,0x90)));
 }

 fn hip_fixture()->HashMap<(usize,usize),u32>{
 let mut m=fixture();
 for (k,v) in [
 ((0x10000,0x1f0),0xb0000),((0xb0000,0xc4),0xc0000),
 ((0x40000,0x748),0x80000),((0x80000,0x1e4),1),((0x50000,0x4c),0),
 ((0xc0000,0x418),0x23e),((0xc0000,0x42c),0xd0000),((0xd0000,0x28),0x8f),
 ((0xc0000,0x132c),0xe0000),((0xc0000,0x1330),1),((0xc0000,0x1334),1),
 ((0xe0000,0),0xf0000),((0xf0000,4),1),((0xf0000,0xc),0x10000),
 ((0x110000,0x54),0),((0x110000,0x46),0x2e66),((0x110000,0x2c),0x120000),
 ((0x110000,0x30),1),((0x120000,0x1a),1),((0x120000,8),0x130000),
 ((0x130000,0),0x1a446e41),((0x130000,8),HIP_SHOT),((0x130000,0x12),4)
 ]{m.insert(k,v);}m
 }
 #[test] fn hip_cut_requires_loaded_authored_clip_and_unique_owned_pending_single_shot(){
 let m=hip_fixture();
 let check=|m:&HashMap<_,_>|authored_hip_cut(0xc0000,0x110000,|p,o|m.get(&(p,o)).copied(),|n|n==123);
 assert!(check(&m));
 for key in m.keys(){let mut bad=m.clone();bad.remove(key);assert!(!check(&bad),"missing {key:?}");}
 for(key,value)in[
 ((0x110000,0x54),0x01000000),((0x110000,0x46),0x3266),((0x110000,0x30),u32::MAX),
 ((0x120000,0x1a),0),((0x130000,8),0x8db239d4),((0x130000,0x12),0),
 ((0xc0000,0x1330),9),((0xc0000,0x1334),0),((0xc0000,0x1334),33),
 ((0xf0000,4),u32::MAX),((0xf0000,0xc),0x20000),((0xd0000,0x28),0x90),
 ((0xb0000,0xc4),0),((0xc0000,0x418),0x23c),((0x80000,0x1e4),2),
 ((0x50000,0x4c),0xb0000),((0x30000,0x60),0),((0x40000,0x740),124)
 ]{let mut bad=m.clone();bad.insert(key,value);assert!(!check(&bad),"invalid {key:?}");}
 let mut dup=m.clone();dup.insert((0xc0000,0x1330),2);dup.insert((0xc0000,0x1334),2);
 dup.insert((0xe0000,4),0xf0000);assert!(!check(&dup));
 let mut fallback=m.clone();fallback.insert((0x10000,0x1f0),0);
 fallback.insert((0x10000,0x1f8),0x140000);fallback.insert((0x140000,0x64),0xc0000);
 assert!(check(&fallback));
 }
 #[test] fn hip_cut_accepts_resolved_path_and_registered_listener_outside_dispatch(){
 let m=hip_fixture();
 for access in [0,1,2] {for depth in [0,1,2] {
  let mut m=m.clone();m.insert((0x110000,0x30),access);m.insert((0xf0000,4),depth);
  assert!(authored_hip_cut(0xc0000,0x110000,|p,o|m.get(&(p,o)).copied(),|n|n==123));
 }}
 }
 #[test] fn hip_cut_callback_revalidates_kind_resource_model_and_handle(){
 let mut m=hip_fixture();
 for(k,v)in[((0xa0000,0),1),((0xa0000,4),0x23e),((0xa0000,8),0x8f),((0xa0000,0x18),HIP_SHOT)]{m.insert(k,v);}
 let check=|m:&HashMap<_,_>|pending_cut(0x10000,0xa0000,|p,o|m.get(&(p,o)).copied(),|n|n==123);
 assert_eq!(check(&m),Some((0xc0000,0x50000,0x8f)));
 for(key,value)in[((0xa0000,0),19),((0xa0000,4),0x242),((0xa0000,8),0x90),
 ((0xa0000,0x18),0x8db239d4),((0xd0000,0x28),0x90),((0x70000,4),1)]{
 let mut bad=m.clone();bad.insert(key,value);assert_eq!(check(&bad),None);}
 }
}
