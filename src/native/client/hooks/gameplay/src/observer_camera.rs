//! Camera motion core, isolated from native pointers and multiplayer authority.
//! Not activated until the native adapter proves local view ownership and isolation.

#[derive(Clone, Copy, Debug, PartialEq)]
pub(crate) struct Pose {
    pub position_cm: [f64; 3],
    pub yaw_degrees: f64,
    pub pitch_degrees: f64,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) struct Binding {
    pub world: usize,
    pub controller: usize,
    pub generation: u32,
}

#[derive(Clone, Copy, Default)]
pub(crate) struct Input {
    // Local camera forward/right and world up, each in [-1, 1].
    pub movement: [f64; 3],
    // Aggregated relative mouse counts since the previous frame, not a velocity.
    pub mouse: [f64; 2],
    pub fast: bool,
    pub slow: bool,
}

pub(crate) struct Camera {
    binding: Binding,
    pose: Pose,
    last_ms: Option<u64>,
    retired: bool,
}

fn valid_pose(pose: Pose) -> bool {
    pose.position_cm.iter().all(|v| v.is_finite() && v.abs() < 1.0e8)
        && pose.yaw_degrees.is_finite() && pose.pitch_degrees.is_finite()
}

impl Camera {
    pub(crate) fn new(binding: Binding, mut pose: Pose) -> Option<Self> {
        if binding.world == 0 || binding.controller == 0 || !valid_pose(pose) { return None; }
        pose.yaw_degrees = pose.yaw_degrees.rem_euclid(360.0);
        pose.pitch_degrees = pose.pitch_degrees.clamp(-89.0, 89.0);
        Some(Self { binding, pose, last_ms: None, retired: false })
    }

    /// `ready` means the native adapter has verified admission, pawn detachment,
    /// HUD isolation and foreground gameplay. False never produces a pose write.
    /// A different world/controller generation permanently retires this instance.
    pub(crate) fn step(&mut self, binding: Binding, ready: bool, now_ms: u64, input: Input) -> Option<Pose> {
        if self.retired || binding != self.binding {
            self.retired = true;
            return None;
        }
        if !ready { self.last_ms = None; return None; }
        let Some(last) = self.last_ms.replace(now_ms) else { return Some(self.pose); };
        // Duplicate/out-of-order callbacks must not consume the same mouse input.
        if now_ms <= last { self.last_ms = Some(last); return None; }
        if input.movement.iter().any(|v| !v.is_finite() || v.abs() > 1.0)
            || input.mouse.iter().any(|v| !v.is_finite() || v.abs() > 1.0e6) {
            return None;
        }
        // A stalled frame cannot teleport the camera. Mouse counts are NOT dt-scaled.
        let dt = (now_ms - last).min(50) as f64 / 1000.0;
        let mut next = self.pose;
        next.yaw_degrees = (next.yaw_degrees + input.mouse[0] * 0.07).rem_euclid(360.0);
        next.pitch_degrees = (next.pitch_degrees - input.mouse[1] * 0.07).clamp(-89.0, 89.0);
        let (sy, cy) = next.yaw_degrees.to_radians().sin_cos();
        let (sp, cp) = next.pitch_degrees.to_radians().sin_cos();
        let [forward, right, up] = input.movement;
        let mut v = [forward * cp * cy - right * sy,
                     forward * cp * sy + right * cy,
                     forward * sp + up];
        let length = v.iter().map(|n| n * n).sum::<f64>().sqrt().max(1.0);
        // Start at 5 m/s, Shift 15 m/s, slow modifier 1 m/s; slow wins together.
        let speed = if input.slow { 100.0 } else if input.fast { 1500.0 } else { 500.0 };
        for i in 0..3 { v[i] /= length; next.position_cm[i] += v[i] * speed * dt; }
        if !valid_pose(next) { return None; }
        self.pose = next;
        Some(next)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn binding() -> Binding { Binding {world: 1, controller: 2, generation: 3} }
    fn camera() -> Camera {
        Camera::new(binding(), Pose {position_cm: [0.0; 3], yaw_degrees: 0.0, pitch_degrees: 0.0}).unwrap()
    }
    fn close(a: f64, b: f64) { assert!((a - b).abs() < 1.0e-8, "{a} != {b}"); }
    #[test] fn movement_and_mouse_are_frame_rate_independent() {
        let mut poses = vec![];
        for frames in [30, 60, 144] {
            let mut c = camera(); c.step(binding(), true, 0, Input::default());
            for frame in 1..=frames {
                c.step(binding(), true, frame * 1000 / frames, Input {
                    movement: [0., 0., 1.], mouse: [1000. / frames as f64, 0.], ..Input::default()
                });
            }
            poses.push(c.pose);
        }
        for p in poses { close(p.position_cm[2], 500.); close(p.yaw_degrees, 70.); }
    }
    #[test] fn diagonal_does_not_gain_speed() {
        let mut c = camera(); c.step(binding(), true, 0, Input::default());
        let p = c.step(binding(), true, 50, Input {movement: [1., 1., 1.], ..Input::default()}).unwrap();
        close(p.position_cm.iter().map(|n| n*n).sum::<f64>().sqrt(), 25.);
    }
    #[test] fn pause_and_resume_drop_input_and_elapsed_time() {
        let mut c = camera(); c.step(binding(), true, 0, Input::default());
        let input = Input {movement: [1., 0., 0.], mouse: [100., 100.], ..Input::default()};
        assert!(c.step(binding(), false, 100, input).is_none());
        let p = c.step(binding(), true, 10000, input).unwrap();
        assert_eq!(p.position_cm, [0.; 3]); close(p.yaw_degrees, 0.);
    }
    #[test] fn map_or_controller_change_retires_old_camera_even_if_address_returns() {
        let mut c = camera(); let mut other = binding(); other.generation += 1;
        assert!(c.step(other, true, 0, Input::default()).is_none());
        assert!(c.step(binding(), true, 1, Input::default()).is_none());
    }
    #[test] fn long_frame_is_bounded_and_duplicate_frame_is_ignored() {
        let mut c = camera(); c.step(binding(), true, 0, Input::default());
        let input = Input {movement: [1., 0., 0.], ..Input::default()};
        close(c.step(binding(), true, 10000, input).unwrap().position_cm[0], 25.);
        assert!(c.step(binding(), true, 10000, input).is_none());
        assert!(c.step(binding(), true, 9999, input).is_none());
    }
    #[test] fn bad_input_never_corrupts_pose() {
        let mut c = camera(); c.step(binding(), true, 0, Input::default());
        let original = c.pose;
        assert!(c.step(binding(), true, 1, Input {mouse: [f64::NAN, 0.], ..Input::default()}).is_none());
        assert_eq!(c.pose, original);
        assert!(Camera::new(binding(), Pose {position_cm: [f64::INFINITY;3], ..original}).is_none());
    }
    #[test] fn pitch_is_clamped_yaw_wraps_and_slow_overrides_fast() {
        let mut c = camera(); c.step(binding(), true, 0, Input::default());
        let p = c.step(binding(), true, 50, Input {movement: [0., 0., 1.], mouse: [-100., -10000.], fast:true, slow:true}).unwrap();
        close(p.yaw_degrees, 353.); close(p.pitch_degrees, 89.); close(p.position_cm[2], 5.);
    }
}
