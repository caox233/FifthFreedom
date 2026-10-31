//! Real JSON file-loader regressions. Address data is never called or patched.
use super::*;
use serde_json::{json, Value};
use std::sync::atomic::{AtomicU64, Ordering};

struct Fixture(PathBuf);
impl Fixture {
    fn new(value: &Value) -> Self {
        static NEXT: AtomicU64 = AtomicU64::new(0);
        let path = env::temp_dir().join(format!("ff-address-format-{}-{}", std::process::id(), NEXT.fetch_add(1, Ordering::Relaxed)));
        std::fs::create_dir(&path).unwrap();
        std::fs::write(path.join("5th-echelon-addresses.json"), serde_json::to_vec(value).unwrap()).unwrap();
        Self(path)
    }
    fn load(&self) -> HashMap<String, HashMap<[u8; 32], Addresses>> {
        load_custom_addresses(&self.0)
    }
}
impl Drop for Fixture {
    fn drop(&mut self) { std::fs::remove_dir_all(&self.0).unwrap(); }
}
fn row() -> Value { serde_json::to_value(dx11_addresses().values().next().unwrap()).unwrap() }
fn document() -> Value { json!({ "dx11": { "11".repeat(32): row() } }) }

#[test]
fn dx11_only_custom_file_loads_without_obsolete_dx9_section() {
    let input = document();
    let fixture = Fixture::new(&input);
    let before = std::fs::read(fixture.0.join("5th-echelon-addresses.json")).unwrap();
    let loaded = fixture.load();
    let selected = loaded.get("blacklist_dx11_game.exe").and_then(|rows| rows.get(&[0x11; 32]));
    assert!(selected.is_some(), "the valid DX11 row must not be discarded because dx9 is absent");
    assert_eq!(serde_json::to_value(selected.unwrap()).unwrap(), row());
    assert!(loaded["blacklist_game.exe"].is_empty());
    assert_eq!(std::fs::read(fixture.0.join("5th-echelon-addresses.json")).unwrap(), before, "loading cannot rewrite the user's table");
}

#[test]
fn legacy_two_section_custom_file_remains_readable() {
    let mut input = document();
    input["dx9"] = json!({ "22".repeat(32): row() });
    let loaded = Fixture::new(&input).load();
    assert_eq!(loaded["blacklist_dx11_game.exe"].len(), 1);
    assert_eq!(loaded["blacklist_game.exe"].len(), 1);
    assert_eq!(serde_json::to_value(&loaded["blacklist_game.exe"][&[0x22; 32]]).unwrap(), row());
}

#[test]
fn absent_or_wrong_typed_dx11_never_becomes_an_accepted_document() {
    for input in [json!({}), json!({"dx9": {}}), json!({"dx11": null}), json!({"dx11": []}), json!({"dx11": "ignored"})] {
        assert!(Fixture::new(&input).load().is_empty(), "{input}");
    }
}

#[test]
fn present_but_malformed_dx9_is_not_silently_discarded() {
    for invalid in [Value::Null, json!([]), json!("obsolete") ] {
        let mut input = document();
        input["dx9"] = invalid;
        assert!(Fixture::new(&input).load().is_empty());
    }
}

#[test]
fn invalid_dx11_digest_rejects_the_whole_custom_table() {
    for invalid in ["a".repeat(63), "a".repeat(65), "gg".repeat(32), "é".repeat(32)] {
        let mut input = document();
        input["dx11"][invalid] = row();
        assert!(Fixture::new(&input).load().is_empty(), "no partial admission beside a valid row");
    }
}

#[test]
fn required_dx11_addresses_are_not_defaulted_or_type_coerced() {
    for key in ["global_onlineconfig_client", "onlineconfig_url"] {
        let mut input = document();
        input["dx11"]["11".repeat(32)].as_object_mut().unwrap().remove(key);
        assert!(Fixture::new(&input).load().is_empty());
        let mut input = document();
        input["dx11"]["11".repeat(32)][key] = json!("0x1234");
        assert!(Fixture::new(&input).load().is_empty());
    }
}

#[test]
fn builtin_pins_still_win_when_dx11_only_custom_file_collides() {
    let builtin = dx11_addresses();
    let (digest, original) = builtin.iter().next().unwrap();
    let mut replacement = serde_json::to_value(original).unwrap();
    replacement["onlineconfig_url"] = json!(1);
    let mut input = document();
    input["dx11"][hex(digest)] = replacement;
    let fixture = Fixture::new(&input);
    // Exercise the real built-in/custom merge, not a reconstruction in the test.
    let merged = build_game_map(&fixture.0);
    assert_eq!(serde_json::to_value(&merged["blacklist_dx11_game.exe"][digest]).unwrap(), serde_json::to_value(original).unwrap());
    assert!(merged["blacklist_dx11_game.exe"].contains_key(&[0x11; 32]));
}

#[test]
fn executable_digest_still_selects_the_exact_custom_row() {
    let bytes = b"synthetic DX11 fixture; never executed";
    let digest = hash_reader(&mut std::io::Cursor::new(bytes)).unwrap();
    let input = json!({ "dx11": { hex(digest): row() } });
    let fixture = Fixture::new(&input);
    let image = fixture.0.join("blacklist_dx11_game.exe");
    std::fs::write(&image, bytes).unwrap();
    assert_eq!(serde_json::to_value(get_from_path(&image).unwrap()).unwrap(), row());
    std::fs::write(&image, b"different synthetic bytes").unwrap();
    assert!(matches!(get_from_path(&image), Err(Error::BinaryMismatch(_, _))));
}
