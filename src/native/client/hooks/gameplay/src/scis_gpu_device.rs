//! Compare native and ReShade device identities without weakening resource proof.
//! ReShade 6.8 redirects resource GetDevice to its proxy, while shader GetDevice
//! can still return the original device. Their ordinary IUnknown identities differ.
use windows::{core::{Interface, IUnknown, GUID}, Win32::Graphics::Direct3D11::ID3D11Device};

// Public IID_UnwrappedObject in ReShade v6.8.0 source/com_utils.hpp.
const UNWRAPPED: GUID = GUID::from_u128(0x7f2c9a11_3b4e_4d6a_812f_5e9cd37a1b42);
const NO_INTERFACE: i32 = 0x80004002u32 as i32;

pub(crate) fn same(a: &ID3D11Device, b: &ID3D11Device) -> bool {
    if a.as_raw() == b.as_raw() { return true; }
    match (identity(a), identity(b)) {
        (Some(a), Some(b)) => a.as_raw() == b.as_raw(),
        _ => false,
    }
}

fn identity(device: &ID3D11Device) -> Option<IUnknown> {
    let mut current = device.cast::<IUnknown>().ok()?;
    let mut seen = [std::ptr::null_mut(); 4];
    for slot in 0..seen.len() {
        let p = current.as_raw();
        if seen[..slot].contains(&p) { return None; }
        seen[slot] = p;
        let mut raw = std::ptr::null_mut();
        // QueryInterface transfers one owned reference on success. No private
        // proxy offsets, device replacement, hook removal or context writes.
        let hr = unsafe { current.query(&UNWRAPPED, &mut raw) };
        if hr.0 == NO_INTERFACE { return Some(current); }
        if hr.is_err() || raw.is_null() { return None; }
        let next = unsafe { IUnknown::from_raw(raw) };
        // An arbitrary unwrapped object is not proof of a D3D11 device.
        current = next.cast::<ID3D11Device>().ok()?.cast::<IUnknown>().ok()?;
    }
    None // Cycles / excessive nesting fail closed.
}
