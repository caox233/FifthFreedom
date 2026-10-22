//! Render-thread camera commit. The paired optic branch changes only secondary P;
//! its exact before-image is restored at the end of the owning main call. All memory
//! ranges are checked before any write; no GPU/native callback runs in between.
use crate::{
    scis_frame_preparation::{still_current, Prepared},
    scis_material_binding::Read,
    scis_scene_transport::Transport,
};

pub(crate) trait CameraMemory: Read {
    fn writable(&self, address: u32, bytes: u32) -> bool;
    /// Called only after every range and current-frame identity is revalidated.
    /// Must copy immediately on the owning render thread, without callbacks.
    unsafe fn copy_matrix(&self, address: u32, value: &[f32; 16]);
    unsafe fn invalidate(&self, address: u32);
}

/// Retail1630100: six normalized planes, with its f64 arithmetic and explicit
/// f32 rounding boundaries. Compute before the commit; no native call under lock.
fn frustum(vp: &[f32; 16]) -> Option<[f32; 24]> {
    let mut planes = [0.; 24];
    for (plane, values) in planes.chunks_exact_mut(4).enumerate() {
        for (row, value) in values.iter_mut().enumerate() {
            let m = &vp[row * 4..];
            *value = match plane {
                0 => (m[3] as f64 + m[0] as f64) as f32,
                1 => (m[3] as f64 - m[0] as f64) as f32,
                2 => (m[3] as f64 + m[1] as f64) as f32,
                3 => (m[3] as f64 - m[1] as f64) as f32,
                4 => m[2],
                _ => (m[3] as f64 - m[2] as f64) as f32,
            };
        }
        let [x, y, z] = [values[0] as f64, values[1] as f64, values[2] as f64];
        let square = ((x * x + y * y) + z * z) as f32;
        let length = (square as f64).sqrt() as f32;
        if !length.is_finite() || length <= 0. {
            return None;
        }
        let gain = (1.0f64 / length as f64) as f32;
        for value in values {
            *value = (*value as f64 * gain as f64) as f32;
            if !value.is_finite() {
                return None;
            }
        }
    }
    Some(planes)
}

/// Caller serializes only the final value-only transport check and short writes.
/// Inspection/readback is finished before entering this function. View and shot
/// sample are never written. Legacy primary and paired secondary paths are exclusive.
pub(crate) unsafe fn commit(t: &mut Transport, m: &impl CameraMemory, p: &Prepared) -> bool {
    if p.correction.secondary.is_some() {
        return SECONDARY.try_with(|slot| {
            let Ok(mut slot) = slot.try_borrow_mut() else { return false; };
            // One correction per main call. Do not stack a new before-image on
            // an already corrected projection, including reentrant callbacks.
            if slot.is_some() || !commit_secondary(t, m, p) { return false; }
            *slot = Some(*p);
            true
        }).unwrap_or(false);
    }
    let Some(planes) = frustum(&p.correction.primary_vp) else {
        return false;
    };
    let ranges = [
        (p.settings.checked_add(0xd0), 64),
        (p.settings.checked_add(0x10), 64),
        (p.record.checked_add(0x234), 64),
        (p.record.checked_add(0x1b4), 64),
        (p.frame.renderer.checked_add(0x1c), 4),
        // OUTER+2100 = embedded camera renderer+20D4. Main16DF020 has
        // already copied the old frustum via+30 before reaching our seam.
        (p.frame.renderer.checked_add(0x20d4), 96),
        // 175CDD0 restores the saved camera including these planes, not just P/VP.
        (p.record.checked_add(0x2c4), 96),
    ];
    let mut addresses = [0; 7];
    let mut ends = [0; 7];
    for (i, (address, bytes)) in ranges.into_iter().enumerate() {
        let Some(a) = address else {
            return false;
        };
        let Some(end) = a.checked_add(bytes) else {
            return false;
        };
        if a < 0x10000 || a & 3 != 0 || !m.writable(a, bytes) {
            return false;
        }
        if (0..i).any(|j| a < ends[j] && addresses[j] < end) {
            return false;
        }
        addresses[i] = a;
        ends[i] = end;
    }
    if !still_current(t, m, p) {
        return false;
    }
    m.copy_matrix(addresses[0], &p.correction.primary);
    m.copy_matrix(addresses[1], &p.correction.primary_vp);
    m.copy_matrix(addresses[2], &p.correction.primary);
    m.copy_matrix(addresses[3], &p.correction.primary_vp);
    // Reuse the immediate matrix-copy primitive. These overlapping copies
    // cover exactly each preflighted 96-byte plane range, no extra bytes.
    for &address in &addresses[5..] {
        m.copy_matrix(address, planes[..16].try_into().unwrap());
        m.copy_matrix(address + 32, planes[8..].try_into().unwrap());
    }
    m.invalidate(addresses[4]);
    true
}

