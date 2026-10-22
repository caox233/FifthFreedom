//! SC-IS relative-magnification policy. Native input/render adapter is separate.
//! The native scoped FOV is the reference on each call, never our last output.

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) struct Session {
    pub world: usize,
    pub controller: usize,
    pub generation: u32,
}

pub(crate) struct Zoom {
    owner: Option<Session>,
    scoped: bool,
    eighths: i32,
    remainder: i32,
}
impl Zoom {
    pub(crate) const fn new() -> Self {
        Self { owner: None, scoped: false, eighths: 8, remainder: 0 }
    }
    /// Caller proves local Merc, exact SC-IS and native scoped state first.
    /// None suspends input without forgetting the current session's setting.
    pub(crate) fn bind(&mut self, owner: Option<Session>) {
        if let Some(owner) = owner {
            if self.owner != Some(owner) {
                *self = Self { owner: Some(owner), scoped: true, ..Self::new() };
            }
            self.scoped = true;
        } else {
            self.scoped = false;
            self.remainder = 0;
        }
    }
    pub(crate) fn factor(&self) -> f32 { self.eighths as f32 / 8.0 }
    pub(crate) fn wheel(&mut self, delta: i32) -> bool {
        if !self.scoped || self.owner.is_none() || delta == 0 || !(-32768..=32767).contains(&delta) {
            return false;
        }
        let total = self.remainder + delta;
        let steps = total / 120; // preserve high-resolution partial notches
        self.remainder = total % 120;
        self.eighths = (self.eighths + steps).clamp(4, 12);
        if (self.eighths == 4 && self.remainder < 0) || (self.eighths == 12 && self.remainder > 0) {
            self.remainder = 0; // no accumulated overscroll when reversing
        }
        true
    }
}

pub(crate) fn project_fov(native_degrees: f32, factor: f32) -> Option<f32> {
    if !native_degrees.is_finite() || !(0.0..180.0).contains(&native_degrees)
        || native_degrees == 0.0 || !factor.is_finite() || !(0.5..=1.5).contains(&factor) {
        return None;
    }
    if factor == 1.0 { return Some(native_degrees); }
    let half = (native_degrees as f64).to_radians() * 0.5;
    let degrees = (2.0 * (half.tan() / factor as f64).atan()).to_degrees() as f32;
    (degrees.is_finite() && degrees > 0.0 && degrees < 180.0).then_some(degrees)
}

#[cfg(test)]
mod tests {
    use super::*;
    const OWNER: Session = Session {world: 10, controller: 20, generation: 3};
    #[test]
    fn scis_zoom_native_first_aim_maximum_one_and_half_and_remembered() {
        let mut z=Zoom::new();assert_eq!(z.factor(),1.0);assert!(!z.wheel(120));
        z.bind(Some(OWNER));assert!(z.wheel(480));assert_eq!(z.factor(),1.5);
        assert!(z.wheel(120));assert_eq!(z.factor(),1.5);
        z.bind(None);assert!(!z.wheel(-480));z.bind(Some(OWNER));assert_eq!(z.factor(),1.5);
        z.wheel(-120);assert_eq!(z.factor(),1.375);
        z.bind(Some(Session{generation:4,..OWNER}));assert_eq!(z.factor(),1.0);
        println!("SCIS_ZOOM first=native max=1.5x reaim=remembered new_session=native");
    }
    #[test]
    fn scis_zoom_high_resolution_wheel_reversal_clamp_and_suspend() {
        let mut z=Zoom::new();z.bind(Some(OWNER));
        for _ in 0..3 {assert!(z.wheel(30));assert_eq!(z.factor(),1.0);}
        assert!(z.wheel(30));assert_eq!(z.factor(),1.125);
        z.wheel(-120);assert_eq!(z.factor(),1.0);
        z.wheel(-12000);assert_eq!(z.factor(),0.5);
        z.wheel(120);assert_eq!(z.factor(),0.625);
        z.wheel(90);z.bind(None);z.bind(Some(OWNER));z.wheel(30);assert_eq!(z.factor(),0.625);
        assert!(!z.wheel(i32::MIN));assert!(!z.wheel(i32::MAX));
    }
    #[test]
    fn scis_zoom_projection_is_magnification_not_angle_division_or_feedback() {
        for native in [12.0f32,24.0,40.0] {
            let out=project_fov(native,1.5).unwrap();
            let ratio=(native.to_radians()/2.0).tan()/(out.to_radians()/2.0).tan();
            assert!((ratio-1.5).abs()<0.00001);
            assert_eq!(project_fov(native,1.0),Some(native));
            for _ in 0..100 {assert_eq!(project_fov(native,1.5),Some(out));}
            assert!(project_fov(native,0.5).unwrap()>native);
            println!("SCIS_PROJECTION native_fov={native} max_fov={out} magnification_ratio={ratio}");
        }
        for fov in [f32::NAN,f32::INFINITY,0.0,-1.0,180.0] {assert!(project_fov(fov,1.0).is_none());}
        for factor in [0.,0.49,1.51,f32::NAN] {assert!(project_fov(24.,factor).is_none());}
    }
}
