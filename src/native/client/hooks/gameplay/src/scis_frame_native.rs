//! Concrete render-thread adapter. Entry points remain inert until the complete
//! signature-checked native patch group is installed; no worker or game polling.
use crate::scis_diagnostics as diag;
#[cfg(feature = "scis-runtime-diagnostics")]
#[path = "scis_selector_observation.rs"]
mod selector_observation;
use crate::{
    scis_frame_commit::CameraMemory,
    scis_frame_projection::Matrix,
    scis_frame_runtime::Runtime,
    scis_material_binding::{self as material, Read},
};
use std::{cell::RefCell, ffi::c_void, sync::LazyLock};
use windows::{core::Interface, Win32::Graphics::Direct3D11::*};

pub(crate) static RUNTIME: LazyLock<Runtime> = LazyLock::new(Runtime::default);
pub(crate) struct Native;
impl Read for Native {
    fn unique_member(&self, first: u32, count: u32, value: u32) -> Option<bool> {
        if first < 0x10000 || count == 0 || count > 4096 {
            return None;
        }
        let bytes = count.checked_mul(4)?;
        first.checked_add(bytes)?;
        unsafe {
            if !crate::equipment_native::readable(first as usize, bytes as usize) {
                return None;
            }
            let mut matches = 0;
            for i in 0..count {
                matches += u32::from(((first + i * 4) as *const u32).read_unaligned() == value);
            }
            Some(matches == 1)
        }
    }
    fn word(&self, a: u32) -> Option<u32> {
        unsafe { crate::equipment_native::word(a as usize, 0) }
    }
}
impl CameraMemory for Native {
    fn writable(&self, a: u32, n: u32) -> bool {
        unsafe { crate::equipment_native::writable(a as usize, n as usize) }
    }
    unsafe fn copy_matrix(&self, a: u32, v: &Matrix) {
        std::ptr::copy_nonoverlapping(v.as_ptr(), a as *mut f32, 16);
    }
    unsafe fn invalidate(&self, a: u32) {
        (a as *mut u32).write(0);
    }
}
#[derive(Default)]
struct Local {
    epoch: u64,
    renderer: u32,
    scope: Option<crate::scis_scene_source::Scope>,
    device: Option<ID3D11Device>,
    cache: crate::scis_gpu_identity::IdentityCache,
}
thread_local! {static LOCAL:RefCell<Local>=RefCell::new(Local::default());}
// Diagnostic ownership is deliberately independent of successful GPU/reticle
// identification. A rejected prediction must not hide the real native draw.
#[cfg(feature = "scis-runtime-diagnostics")]
thread_local! {static OBSERVED:RefCell<Option<(u64,u32,crate::scis_scene_source::Scope)>>=const{RefCell::new(None)};}

#[cfg(feature = "scis-runtime-diagnostics")]
thread_local! {static DRAW_SOURCE:RefCell<Option<(u32,crate::scis_scene_source::Source)>>=const{RefCell::new(None)};}

/// Copy the joined scene identity, not a pointer to a producer's stack. This is
/// diagnostic-only and is cleared at every before_main, including rejected ones.
#[cfg(feature = "scis-runtime-diagnostics")]
pub(crate) fn observe_source(renderer: u32, source: crate::scis_scene_source::Source) {
    if diag::active() {
        let _ = DRAW_SOURCE.try_with(|s| {
            if let Ok(mut s) = s.try_borrow_mut() { *s = Some((renderer, source)); }
        });
    }
}

/// CPU camera state at the native emissive callback. It is NOT a GPU capture.
/// Bits 0..11 mark individual successful reads; missing is not aliased to zero.
#[cfg(feature = "scis-runtime-diagnostics")]
fn draw_view_basis(r: &impl Read, key: [u32; 3], camera: u32, offset: u32, settings: u32) -> [u32; 16] {
    let mut out = [0; 16];
    out[..3].copy_from_slice(&key);
    for (i, k) in [0, 1, 2, 4, 5, 6, 8, 9, 10].into_iter().enumerate() {
        if let Some(v) = r.at(camera, offset + k * 4) { out[4 + i] = v; out[3] |= 1 << i; }
    }
    for i in 0..3 {
        if let Some(v) = r.at(settings, 0x150 + i * 4) { out[13 + i as usize] = v; out[3] |= 1 << (9 + i); }
    }
    out
}

