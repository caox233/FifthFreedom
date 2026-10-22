use super::*;

#[test]
fn unknown_generation_or_invalid_distance_falls_back() {
    for value in [0.0f32, -0.0, 1.0, 10000.0, f32::MAX, f32::MIN_POSITIVE] {
        assert!(eligible(1, Some(value.to_bits())));
        assert!(!eligible(0, Some(value.to_bits())));
    }
    for value in [-1.0f32, f32::NEG_INFINITY, f32::INFINITY, f32::NAN] {
        assert!(!eligible(1, Some(value.to_bits())));
    }
    assert!(!eligible(1, None));
}

fn fixture() -> [Vec<u8>; 4] {
    let root = std::path::PathBuf::from(std::env::var("SCBL_AUDIO_RANGE_FIXTURE").unwrap());
    PINS.map(|(at, _, _)| std::fs::read(root.join(format!("{at:x}.bin"))).unwrap())
}
fn target(code: &[u8], site: usize, base: u32) -> u32 {
    assert_eq!(code[site], 0xe8);
    let rel = u32::from_le_bytes(code[site + 1..site + 5].try_into().unwrap());
    (base + site as u32 + 5).wrapping_add(rel)
}

#[test]
#[ignore = "requires local pinned offline range bytes; never launches game"]
fn private_copy_is_exactly_pinned_and_originals_stay_immutable() {
    let bytes = fixture(); let old = bytes.clone();
    let output = prepare(std::array::from_fn(|i| bytes[i].as_slice()), 0x10000000).unwrap();
    if let Ok(path) = std::env::var("SCBL_AUDIO_RANGE_BANK_OUTPUT") {
        std::fs::write(path, &output).unwrap();
    }
    assert_eq!(bytes, old); assert_eq!(output.len(), BANK_SIZE);
    assert_eq!(target(&output, 0x3d, 0x10000000), 0x10000100);
    assert_eq!(target(&output, 0x56, 0x10000000), 0x10000240);
    for site in [0x11b, 0x148, 0x280, 0x30f] {
        assert_eq!(target(&output, site, 0x10000000), 0x10000350);
        assert_eq!(output[site + 5], 0x90);
    }
    assert_eq!(&output[0x350..0x358], &[0xd9,0x86,0xf4,3,0,0,0xd8,0x0d]);
    assert_eq!(&output[HALF..HALF + 4], &0.5f32.to_le_bytes());
    assert_eq!(&output[RADIUS..RADIUS + 8], &2.200000047683716f64.to_le_bytes());
    for bad_index in 0..4 {
        let mut bad = bytes.clone(); bad[bad_index][0] ^= 1;
        assert!(prepare(std::array::from_fn(|i| bad[i].as_slice()), 0x10000000).is_none());
    }
    assert!(prepare([&[]; 4], 0x10000000).is_none());
    assert!(prepare(std::array::from_fn(|i| bytes[i].as_slice()), 0).is_none());
    assert!(prepare(std::array::from_fn(|i| bytes[i].as_slice()), u32::MAX as usize - 1024).is_none());
}

#[test]
fn disabled_range_adapters_do_not_inspect_unknown_objects_or_consume_native_twice() {
    let calls = std::cell::Cell::new(0);
    unsafe {
        assert_eq!(evaluate(0, 0, 0, || { calls.set(calls.get() + 1); 71 }), 71);
        assert_eq!(relevance(0, 0, 0, false, || { calls.set(calls.get() + 1); 72 }), 72);
    }
    assert_eq!(calls.get(), 2);
}
