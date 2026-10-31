use super::*;
use serde_json::json;

fn token(claims: serde_json::Value) -> String {
    format!("v1.{}.{}", URL_SAFE_NO_PAD.encode(serde_json::to_vec(&claims).unwrap()), URL_SAFE_NO_PAD.encode([7u8; 32]))
}
fn claims() -> serde_json::Value {
    json!({"release":format!("1|release|compat|{}", "a".repeat(64)), "user_id":1000,
        "username":"synthetic-user", "source_ip":"10.23.0.2", "issued_at":1, "expires_at":99,
        "auth_version":1, "game_run":{"product":"scbl-lan/2/game/official", "instance":"0123456789abcdef0123456789abcdef"}})
}
fn returned() -> ReturnedAuthentication {
    ReturnedAuthentication::new(token(claims()), "synthetic-account".into(), "synthetic-user".into()).unwrap()
}
fn descriptor() -> serde_json::Value {
    json!({"schema":1,"pipe":format!("ff-public-return-{}","a".repeat(32)),"launch_id":"b".repeat(32),
        "launcher_pid":42,"launcher_creation_filetime":133123456789000000u64,
        "launcher_path":r"C:\approved\FifthFreedom.Launcher.exe","launcher_sha256":"c".repeat(64)})
}

#[test]
fn descriptor_is_bounded_strict_and_contains_no_secret_or_remote_path() {
    let original = descriptor();
    assert!(Descriptor::parse(&original.to_string()).is_ok());
    for (field, value) in [("schema", json!(2)), ("launcher_pid", json!(0)),
        ("launch_id", json!("0".repeat(32))), ("launcher_creation_filetime", json!(0)),
        ("launcher_sha256", json!("C".repeat(64))), ("launcher_path", json!(r"\\remote\share\app.exe")),
        ("launcher_path", json!(r"C:\approved\..\foreign.exe")), ("launcher_path", json!(r"C:\file.exe:stream")),
        ("pipe", json!(format!("ff-public-return-{}", "0".repeat(32)))), ("token", json!("must-not-appear"))] {
        let mut value2 = original.clone(); value2[field] = value;
        assert_eq!(Descriptor::parse(&value2.to_string()).unwrap_err(), Invalid);
    }
    assert!(Descriptor::parse(&format!("{{\"schema\":1,{}", &original.to_string()[1..])).is_err());
    assert!(Descriptor::parse(&"x".repeat(4097)).is_err());
    assert_eq!(format!("{:?}", Descriptor::parse(&original.to_string()).unwrap()), "PublicReturnDescriptor");
}

#[test]
fn frame_lengths_json_shape_duplicates_and_token_bounds_fail_closed() {
    let authenticated = Frame::Authenticated { schema: 1, generation: 2, token: token(claims()),
        account_id: "synthetic-account".into(), username: "synthetic-user".into() };
    let bytes = authenticated.encode().unwrap();
    assert_eq!(Frame::decode(&bytes).unwrap(), authenticated);
    for bytes in [b"".to_vec(), vec![b' '; MAX_FRAME + 1], vec![255],
        br#"{"kind":"invalidate","schema":1,"generation":0}"#.to_vec(),
        br#"{"kind":"invalidate","schema":1,"schema":1,"generation":1}"#.to_vec(),
        br#"{"kind":"invalidate","schema":1,"generation":1,"extra":true}"#.to_vec(),
        br#"{"kind":"heartbeat","schema":1,"generation":-1}"#.to_vec(),
        br#"{"kind":"heartbeat","schema":1,"generation":true}"#.to_vec(),
        br#"{"kind":"heartbeat","schema":1,"generation":18446744073709551616}"#.to_vec()] {
        assert!(Frame::decode(&bytes).is_err());
    }
    for invalid in [String::new(), "x".repeat(MAX_TOKEN + 1), "v1.a.a".into(), "Bearer synthetic".into()] {
        assert!(Frame::Authenticated { schema: 1, generation: 1, token: invalid,
            account_id: "synthetic-account".into(), username: "synthetic-user".into() }.encode().is_err());
    }
    assert_eq!(format!("{authenticated:?}"), "PublicReturnFrame [redacted]");
}

#[test]
fn challenge_echo_binds_launch_but_hello_uses_native_run() {
    let launch = "a".repeat(32);
    let run = "b".repeat(32);
    let challenge = Frame::Challenge { schema: 1, launch_id: launch.clone(), challenge: "c".repeat(64) };
    let hello = challenge.clone().hello(&launch, &run).unwrap();
    assert!(matches!(hello, Frame::Hello { game_instance, launch_id, .. } if game_instance == run && launch_id == launch));
    assert!(challenge.clone().hello(&run, &run).is_err());
    assert!(challenge.clone().hello(&launch, &"0".repeat(32)).is_err());
    assert!(Frame::Invalidate { schema: 1, generation: 1 }.hello(&launch, &run).is_err());
}