unsafe fn identify(
    renderer: u32,
    scope: crate::scis_scene_source::Scope,
) -> Option<crate::scis_reticle_content::Reticle> {
    diag::hit(diag::IDENTIFY_ENTER);
    #[cfg(feature = "scis-runtime-diagnostics")]
    if diag::active() {
        let _=OBSERVED.try_with(|s|{if let Ok(mut s)=s.try_borrow_mut(){
            *s=Some((crate::scis_scene_source::binding_epoch(),renderer,scope));
        }});
    }
    let node = scope.node;
    let r = Native;
    let outer = renderer.checked_sub(0x2c)?;
    let pass = outer.checked_add(0x66c4)?;
    let class = material::EmissiveClass {
        properties: 0x2c08a14,
        implementation: 0x2c089f0,
        vertex_query: 0x1729a90,
        pixel_query: 0x1729ab0,
    };
    let mesh = r.pointer(node, 0xf8)?;
    let subs = r.pointer(mesh, 0x60)?;
    let count = r.at(mesh, 0x64)?;
    if count == 0 || count > 32 {
        return None;
    }
    diag::sample(diag::IDENTIFY_ENTRY_CONTEXT,||{
        let v=|p,o|r.at(p,o).unwrap_or(0);let settings=v(renderer,0x20d0);
        [renderer,node,pass,mesh,subs,count,settings,v(settings,0x26c),v(renderer,0x1c),
         v(node,0x38),v(node,0x34),v(node,0x2c),v(settings,0x274),v(settings,0x340),v(renderer,0x2334),v(node,0x1f4)]
    });
    // Observe the actual prepare call, not a later replay of potentially changed
    // state. Outside the bounded sample slot the original path runs unchanged.
    #[cfg(feature = "scis-runtime-diagnostics")]
    let stage = {
        let mut sampled = None;
        diag::sample(diag::STAGE_GUARD_READS, || {
            let observed = selector_observation::Observed::new(&r);
            let result = crate::scis_normal_stage::prepare_current(&observed, renderer, pass, node);
            let context = [renderer, pass, node, crate::scis_scene_source::binding_epoch() as u32];
            let words = observed.snapshot(context, result.is_some());
            diag::sample(diag::STAGE_MEMBERSHIP, || observed.membership_snapshot(context));
            sampled = Some(result);
            words
        });
        sampled.unwrap_or_else(|| crate::scis_normal_stage::prepare_current(&r, renderer, pass, node))
    };
    #[cfg(not(feature = "scis-runtime-diagnostics"))]
    let stage = crate::scis_normal_stage::prepare_current(&r, renderer, pass, node);
    let Some(stage) = stage else {
        diag::hit(diag::IDENTIFY_ALPHA_REJECT);
        return None;
    };
    let layouts = r.word(0x341fa48)?;
    let rows = r.word(0x341fa4c)?;
    let cols = r.word(0x341fa40)?;
    LOCAL
        .try_with(|local| {
            let mut local = local.try_borrow_mut().ok()?;
            let epoch = crate::scis_scene_source::binding_epoch();
            if local.epoch != epoch || local.scope != Some(scope) {
                *local = Local {
                    epoch,
                    scope: Some(scope),
                    ..Local::default()
                };
            }
            let mut budget = 4;
            local.renderer = renderer;
            let mut found = None;
            for i in 0..count {
                let sub = subs.checked_add(i * 0x90)?;
                #[cfg(feature = "scis-runtime-diagnostics")]
                let selection = {
                    let mut sampled = None;
                    if i < 8 {
                        // Only sample inside the opt-in window, once per second
                        // per submesh; no second traversal or shared trace budget.
                        diag::sample(diag::ALPHA_READS_BASE + i as usize, || {
                            let observed = selector_observation::Observed::new(&r);
                            let result = stage.select(&observed, sub, class);
                            let values =
                                observed.snapshot([renderer, pass, node, sub], result.is_some());
                            sampled = Some(result);
                            values
                        });
                    }
                    sampled.unwrap_or_else(|| stage.select(&r, sub, class))
                };
                #[cfg(not(feature = "scis-runtime-diagnostics"))]
                let selection = stage.select(&r, sub, class);
                let Some(selected) = selection else {
                    diag::hit(diag::IDENTIFY_ALPHA_REJECT);
                    continue;
                };
                let Some(draw) = material::selected_draw(&r, selected, layouts, rows, cols) else {
                    diag::hit(diag::IDENTIFY_DRAW_REJECT);
                    continue;
                };
                diag::hit(diag::IDENTIFY_DRAW_SELECTED);
                let raw = draw.vs as *mut c_void;
                let vs = ID3D11VertexShader::from_raw_borrowed(&raw)?;
                let device = vs.GetDevice().ok()?;
                if local.device.as_ref().is_some_and(|old| old != &device) {
                    local.cache.reset();
                }
                local.device = Some(device.clone());
                let ctx = device.GetImmediateContext().ok()?;
                if ctx.GetType() != D3D11_DEVICE_CONTEXT_IMMEDIATE {
                    return None;
                }
                if let Some(reticle) = crate::scis_reticle_content::inspect_selected(
                    &r,
                    &mut local.cache,
                    &ctx,
                    &mut budget,
                    selected,
                    layouts,
                    rows,
                    cols,
                ) {
                    // More than one eligible centre is ambiguous; never pick last.
                    if found.is_some() {
                        return None;
                    }
                    found = Some(reticle);
                    diag::hit(diag::IDENTIFY_RETICLE_FOUND);
                }
            }
            (crate::scis_scene_source::binding_epoch() == epoch)
                .then_some(found)
                .flatten()
        })
        .ok()
        .flatten()
}

