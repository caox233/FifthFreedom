//! MP preload admits only names containing ADV. SC-IS is a campaign name,
//! so publishing its catalog record alone leaves the native asset cache empty.
//! Extend the name filter and exact SC-IS FP resource requests; retain native streaming.
use crate::equipment_native::{patch, signature};
use std::sync::atomic::{AtomicBool, Ordering};

const SITE: usize = 0xf1dedd;
const ORIGINAL: [u8; 5] = [0xe8, 0x5e, 0xb7, 0x59, 0xff];
static READY: AtomicBool = AtomicBool::new(false);

fn call_bytes(site: usize, target: usize) -> [u8; 5] {
    let mut b = [0xe8, 0, 0, 0, 0];
    b[1..].copy_from_slice(&(target as u32).wrapping_sub(site as u32 + 5).to_le_bytes());
    b
}
unsafe fn decide(text: usize, needle: usize, native: usize, enabled: bool) -> usize {
    if native == 0
        && enabled
        && super::cbytes(needle, 4) == Some(b"ADV")
        && [super::scis::RETAIL_NAME, super::vss::RETAIL_NAME].iter()
            .any(|name| super::cbytes(text, name.len()+1) == Some(*name))
    {
        text
    } else {
        native
    }
}
unsafe extern "C" fn filter(text: usize, needle: usize) -> usize {
    let original: unsafe extern "C" fn(usize, usize) -> usize = std::mem::transmute(0x4b9640usize);
    let native = original(text, needle);
    decide(text, needle, native, READY.load(Ordering::Acquire))
}
#[path = "scis_fire_recovery.rs"]
mod fire_recovery;
#[path = "scis_scope_actor.rs"]
mod scope_actor;
#[path = "scis_streams.rs"]
mod streams;
#[path = "vss_vfx.rs"]
mod vss_vfx;
#[path = "scis_render_bank.rs"]
mod render_bank;
#[path = "scis_resource_request.rs"]
mod resource_request;
pub(super) unsafe fn resource_import<T>(enabled:bool,name:u32, original:impl FnOnce()->T)->T {
    render_bank::import(enabled && campaign_weapon(name),original)
}
// The caller binds exact campaign attachment identity and MP/local view.
pub(super) unsafe fn attachment_import<T>(enabled: bool, original: impl FnOnce() -> T) -> T {
    render_bank::import(enabled, original)
}

