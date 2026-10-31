use prost::Message;
use crate::{misc, users};

#[test]
fn invitation_and_session_field_numbers_keep_native_identity() {
    // id=150, kind=HostMigrationFollow(5), session{id=7,data=[aa,bb],flags=3,invite_only=true}.
    let wire = [0x08,0x96,0x01,0x20,0x05,0x2a,0x0a,0x08,0x07,0x12,0x02,0xaa,0xbb,0x18,0x03,0x20,0x01];
    let event=misc::InviteEvent::decode(wire.as_slice()).unwrap();
    assert_eq!(event.id,150);assert_eq!(event.kind,5);assert!(!event.force_join);assert!(event.sender.is_none());
    let session=event.game_session.as_ref().unwrap();assert_eq!((session.id,session.flags,session.invite_only),(7,3,true));assert_eq!(session.data,[0xaa,0xbb]);
    assert_eq!(event.encode_to_vec(),wire);
    assert_eq!([misc::InviteKind::Unspecified as i32,misc::InviteKind::PrivateRoom as i32,misc::InviteKind::LobbyParty as i32,misc::InviteKind::PartyFollow as i32,misc::InviteKind::HostMigrationFollow as i32],[0,1,2,3,5]);
    println!("WIRE invitation_golden_bytes={} kinds=0,1,2,3,5",wire.len());
}

#[test]
fn additive_unknown_fields_preserve_known_values_without_reclassifying_kind() {
    let wire=[0x08,0x2a,0x20,0x63,0xa0,0x06,0x07]; // invite 42, unknown kind 99, field100=7
    let event=misc::InviteEvent::decode(wire.as_slice()).unwrap();
    assert_eq!(event.id,42);assert_eq!(event.kind,99);assert!(misc::InviteKind::try_from(event.kind).is_err());
    // Prost discards unknown fields; consumers must not treat unknown enum as a private invite.
    assert_eq!(event.encode_to_vec(),[0x08,0x2a,0x20,0x63]);
}

#[test]
fn client_product_stays_at_tag_ten_separate_from_account_fields() {
    let wire=[0x0a,0x01,b'u',0x12,0x01,b'p',0x52,0x01,b'x']; // synthetic u/p, product x
    let req=users::LoginRequest::decode(wire.as_slice()).unwrap();
    assert_eq!((&*req.username,&*req.password,&*req.client_product),("u","p","x"));
    assert!(crate::gameplay_contract::ClientContract::parse(&req.client_product).is_none());
    let mut old=wire[..6].to_vec();old.extend_from_slice(&[0xa0,0x06,1]);
    assert!(users::LoginRequest::decode(old.as_slice()).unwrap().client_product.is_empty());
}

#[test]
fn malformed_wire_fails_as_decode_error_without_panicking() {
    let inputs: &[&[u8]]=&[
        &[0x00],                         // invalid field zero
        &[0x08,0x80],                    // truncated varint
        &[0x2a,0x80],                    // truncated message length
        &[0x2a,0xff,0xff,0xff,0xff,0x0f],// body absent despite huge length
        &[0x12,0x03,0x0a,0x01,0xff],     // invalid UTF8 in nested sender
        &[0x0f],                         // invalid wire type
    ];
    for wire in inputs {
        let result=std::panic::catch_unwind(|| misc::InviteEvent::decode(*wire));
        assert!(matches!(result,Ok(Err(_))),"malformed fixture {wire:02x?}");
    }
    println!("WIRE malformed_inputs={} panics=0",inputs.len());
}

#[test]
fn p2p_request_and_response_keep_distinct_challenge_tags() {
    assert_eq!(misc::TestP2pRequest{challenge:vec![0xaa,0xbb]}.encode_to_vec(),[0x0a,0x02,0xaa,0xbb]);
    assert_eq!(misc::TestP2pResponse{challenge:vec![0xaa,0xbb]}.encode_to_vec(),[0x12,0x02,0xaa,0xbb]);
}

#[test]
fn login_and_registration_keep_package_identity_at_tag_eleven() {
    // Synthetic account u/p; product x at tag 10; package schema 1 at tag 11.
    let wire = [0x0a, 0x01, b'u', 0x12, 0x01, b'p', 0x52, 0x01, b'x', 0x5a, 0x02, 0x08, 0x01];
    let login = users::LoginRequest::decode(wire.as_slice()).unwrap();
    let register = users::RegisterRequest::decode(wire.as_slice()).unwrap();
    assert_eq!(login.package_identity.as_ref().unwrap().schema, 1);
    assert_eq!(register.package_identity.as_ref().unwrap().schema, 1);
    assert_eq!(login.encode_to_vec(), wire);
    assert_eq!(register.encode_to_vec(), wire);
    // A legacy donor request stays legacy: decoding must never manufacture a
    // product or package identity that would bypass release_for_login admission.
    let old = &wire[..6];
    let login = users::LoginRequest::decode(old).unwrap();
    let register = users::RegisterRequest::decode(old).unwrap();
    assert!(login.client_product.is_empty() && login.package_identity.is_none());
    assert!(register.client_product.is_empty() && register.package_identity.is_none());
}

#[test]
fn capabilities_decode_all_stable_tags_without_upgrading_unknown_protocol() {
    // product x, unknown protocol 99, version v, ready, official-only,
    // config/content/auth/secure ports 80/8000/21126/21127 and observer allowed.
    let wire = [0x0a, 0x01, b'x', 0x10, 99, 0x1a, 0x01, b'v', 0x20, 1, 0x28, 1,
        0x30, 80, 0x38, 0xc0, 0x3e, 0x40, 0x86, 0xa5, 0x01, 0x48, 0x87, 0xa5, 0x01, 0x50, 1];
    let capabilities = users::CapabilitiesResponse::decode(wire.as_slice()).unwrap();
    assert_eq!((&*capabilities.product, capabilities.protocol_version, &*capabilities.version), ("x", 99, "v"));
    assert!(capabilities.ready && capabilities.official_only && capabilities.observer_allowed);
    assert_eq!([capabilities.config_port, capabilities.content_port, capabilities.auth_port, capabilities.secure_port], [80, 8000, 21126, 21127]);
    assert_eq!(capabilities.encode_to_vec(), wire);
    let absent = users::CapabilitiesResponse::decode(&[][..]).unwrap();
    assert!(!absent.ready && !absent.observer_allowed);
    assert_eq!(absent.protocol_version, 0);
}