/// Observe the actual owned emissive draw after native shader selection, rather
/// than guessing its stage from the camera selected before the main render call.
/// This diagnostic callback never suppresses or changes the native draw.
#[cfg(feature = "scis-runtime-diagnostics")]
pub(crate) unsafe extern "thiscall" fn observe_emissive(
    implementation: u32,
    renderer: u32,
    packet: u32,
    props: u32,
) {
    if diag::active() && crate::scis_frame_install::ready() {
        diag::hit(diag::DRAW_CAMERA); // All callback entries, not only matched draws.
        let _ = OBSERVED.try_with(|local| {
            let Ok(local) = local.try_borrow() else { return; };
            diag::sample(diag::DRAW_CAMERA,||{
                let r=Native;let observed=*local;
                [renderer,packet,props,implementation,r.pointer(packet,0).unwrap_or(0),
                 r.pointer(packet,0x20).unwrap_or(0),observed.map_or(0,|v|v.0 as u32),
                 observed.map_or(0,|v|v.1),observed.map_or(0,|v|v.2.node),
                 observed.map_or(0,|v|v.2.generation),crate::scis_scene_source::binding_epoch()as u32,
                 0,0,0,0,0]
            });
            let Some((epoch,owner,scope))=*local else { return; };
            if owner!=renderer || epoch!=crate::scis_scene_source::binding_epoch(){return;}
            let r = Native;
            if r.pointer(packet, 0) != Some(scope.node) || r.pointer(packet, 0x20) != Some(props) {
                return;
            }
            let Some(mesh) = r.pointer(scope.node, 0xf8) else {
                return;
            };
            let (Some(first), Some(sub), Some(count)) = (
                r.pointer(mesh, 0x60),
                r.pointer(packet, 4),
                r.at(mesh, 0x64),
            ) else {
                return;
            };
            let Some(delta) = sub.checked_sub(first) else {
                return;
            };
            if count==0 || count>32 || delta % 0x90 != 0 || delta / 0x90 >= count || delta / 0x90 >= 8 {
                return;
            }
            let _ = DRAW_SOURCE.try_with(|s| {
                let Ok(s) = s.try_borrow() else { return; };
                let Some((source_renderer, source)) = *s else { return; };
                if source_renderer != renderer || source.scope != scope { return; }
                crate::scis_draw_diagnostics::draw(&r,renderer,scope.node,scope.generation,epoch,sub);
                let key = [source.aim.frame[0], source.aim.frame[1], source.aim.request.scene];
                diag::trace(diag::DRAW_VIEW_BASIS, || {
                    let settings = r.pointer(renderer, 0x20d0).unwrap_or(0);
                    draw_view_basis(&r, key, settings, 0x50, settings)
                });
                diag::trace(diag::DRAW_RECORD_BASIS, || {
                    let settings = r.pointer(renderer, 0x20d0).unwrap_or(0);
                    let record = r.pointer(renderer, 0x1c).unwrap_or(0);
                    draw_view_basis(&r, key, record, 0x1f4, settings)
                });
                diag::trace(diag::DRAW_VIEW_CONTEXT, || {
                    [key[0], key[1], key[2], scope.node, renderer, sub,
                     r.pointer(renderer, 0x20d0).unwrap_or(0), r.pointer(renderer, 0x1c).unwrap_or(0),
                     source.aim.camera, source.aim.pawn, scope.generation, epoch as u32,
                     source.aim.direction[0].to_bits(), source.aim.direction[1].to_bits(), source.aim.direction[2].to_bits(),
                     r.at(renderer, 0x2334).unwrap_or(u32::MAX)]
                });
            });
            let id = diag::DRAW_NATIVE_BASE + (delta / 0x90) as usize * 2;
            let value = |p, o| r.at(p, o).unwrap_or(0);
            diag::trace(diag::DRAW_CAMERA,||{
                let settings=value(renderer,0x20d0);let record=value(renderer,0x1c);
                [renderer,scope.node,sub,record,settings,value(settings,0x26c),
                 value(settings,0xd0),value(settings,0xe4),value(settings,0xf0),value(settings,0xf4),
                 value(settings,0x160),value(settings,0x174),value(settings,0x180),value(settings,0x184),
                 value(record,0x254),value(record,0x258)]
            });
            diag::sample(id, || {
                let settings = value(renderer, 0x20d0);
                [
                    renderer,
                    packet,
                    scope.node,
                    sub,
                    props,
                    implementation,
                    settings,
                    value(settings, 0x340),
                    value(settings, 0x274),
                    value(renderer, 0x2334),
                    value(scope.node, 0x38),
                    value(scope.node, 0x34),
                    value(scope.node, 0x2c),
                    value(renderer, 0x2300),
                    value(renderer, 0x2304),
                    value(props, 0),
                ]
            });
            diag::sample(id + 1, || {
                let state = value(renderer, 0xc);
                let vs = value(state, 0x64);
                let ps = value(state, 0x6c);
                let texture = value(props, 0x10c);
                let vertex = value(sub, 0x50);
                [
                    state,
                    vs,
                    ps,
                    value(vs, 0x1c),
                    value(vs, 8),
                    value(ps, 8),
                    texture,
                    value(value(texture, 0x30), 0x14),
                    r.byte(texture, 0x6d).unwrap_or(0) as u32,
                    value(sub, 0x64),
                    value(vertex, 0x40),
                    value(props, 0x110),
                    value(renderer, 0x22f8),
                    value(renderer, 0x22fc),
                    value(implementation, 0),
                    value(scope.node, 0x1f4),
                ]
            });
        });
    }
    let original: unsafe extern "thiscall" fn(u32, u32, u32, u32) =
        std::mem::transmute(0x172a2d0usize);
    original(implementation, renderer, packet, props);
}

