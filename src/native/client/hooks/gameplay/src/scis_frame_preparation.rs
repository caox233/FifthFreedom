//! Read-only join of a retained native scene, selected reticle and final pose.
//! Called only on the render thread after full queue drain and native camera
//! selection. A Prepared value is not a license to retain or reuse pointers.
use crate::scis_diagnostics as diag;
use crate::{
    scis_frame_projection::{self as projection, Camera, Correction, Matrix},
    scis_material_binding::Read,
    scis_reticle_content::Reticle,
    scis_scene_source::Source,
    scis_scene_transport::Transport,
};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) struct Frame {
    pub provider: u32,
    pub batch: u64,
    pub info: u32,
    pub renderer: u32,
}
#[derive(Clone, Copy, Debug, PartialEq)]
pub(crate) struct Prepared {
    pub frame: Frame,
    pub nonce: u64,
    pub source: Source,
    pub settings: u32,
    pub record: u32,
    pub camera: Camera,
    pub before_vp: Matrix,
    pub correction: Correction,
}
fn floats<const N: usize>(r: &impl Read, p: u32, o: u32) -> Option<[f32; N]> {
    let mut a = [0.; N];
    for (i, v) in a.iter_mut().enumerate() {
        *v = f32::from_bits(r.at(p, o.checked_add(i as u32 * 4)?)?);
        if !v.is_finite() {
            return None;
        }
    }
    Some(a)
}

/// `identify` must resolve current normal Alpha selection and call
/// scis_reticle_content::inspect_selected, not reuse last bound GPU resources.
/// Convenience entry for exclusively-owned transport. The concurrent native
/// adapter instead uses prepare_entry outside its lock, then still_current.
pub(crate) fn prepare(
    transport: &mut Transport,
    frame: Frame,
    r: &impl Read,
    identify: impl FnOnce(u32) -> Option<Reticle>,
    multiply: impl Fn(&Matrix, &Matrix) -> Matrix,
) -> Option<Prepared> {
    let settings = r.pointer(frame.renderer, 0x20d0)?;
    let entry = transport.selected(
        frame.provider,
        frame.batch,
        frame.info,
        r.at(frame.provider, 0x4330)?,
        r.at(frame.info, 8)?,
        r.at(settings, 0)?,
    )?;
    let p = prepare_entry(entry, frame, r, identify, multiply)?;
    still_current(transport, r, &p).then_some(p)
}

