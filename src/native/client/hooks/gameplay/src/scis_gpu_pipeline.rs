//! Creation-time proof for the exact captured reticle shader and input layout.
//! Four narrow native call-site adapters can use these wrappers; device vtables
//! are never changed. The actual D3D creation runs once with unchanged arguments.
use crate::scis_gpu_identity::matches_hash;
use std::{
    ffi::{c_void, CStr},
    sync::Mutex,
};
use windows::{
    core::{Interface, HRESULT},
    Win32::Graphics::{Direct3D11::*, Dxgi::Common::*},
};
const VS: &str = "f8a45e6636a805e4f35ddd7354db215cbfd3c70723ad6d665dbbb66d974e5176";
const PS: &str = "715a52efe26429baae8a573758eb31f862a633776975788722c0c5ff47c6531b";
#[derive(Default)]
struct Registry {
    vs: Vec<ID3D11VertexShader>,
    ps: Vec<ID3D11PixelShader>,
    layout: Vec<ID3D11InputLayout>,
}
static KNOWN: Mutex<Registry> = Mutex::new(Registry {
    vs: Vec::new(),
    ps: Vec::new(),
    layout: Vec::new(),
});
fn insert<T: PartialEq>(list: &mut Vec<T>, value: T) {
    if list.len() < 8 && !list.contains(&value) {
        list.push(value);
    }
}
unsafe fn known_bytes(code: *const c_void, length: usize, vs: bool) -> bool {
    !code.is_null()
        && length == if vs { 2972 } else { 3984 }
        && matches_hash(
            std::slice::from_raw_parts(code.cast::<u8>(), length),
            if vs { VS } else { PS },
        )
}
unsafe fn known_layout(desc: *const D3D11_INPUT_ELEMENT_DESC, count: u32) -> bool {
    if count != 6 || desc.is_null() {
        return false;
    }
    let expected = [
        (b"POSITION".as_slice(), 0, DXGI_FORMAT_R32G32B32_FLOAT, 0),
        (b"TEXCOORD".as_slice(), 0, DXGI_FORMAT_R16G16_SINT, 12),
        (b"TEXCOORD".as_slice(), 1, DXGI_FORMAT_R16G16_SINT, 16),
        (b"TANGENT".as_slice(), 0, DXGI_FORMAT_R8G8B8A8_UNORM, 20),
        (b"BINORMAL".as_slice(), 0, DXGI_FORMAT_R8G8B8A8_UNORM, 24),
        (b"NORMAL".as_slice(), 0, DXGI_FORMAT_R8G8B8A8_UNORM, 28),
    ];
    std::slice::from_raw_parts(desc, 6)
        .iter()
        .zip(expected)
        .all(|(d, (name, index, format, offset))| {
            !d.SemanticName.is_null()
                && CStr::from_ptr(d.SemanticName.0.cast()).to_bytes() == name
                && d.SemanticIndex == index
                && d.Format == format
                && d.InputSlot == 0
                && d.AlignedByteOffset == offset
                && d.InputSlotClass == D3D11_INPUT_PER_VERTEX_DATA
                && d.InstanceDataStepRate == 0
        })
}
pub unsafe extern "system" fn create_vs(
    raw: *mut c_void,
    code: *const c_void,
    length: usize,
    link: *mut c_void,
    out: *mut *mut c_void,
) -> HRESULT {
    let Some(d) = ID3D11Device::from_raw_borrowed(&raw) else {
        return HRESULT(0x80070057u32 as i32);
    };
    let hr = (d.vtable().CreateVertexShader)(raw, code, length, link, out);
    if hr.is_ok()
        && link.is_null()
        && !out.is_null()
        && !(*out).is_null()
        && known_bytes(code, length, true)
    {
        if let (Some(v), Ok(mut r)) = (ID3D11VertexShader::from_raw_borrowed(&*out), KNOWN.lock()) {
            insert(&mut r.vs, v.clone());
        }
    }
    hr
}
pub unsafe extern "system" fn create_ps(
    raw: *mut c_void,
    code: *const c_void,
    length: usize,
    link: *mut c_void,
    out: *mut *mut c_void,
) -> HRESULT {
    let Some(d) = ID3D11Device::from_raw_borrowed(&raw) else {
        return HRESULT(0x80070057u32 as i32);
    };
    let hr = (d.vtable().CreatePixelShader)(raw, code, length, link, out);
    if hr.is_ok()
        && link.is_null()
        && !out.is_null()
        && !(*out).is_null()
        && known_bytes(code, length, false)
    {
        if let (Some(v), Ok(mut r)) = (ID3D11PixelShader::from_raw_borrowed(&*out), KNOWN.lock()) {
            insert(&mut r.ps, v.clone());
        }
    }
    hr
}
pub unsafe extern "system" fn create_layout(
    raw: *mut c_void,
    desc: *const D3D11_INPUT_ELEMENT_DESC,
    count: u32,
    code: *const c_void,
    length: usize,
    out: *mut *mut c_void,
) -> HRESULT {
    let Some(d) = ID3D11Device::from_raw_borrowed(&raw) else {
        return HRESULT(0x80070057u32 as i32);
    };
    let hr = (d.vtable().CreateInputLayout)(raw, desc, count, code, length, out);
    if hr.is_ok()
        && !out.is_null()
        && !(*out).is_null()
        && known_bytes(code, length, true)
        && known_layout(desc, count)
    {
        if let (Some(v), Ok(mut r)) = (ID3D11InputLayout::from_raw_borrowed(&*out), KNOWN.lock()) {
            insert(&mut r.layout, v.clone());
        }
    }
    hr
}
pub fn reset() {
    if let Ok(mut r) = KNOWN.lock() {
        *r = Registry::default();
    }
}
/// Exact current logical pipeline, resolved from the native shader selection.
/// The registry proves immutable creation data, not a previous draw's binding.
pub fn current(v: &ID3D11VertexShader, p: &ID3D11PixelShader, l: &ID3D11InputLayout) -> bool {
    unsafe {
        let (Ok(vd), Ok(pd), Ok(ld)) = (v.GetDevice(), p.GetDevice(), l.GetDevice()) else {
            return false;
        };
        vd == pd
            && vd == ld
            && KNOWN
                .try_lock()
                .is_ok_and(|r| r.vs.contains(v) && r.ps.contains(p) && r.layout.contains(l))
    }
}
/// ContentOnly alone is never a camera-correction gate. This also requires the
/// exact bound shader and layout COM objects proven at successful creation.
pub unsafe fn bound(ctx: &ID3D11DeviceContext) -> bool {
    if ctx.GetType() != D3D11_DEVICE_CONTEXT_IMMEDIATE {
        return false;
    }
    let Ok(r) = KNOWN.try_lock() else {
        return false;
    };
    let (mut v, mut p, mut g, mut h, mut d) = (None, None, None, None, None);
    let (mut vc, mut pc) = (0, 0);
    ctx.VSGetShader(&mut v, None, Some(&mut vc));
    ctx.PSGetShader(&mut p, None, Some(&mut pc));
    ctx.GSGetShader(&mut g, None, None);
    ctx.HSGetShader(&mut h, None, None);
    ctx.DSGetShader(&mut d, None, None);
    vc == 0
        && pc == 0
        && g.is_none()
        && h.is_none()
        && d.is_none()
        && v.is_some_and(|v| r.vs.contains(&v))
        && p.is_some_and(|p| r.ps.contains(&p))
        && ctx.IAGetInputLayout().is_ok_and(|l| r.layout.contains(&l))
}