/// Signature guard and READY belong to the still-required atomic installer.
/// Called only at the ordinary main render seam, not prepass or special+6C.
thread_local! {
    static LATE_MAIN:RefCell<Option<(u32,u32,u32)>>=const{RefCell::new(None)};
}
pub(crate) unsafe extern "system" fn after_main() {
    let _restored=crate::scis_frame_commit::restore_secondary(&Native);
    #[cfg(feature="scis-runtime-diagnostics")] crate::scis_draw_diagnostics::end(_restored);
    let _=LATE_MAIN.try_with(|s|{if let Ok(mut s)=s.try_borrow_mut(){*s=None;}});
}
pub(crate) unsafe extern "system" fn before_main(provider: u32, info: u32, outer: u32) {
    after_main();
    #[cfg(feature="scis-runtime-diagnostics")] crate::scis_draw_diagnostics::begin(provider,info,outer);
    diag::hit(diag::MAIN_RENDER_ENTER);
    #[cfg(feature = "scis-runtime-diagnostics")]
    let _=OBSERVED.try_with(|s|{if let Ok(mut s)=s.try_borrow_mut(){*s=None;}});
    #[cfg(feature = "scis-runtime-diagnostics")]
    let _=DRAW_SOURCE.try_with(|s|{if let Ok(mut s)=s.try_borrow_mut(){*s=None;}});
    if !crate::scis_frame_install::ready() {
        return;
    }
    let r = Native;
    let Some(vt) = r.pointer(outer, 0) else {
        return;
    };
    if r.at(vt, 0x70) != Some(0x16dc390) || r.at(vt, 0x1c) != Some(0x16da890) {
        diag::hit(diag::MAIN_RENDER_VTABLE_REJECT);
        return;
    }
    // Ordinary deferred membership is built later by16D80C0/17462E0.
    // Keep only this call's identities, not a resource or previous-frame result.
    let stage=info.checked_add(0xe0).and_then(|settings|r.at(settings,0x340));
    if stage==Some(0) {
        let _=LATE_MAIN.try_with(|s|{if let Ok(mut s)=s.try_borrow_mut(){*s=Some((provider,info,outer));}});
    } else if stage.is_some() {
        render_bound(provider,info,outer);
    }
}