// Both copies retain campaign resources. VSS remains third person, but native
// local-owner imports can still select its otherwise empty high-res list.
// Mesh fallback does not imply SC-IS sound or animation queue repair.
pub(super) unsafe fn campaign_weapon(name:u32)->bool {
    [super::scis::RETAIL_NAME, super::vss::RETAIL_NAME].iter()
        .any(|n|super::native_name_is(name,n))
}
// Called once per native preload record, not on the render/input hot paths.
unsafe extern "thiscall" fn weapon_stream(
    manager: usize,
    name: u32,
    upgrades: usize,
    ammo: usize,
    vfx: usize,
) -> usize {
    let original: unsafe extern "thiscall" fn(usize, u32, usize, usize, usize) -> usize =
        std::mem::transmute(0xf18d00usize);
    let exact = ready() && campaign_weapon(name);
    let scis = exact && super::native_name_is(name, super::scis::RETAIL_NAME);
    // F18D00 imports temporary sound definitions directly via LoadConfig; it
    // never calls the runtime CA3E00 importer. Carry the exact MP SC-IS scope
    // through this preload too, so the native collectors request SVU events.
    let result = super::scis_sound::import_scope(scis, ||
        render_bank::import(exact, || original(manager, name, upgrades, ammo, vfx)));
    if scis {
        streams::complete(manager, name, true);
    } else if exact {
        streams::complete_mesh(manager, name);
    }
    result
}
unsafe extern "thiscall" fn upgrade_stream(manager: usize, name: u32) -> usize {
    let original: unsafe extern "thiscall" fn(usize, u32) -> usize =
        std::mem::transmute(0xf11b20usize);
    let scis = ready()
        && [b"Sniper_Proto_Scope".as_slice(), b"Optic_HighPowerScope"]
            .iter()
            .any(|n| streams::upgrade(n) && super::native_name_is(name, n));
    let mp = crate::equipment_native::word(0x339e5a0, 0)
        .and_then(|world| crate::equipment_native::word(world as usize, 0x330))
        .is_some_and(|flags| flags & 0x10 != 0);
    let vss = ready() && mp && super::native_name_is(name, b"Sniper_VSS_Scope");
    let result = render_bank::import(scis || vss, || original(manager, name));
    if scis || vss {
        streams::complete(manager, name, false);
    }
    result
}
// The MP catalogue preloads VFX after gun and upgrade records. Campaign VSS
// references a rifle-case class whose serialized dependency blocks this path.
// Keep its own VFX definition, but scope one property import to the retail MP class.
unsafe extern "thiscall" fn vfx_stream(manager: usize, name: u32) {
    let native: unsafe extern "thiscall" fn(usize, u32) = std::mem::transmute(0xee62f0usize);
    let mp = crate::equipment_native::word(0x339e5a0, 0)
        .and_then(|world| crate::equipment_native::word(world as usize, 0x330))
        .is_some_and(|flags| flags & 0x10 != 0);
    let exact = ready() && mp && super::native_name_is(name, super::vss::RETAIL_NAME);
    vss_vfx::scope(exact, || native(manager, name));
}
// F11290 converts a selected catalogue NetName into an equipped-weapon record.
// Unlike its catalogue lookup, CA3300 expects the native archetype name. Keep
// the saved NetName and attachment lookup intact; map only the transient FName.
unsafe fn equipment_name(text: usize, enabled: bool) -> usize {
    if enabled
        && super::cbytes(text, super::scis::COPY_NAME.len() + 1)
            == Some(super::scis::COPY_NAME)
    {
        b"Sniper_Proto\0".as_ptr() as usize
    } else if enabled && super::cbytes(text, super::vss::COPY_NAME.len()+1) == Some(super::vss::COPY_NAME) {
        b"Sniper_VSS\0".as_ptr() as usize
    } else {
        text
    }
}
unsafe extern "thiscall" fn equipment_archetype(
    output: usize,
    text: usize,
    mode: u32,
    split: u32,
) -> usize {
    let original: unsafe extern "thiscall" fn(usize, usize, u32, u32) -> usize =
        std::mem::transmute(0x4c7d80usize);
    original(output, equipment_name(text, ready()), mode, split)
}
fn sites() -> [(usize, usize, usize); 5] {
    [
        (SITE, 0x4b9640, filter as usize),
        (0xf1deff, 0xf18d00, weapon_stream as usize),
        (0xf1df58, 0xf11b20, upgrade_stream as usize),
        (0xf112e1, 0x4c7d80, equipment_archetype as usize),
        (0xf1df95, 0xee62f0, vfx_stream as usize),
    ]
}
#[cfg(test)]
fn streams_installed_in_source() -> bool {
    sites()
        .iter()
        .any(|&(s, _, h)| s == 0xf1deff && h == weapon_stream as usize)
        && sites()
            .iter()
            .any(|&(s, _, h)| s == 0xf1df58 && h == upgrade_stream as usize)
}
pub(super) fn ready() -> bool {
    READY.load(Ordering::Acquire)
}
pub(super) unsafe fn init() {
    if ready() {
        return;
    }
    if !render_bank::init() {
        tracing::error!("SCBL_MOD_SCIS_PRELOAD enabled=false reason=render-bank-signature");
        return;
    }
    for &(a, n, hash) in SIGNATURES {
        if !signature(a, n, hash) {
            tracing::error!("SCBL_MOD_SCIS_PRELOAD enabled=false reason=signature");
            return;
        }
    }
    if !vss_vfx::init() {
        tracing::error!("SCBL_MOD_SCIS_PRELOAD enabled=false reason=vss-vfx-import");
        return;
    }
    let entries = sites();
    for (i, &(site, native, hook)) in entries.iter().enumerate() {
        if !patch(site, &call_bytes(site, native), &call_bytes(site, hook)) {
            // Roll back every installed call, including the original catalog gate.
            for &(s, n, h) in entries[..i].iter().rev() {
                if !patch(s, &call_bytes(s, h), &call_bytes(s, n)) {
                    tracing::error!("SCBL_MOD_SCIS_PRELOAD rollback_failed site={:x}", s);
                }
            }
            vss_vfx::deinit();
            tracing::error!(
                "SCBL_MOD_SCIS_PRELOAD enabled=false reason=callsite site={:x}",
                site
            );
            return;
        }
    }
    if !scope_actor::init() {
        for &(s, n, h) in entries.iter().rev() {
            let _ = patch(s, &call_bytes(s, h), &call_bytes(s, n));
        }
        vss_vfx::deinit();
        tracing::error!("SCBL_MOD_SCIS_PRELOAD enabled=false reason=scope-lifecycle");
        return;
    }
    if !resource_request::init() {
        resource_request::deinit();
        scope_actor::deinit();
        for &(s,n,h) in entries.iter().rev() { let _=patch(s,&call_bytes(s,h),&call_bytes(s,n)); }
        render_bank::deinit();
        vss_vfx::deinit();
        tracing::error!("SCBL_MOD_SCIS_PRELOAD enabled=false reason=resource-request");
        return;
    }
    if !fire_recovery::init() {
        deinit();
        tracing::error!("SCBL_MOD_SCIS_PRELOAD enabled=false reason=fire-recovery");
        return;
    }
    READY.store(true, Ordering::Release);
    tracing::info!("SCBL_MOD_SCIS_PRELOAD enabled=true scope=mp-preload exact=Sniper_Proto native_dependencies=true fp_resource_fallback=true equipment_archetype_route=true");
}
pub(super) unsafe fn deinit() {
    READY.store(false, Ordering::Release);
    vss_vfx::deinit();
    fire_recovery::deinit();
    resource_request::deinit();
    render_bank::deinit();
    scope_actor::deinit();
    for (site, native, hook) in sites().into_iter().rev() {
        // Restore only our bytes. A partial init must also remain reversible.
        if crate::equipment_native::readable(site, 5)
            && std::slice::from_raw_parts(site as *const u8, 5) == call_bytes(site, hook)
            && !patch(site, &call_bytes(site, hook), &call_bytes(site, native))
        {
            tracing::error!("SCBL_MOD_SCIS_PRELOAD restore_failed site={:x}", site);
        }
    }
}
const SIGNATURES: &[(usize, usize, &str)] = &[
    (
        0xf11290,
        392,
        "2c523e3720504187bda1e86767f46522a585904acb710a58a6a60e877c90cba0",
    ),
    (
        0xf1de40,
        542,
        "b9351ffd48f2a2f04388ac4374d6d1cf4bf650f82d5e3eb393706bd6a60c0b87",
    ),
    (
        0xf18d00,
        2571,
        "0b99f5b20b6cc4c6f833114c15aa8fec6b5332fbd42e05fe80d6341e062bf028",
    ),
    (
        0xf11b20,
        930,
        "42f24a49c5007b094a4dfd866a395f468d75862f25609984bb2f7cd70302a1c0",
    ),
    (
        0x452800,
        111,
        "a94ea3ba58c4700c37fbda39bce0675e1166ee16d046665bb3d1706e85c3cc6b",
    ),
    (
        0x4b9640,
        165,
        "c6805f6683a7d1103a2fe35cffc1ae6cbf7e881eb35a39786cb878709f8b598b",
    ),
];

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn scis_preload_admits_exact_campaign_name_without_broadening_other_assets() {
        let name = b"Sniper_Proto\0";
        let needle = b"ADV\0";
        let text = name.as_ptr() as usize;
        let decide = |native, enabled| unsafe {
            super::decide(text, needle.as_ptr() as usize, native, enabled)
        };
        assert_eq!(
            decide(0, true),
            text,
            "SC-IS must reach native F18D00 preload before F1E180 consumes its record"
        );
        assert_eq!(decide(0, false), 0);
        assert_eq!(decide(123, true), 123);
        for n in [
            b"Sniper_Proto_1\0".as_slice(),
            b"Sniper_SR25\0",
            b"Sniper_Proto.Internal_MatchGradeTrigger\0",
            b"sniper_proto\0",
        ] {
            assert_eq!(
                unsafe { super::decide(n.as_ptr() as usize, needle.as_ptr() as usize, 0, true) },
                0
            );
        }
        assert_eq!(
            unsafe { super::decide(text, b"Other\0".as_ptr() as usize, 0, true) },
            0
        );
        assert_eq!(unsafe { super::decide(0, 0, 0, true) }, 0);
        assert_eq!(call_bytes(SITE, 0x4b9640), ORIGINAL);
        println!("SCIS_PRELOAD exact=admitted native_ADV=unchanged other_campaign=rejected");
    }
    #[test]
    #[ignore = "explicit x86 retail preload branch and crash-instruction replay"]
    fn scis_preload_native_branch_replay() {
        use std::ffi::c_void;
        #[link(name = "kernel32")]
        extern "system" {
            fn LoadLibraryW(path: *const u16) -> *mut c_void;
            fn GetProcAddress(m: *mut c_void, n: *const u8) -> *mut c_void;
        }
        unsafe extern "C" fn selected(text: usize, needle: usize) -> usize {
            super::decide(text, needle, 0, true)
        }
        let path =
            std::env::var("SCBL_SCIS_PRELOAD_REPLAY").expect("explicit native fixture required");
        let wide: Vec<u16> = path.encode_utf16().chain([0]).collect();
        unsafe {
            let h = LoadLibraryW(wide.as_ptr());
            assert!(!h.is_null());
            let p = GetProcAddress(h, b"scis_preload_replay\0".as_ptr());
            assert!(!p.is_null());
            let replay: unsafe extern "C" fn(usize) -> u32 = std::mem::transmute(p);
            let baseline = replay(0);
            let modified = replay(selected as usize);
            println!("SCIS_NATIVE_REPLAY retail={baseline:#x} candidate={modified:#x} fault_instruction=F1E2BA native_branch=F1DEDD");
            assert_eq!(baseline, 0xc0000005);
            assert_eq!(
                modified, 0,
                "exact SC-IS preload must prevent the missing-record null dereference"
            );
        }
    }
}

