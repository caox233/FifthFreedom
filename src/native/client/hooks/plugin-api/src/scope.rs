//! Optional input export. Lifecycle ABI v1 is unchanged; absent means retail.
pub const WHEEL_EXPORT: &[u8] = b"scbl_plugin_scope_wheel\0";
pub type WheelFn = unsafe extern "system" fn(viewport: u32, delta: i32) -> i32;
pub fn valid(viewport: u32, delta: i32) -> bool {
    viewport >= 0x10000 && delta != 0 && (-32768..=32767).contains(&delta)
}
pub fn wheel_delta(message: u32, wp: usize) -> Option<i32> {
    if message != 0x20a {
        return None;
    }
    let delta = ((wp >> 16) as u16 as i16) as i32;
    (delta != 0).then_some(delta)
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn vertical_wheel_signed_high_word_only() {
        assert_eq!(wheel_delta(0x20a, 0x00780008), Some(120));
        assert_eq!(wheel_delta(0x20a, 0xff880008), Some(-120));
        assert_eq!(wheel_delta(0x20a, 0x001e0000), Some(30));
        assert_eq!(wheel_delta(0x20a, 8), None);
        assert_eq!(wheel_delta(0x20e, 0x00780000), None);
        assert!(!valid(0, 120));
        assert!(!valid(0x10000, 32768));
    }
}