/// Exact ordinary main call site, immediately after its source lists are built.
/// No callback outside the matching +70 call may consume the retained identity.
pub(crate) unsafe extern "system" fn after_classified(outer:u32) {
    let pending=LATE_MAIN.try_with(|s|s.try_borrow_mut().ok().and_then(|mut s|s.take())).ok().flatten();
    let Some((provider,info,bound_outer))=pending else{return;};
    if outer!=bound_outer || !crate::scis_frame_install::ready(){return;}
    if render_bound(provider,info,outer) {
        // Commit invalidates renderer+1C (legacy: saved primary; paired: secondary).
        // Refresh BOTH derived constants through the original camera consumer
        // before1779EF0/geometry, not a late settings-only or GPU-only write.
        let select:unsafe extern "thiscall" fn(u32)=std::mem::transmute(0x1760b00usize);
        select(outer+0x2c);
    }
}

unsafe fn render_bound(provider:u32,info:u32,outer:u32)->bool {
    let r=Native;
    let Some(address) = r.word(0x29738f4).filter(|v| *v >= 0x10000) else {
        return false;
    };
    let multiply: unsafe extern "system" fn(*mut f32, *const f32, *const f32) -> *mut f32 =
        std::mem::transmute(address as usize);
    RUNTIME.render(
        &r,
        provider,
        info,
        outer,
        |renderer, node| identify(renderer, node),
        |a, b| {
            let mut out = [0.; 16];
            multiply(out.as_mut_ptr(), a.as_ptr(), b.as_ptr());
            out
        },
    )
}