#[cfg(test)]
fn scope_installed_in_source() -> bool {
    scope_actor::entry() == 0xda6b10
}

#[test]
#[ignore = "retail x86 shared preview/loadout converter and inventory list replay"]
fn scis_native_equipment_route_preview_and_inventory() {
    use std::ffi::c_void;
    #[link(name="kernel32")] extern "system" {
        fn LoadLibraryW(p:*const u16)->*mut c_void;
        fn GetProcAddress(m:*mut c_void,p:*const u8)->*mut c_void;
    }
    unsafe extern "C" fn selected(text:usize)->usize { equipment_name(text,true) }
    unsafe {
        let path=std::env::var("SCBL_SCIS_ROUTE_FIXTURE").unwrap();
        let path:Vec<u16>=path.encode_utf16().chain(Some(0)).collect();
        let dll=LoadLibraryW(path.as_ptr());assert!(!dll.is_null());
        let init:unsafe extern "C" fn()->u32=std::mem::transmute(GetProcAddress(dll,b"init_replay\0".as_ptr()));
        let run:unsafe extern "C" fn(usize,u32,*mut u32)->u32=std::mem::transmute(GetProcAddress(dll,b"run_replay\0".as_ptr()));
        assert_eq!(init(),1);
        let hooked=sites().iter().any(|&(s,n,_)|s==0xf112e1 && n==0x4c7d80);
        let mut report=[0u32;8];assert_eq!(run(if hooked {selected as usize} else {0},0,report.as_mut_ptr()),1);
        println!("SCIS_EQUIPMENT_REPLAY preview_route={} archetype={} inventory_count={} scis_secondary={} copied_catalog_lookups={} saved_name_unchanged={} abi_errors={} list_ok={}",report[0],report[1],report[2],report[3],report[4],report[5],report[6],report[7]);
        assert_eq!(report,[1,111,2,1,2,1,0,1],"SCIS must survive the native shared preview/loadout conversion and inventory append");
        for (scenario,expected) in [(1,111),(2,222)] {
            assert_eq!(run(selected as usize,scenario,report.as_mut_ptr()),1);
            assert_eq!((report[0],report[1],report[2],report[5],report[6]),(1,expected,2,1,0));
        }
        let copy=b"SCBL_Merc_SCIS\0";let p=copy.as_ptr() as usize;
        assert_eq!(equipment_name(p,false),p);
        for text in [b"SCBL_Merc_SCIS.Other\0".as_slice(),b"Other\0",b"Sniper_Proto\0",b"scbl_merc_scis\0"] {
            let p=text.as_ptr() as usize;assert_eq!(equipment_name(p,true),p);
        }
        println!("SCIS_EQUIPMENT_CONTROLS campaign=unchanged ordinary=unchanged disabled=unchanged attachments=not_rewritten");
    }
}

