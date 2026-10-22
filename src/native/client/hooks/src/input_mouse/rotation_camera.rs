//! Preserve the fractional view through the native first-person head transform.
//! The native animation transform still composes the camera; this is not input
//! smoothing, an animation replacement, or a camera reconstructed from the mouse.
use super::rotation_precision::Snapshot;

pub(super) fn owned_input(token: Snapshot, hands: usize, rotation: usize,
    read: impl Fn(usize,usize)->Option<u32>) -> bool {
    let o=token.owner;
    hands>=0x10000 && rotation>=0x10000
        && read(o.pawn,0x12ac)==Some(hands as u32) && read(hands,0)==Some(0x02a23634)
        && read(hands,0x310)==Some(o.pawn as u32) && read(hands,0x144)==Some(o.world as u32)
        && (0..3).all(|i|read(rotation,i*4).is_some_and(|r|read(o.core,0x6c+i*4)==Some(r)))
}

pub(super) fn input_matrix(rotation: [i32; 3], fraction: [f64; 2]) -> Option<[f32; 16]> {
    if fraction.iter().any(|v| !v.is_finite() || v.abs() >= 1.) { return None; }
    let k = std::f64::consts::TAU / 65536.;
    let (sp, cp) = ((rotation[0] as f64 + fraction[0]).rem_euclid(65536.) * k).sin_cos();
    let (sy, cy) = ((rotation[1] as f64 + fraction[1]).rem_euclid(65536.) * k).sin_cos();
    let (sr, cr) = ((rotation[2] as f64).rem_euclid(65536.) * k).sin_cos();
    Some([cp*cy, cp*sy, sp, 0.,
        sr*sp*cy-cr*sy, sr*sp*sy+cr*cy, -sr*cp, 0.,
        -cr*sp*cy-sr*sy, cy*sr-cr*sp*sy, cr*cp, 0.,
        0., 0., 0., 1.].map(|v| v as f32))
}

/// Native frame layout: translation, forward, right, up. Reject malformed,
/// reflected or degenerate frames rather than suppressing native camera effects.
pub(super) fn frame_angles(frame: [f32; 12]) -> Option<[f64; 3]> {
    if frame.iter().any(|v| !v.is_finite()) { return None; }
    let f = frame.map(f64::from);
    let dot = |a: usize, b: usize| (0..3).map(|i| f[a+i]*f[b+i]).sum::<f64>();
    for a in [3, 6, 9] { if (dot(a,a)-1.).abs() > 0.002 { return None; } }
    for (a,b) in [(3,6),(3,9),(6,9)] { if dot(a,b).abs() > 0.002 { return None; } }
    let det = f[3]*(f[7]*f[11]-f[8]*f[10]) - f[4]*(f[6]*f[11]-f[8]*f[9])
        + f[5]*(f[6]*f[10]-f[7]*f[9]);
    let horizontal = f[3].hypot(f[4]);
    if det < 0.998 || horizontal < 0.0001 { return None; }
    let k = 65536. / std::f64::consts::TAU;
    Some([f[5].atan2(horizontal)*k, f[4].atan2(f[3])*k, (-f[8]).atan2(f[11])*k])
}

#[derive(Clone, Copy, Default)]
pub(super) enum Composition {
    #[default] Empty,
    Bound(Snapshot),
    Input(Snapshot),
    Complete { token: Snapshot, native: [i32;3], angles: [f64;3] },
    Invalid,
}
impl Composition {
    pub fn bind(&mut self, token: Snapshot) { *self = Self::Bound(token); }
    pub fn input(&mut self, token: Snapshot, rotation: [i32;3]) -> Option<[f32;16]> {
        let valid = matches!(*self, Self::Bound(t) if t==token);
        *self = Self::Invalid;
        if !valid { return None; }
        let m = input_matrix(rotation, token.fraction)?;
        *self = Self::Input(token);
        Some(m)
    }
    pub fn complete(&mut self, token: Snapshot, frame: [f32;12], native: [i32;3]) {
        let valid = matches!(*self, Self::Input(t) if t==token);
        *self = Self::Invalid;
        if !valid { return; }
        let Some(angles) = frame_angles(frame) else { return; };
        // The precise frame and native integer publication must describe the
        // same orientation. The native lookup has four-rotator-unit bins.
        if (0..3).any(|i| ((angles[i]-native[i] as f64+32768.).rem_euclid(65536.)-32768.).abs()>16.) { return; }
        *self = Self::Complete { token, native, angles };
    }
    pub fn angles(self, token: Snapshot, native: [i32;3]) -> Option<[f64;3]> {
        match self { Self::Complete{token:t,native:n,angles} if t==token && n==native => Some(angles), _=>None }
    }
}

