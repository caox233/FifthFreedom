//! Proof for one selected, render-thread-owned native emissive draw.
//! Does not authorize a camera write on its own: provider/frame ownership and
//! the consumed final node transform must be joined separately.
use crate::{
    scis_gpu_identity::{CurrentDraw, DrawRange, IdentityCache, Proof},
    scis_gpu_pipeline,
    scis_material_binding::{self, Draw, Read},
};
use std::ffi::c_void;
use windows::{
    core::Interface,
    Win32::Graphics::{
        Direct3D::D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST, Direct3D11::*,
        Dxgi::Common::DXGI_FORMAT_R16_UINT,
    },
};

#[derive(Clone, Copy, Debug, PartialEq)]
pub(crate) struct Reticle {
    pub node: u32,
    pub local: [f32; 3],
    pub draw: Draw,
}
pub(crate) unsafe fn inspect(
    r: &impl Read,
    cache: &mut IdentityCache,
    ctx: &ID3D11DeviceContext,
    budget: &mut u32,
    packet: u32,
    node: u32,
    state: u32,
    layouts: u32,
    rows: u32,
    cols: u32,
) -> Option<Reticle> {
    let draw = scis_material_binding::current(r, packet, node, state, layouts, rows, cols)?;
    inspect_draw(cache, ctx, budget, node, draw)
}

pub(crate) unsafe fn inspect_selected(
    r: &impl Read,
    cache: &mut IdentityCache,
    ctx: &ID3D11DeviceContext,
    budget: &mut u32,
    selected: scis_material_binding::Selection,
    layouts: u32,
    rows: u32,
    cols: u32,
) -> Option<Reticle> {
    let draw = scis_material_binding::selected_draw(r, selected, layouts, rows, cols)?;
    inspect_draw(cache, ctx, budget, selected.node, draw)
}

unsafe fn inspect_draw(
    cache: &mut IdentityCache,
    ctx: &ID3D11DeviceContext,
    budget: &mut u32,
    node: u32,
    draw: Draw,
) -> Option<Reticle> {
    // Record only already-read values, inside the existing opt-in time window.
    // This distinguishes shader/device/content rejection without another GPU
    // readback or a second traversal of live material state.
    let observed = |state: u32, remaining: u32, readbacks: u32, hits: u32| {
        crate::scis_diagnostics::trace(crate::scis_diagnostics::RETICLE_GPU_PROOF, || [
            node, state, draw.vb, draw.ib, draw.srv, draw.vs, draw.ps, draw.layout,
            draw.stride, draw.indices, draw.offset, draw.index_offset,
            draw.base as u32, remaining, readbacks, hits,
        ]);
    };
    let (raw_v, raw_i, raw_s, raw_vs, raw_ps, raw_l) = (
        draw.vb as *mut c_void,
        draw.ib as *mut c_void,
        draw.srv as *mut c_void,
        draw.vs as *mut c_void,
        draw.ps as *mut c_void,
        draw.layout as *mut c_void,
    );
    let (vb, ib, srv, vs, ps, layout) = (
        ID3D11Buffer::from_raw_borrowed(&raw_v)?,
        ID3D11Buffer::from_raw_borrowed(&raw_i)?,
        ID3D11ShaderResourceView::from_raw_borrowed(&raw_s)?,
        ID3D11VertexShader::from_raw_borrowed(&raw_vs)?,
        ID3D11PixelShader::from_raw_borrowed(&raw_ps)?,
        ID3D11InputLayout::from_raw_borrowed(&raw_l)?,
    );
    if !scis_gpu_pipeline::current(vs, ps, layout) {
        observed(1, *budget, cache.readbacks, cache.hits);
        return None;
    }
    let (Ok(context_device), Ok(shader_device)) = (ctx.GetDevice(), vs.GetDevice()) else {
        observed(2, *budget, cache.readbacks, cache.hits);
        return None;
    };
    if !crate::scis_gpu_device::same(&context_device, &shader_device) {
        observed(2, *budget, cache.readbacks, cache.hits);
        return None;
    }
    let proof = cache.current_draw(
        ctx,
        CurrentDraw {
            vb,
            ib,
            srv,
            stride: draw.stride,
            offset: draw.offset,
            index_offset: draw.index_offset,
            format: DXGI_FORMAT_R16_UINT,
            topology: D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST,
            range: DrawRange {
                indices: draw.indices,
                start: draw.start,
                base: draw.base,
            },
        },
        budget,
    );
    observed(match proof { Proof::Unknown => 3, Proof::Mismatch => 4, Proof::ContentOnly => 5 },
             *budget, cache.readbacks, cache.hits);
    if proof != Proof::ContentOnly {
        return None;
    }
    // The exact registered shaders pass TEXCOORD0/2048 directly to diffuse t0;
    // they do not use props+F0/F8 for its UV. Pinned triangle [8,10,2] and texture
    // centre produce this local position, independently verified against RDC.
    Some(Reticle {
        node,
        local: [20.0660524373, 0.0013266886, 0.3026576026],
        draw,
    })
}
