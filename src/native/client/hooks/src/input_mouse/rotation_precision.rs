//! Keep fractional native rotator units, rather than dropping them each frame.
//! Counts, native gain, acceleration, time scaling and camera limits stay native.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) struct Owner {
    pub viewport: usize,
    pub window: usize,
    pub pawn: usize,
    pub mode: usize,
    pub core: usize,
    pub world: usize,
    pub generation: u32,
}
#[derive(Clone,Copy,Debug,PartialEq)]
pub(super) struct Snapshot { pub owner:Owner, pub frame:u64, pub targets:[i32;2], pub fraction:[f64;2] }
#[derive(Default)]
pub(super) struct Precision {
    owner: Option<Owner>,
    remainder: [f64; 2],
    mouse_active: bool,
    roundtrip_target: [Option<i32>; 2],
    producer_target: [Option<i32>; 2],
    fraction_ready: [Option<f64>; 2],
    final_stage: u8,
    frame: u64,
}
impl Precision {
    pub fn bind(&mut self, owner: Option<Owner>) {
        if self.owner != owner || owner.is_none() {
            self.remainder = [0.; 2];
            self.roundtrip_target = [None; 2];
            self.producer_target = [None; 2];
            self.fraction_ready = [None; 2];
            self.final_stage = 0;
        }
        self.owner = owner;
        self.mouse_active = owner.is_some();
    }
    pub fn bind_frame(&mut self, owner: Option<Owner>, mouse_active: bool) {
        self.bind(owner);
        self.frame = self.frame.wrapping_add(1);
        self.producer_target = [None; 2];
        self.fraction_ready = [None; 2];
        self.final_stage = 0;
        // A frame without mouse movement is not a new input owner. Keep the
        // fraction, but do not apply it to native non-mouse camera updates.
        self.mouse_active = mouse_active && owner.is_some();
    }
    pub fn owns_mode(&self, core: usize, mode: usize) -> bool {
        self.owner.is_some_and(|owner| owner.core == core && owner.mode == mode)
    }
    /// Repair only the loss introduced by native integer -> radians -> integer.
    /// The target has already passed native camera mode constraints. Never add
    /// the input fraction twice or override nonzero native interpolation.
    pub fn roundtrip(&mut self, core: usize, axis: usize, target: i32, duration: f32, value: f64) -> f64 {
        let Some(owner) = self.owner else { return value; };
        if core != owner.core || axis > 1 { return value; }
        // Native DA4030 always publishes pitch then yaw. Start a new pair at
        // pitch; do not combine a new axis with the previous pair's other axis.
        if axis==0 || self.final_stage!=1 { self.fraction_ready=[None;2]; }
        self.final_stage=if axis==0 {1} else {2};
        self.fraction_ready[axis] = None;
        // This consumer uses 65535 units per turn (29F7A20/29F7B38),
        // unlike the separate 65536-entry rotator lookup convention.
        let normalized = ((target as i64 + 32767).rem_euclid(65535) - 32767) as i32;
        let continuing = self.roundtrip_target[axis] == Some(normalized);
        // A validated stationary native target still needs a scene publication.
        // Mouse report gaps must not switch the renderer back to the four-unit
        // lookup table. No input carry is created here; changed non-mouse
        // targets clear it below, and native interpolation remains untouched.
        if duration != 0.0 || !value.is_finite() || value.abs() > 32768.02 {
            self.roundtrip_target[axis] = None;
            self.remainder[axis] = 0.;
            self.producer_target[axis] = None;
            return value;
        }
        // +/-pi has two equivalent representatives. Choose the one already
        // selected by the native wrap, not a new rotation direction.
        let n = normalized as f64;
        let exact = n + ((value - n) / 65535.0).round() * 65535.0;
        if (exact - value).abs() > 0.02 {
            self.roundtrip_target[axis] = None;
            self.remainder[axis] = 0.;
            self.producer_target[axis] = None;
            return value;
        }
        // Native limits/constraints take precedence over input carry. A changed
        // post-mode target must not retain an outward fraction at that limit.
        if let Some(produced) = self.producer_target[axis] {
            let produced = ((produced as i64 + 32767).rem_euclid(65535)-32767) as i32;
            if produced != normalized { self.remainder[axis] = 0.; }
        } else if !continuing { self.remainder[axis] = 0.; }
        self.fraction_ready[axis] = Some(self.remainder[axis]);
        self.roundtrip_target[axis] = Some(normalized);
        exact
    }
    pub fn snapshot(&self, core:usize, mode:usize, targets:[i32;2])->Option<Snapshot> {
        let owner = self.owner?;
        if core != owner.core || mode != owner.mode || self.final_stage!=2 { return None; }
        for axis in 0..2 {
            let n = ((targets[axis] as i64 + 32767).rem_euclid(65535)-32767) as i32;
            if self.roundtrip_target[axis] != Some(n) { return None; }
        }
        Some(Snapshot { owner, frame:self.frame, targets,
            fraction:[self.fraction_ready[0]?, self.fraction_ready[1]?] })
    }
    pub fn correct(&mut self, mode: usize, output: usize, axis: usize, value: f64) -> f64 {
        let Some(owner) = self.owner else { return value; };
        if !self.mouse_active || mode != owner.mode || output != owner.core + 0x6c || axis > 1 {
            return value;
        }
        self.fraction_ready = [None;2];
        self.final_stage = 0;
        // The native CVTTSD2SI is left in place. Preserve its invalid-value
        // behavior, but never retain NaN/overflow as future input.
        let total = value + self.remainder[axis];
        if !total.is_finite() || total <= i32::MIN as f64 || total >= i32::MAX as f64 {
            self.remainder[axis] = 0.;
            self.producer_target[axis] = None;
            return value;
        }
        self.remainder[axis] = total - total.trunc();
        self.producer_target[axis] = Some(total.trunc() as i32);
        total
    }
}