#[cfg(test)] mod tests {
    use super::*;
    use super::super::rotation_precision::Owner;
    fn token() -> Snapshot { Snapshot { owner: Owner {viewport:1,window:2,pawn:3,mode:4,core:5,world:6,generation:7}, frame:8, targets:[100,200], fraction:[0.25,-0.25] } }
    fn frame(m: [f32;16]) -> [f32;12] { [0.,0.,0.,m[0],m[1],m[2],m[4],m[5],m[6],m[8],m[9],m[10]] }
    #[test] fn only_current_local_first_person_hands_and_exact_native_input() {
        use std::collections::HashMap;
        let t=token();let h=0x10000;let r=0x20000;
        let m=HashMap::from([((t.owner.pawn,0x12ac),h as u32),((h,0),0x02a23634),
            ((h,0x310),t.owner.pawn as u32),((h,0x144),t.owner.world as u32),
            ((r,0),100),((r,4),200),((r,8),10),
            ((t.owner.core,0x6c),100),((t.owner.core,0x70),200),((t.owner.core,0x74),10)]);
        assert!(owned_input(t,h,r,|p,o|m.get(&(p,o)).copied()));
        for key in m.keys(){let mut bad=m.clone();bad.remove(key);assert!(!owned_input(t,h,r,|p,o|bad.get(&(p,o)).copied()));
            let mut bad=m.clone();*bad.get_mut(key).unwrap()+=1;assert!(!owned_input(t,h,r,|p,o|bad.get(&(p,o)).copied()));}
        assert!(!owned_input(t,h+4,r,|p,o|m.get(&(p,o)).copied()));
        assert!(!owned_input(t,h,r+4,|p,o|m.get(&(p,o)).copied()));
    }
    #[test] fn continuous_pitch_yaw_roll_and_wrap_without_a_mouse_filter() {
        for p in [-15000, -1, 0, 1, 12000] { for y in [-65536,-32768,-1,0,1,32767,65536] { for r in [-20000,0,20000] {
            let a=frame_angles(frame(input_matrix([p,y,r],[0.25,-0.25]).unwrap())).unwrap();
            for (actual,want) in a.into_iter().zip([p as f64+0.25,y as f64-0.25,r as f64]) {
                assert!(((actual-want+32768.).rem_euclid(65536.)-32768.).abs()<0.003);
            }
        } } }
    }
    #[test] fn rejects_invalid_or_reflected_frames_and_fraction() {
        assert!(input_matrix([0;3],[1.,0.]).is_none());
        assert!(input_matrix([0;3],[f64::NAN,0.]).is_none());
        let f=frame(input_matrix([0;3],[0.;2]).unwrap());
        for i in 0..12 { let mut bad=f; bad[i]=f32::NAN; assert!(frame_angles(bad).is_none()); }
        for i in 3..12 { let mut bad=f; bad[i]+=0.1; assert!(frame_angles(bad).is_none()); }
        let mut bad=f; bad[11]=-1.;assert!(frame_angles(bad).is_none());
    }
    #[test] fn consumes_one_composition_only_for_the_same_frame_and_native_publication() {
        let t=token();let mut c=Composition::default();
        assert!(c.input(t,[100,200,0]).is_none());
        c.bind(t);let f=frame(c.input(t,[100,200,0]).unwrap());c.complete(t,f,[100,199,0]);
        let a=c.angles(t,[100,199,0]).unwrap();assert!((a[0]-100.25).abs()<0.003);
        assert!(c.angles(t,[101,199,0]).is_none());
        assert!(c.angles(Snapshot{frame:9,..t},[100,199,0]).is_none());
        c.complete(t,f,[100,199,0]);assert!(c.angles(t,[100,199,0]).is_none());
        c.bind(t);assert!(c.input(Snapshot{frame:9,..t},[100,200,0]).is_none());
        c.bind(t);c.input(t,[100,200,0]);assert!(c.input(t,[100,200,0]).is_none());
        c.bind(t);c.input(t,[100,200,0]);c.complete(t,f,[500,199,0]);assert!(c.angles(t,[500,199,0]).is_none());
    }
}
