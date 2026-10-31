//! Production sound + whistle source with only audio/original-whistle leaves
//! replaced. The fresh child owns synthetic memory; no game/native body replay,
//! process attach, packet, GPU, asset import or capture is performed here.
use super::*;
use std::{cell::{Cell, RefCell}, ffi::c_void};

const TEST_NAME: &str = "sticky_camera_sound::lifecycle_replay::sticky_camera_lifecycle_callbacks";
const CHILD: &str = "FF_STICKY_CAMERA_FIXTURE_CHILD";
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Action { None, DestroyEnd, ReplaceEnd, UnloadEnd, WorldEnd, DisableEnd,
    DestroyStart, ReplaceStart, NestedStartFailure, NestedStartFromEndFailure, DestroyAvailable, ReplaceAvailable }
thread_local! {
    static ACTION: Cell<Action> = const { Cell::new(Action::None) };
    static END: Cell<usize> = const { Cell::new(0) };
    static END_ACCEPTED: Cell<bool> = const { Cell::new(true) };
    static COUNTS: Cell<[usize;5]> = const { Cell::new([0;5]) };
    static CHECKS: Cell<usize> = const { Cell::new(0) };
    static FAILURES: RefCell<Vec<String>> = const { RefCell::new(Vec::new()) };
    static AVAILABLE: Cell<usize> = const { Cell::new(0) };
    static TRIGGERED: Cell<usize> = const { Cell::new(0) };
}
fn count(i:usize) { COUNTS.with(|c|{let mut v=c.get();v[i]+=1;c.set(v);}); }
fn check(ok:bool,case:&str) {
    CHECKS.with(|c|c.set(c.get()+1));
    if !ok { FAILURES.with(|f|f.borrow_mut().push(case.to_owned())); }
}
#[link(name="kernel32")] extern "system" {
    fn VirtualAlloc(p:*mut c_void,n:usize,kind:u32,protect:u32)->*mut c_void;
    fn VirtualFree(p:*mut c_void,n:usize,kind:u32)->i32;
    fn GetCurrentProcess()->*mut c_void;
    fn FlushInstructionCache(h:*mut c_void,p:*const c_void,n:usize)->i32;
}
struct Pages(Vec<usize>);
impl Pages {
    unsafe fn new()->Self {
        let mut p=Self(Vec::new());
        for (base,protection) in [(0x3390000usize,0x04),(0x540000,0x40),(0x680000,0x40),
            (0x2ab0000,0x04),(0xad0000,0x40),(0xaa0000,0x40)] {
            assert_eq!(VirtualAlloc(base as _,0x10000,0x3000,protection) as usize,base,"owned fixture reservation");
            p.0.push(base);
        }
        for (site,target) in [(0x544000usize,audio as usize),(0x6843d0,stop as usize),
            (0xad3ea0,available as usize),(0xaa5280,trigger as usize)] {
            let mut b=call(site,target);b[0]=0xe9;
            std::ptr::copy_nonoverlapping(b.as_ptr(),site as *mut u8,b.len());
            assert_ne!(FlushInstructionCache(GetCurrentProcess(),site as _,b.len()),0);
        }
        put(CAMERA as usize,0x7b0,0xad3ea0);put(CAMERA as usize,0x7b4,0xaa5280);
        p
    }
}
impl Drop for Pages {fn drop(&mut self){unsafe{for &p in &self.0{assert_ne!(VirtualFree(p as _,0,0x8000),0);}}}}
unsafe fn put(p:usize,o:usize,v:u32) { ((p+o) as *mut u32).write_unaligned(v); }
#[inline(never)]
unsafe extern "thiscall" fn original(_:usize) { count(3); }
#[inline(never)]
unsafe extern "thiscall" fn sender(_:usize) { count(4); }
unsafe extern "thiscall" fn trigger(_:usize) {TRIGGERED.with(|n|n.set(n.get()+1));}
unsafe extern "thiscall" fn available(camera:usize)->u32 {
    AVAILABLE.with(|n|n.set(n.get()+1));
    let action=ACTION.with(Cell::get);
    if matches!(action,Action::DestroyAvailable|Action::ReplaceAvailable) {
        ACTION.with(|a|a.set(Action::None));
        if action==Action::DestroyAvailable{put(camera,0x5c,1);}
        else{put(camera,0x20,word(camera,0x20).unwrap()+1);}
        crate::sticky_camera_runtime::lifecycle_fixture::destroy(camera);
    }
    1
}
unsafe extern "thiscall" fn stop(_:usize,_:usize,_:f32,_:u32) { count(2); }
unsafe extern "thiscall" fn audio(camera:usize,event:usize,_:u32,_:f32,_:u32,_:u32)->u32 {
    let ending=END.with(|e|e.get()==event);count(if ending{1}else{0});
    let action=ACTION.with(Cell::get);
    let applies=if ending {matches!(action,Action::DestroyEnd|Action::ReplaceEnd|Action::UnloadEnd|Action::WorldEnd|Action::DisableEnd|Action::NestedStartFromEndFailure)}
        else {matches!(action,Action::DestroyStart|Action::ReplaceStart|Action::NestedStartFailure)};
    if applies {
        ACTION.with(|a|a.set(Action::None)); // one bounded reentrant event
        match action {
            Action::DestroyEnd|Action::DestroyStart => {
                put(camera,0x5c,1);
                crate::sticky_camera_runtime::lifecycle_fixture::destroy(camera);
            },
            Action::ReplaceEnd|Action::ReplaceStart => {
                put(camera,0x20,word(camera,0x20).unwrap()+1);
                crate::sticky_camera_runtime::lifecycle_fixture::destroy(camera);
            },
            Action::UnloadEnd => put(event,0x20,word(event,0x20).unwrap()+1),
            Action::WorldEnd => {let info=word(0x339e5a0,0).unwrap() as usize;put(info,0x144,0);},
            Action::DisableEnd => ENABLED.store(false,Ordering::Release),
            Action::NestedStartFailure => {
                cancel(camera);
                crate::sticky_camera_runtime::lifecycle_fixture::run(camera);
                check(!BANK.lock().unwrap().shots.is_empty(),"nested_start_was_accepted");
                return 0; // only the outer publication fails
            },
            Action::NestedStartFromEndFailure => {
                crate::sticky_camera_runtime::lifecycle_fixture::run(camera);
                check(!BANK.lock().unwrap().shots.is_empty(),"nested_end_start_was_accepted");
                return 0; // old End fails after the replacement has started
            },
            _=>{},
        }
    }
    u32::from(!ending||END_ACCEPTED.with(Cell::get))
}
struct Mem(Vec<u32>);
impl Mem {
    fn new(bytes:usize)->Self {Self(vec![0;bytes/4+1])}
    fn p(&self)->usize {self.0.as_ptr() as usize}
    fn w(&mut self,o:usize,v:u32) {self.0[o/4]=v;}
}
struct Scene { world:Mem,info:Mem,world_info:Mem,manager:Mem,pawn:Mem,camera:Mem,start:Mem,end:Mem,net:Mem }
impl Scene {
    unsafe fn new()->Self {
        let mut s=Self{world:Mem::new(0x700),info:Mem::new(0x400),world_info:Mem::new(0x400),
            manager:Mem::new(0x40),pawn:Mem::new(0x960),camera:Mem::new(0xa00),start:Mem::new(0x40),end:Mem::new(0x40),net:Mem::new(0x90)};
        s.world.w(0x20,1);s.world.w(0x5a0,1.0f32.to_bits());s.world.w(0x6d0,s.world_info.p() as u32);
        s.info.w(0x20,2);s.info.w(0x330,0x10);s.info.w(0x144,s.world.p() as u32);
        s.world_info.w(0x144,s.world.p() as u32);s.world_info.w(0x37c,7);
        s.manager.w(0x20,3);
        s.pawn.w(0,0x02ad3234);s.pawn.w(0x20,4);s.pawn.w(0x144,s.world.p() as u32);s.pawn.w(0x374,0x40000);
        s.camera.w(0,CAMERA);s.camera.w(0x20,5);s.camera.w(0x144,s.world.p() as u32);s.camera.w(0x8d4,s.pawn.p() as u32);s.camera.w(0x860,2);
        s.start.w(0,EVENT);s.start.w(0x20,6);s.end.w(0,EVENT);s.end.w(0x20,7);
        put(0x339e5a0,0,s.info.p() as u32);
        let voice=Voice{start:object(s.start.p()).unwrap(),end:object(s.end.p()).unwrap(),delay:0.07};
        {let mut b=BANK.lock().unwrap();b.requests.clear();b.shots.clear();
            b.ready=Some(Ready{world:object(s.world.p()).unwrap(),manager:object(s.manager.p()).unwrap(),voices:[voice;4]});}
        END.with(|e|e.set(s.end.p()));ACTION.with(|a|a.set(Action::None));END_ACCEPTED.with(|e|e.set(true));COUNTS.with(|c|c.set([0;5]));
        AVAILABLE.with(|n|n.set(0));TRIGGERED.with(|n|n.set(0));
        ENABLED.store(true,Ordering::Release);crate::sticky_camera_runtime::lifecycle_fixture::ready(true);
        s
    }
    fn network(&mut self) {
        self.net.w(0,0x02b08c9c);self.net.w(0x88,self.camera.p() as u32);
        self.camera.w(0x4c,1<<8);self.camera.w(0x2c0,self.net.p() as u32);
    }
    fn counts(&self)->[usize;5] {COUNTS.with(Cell::get)}
    fn reset_counts(&self) {COUNTS.with(|c|c.set([0;5]));}
    unsafe fn prime(&self) {check(try_whistle(self.camera.p()),"fixture_prime_started");self.reset_counts();}
}
impl Drop for Scene {fn drop(&mut self){
    ENABLED.store(false,Ordering::Release);crate::sticky_camera_runtime::lifecycle_fixture::ready(false);
    let mut b=BANK.lock().unwrap();b.requests.clear();b.ready=None;b.shots.clear();
}}
unsafe fn replay() {
    println!(); // keep case markers separate from Rust's nocapture test prefix
    let _pages=Pages::new();
    crate::sticky_camera_runtime::lifecycle_fixture::initialize(original,sender);
    // These are production eligibility boundaries, not an implementation of
    // retail throw/replication. Native leaves record calls instead of spawning.
    for role in [1,2] {
        let mut s=Scene::new();s.pawn.w(0x4c,role<<8);s.camera.w(0x4c,role<<8);
        crate::sticky_camera_runtime::lifecycle_fixture::run(s.camera.p());
        check(s.counts()==[1,0,0,0,0],"ready_local_or_replica_uses_one_start");
        tick(s.camera.p(),1.03);check(s.counts()==[1,0,0,0,0],"end_not_due");
        tick(s.camera.p(),1.08);check(s.counts()==[1,1,0,0,0],"paired_end_once");
        cancel(s.camera.p());check(s.counts()==[1,1,0,0,0],"duplicate_cleanup_no_second_end");
    }
    {
        let mut s=Scene::new();s.camera.w(0x860,1);
        crate::sticky_camera_runtime::lifecycle_fixture::run(s.camera.p());
        check(s.counts()==[0,0,0,1,0],"initial_throw_keeps_native_whistle_leaf");
        s.camera.w(0x860,2);BANK.lock().unwrap().ready=None;s.reset_counts();
        crate::sticky_camera_runtime::lifecycle_fixture::run(s.camera.p());
        check(s.counts()==[0,0,0,1,0],"unready_assets_keep_native_whistle_leaf");
    }
    {
        let s=Scene::new();let mut copied=Mem(s.camera.0.clone());copied.w(0x20,31);
        check(try_whistle(s.camera.p())&&try_whistle(copied.p()),"separate_replica_records");
        cancel(s.camera.p());check(BANK.lock().unwrap().shots.len()==1,"one_copy_cleanup_preserves_other");
        cancel(copied.p());check(s.counts()==[2,2,0,0,0],"two_copy_pairs_close_once");
        check(try_whistle(s.camera.p()),"redeployment_after_cleanup_starts_new_pair");
    }
    for (action,marker) in [(Action::DestroyEnd,"cancel_destroy_blocks_new_start"),(Action::ReplaceEnd,"cancel_replacement_blocks_new_start"),
        (Action::UnloadEnd,"cancel_asset_reuse_blocks_new_start"),(Action::WorldEnd,"cancel_world_switch_blocks_new_start"),(Action::DisableEnd,"cancel_shutdown_blocks_new_start")] {
        let s=Scene::new();s.prime();ACTION.with(|a|a.set(action));
        check(!try_whistle(s.camera.p()),marker);
        check(s.counts()==[0,1,0,0,0],marker);
        check(BANK.lock().unwrap().shots.is_empty(),marker);
    }
    for action in [Action::DestroyEnd,Action::ReplaceEnd,Action::WorldEnd] {
        let s=Scene::new();s.prime();END_ACCEPTED.with(|a|a.set(false));ACTION.with(|a|a.set(action));
        cancel(s.camera.p());
        check(s.counts()==[0,1,0,0,0],"end_failure_does_not_stop_destroyed_or_replaced_actor");
    }
    {
        let s=Scene::new();s.prime();END_ACCEPTED.with(|a|a.set(false));cancel(s.camera.p());
        check(s.counts()==[0,1,1,0,0],"valid_end_failure_preserves_native_stop_fallback");
    }
    {
        let s=Scene::new();ACTION.with(|a|a.set(Action::NestedStartFailure));
        check(try_whistle(s.camera.p()),"nested_accepted_start_consumes_outer_fallback");
        check(BANK.lock().unwrap().shots.len()==1,"outer_failure_keeps_nested_pair");
        s.reset_counts();cancel(s.camera.p());check(s.counts()==[0,1,0,0,0],"nested_pair_still_has_paired_end");
    }
    {
        let mut s=Scene::new();s.network();ACTION.with(|a|a.set(Action::NestedStartFailure));
        crate::sticky_camera_runtime::lifecycle_fixture::run(s.camera.p());
        check(s.counts()==[2,1,0,0,1],"runtime_nested_start_never_mixes_original_whistle");
        check(s.counts()[4]==1,"runtime_nested_start_sends_once");
        check(BANK.lock().unwrap().shots.len()==1,"runtime_nested_start_remains_owned");
    }
    {
        let s=Scene::new();s.prime();ACTION.with(|a|a.set(Action::NestedStartFromEndFailure));cancel(s.camera.p());
        check(s.counts()==[1,1,0,0,0],"old_failed_end_does_not_stop_nested_pair");
        check(BANK.lock().unwrap().shots.len()==1,"nested_pair_after_end_remains_owned");
        s.reset_counts();cancel(s.camera.p());check(s.counts()==[0,1,0,0,0],"nested_pair_after_end_closes_once");
    }
    {
        let mut s=Scene::new();s.prime();s.network();ACTION.with(|a|a.set(Action::NestedStartFromEndFailure));
        crate::sticky_camera_runtime::lifecycle_fixture::run(s.camera.p());
        check(s.counts()==[1,1,0,0,1],"runtime_end_nested_start_never_mixes_or_republishes");
        check(BANK.lock().unwrap().shots.len()==1,"runtime_end_nested_start_remains_owned");
    }
    for action in [Action::DestroyStart,Action::ReplaceStart] {
        let s=Scene::new();ACTION.with(|a|a.set(action));
        crate::sticky_camera_runtime::lifecycle_fixture::run(s.camera.p());
        check(s.counts()==[1,0,0,0,0],"accepted_start_destruction_no_native_fallback");
        check(BANK.lock().unwrap().shots.is_empty(),"accepted_start_destruction_forgets_pair");
    }
    for action in [Action::DestroyEnd,Action::ReplaceEnd] {
        let s=Scene::new();s.prime();ACTION.with(|a|a.set(action));
        crate::sticky_camera_runtime::lifecycle_fixture::run(s.camera.p());
        check(s.counts()==[0,1,0,0,0],"cancel_destruction_no_native_whistle_fallback");
    }
    for action in [Action::None,Action::DestroyEnd,Action::ReplaceEnd,Action::DestroyAvailable,Action::ReplaceAvailable] {
        let s=Scene::new();s.prime();ACTION.with(|a|a.set(action));
        let expected=action==Action::None;
        check(crate::sticky_camera_runtime::lifecycle_fixture::detonate(s.camera.p())==expected,
            "detonate_result_after_native_lifecycle_callback");
        check(TRIGGERED.with(Cell::get)==usize::from(expected),"lifecycle_destruction_blocks_native_trigger");
        check(AVAILABLE.with(Cell::get)==1,"detonate_native_availability_kept_once");
    }
    let failures=FAILURES.with(|f|f.borrow().clone());
    for failure in &failures {println!("STICKY_CALLBACK_FAIL {failure}");}
    println!("STICKY_CALLBACK_RESULT checks={} failures={} native_audio=synthetic game=false",CHECKS.with(Cell::get),failures.len());
    assert!(failures.is_empty(),"STICKY_CALLBACK_EXPECTATION_FAILED");
}
#[test]
fn sticky_camera_lifecycle_callbacks() {
    if std::env::var_os(CHILD).as_deref()==Some(std::ffi::OsStr::new("1")) {
        unsafe{replay();}return;
    }
    // Fixed reservations belong to a dedicated process, not other concurrent
    // unit tests and never another application. No callback panics across ABI.
    let result=std::process::Command::new(std::env::current_exe().unwrap())
        .args(["--exact",TEST_NAME,"--nocapture","--test-threads=1"]).env(CHILD,"1").output().unwrap();
    let out=String::from_utf8_lossy(&result.stdout);let err=String::from_utf8_lossy(&result.stderr);
    assert!(out.len()+err.len()<32*1024,"bounded fixture output");
    print!("{out}");eprint!("{err}");
    assert!(result.status.success(),"STICKY_CALLBACK_CHILD_FAILED");
}