pub(super) const PITCH: [u8; 6] = [0xf2, 0x0f, 0x2c, 0xca, 0x89, 0x08];
pub(super) const YAW: [u8; 7] = [0xf2, 0x0f, 0x2c, 0xca, 0x89, 0x48, 0x04];
pub(super) fn bridge(base: u32, callback: u32, resume: u32, axis: u8) -> Vec<u8> {
    assert!(axis <= 1);
    // Save flags/GPRs and an aligned FXSAVE area. XMM2's low double is the
    // native pre-truncation angle; only this saved value may be corrected.
    let mut b = vec![0x9c,0x60,0x89,0xe0,0x81,0xec,0x20,0x02,0,0,0x83,0xe4,0xf0,
        0x89,0x84,0x24,0,0x02,0,0,0x0f,0xae,0x04,0x24,0xfc,
        0x8d,0x8c,0x24,0xc0,0,0,0, // lea ecx,[esp+c0] = saved XMM2
        0x51,0x6a,axis,             // sample pointer, axis
        0xff,0x70,0x1c,0x56,       // original EAX=rotator pointer, ESI=mode
        0xb8];
    b.extend(callback.to_le_bytes());
    b.extend([0xff,0xd0,0x0f,0xae,0x0c,0x24,0x8b,0xa4,0x24,0,0x02,0,0,0x61,0x9d]);
    b.extend(if axis == 0 { &PITCH[..] } else { &YAW[..] });
    b.push(0xe9);
    let end = base.wrapping_add(b.len() as u32 + 4);
    b.extend(resume.wrapping_sub(end).to_le_bytes());
    b
}

#[cfg(test)] mod tests {
    use super::*;
    fn owner()->Owner {Owner{viewport:1,window:2,pawn:3,mode:4,core:0x10000,world:6,generation:7}}
    #[test] fn fractional_turns_survive_both_directions_without_drift() {
        for start in [-10000.,0.,10000.] { for delta in [-0.1,0.1,-2.7,2.7] {
            let o=owner();let mut p=Precision::default();p.bind(Some(o));let mut angle=start;
            for _ in 0..1000 {p.bind(Some(o));angle=p.correct(o.mode,o.core+0x6c,0,angle+delta).trunc();}
            assert!((angle-(start+1000.*delta)).abs()<1.00001);
            let held=angle;
            for _ in 0..1000 {angle=p.correct(o.mode,o.core+0x6c,0,angle).trunc();}
            assert_eq!(angle,held,"zero input must not creep");
        }}
    }
    #[test] fn ownership_axes_and_invalid_samples_do_not_leak() {
        let o=owner();let mut p=Precision::default();p.bind(Some(o));
        assert_eq!(p.correct(o.mode,o.core+0x6c,0,0.6),0.6);
        assert_eq!(p.correct(o.mode,o.core+0x6c,1,0.2),0.2);
        assert_eq!(p.correct(99,o.core+0x6c,0,0.6),0.6);
        assert_eq!(p.correct(o.mode,99,0,0.6),0.6);
        assert_eq!(p.correct(o.mode,o.core+0x6c,0,0.6).trunc(),1.);
        p.bind(None);p.bind(Some(o));assert_eq!(p.correct(o.mode,o.core+0x6c,0,0.1),0.1);
        p.bind(Some(Owner{generation:8,..o}));assert_eq!(p.correct(o.mode,o.core+0x6c,0,0.1),0.1);
        assert!(p.correct(o.mode,o.core+0x6c,0,f64::NAN).is_nan());
        assert_eq!(p.correct(o.mode,o.core+0x6c,0,0.1),0.1);
    }
    #[test]
    fn no_report_frames_publish_validated_native_target_without_stale_fraction() {
        let o = owner();
        let mut p = Precision::default();
        // DIAG1 starts with valid ADS, before the first mouse report. Both
        // final native conversions must publish even without an input producer.
        for i in 0..1000 {
            p.bind_frame(Some(o), i % 2 != 0);
            let targets = [0, 13362 + i / 10];
            for (axis, target) in targets.into_iter().enumerate() {
                assert_eq!(p.roundtrip(o.core, axis, target, 0., target as f64 - 0.0001), target as f64);
            }
            let snapshot = p.snapshot(o.core, o.mode, targets).unwrap();
            assert_eq!(snapshot.fraction, [0., 0.]);
        }
        // Do not turn native interpolation or foreign ownership into a
        // precision publication just to keep the renderer on the same path.
        p.bind_frame(Some(o), false);
        p.roundtrip(o.core, 0, 0, 0.2, 0.);
        p.roundtrip(o.core, 1, 13362, 0.2, 13362.);
        assert!(p.snapshot(o.core, o.mode, [0, 13362]).is_none());
        p.bind_frame(None, false);
        p.roundtrip(o.core, 0, 0, 0., 0.);
        p.roundtrip(o.core, 1, 13362, 0., 13362.);
        assert!(p.snapshot(o.core, o.mode, [0, 13362]).is_none());
    }
    #[test] fn idle_frames_retain_fraction_without_affecting_non_mouse_rotation() {
        let o=owner();let mut p=Precision::default();let mut angle=0.;
        for _ in 0..1000 {
            p.bind_frame(Some(o),true);
            angle=p.correct(o.mode,o.core+0x6c,0,angle+0.1).trunc();
            p.bind_frame(Some(o),false);
            assert_eq!(p.correct(o.mode,o.core+0x6c,0,123.25),123.25);
        }
        assert!((angle-100.).abs()<1.00001);
        p.bind_frame(None,false);p.bind_frame(Some(o),true);
        assert_eq!(p.correct(o.mode,o.core+0x6c,0,0.1),0.1);
    }
}

