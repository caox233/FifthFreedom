//! Startup-only, all-signature installation. Every callback remains off until
//! every site is published. Never install from a render callback or worker.
use crate::scis_diagnostics as diag;
use crate::{
    scis_frame_bridges as bridges,
    scis_frame_native::{Native, RUNTIME},
    scis_frame_patch_group as group, scis_frame_sites as sites,
    scis_material_binding::Read,
};
use std::{
    ffi::c_void,
    sync::{
        atomic::{AtomicBool, AtomicUsize, Ordering},
        Mutex,
    },
};
static READY: AtomicBool = AtomicBool::new(false);
static ADMIT_ORIGINAL: AtomicUsize = AtomicUsize::new(0);
static PATCHES: Mutex<Vec<group::Site>> = Mutex::new(Vec::new());
extern "system" {
    fn VirtualAlloc(address: *mut c_void, bytes: usize, kind: u32, protection: u32) -> *mut c_void;
    fn FlushInstructionCache(process: *mut c_void, address: *const c_void, bytes: usize) -> i32;
    fn GetCurrentProcess() -> *mut c_void;
}
pub(crate) fn ready() -> bool {
    READY.load(Ordering::Acquire)
}
unsafe fn emit(make: impl FnOnce(u32) -> Vec<u8>) -> Option<u32> {
    let p = VirtualAlloc(std::ptr::null_mut(), 512, 0x3000, 0x40);
    if p.is_null() {
        return None;
    }
    let code = make(p as u32);
    if code.is_empty() || code.len() > 512 {
        return None;
    }
    std::ptr::copy_nonoverlapping(code.as_ptr(), p.cast::<u8>(), code.len());
    if FlushInstructionCache(GetCurrentProcess(), p, code.len()) == 0 {
        return None;
    }
    // Retain executable storage for process lifetime, including install failures.
    Some(p as u32)
}
unsafe fn write(address: usize, expected: &[u8], next: &[u8]) -> bool {
    if !crate::equipment_native::readable(address, expected.len())
        || std::slice::from_raw_parts(address as *const u8, expected.len()) != expected
    {
        return false;
    }
    let Ok(_protection) = region::protect_with_handle(
        address as *mut c_void,
        next.len(),
        region::Protection::READ_WRITE_EXECUTE,
    ) else {
        return false;
    };
    // Recheck after protection acquisition; never steal a foreign hook on rollback.
    if std::slice::from_raw_parts(address as *const u8, expected.len()) != expected {
        return false;
    }
    std::ptr::copy_nonoverlapping(next.as_ptr(), address as *mut u8, next.len());
    FlushInstructionCache(GetCurrentProcess(), address as *const c_void, next.len()) != 0
}
unsafe extern "system" fn publish(command: u32, provider: u32, info: u32) {
    if ready() {
        if RUNTIME.publish(command, provider, info){diag::hit(diag::QUEUE_PUBLISHED);}else{diag::hit(diag::QUEUE_PUBLISH_REJECT);}
    }
}
unsafe extern "system" fn drained(provider: u32, _context: u32) {
    if ready() {
        if RUNTIME.drained(provider).is_some(){diag::hit(diag::QUEUE_DRAINED);}else{diag::hit(diag::QUEUE_DRAIN_REJECT);}
    }
}
unsafe extern "system" fn finished(provider: u32) {
    if ready() {
        RUNTIME.finished(provider);
    }
}
unsafe extern "thiscall" fn admitted(provider: u32, info: u32) -> u32 {
    let original: unsafe extern "thiscall" fn(u32, u32) -> u32 =
        std::mem::transmute(ADMIT_ORIGINAL.load(Ordering::Acquire));
    let before = Native.at(provider, 0x4330);
    let result = original(provider, info);
    if ready() {
        if let (Some(command), Some(b), Some(a), Some(index)) = (
            info.checked_sub(0x10),
            before,
            Native.at(provider, 0x4330),
            Native.at(info, 8),
        ) {
            if RUNTIME.admitted(command, provider, b, a, index){diag::hit(diag::QUEUE_ADMITTED);}else{diag::hit(diag::QUEUE_ADMIT_REJECT);}
        }
    }
    result
}
/// Startup only, before native resources and rendering begin.
/// The complete group publishes READY last, never independent hooks.
pub(crate) unsafe fn init() -> bool {
    let Ok(mut installed) = PATCHES.lock() else {
        return false;
    };
    if !installed.is_empty() {
        return ready();
    }
    READY.store(false, Ordering::Release);
    // Preflight every retail span before allocating/publishing anything.
    let dummy = sites::Targets {
        source: 1,
        publish: 1,
        admit: 1,
        drain: 1,
        finish: 1,
        main: 1,
        classified: 1,
        layout: 1,
        vs: 1,
        ps: 1,
        masks: 1,
        #[cfg(feature = "scis-runtime-diagnostics")]
        emissive_observer: 1,
    };
    if sites::plan(dummy).iter().any(|s| {
        !crate::equipment_native::readable(s.address, s.original.len())
            || std::slice::from_raw_parts(s.address as *const u8, s.original.len()) != s.original
    }) {
        return false;
    }
    // Every published trampoline calls this DLL. Keep it mapped for process life;
    // explicit shutdown disables callbacks but cannot assume native quiescence.
    if !crate::scis_scene_source::retain_code(init as usize) {
        return false;
    }
    let build = (|| {
        let trampoline = emit(|base| {
            let mut b = sites::ADMIT_BYTES.to_vec();
            b.extend(group::jump(
                base as usize + b.len(),
                sites::ADMIT as usize + 5,
                5,
            ));
            b
        })?;
        ADMIT_ORIGINAL.store(trampoline as usize, Ordering::Release);
        Some(sites::Targets {
            source: emit(|_| {
                crate::scis_scene_source::publication_bridge(
                    crate::scis_scene_source::publish_frame as usize as u32,
                )
            })?,
            publish: emit(|base| {
                bridges::publish(base, publish as usize as u32, 0x163a2e0, 0x667a31)
            })?,
            admit: admitted as usize as u32,
            drain: emit(|base| bridges::after_drain(base, drained as usize as u32))?,
            finish: emit(|base| bridges::finish(base, finished as usize as u32))?,
            main: emit(|base| {
                bridges::main_render(
                    base,
                    crate::scis_frame_native::before_main as usize as u32,
                    crate::scis_frame_native::after_main as usize as u32,
                    0x16df0de,
                )
            })?,
            classified: emit(|base| bridges::classified(base,crate::scis_frame_native::after_classified as usize as u32))?,
            layout: crate::scis_gpu_pipeline::create_layout as usize as u32,
            vs: crate::scis_gpu_pipeline::create_vs as usize as u32,
            ps: crate::scis_gpu_pipeline::create_ps as usize as u32,
            masks: crate::scis_shader_masks::capture as usize as u32,
            #[cfg(feature = "scis-runtime-diagnostics")]
            emissive_observer: crate::scis_frame_native::observe_emissive as usize as u32,
        })
    })();
    let Some(targets) = build else {
        return false;
    };
    let plan = sites::plan(targets);
    RUNTIME.reset();
    crate::scis_gpu_pipeline::reset();
    crate::scis_shader_masks::reset();
    if !group::publish(&plan, |a, b, n| write(a, b, n)) {
        // Retain the complete plan if rollback was denied, so shutdown can retry.
        // Keeping READY=false prevents any projection write from a leftover hook.
        let pending = group::restore(&plan, |a, b, n| {
            if crate::equipment_native::readable(a, n.len())
                && std::slice::from_raw_parts(a as *const u8, n.len()) == n
            {
                return true;
            }
            write(a, b, n)
        });
        *installed = pending;
        return false;
    }
    *installed = plan;
    READY.store(true, Ordering::Release);
    true
}
/// Non-unpatching shutdown: no instruction rewrites, COM release or loader wait.
/// The main host binding is cleared by plugin shutdown after invalidating entries.
pub(crate) fn disable() {
    READY.store(false, Ordering::Release);
    RUNTIME.reset();
}
/// Only for an explicitly quiescent owner (offline verifier / failed startup).
/// Production explicit detach uses disable; published bridges remain resident.
pub(crate) unsafe fn stop() {
    disable();
    if let Ok(mut sites) = PATCHES.lock() {
        *sites = group::restore(&sites, |a, b, n| write(a, b, n));
    }
    crate::scis_gpu_pipeline::reset();
    crate::scis_shader_masks::reset();
}