#[test]
fn vss_equipment_and_preload_names_are_exact_and_do_not_rewrite_saved_ids() {
    unsafe {
        let copy=b"SCBL_Spy_VSS\0";let p=copy.as_ptr() as usize;
        assert_eq!(super::cbytes(equipment_name(p,true),64),Some(b"Sniper_VSS".as_slice()));
        assert_eq!(equipment_name(p,false),p);
        assert_eq!(copy,b"SCBL_Spy_VSS\0");
        let vss=b"Sniper_VSS\0".as_ptr() as usize;
        assert_eq!(decide(vss,b"ADV\0".as_ptr() as usize,0,true),vss);
        assert_eq!(decide(vss,b"ADV\0".as_ptr() as usize,0,false),0);
        for other in [b"SCBL_Spy_VSS.Other\0".as_slice(),b"scbl_spy_vss\0",b"Sniper_VSS\0",b"ADV_Spy_SMG_MP5\0"] {
            assert_eq!(equipment_name(other.as_ptr() as usize,true),other.as_ptr() as usize);
        }
    }
}

pub(crate) fn owns_vss_scope_node(node:usize)->bool{scope_actor::owns_vss_scope_node(node)}

pub(crate) fn scope_motion_enabled()->bool{scope_actor::scope_motion_enabled()}
pub(crate) fn scope_motion_enable(abi:u32)->bool{scope_actor::scope_motion_enable(abi)}
pub(crate) unsafe fn scope_motion_query(r:hooks_plugin_api::scope_motion::Request)->Option<[f32;2]>{scope_actor::scope_motion_query(r)}

pub(crate) unsafe fn scope_motion_query_phase(r:hooks_plugin_api::scope_motion::Request,post:bool)->Option<[f32;2]>{scope_actor::scope_motion_query_phase(r,post)}
