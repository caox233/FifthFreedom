use super::*;
use std::ffi::c_void;
#[link(name = "kernel32")]
extern "system" {
    fn LoadLibraryW(path: *const u16) -> *mut c_void;
    fn GetProcAddress(module: *mut c_void, name: *const u8) -> *mut c_void;
}
fn props() -> Vec<u32> {
    let mut p = vec![0; 0x200 / 4];
    p[0] = ARCHETYPE_VT;
    p
}
unsafe fn render(p: usize, mesh: usize) -> [u32; 3] {
    let path = std::env::var("SCBL_SCIS_MESH_REPLAY").expect("native binding fixture required");
    let path: Vec<u16> = path.encode_utf16().chain(Some(0)).collect();
    let module = LoadLibraryW(path.as_ptr());
    assert!(!module.is_null());
    let entry = GetProcAddress(module, b"scis_mesh_replay\0".as_ptr());
    assert!(!entry.is_null());
    let run: unsafe extern "C" fn(usize, usize, *mut u32) -> u32 = std::mem::transmute(entry);
    let mut out = [0; 3];
    assert_eq!(run(p, mesh, out.as_mut_ptr()), 0);
    out
}
#[test]
#[ignore = "explicit x86 retail mesh selector/setter replay; not GPU pixels"]
fn scis_visual_native_binding_replay() {
    let mut tp = props(); let mut fp = props();
    let mut mesh = vec![0u32; 0x100 / 4];
    let m = mesh.as_mut_ptr() as usize;
    tp[0x4c / 4] = m as u32;
    let original_tp = tp.clone();
    let t = tp.as_mut_ptr() as usize; let f = fp.as_mut_ptr() as usize;
    let mut calls = Vec::new();
    let returned = unsafe { resolve_with(0x10100, 42, 1, 7, true, |mgr,n,a,b| {
        assert_eq!((mgr,n,b),(0x10100,42,7)); calls.push(a); if a==0 {t} else {f}
    }) };
    let out = unsafe { render(returned, m) };
    println!("SCIS_VISUAL returned_fp={} bound_gun={} native_draw_mode={} native_mesh_notifies={} campaign_unchanged={} calls={:?}",returned==f,out[0]!=0,out[1],out[2],tp==original_tp,calls);
    assert_eq!(returned, f);
    assert_eq!(tp, original_tp, "never write campaign/TP properties");
    assert_eq!(out[0], m as u32, "native render branch must bind the actual mesh reference");
    assert_eq!(out[1], 2);
    assert_eq!(out[2], 1);
    assert_eq!(calls, [1,0,1], "supplemental lookups retain native model cache checks");
    let mut cached_calls=0;
    assert_eq!(unsafe{resolve_with(0x10100,42,1,7,true,|_,_,a,_|{cached_calls+=1;assert_eq!(a,1);f})},f);
    assert_eq!(cached_calls,1,"cached model needs no additional load");
}

#[test]
fn scis_visual_supplemental_import_guard_is_scoped_and_thread_local() {
    let pass = |m, n, f| { assert_eq!((m, n, f), (123, 42, 1)); 77 };
    assert_eq!(default_upgrade_with(123, 42, 1, pass), 77);
    supplemental(123, || {
        assert_eq!(default_upgrade_with(123, 42, 1, |_,_,_| panic!("extra import")), 0);
        assert_eq!(default_upgrade_with(456, 42, 1, |_,_,_| 88), 88);
        assert_eq!(std::thread::spawn(|| default_upgrade_with(123, 42, 1, |_,_,_| 99)).join().unwrap(), 99);
        supplemental(456, || assert_eq!(default_upgrade_with(123, 42, 1, pass), 77));
        assert_eq!(default_upgrade_with(123, 42, 0, |_,_,_| panic!("TP import")), 0);
    });
    assert_eq!(default_upgrade_with(123, 42, 1, pass), 77);
    let _ = std::panic::catch_unwind(|| supplemental(123, || panic!("fixture")));
    assert_eq!(default_upgrade_with(123, 42, 1, pass), 77);
}

#[test]
fn scis_visual_refuses_replaced_cache_and_preserves_failure() {
    let mut tp=props();let mut fp=props();let mut replacement=props();let mut mesh=[0u32;16];
    tp[0x4c/4]=mesh.as_mut_ptr() as u32;
    let t=tp.as_mut_ptr() as usize;let f=fp.as_mut_ptr() as usize;let r=replacement.as_mut_ptr() as usize;
    let mut calls=0;
    assert_eq!(unsafe{resolve_with(0,42,1,7,true,|_,_,a,_|{calls+=1;if a==0{t}else if calls==1{f}else{r}})},f);
    assert_eq!(fp[0x4c/4],0);assert_eq!(replacement[0x4c/4],0);
    assert_eq!(unsafe{resolve_with(0,42,1,7,true,|_,_,_,_|0)},0);
}

#[test]
fn scis_visual_preserves_campaign_other_weapons_and_view_modes() {
    for (allow, first) in [(false,0),(false,1),(true,0),(true,2)] {
        let mut p=props(); let original=p.clone(); let addr=p.as_mut_ptr() as usize;
        let mut calls=0;
        let result=unsafe { resolve_with(123,456,first,9,allow,|m,n,a,b|{
            calls+=1;assert_eq!((m,n,a,b),(123,456,first,9));addr
        })};
        assert_eq!(result,addr);assert_eq!(calls,1);assert_eq!(p,original);
    }
}

#[test]
fn scis_visual_does_not_replace_native_models_or_alias_configs() {
    let mut tp=props();let mut fp=props();let mut mesh=[0u32;16];
    tp[0x4c/4]=mesh.as_mut_ptr() as u32;
    let t=tp.as_mut_ptr() as usize;let f=fp.as_mut_ptr() as usize;
    for field in [0x4c,0x50,0x54] {
        fp[field/4]=123;let before=fp.clone();
        assert!(!unsafe{bind_fallback(f,t)});assert_eq!(fp,before);fp[field/4]=0;
    }
    let before=tp.clone();assert!(!unsafe{bind_fallback(t,t)});assert_eq!(tp,before);
    assert!(!unsafe{bind_fallback(0,t)});
    assert!(!unsafe{bind_fallback(f,0)});
    tp[0]=0;assert!(!unsafe{bind_fallback(f,t)});tp[0]=ARCHETYPE_VT;
    tp[0x4c/4]=0;assert!(!unsafe{bind_fallback(f,t)});
    assert_eq!(fp[0x4c/4],0);
}