// Native DA4030 final conversions: preserve the original CVTT and store.
pub(super) const FINAL_PITCH: [u8; 7] = [0xf2,0x0f,0x2c,0xc8,0x89,0x4e,0x6c];
pub(super) const FINAL_YAW: [u8; 7] = [0xf2,0x0f,0x2c,0xd0,0x89,0x56,0x70];
pub(super) fn final_bridge(base:u32, callback:u32, resume:u32, axis:u8)->Vec<u8> {
    assert!(axis <= 1);
    // Aligned FXSAVE, all GPRs/flags retained. XMM0 contains radians converted
    // back to native units; ESI is the camera core. Callback is stdcall.
    let mut b=vec![0x9c,0x60,0x89,0xe0,0x81,0xec,0x20,0x02,0,0,0x83,0xe4,0xf0,
        0x89,0x84,0x24,0,0x02,0,0,0x0f,0xae,0x04,0x24,0xfc,
        0x8d,0x8c,0x24,0xa0,0,0,0,0x51,0x6a,axis,0x56,0xb8];
    b.extend(callback.to_le_bytes());
    b.extend([0xff,0xd0,0x0f,0xae,0x0c,0x24,0x8b,0xa4,0x24,0,0x02,0,0,0x61,0x9d]);
    b.extend(if axis==0{FINAL_PITCH}else{FINAL_YAW});b.push(0xe9);
    let end=base.wrapping_add(b.len() as u32+4);
    b.extend(resume.wrapping_sub(end).to_le_bytes());b
}

/// Replace the single CALL at 58FEB5, not the shared matrix routine. The original
/// thiscall still consumes all three native angles and returns its output in EAX.
/// Callback receives only that output and the current builder's scene pointer.
pub(super) fn view_bridge(base:u32, original:u32, callback:u32, resume:u32)->Vec<u8> {
    let mut b=vec![0xe8];b.extend(original.wrapping_sub(base+5).to_le_bytes());
    b.extend([0x9c,0x60,0x89,0xe0,0x81,0xec,0x20,0x02,0,0,0x83,0xe4,0xf0,
        0x89,0x84,0x24,0,0x02,0,0,0x0f,0xae,0x04,0x24,0xfc,
        0xff,0x70,0x1c, // output = saved EAX
        0xff,0xb5,0x6c,0xff,0xff,0xff, // current scene [ebp-94]
        0xb8]);b.extend(callback.to_le_bytes());
    b.extend([0xff,0xd0,0x0f,0xae,0x0c,0x24,0x8b,0xa4,0x24,0,0x02,0,0,0x61,0x9d,0xe9]);
    let end=base.wrapping_add(b.len()as u32+4);b.extend(resume.wrapping_sub(end).to_le_bytes());b
}