thread_local! {
    static SECONDARY: std::cell::RefCell<Option<Prepared>> = const { std::cell::RefCell::new(None) };
}

unsafe fn commit_secondary(t: &mut Transport, m: &impl CameraMemory, p: &Prepared) -> bool {
    let Some(secondary) = p.correction.secondary else { return false; };
    // Native record274 contains settings110, a relative-world transform, not
    // secondary P. The native saved-camera restore cannot undo this write.
    let Some(address) = p.settings.checked_add(0x160) else { return false; };
    let Some(invalidate) = p.frame.renderer.checked_add(0x1c) else { return false; };
    let Some(end) = address.checked_add(64) else { return false; };
    let Some(invalidate_end) = invalidate.checked_add(4) else { return false; };
    if address < 0x10000 || invalidate < 0x10000 || address & 3 != 0 || invalidate & 3 != 0
        || (address < invalidate_end && invalidate < end)
        || !m.writable(address, 64) || !m.writable(invalidate, 4)
        || p.correction.primary != p.camera.primary || p.correction.primary_vp != p.before_vp
        || !secondary.into_iter().all(f32::is_finite) || !still_current(t, m, p) { return false; }
    m.copy_matrix(address, &secondary);
    m.invalidate(invalidate);
    true
}

/// Owning render-thread +70 return only; consumes the token even on rejection.
/// Never restore over a foreign/native modification or through a replaced scene.
/// No resource pointers are dereferenced and no token survives into another call.
pub(crate) unsafe fn restore_secondary(m: &impl CameraMemory) -> bool {
    let pending = SECONDARY.try_with(|slot| slot.try_borrow_mut().ok().and_then(|mut s| s.take())).ok().flatten();
    let Some(p) = pending else { return false; };
    let Some(applied) = p.correction.secondary else { return false; };
    let Some(outer) = p.frame.renderer.checked_sub(0x2c) else { return false; };
    let Some(address) = p.settings.checked_add(0x160) else { return false; };
    let Some(invalidate) = p.frame.renderer.checked_add(0x1c) else { return false; };
    if m.pointer(p.frame.provider, 0x4378) != Some(outer)
        || m.pointer(p.frame.renderer, 0x20d0) != Some(p.settings)
        || p.frame.info.checked_add(0xe0) != Some(p.settings)
        || (0..16).any(|i| m.at(p.settings, 0x160 + i as u32 * 4) != Some(applied[i].to_bits()))
        || !m.writable(address, 64) || !m.writable(invalidate, 4) { return false; }
    m.copy_matrix(address, &p.camera.secondary);
    m.invalidate(invalidate);
    true
}

#[cfg(test)]
mod tests {
    use super::frustum;

    #[test]
    fn identity_planes_use_native_d3d_near_and_far_convention() {
        let mut vp = [0.; 16];
        for i in [0, 5, 10, 15] {
            vp[i] = 1.;
        }
        assert_eq!(
            frustum(&vp).unwrap(),
            [
                1., 0., 0., 1., -1., 0., 0., 1., 0., 1., 0., 1., 0., -1., 0., 1., 0., 0., 1., 0.,
                0., 0., -1., 1.,
            ]
        );
    }

    #[test]
    fn invalid_planes_fail_before_camera_writes() {
        for value in [0., f32::NAN, f32::INFINITY, f32::MAX] {
            assert!(frustum(&[value; 16]).is_none());
        }
    }
}