#[test]
fn context_screening_rejects_account_wrong_run_release_source_and_observer() {
    let release = ReleaseIdentity::new(1, "release", "compat", &"a".repeat(64)).unwrap();
    let expected = ContextExpectation { username: "synthetic-user", source_ip: "10.23.0.2".parse().unwrap(),
        product: "scbl-lan/2/game/official", instance: "0123456789abcdef0123456789abcdef",
        release: &release, observer_requested: false };
    assert_eq!(screen_token(&token(claims()), &expected), Ok(1000));
    for (key, value) in [("user_id", json!(0)), ("username", json!("other")), ("source_ip", json!("10.23.0.3")),
        ("release", json!(format!("1|release|other|{}", "a".repeat(64)))), ("auth_version", json!(0)),
        ("observer_admitted_run", json!("a".repeat(32))), ("unknown", json!(1)),
        ("game_run", json!({"product":"scbl-lan/1/official","instance":expected.instance})),
        ("game_run", json!({"product":expected.product,"instance":"b".repeat(32)}))] {
        let mut value2 = claims(); value2[key] = value;
        assert_eq!(screen_token(&token(value2), &expected), Err(Invalid));
    }
    let observer = ContextExpectation { observer_requested: true, ..expected };
    assert!(screen_token(&token(claims()), &observer).is_err());
    let mut accepted = claims(); accepted["observer_admitted_run"] = json!(observer.instance);
    assert_eq!(screen_token(&token(accepted), &observer), Ok(1000));
    // This deliberately accepts a syntactically valid synthetic signature.
    // It demonstrates screening is not a signature/authentication authority.
}

#[test]
fn every_transition_advances_and_stale_owner_or_pending_frame_cannot_publish() {
    let mut state = ReturnState::default();
    assert!(state.snapshot().is_none());
    let first = state.begin().unwrap();
    assert_eq!(state.snapshot(), Some(Frame::Invalidate { schema: 1, generation: 1 }));
    assert!(state.publish(first, returned()).unwrap());
    let old = state.snapshot().unwrap();
    assert_eq!(state.generation(), 2);
    let next = state.begin().unwrap();
    assert_eq!(state.generation(), 3);
    assert!(!state.is_current(&old));
    assert!(!state.publish(first, returned()).unwrap());
    assert!(!state.invalidate(first).unwrap());
    assert!(state.invalidate(next).unwrap());
    assert_eq!(state.generation(), 4);
    assert!(!state.publish(next, returned()).unwrap());
    let current = state.begin().unwrap();
    assert!(state.publish(current, returned()).unwrap());
    assert!(!state.publish(current, returned()).unwrap());
    assert_eq!(state.generation(), 6);
}

#[test]
fn cancellation_drop_style_invalidation_and_close_remove_bearer() {
    let mut state = ReturnState::default();
    let first = state.begin().unwrap();
    state.invalidate(first).unwrap();
    let current = state.begin().unwrap();
    state.publish(current, returned()).unwrap();
    let snapshot = state.snapshot().unwrap();
    assert!(state.is_current(&snapshot));
    state.close();
    assert!(state.is_closed());
    assert!(state.snapshot().is_none());
    assert!(!state.is_current(&snapshot));
    assert!(state.begin().is_err());
    assert!(!state.publish(current, returned()).unwrap());
    assert_eq!(format!("{state:?}"), "PublicReturnState [redacted]");
}

#[test]
fn generation_overflow_is_sticky_closed_not_saturating_reuse() {
    let mut state = ReturnState { generation: u64::MAX - 1, owner: None, token: None, closed: false };
    let current = state.begin().unwrap();
    assert_eq!(state.generation(), u64::MAX);
    assert_eq!(state.publish(current, returned()), Err(Invalid));
    assert!(state.is_closed());
    assert!(state.snapshot().is_none());
    assert!(state.begin().is_err());
}

#[test]
fn actual_native_fixture_is_screened_without_relabeling_signature_authority() {
    let fixture: serde_json::Value = serde_json::from_str(include_str!("../../../../../tests/server/fixtures/native_game_token_v1.json")).unwrap();
    let release = ReleaseIdentity::new(1, "native-bridge-synthetic", "native-bridge-compat", &"a".repeat(64)).unwrap();
    let expected = ContextExpectation { username: "native-bridge-fixture", source_ip: "10.66.0.10".parse().unwrap(),
        product: "scbl-lan/2/game/mod", instance: "0123456789abcdef0123456789abcdef", release: &release,
        observer_requested: false };
    assert_eq!(screen_token(fixture["token"].as_str().unwrap(), &expected), Ok(1000));
    assert_eq!(screen_token(fixture["rotated_token"].as_str().unwrap(), &expected), Ok(1000));
}

#[test]
fn actual_response_account_metadata_is_bounded_opaque_and_required_on_wire() {
    for account in ["account-opaque", "001", "玩家", "same account"] {
        assert!(ReturnedAuthentication::new(token(claims()), account.into(), "synthetic-user".into()).is_ok());
    }
    for account in [String::new(), "x".repeat(65), "x\0y".into(), "x\ny".into(), "玩家".repeat(11)] {
        assert!(ReturnedAuthentication::new(token(claims()), account, "synthetic-user".into()).is_err());
    }
    for username in ["", "two users", "用户", "user\n"] {
        assert!(ReturnedAuthentication::new(token(claims()), "account".into(), username.into()).is_err());
    }
    let frame = json!({"schema":1,"kind":"authenticated","generation":1,"token":token(claims())});
    assert!(Frame::decode(frame.to_string().as_bytes()).is_err());
    assert_eq!(format!("{:?}", returned()), "ReturnedAuthentication [redacted]");
}