/// Native adapter copies Entry under the transport lock, then releases it before
/// material/GPU inspection. Revalidate against the live Transport before commit.
pub(crate) fn prepare_entry(
    entry: crate::scis_frame_join::Entry,
    frame: Frame,
    r: &impl Read,
    identify: impl FnOnce(u32) -> Option<Reticle>,
    multiply: impl Fn(&Matrix, &Matrix) -> Matrix,
) -> Option<Prepared> {
    let settings = r.pointer(frame.renderer, 0x20d0)?;
    if settings != frame.info.checked_add(0xe0)? || r.byte(frame.renderer, 0x220c)? != 0 {
        diag::hit(diag::PREPARE_SETTINGS_REJECT);return None;
    }
    if !entry.source.valid() {
        return None;
    }
    let node = entry.source.scope.node;
    // Scope preparation pins the native secondary camera group (bits18..19).
    if r.at(node, 0x38)? & 0xc0000 != 0xc0000 {
        return None;
    }
    let reticle = (identify(node)).or_else(||{diag::hit(diag::PREPARE_RETICLE_REJECT);None})?;
    if reticle.node != node {
        return None;
    }
    let pose = (projection::consumed_pose(r, frame.renderer, node, &multiply)).or_else(||{diag::hit(diag::PREPARE_POSE_REJECT);None})?;
    let slot = r.at(settings, 0x26c)?;
    // Ten 0x344-byte records end immediately before renderer+20D0.
    if slot >= 10 {
        diag::hit(diag::PREPARE_SLOT_REJECT);return None;
    }
    let record = frame
        .renderer
        .checked_add(0x24)?
        .checked_add(slot.checked_mul(0x344)?)?;
    let camera = Camera {
        view: floats(r, settings, 0x50)?,
        primary: floats(r, settings, 0xd0)?,
        secondary: floats(r, settings, 0x160)?,
        eye: floats(r, settings, 0x150)?,
    };
    let before_vp: Matrix = floats(r, settings, 0x10)?;
    diag::sample(diag::PREPARE_CAMERA,||[camera.eye[0].to_bits(),camera.eye[1].to_bits(),camera.eye[2].to_bits(),entry.source.aim.camera_origin[0].to_bits(),entry.source.aim.camera_origin[1].to_bits(),entry.source.aim.camera_origin[2].to_bits(),u32::from(floats::<16>(r,record,0x234)==Some(camera.primary)),u32::from(floats::<16>(r,record,0x1f4)==Some(camera.view)),u32::from(floats::<16>(r,record,0x1b4)==Some(before_vp)),slot,0,0,0,0,0,0]);
    if camera.eye != entry.source.aim.camera_origin
        || floats::<16>(r, record, 0x234)? != camera.primary
        || floats::<16>(r, record, 0x1f4)? != camera.view
        || floats::<16>(r, record, 0x1b4)? != before_vp
    {
        diag::hit(diag::PREPARE_CAMERA_REJECT);return None;
    }
    let correction = if crate::mac11_catalog::scope_motion_enabled() {
        projection::correct_optic_saved_phase(
            camera, before_vp, pose, reticle.local, entry.source.aim.target, f32::from_bits(entry.source.ads_fraction_bits), multiply,
        )
    } else {
        projection::correct(camera, pose, reticle.local, entry.source.aim.target, multiply)
    }.or_else(||{diag::hit(diag::PREPARE_PROJECTION_REJECT);None})?;
    diag::hit(diag::PREPARE_SUCCESS);
    #[cfg(feature="scis-runtime-diagnostics")]
    crate::scis_draw_diagnostics::prepared(r,crate::scis_draw_diagnostics::Context{
        provider:frame.provider,info:frame.info,renderer:frame.renderer,frame:entry.source.aim.frame,
        node,generation:entry.source.scope.generation,epoch:crate::scis_scene_source::binding_epoch(),nonce:entry.nonce,
        local:reticle.local,target:entry.source.aim.target,
    });
    Some(Prepared {
        frame,
        nonce: entry.nonce,
        source: entry.source,
        settings,
        record,
        camera,
        before_vp,
        correction,
    })
}

/// Revalidate immediately before the adapter writes the complete native saved
/// P+VP pair. Returned values are copied; no callback may intervene afterwards.
pub(crate) fn still_current(t: &mut Transport, r: &impl Read, p: &Prepared) -> bool {
    let check = || -> Option<bool> {
        let f = p.frame;
        let e = t.selected(
            f.provider,
            f.batch,
            f.info,
            r.at(f.provider, 0x4330)?,
            r.at(f.info, 8)?,
            r.at(p.settings, 0)?,
        )?;
        Some(
            e.nonce == p.nonce
                && e.source == p.source
                && r.pointer(f.renderer, 0x20d0)? == p.settings
                && r.byte(f.renderer, 0x220c)? == 0
                && f.renderer
                    .checked_add(0x24)?
                    .checked_add(r.at(p.settings, 0x26c)?.checked_mul(0x344)?)?
                    == p.record
                && floats::<16>(r, p.settings, 0xd0)? == p.camera.primary
                && floats::<16>(r, p.settings, 0x10)? == p.before_vp
                && floats::<16>(r, p.settings, 0x50)? == p.camera.view
                && floats::<16>(r, p.settings, 0x160)? == p.camera.secondary
                && floats::<3>(r, p.settings, 0x150)? == p.camera.eye
                && floats::<16>(r, p.record, 0x234)? == p.camera.primary
                && floats::<16>(r, p.record, 0x1b4)? == p.before_vp
                && floats::<16>(r, p.record, 0x1f4)? == p.camera.view,
        )
    };
    let mut check = check;
    check() == Some(true)
}
