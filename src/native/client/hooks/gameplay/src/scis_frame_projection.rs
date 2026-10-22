//! Final render-node pose and paired camera projection for a retained scene.
//! This is preparation only. The native adapter must commit the pair to the
//! same saved camera record, before use; changing just settings.P is ineffective.
use crate::scis_material_binding::Read;

pub(crate) type Matrix = [f32; 16];
#[derive(Clone, Copy, Debug, PartialEq)]
pub(crate) struct Pose {
    pub matrix: [f32; 12],
    pub scale: [f32; 4],
}

fn floats<const N: usize>(r: &impl Read, base: u32, offset: u32) -> Option<[f32; N]> {
    let mut out = [0.; N];
    for (i, v) in out.iter_mut().enumerate() {
        *v = f32::from_bits(r.at(base, offset.checked_add(i as u32 * 4)?)?);
        if !v.is_finite() {
            return None;
        }
    }
    Some(out)
}

/// Predict exactly the pose consumed by ordinary native1737E00. A clean CB
/// owns its cached matrix AND scale; reading today's node matrix is then wrong.
/// `multiply` is the same D3DXMatrixMultiply entry used by retail, not a new
/// actor transform. Allocation, auxiliary and special-object paths fail closed.
pub(crate) fn consumed_pose(
    r: &impl Read,
    renderer: u32,
    node: u32,
    multiply: impl Fn(&Matrix, &Matrix) -> Matrix,
) -> Option<Pose> {
    if r.at(node, 0xfc)? != 0 || r.at(node, 0x2c)? & 0x80000 != 0 {
        return None;
    }
    let slot = if r.at(renderer, 0x2334)? == 4 {
        0x218
    } else {
        0x214
    };
    let cb = r.pointer(node, slot)?;
    let relative = r.at(node, 0x34)? & (1 << 27) != 0;
    if !relative && r.byte(cb, 0x6a4)? == 0 {
        let data = r.pointer(cb, 0x6a8)?;
        let packed = r.at(cb, 0x24c)?;
        let matrix_offset = packed >> 16;
        let scale_offset = packed & 0xffff;
        // These are byte offsets supplied by the current native CB layout.
        // Avoid the 0xffff absent-constant sentinel and overlapping reads.
        if matrix_offset == 0xffff
            || scale_offset == 0xffff
            || matrix_offset % 4 != 0
            || scale_offset % 4 != 0
            || (matrix_offset < scale_offset + 16 && scale_offset < matrix_offset + 48)
        {
            return None;
        }
        return Some(Pose {
            matrix: floats(r, data, matrix_offset)?,
            scale: floats(r, data, scale_offset)?,
        });
    }
    let mut matrix: Matrix = floats(r, node, 0x40)?;
    if relative {
        let settings = r.pointer(renderer, 0x20d0)?;
        matrix = multiply(&matrix, &floats(r, settings, 0x110)?);
        if !matrix.into_iter().all(f32::is_finite) {
            return None;
        }
    }
    let mut transposed = [0.; 12];
    for row in 0..3 {
        for col in 0..4 {
            transposed[row * 4 + col] = matrix[col * 4 + row];
        }
    }
    Some(Pose {
        matrix: transposed,
        scale: floats(r, node, 0xc0)?,
    })
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub(crate) struct Camera {
    pub view: Matrix,
    pub primary: Matrix,
    pub secondary: Matrix,
    pub eye: [f32; 3],
}
#[derive(Clone, Copy, Debug, PartialEq)]
pub(crate) struct Correction {
    /// Present only when the paired aim-motion protocol is active. Retail saves
    /// P/VP but NOT this secondary projection in its 0x344-byte camera record.
    pub secondary: Option<Matrix>,
    pub primary: Matrix,
    pub primary_vp: Matrix,
    pub reticle_ndc: [f32; 2],
    pub original_aim_ndc: [f32; 2],
}

fn transform(v: [f32; 4], m: &Matrix) -> [f32; 4] {
    let mut out = [0.; 4];
    for k in 0..4 {
        out[k] = ((v[0] * m[k] + v[1] * m[4 + k]) + v[2] * m[8 + k]) + v[3] * m[12 + k];
    }
    out
}
// Native1626E30 computes eye*VP in f64 then rounds each component to f32.
// Shader multiplication is f32. Combining those two precision domains is
// essential at real map coordinates; an all-f32 CPU dot is not equivalent.
fn eye_clip(eye: [f32; 3], m: &Matrix) -> [f32; 4] {
    let mut out = [0.; 4];
    for k in 0..4 {
        out[k] = (((eye[0] as f64 * m[k] as f64 + eye[1] as f64 * m[4 + k] as f64)
            + eye[2] as f64 * m[8 + k] as f64)
            + m[12 + k] as f64) as f32;
    }
    out
}
fn point_clip(point: [f32; 3], eye: [f32; 3], m: &Matrix) -> [f32; 4] {
    let rel = [point[0] - eye[0], point[1] - eye[1], point[2] - eye[2], 0.];
    let mut out = transform(rel, m);
    let skin = eye_clip(eye, m);
    for k in 0..4 {
        out[k] += skin[k];
    }
    out
}
fn ndc(c: [f32; 4]) -> Option<[f32; 2]> {
    if !c.into_iter().all(f32::is_finite) || c[3] <= 0.00001 {
        return None;
    }
    let result = [c[0] / c[3], c[1] / c[3]];
    result.into_iter().all(f32::is_finite).then_some(result)
}
fn perspective(p: &Matrix) -> bool {
    p.iter().all(|v| v.is_finite())
        && p[3] == 0.
        && p[7] == 0.
        && p[11] == 1.
        && p[15] == 0.
        && p[0] > 0.
        && p[5] > 0.
}

/// The anchor is the selected mesh's immutable texture-centre, not actor origin
/// or a fixed screen offset. Reproduce the shader's eye-relative calculation to
/// retain precision at map-sized coordinates. The target is copied from the
/// same scene's verified native shot query; this never changes the bullet ray.
pub(crate) fn correct(
    camera: Camera,
    pose: Pose,
    local: [f32; 3],
    target: [f32; 3],
    multiply: impl Fn(&Matrix, &Matrix) -> Matrix,
) -> Option<Correction> {
    if !perspective(&camera.primary)
        || !perspective(&camera.secondary)
        || !camera
            .view
            .into_iter()
            .chain(camera.eye)
            .chain(pose.matrix)
            .chain(pose.scale)
            .chain(local)
            .chain(target)
            .all(f32::is_finite)
    {
        return None;
    }
    let secondary_vp = multiply(&camera.view, &camera.secondary);
    let primary_vp = multiply(&camera.view, &camera.primary);
    let skin = eye_clip(camera.eye, &secondary_vp);
    let mut relative = [0.; 4];
    for j in 0..3 {
        let row = &pose.matrix[j * 4..j * 4 + 4];
        relative[j] = ((local[0] * pose.scale[0] * row[0] + local[1] * pose.scale[1] * row[1])
            + local[2] * pose.scale[2] * row[2])
            + (row[3] - camera.eye[j]);
    }
    let mut clip = transform(relative, &secondary_vp);
    for j in 0..4 {
        clip[j] += skin[j];
    }
    let reticle_ndc = ndc(clip)?;
    let original_aim_ndc = ndc(point_clip(target, camera.eye, &primary_vp))?;
    // Offscreen/near-plane predictions are not grounds for shifting a camera.
    if reticle_ndc.into_iter().any(|v| v.abs() > 1.) {
        return None;
    }
    let mut primary = camera.primary;
    primary[8] += reticle_ndc[0] - original_aim_ndc[0];
    primary[9] += reticle_ndc[1] - original_aim_ndc[1];
    // Native f32 VP construction followed by its f64 eye compensation can
    // leave subpixel cancellation residue at map-sized positions. Refine the
    // same two projection offsets, never relax the acceptance threshold or
    // edit a shot/secondary matrix. Work is bounded; unknown cases fail closed.
    for _ in 0..8 {
        let primary_vp = multiply(&camera.view, &primary);
        let target_clip = point_clip(target, camera.eye, &primary_vp);
        let after = ndc(target_clip)?;
        if !primary
            .iter()
            .chain(primary_vp.iter())
            .all(|v| v.is_finite())
        {
            return None;
        }
        let delta = [reticle_ndc[0] - after[0], reticle_ndc[1] - after[1]];
        if delta.into_iter().all(|v| v.abs() <= 0.00002) {
            return Some(Correction {
                secondary: None,
                primary,
                primary_vp,
                reticle_ndc,
                original_aim_ndc,
            });
        }
        // Within a quantized VP-translation interval, offset changes act on
        // the untranslated world-depth, not just view-depth. A full NDC-sized
        // step jumps to another interval and can oscillate at large origins.
        let depth = ((target[0] as f64 * camera.view[2] as f64
            + target[1] as f64 * camera.view[6] as f64)
            + target[2] as f64 * camera.view[10] as f64)
            / target_clip[3] as f64;
        let gain = if depth.is_finite() && depth.abs() >= 0.25 {
            depth as f32
        } else {
            1.
        };
        primary[8] += delta[0] / gain;
        primary[9] += delta[1] / gain;
    }
    None
}

/// Align the separate optic camera to the same-frame shot. Never move the world
/// to follow a native weapon animation. The world P, view and shot stay intact;
/// only secondary off-axis translation changes, so the rim's scale stays fixed.
pub(crate) fn correct_optic(camera:Camera,pose:Pose,local:[f32;3],target:[f32;3],multiply:impl Fn(&Matrix,&Matrix)->Matrix)->Option<Correction>{
 let primary_vp=multiply(&camera.view,&camera.primary);
 correct_optic_saved(camera,primary_vp,pose,local,target,multiply)
}

/// The selected native camera's saved VP is authoritative. Recomputing V*P
/// can differ by rounding and is neither the consumed world transform nor a
/// valid before-image for commit. Keep the exact saved VP; never rewrite it.
pub(crate) fn correct_optic_saved(camera:Camera,primary_vp:Matrix,pose:Pose,local:[f32;3],target:[f32;3],multiply:impl Fn(&Matrix,&Matrix)->Matrix)->Option<Correction>{
 correct_optic_saved_inner(camera,primary_vp,pose,local,target,None,multiply)
}

/// Blend from the authored raise/lower pose using the fraction captured with
/// this exact scene. A partial ADS reticle is not yet a settled firing sight.
/// Offscreen positive-depth reticles remain continuous through the screen edge;
/// only the optic projection moves, never the primary camera or the bullet ray.
pub(crate) fn correct_optic_saved_phase(camera:Camera,primary_vp:Matrix,pose:Pose,local:[f32;3],target:[f32;3],fraction:f32,multiply:impl Fn(&Matrix,&Matrix)->Matrix)->Option<Correction>{
 if !fraction.is_finite() || !(0. ..=1.).contains(&fraction){return None;}
 correct_optic_saved_inner(camera,primary_vp,pose,local,target,Some(fraction),multiply)
}

fn correct_optic_saved_inner(camera:Camera,primary_vp:Matrix,pose:Pose,local:[f32;3],target:[f32;3],fraction:Option<f32>,multiply:impl Fn(&Matrix,&Matrix)->Matrix)->Option<Correction>{
 if !perspective(&camera.primary)||!perspective(&camera.secondary)
  ||!camera.view.into_iter().chain(primary_vp).chain(camera.eye).chain(pose.matrix).chain(pose.scale).chain(local).chain(target).all(f32::is_finite){return None;}
 let aim=ndc(point_clip(target,camera.eye,&primary_vp))?;
 if aim.into_iter().any(|v|v.abs()>1.){return None;}
 let mut rel=[0.;4];
 for j in 0..3{let row=&pose.matrix[j*4..j*4+4];
  rel[j]=((local[0]*pose.scale[0]*row[0]+local[1]*pose.scale[1]*row[1])+local[2]*pose.scale[2]*row[2])+(row[3]-camera.eye[j]);}
 let mut secondary=camera.secondary;
 let mut best=secondary;let mut errors=[f32::INFINITY;2];
 let mut desired=aim;
 for iteration in 0..16 {
  let vp=multiply(&camera.view,&secondary);
  let mut clip=transform(rel,&vp);let skin=eye_clip(camera.eye,&vp);
  for j in 0..4{clip[j]+=skin[j];}
  let reticle=ndc(clip)?;
  if iteration==0 {
   if let Some(a)=fraction {
    // Smoothstep has zero endpoint derivative; alpha zero is exactly native,
    // alpha one is the existing full-ADS solve, not a late branch handoff.
    let weight=a*a*(3.-2.*a);
    desired=if a==1.{aim}else{std::array::from_fn(|j|reticle[j]+(aim[j]-reticle[j])*weight)};
    if !desired.into_iter().all(f32::is_finite){return None;}
   }else if reticle.into_iter().any(|v|v.abs()>1.){return None;}
  }
  let delta=[desired[0]-reticle[0],desired[1]-reticle[1]];
  for j in 0..2{if delta[j].abs()<errors[j]{errors[j]=delta[j].abs();best[8+j]=secondary[8+j];}}
  if delta.into_iter().all(|v|v.abs()<=0.00002){return Some(Correction{secondary:Some(secondary),primary:camera.primary,primary_vp,reticle_ndc:reticle,original_aim_ndc:aim});}
  // First take the full projective correction. Subsequent small steps account
  // for native f32 VP translation plus f64 eye compensation at map coordinates.
  let depth=((rel[0]as f64+camera.eye[0]as f64)*camera.view[2]as f64
    +(rel[1]as f64+camera.eye[1]as f64)*camera.view[6]as f64
    +(rel[2]as f64+camera.eye[2]as f64)*camera.view[10]as f64)/clip[3]as f64;
  let gain=if iteration!=0&&depth.is_finite()&&depth.abs()>=0.25{depth as f32}else{1.};
  secondary[8]+=delta[0]/gain;secondary[9]+=delta[1]/gain;
  if !secondary.into_iter().all(f32::is_finite){return None;}
 }
 // Float VP translations form quantized intervals at map-sized coordinates.
 // The derivative iteration can cycle across those intervals (DIAG17: 763
 // rejects / 1007 attempts), alternating corrected/native optic positions.
 // Probe a bounded neighbourhood of the best per-axis result. The step is
 // one twentieth of the existing NDC tolerance, not a temporal filter or a
 // relaxed accuracy limit. Columns 0/1 are independent; verify their combined
 // result before returning. Never reuse another frame or change world/shot.
 let centre=best;
 const SEARCH_STEP:f32=0.00002/20.;
 for distance in 0..=256u32 {
  for sign in [-1i32,1] {
   let mut candidate=best;
   for j in 0..2 {
    if errors[j]>0.00002 {
     candidate[8+j]=centre[8+j]+sign as f32*distance as f32*SEARCH_STEP;
    }
   }
   if !candidate.into_iter().all(f32::is_finite){continue;}
   let vp=multiply(&camera.view,&candidate);let mut clip=transform(rel,&vp);let skin=eye_clip(camera.eye,&vp);
   for j in 0..4{clip[j]+=skin[j];}
   let reticle=ndc(clip)?;
   for j in 0..2{let error=(desired[j]-reticle[j]).abs();if error<errors[j]{errors[j]=error;best[8+j]=candidate[8+j];}}
   if errors.into_iter().all(|v|v<=0.00002){
    let vp=multiply(&camera.view,&best);let mut clip=transform(rel,&vp);let skin=eye_clip(camera.eye,&vp);
    for j in 0..4{clip[j]+=skin[j];}let reticle=ndc(clip)?;
    if (0..2).all(|j|(desired[j]-reticle[j]).abs()<=0.00002){return Some(Correction{secondary:Some(best),primary:camera.primary,primary_vp,reticle_ndc:reticle,original_aim_ndc:aim});}
   }
  }
 }
 None
}

#[cfg(test)]
mod optic_continuity_tests {
 use super::*;
 fn mul(a:&Matrix,b:&Matrix)->Matrix {
  let mut out=[0.;16];
  for row in 0..4 {for col in 0..4 {
   out[row*4+col]=((a[row*4]*b[col]+a[row*4+1]*b[4+col])+a[row*4+2]*b[8+col])+a[row*4+3]*b[12+col];
  }} out
 }
 fn input()->(Camera,Pose,[f32;3]) {
  let view=[3210347898u32,1040466623u32,1057098041u32,0u32,1057369230u32,1045883331u32,1062423588u32,0u32,3033326627u32,1064835793u32,3195820446u32,0u32,3323028529u32,1110548790u32,1160883622u32,1065353216u32].map(f32::from_bits);
  let primary=[1077557922u32,0u32,0u32,0u32,0u32,1084590452u32,0u32,0u32,2147483648u32,2147483648u32,1065354055u32,1065353216u32,0u32,0u32,3212837703u32,0u32].map(f32::from_bits);
  let secondary=[1085946530u32,0u32,0u32,0u32,0u32,1092979060u32,0u32,0u32,2147483648u32,2147483648u32,1065354055u32,1065353216u32,0u32,0u32,3212837703u32,0u32].map(f32::from_bits);
  let eye=[3323099524u32,1159566054u32,1143235196u32].map(f32::from_bits);
  let packed=[1065353216u32,0u32,0u32,3323095376u32,0u32,1065353216u32,0u32,1159593169u32,0u32,0u32,1065353216u32,1143203371u32,1065353216u32,1065353216u32,1065353216u32,1065353216u32].map(f32::from_bits);
  let target=[3322578891u32,1162948792u32,1137630278u32].map(f32::from_bits);
  (Camera{view,primary,secondary,eye},Pose{matrix:packed[..12].try_into().unwrap(),scale:packed[12..].try_into().unwrap()},target)
 }
 #[test] fn optic_quantized_map_camera_never_alternates_with_native_fallback() {
  // Retained DIAG17 eye/view; controlled optic point eight units ahead.
  // Old Newton-style correction cycles at f32 boundaries despite a valid solve.
  let(c,p,t)=input();
  for i in 0..64 {
   let mut p=p;p.matrix[3]+=i as f32*0.0001;
   let out=correct_optic(c,p,[0.;3],t,mul).expect("valid close optic must not flicker to native projection");
   assert_eq!(out.primary,c.primary);assert_eq!(out.primary_vp,mul(&c.view,&c.primary));
   for k in 0..16 {if k!=8&&k!=9 {assert_eq!(out.secondary.unwrap()[k],c.secondary[k]);}}
   for j in 0..2 {assert!((out.reticle_ndc[j]-out.original_aim_ndc[j]).abs()<=0.00002);}
  }
 }
 #[test] fn optic_quantized_solver_retains_invalid_input_guards() {
  let(c,p,t)=input();
  for v in [f32::NAN,f32::INFINITY] {let mut bad=p;bad.matrix[3]=v;assert!(correct_optic(c,bad,[0.;3],t,mul).is_none());}
  let mut bad=c;bad.secondary[11]=0.;assert!(correct_optic(bad,p,[0.;3],t,mul).is_none());
  let behind=std::array::from_fn(|i|c.eye[i]-c.view[i*4+2]*100.);
  assert!(correct_optic(c,p,[0.;3],behind,mul).is_none());
 }
 #[test] fn optic_saved_world_projection_is_not_recomputed_or_replaced() {
  let(c,p,t)=input();let mut saved=mul(&c.view,&c.primary);
  // A separately rounded but finite producer result remains the exact shader
  // input. This is a controlled boundary, not a captured UNIFIED18 VP.
  saved[0]=f32::from_bits(saved[0].to_bits()+1);
  let out=correct_optic_saved(c,saved,p,[0.;3],t,mul).unwrap();
  assert_eq!(out.primary,c.primary);assert_eq!(out.primary_vp,saved);
  assert_eq!(out.original_aim_ndc,ndc(point_clip(t,c.eye,&saved)).unwrap());
  for j in 0..2{assert!((out.reticle_ndc[j]-out.original_aim_ndc[j]).abs()<=0.00002);}
  for invalid in [f32::NAN,f32::INFINITY] {
   let mut bad=saved;bad[0]=invalid;
   assert!(correct_optic_saved(c,bad,p,[0.;3],t,mul).is_none());
  }
 }
}
