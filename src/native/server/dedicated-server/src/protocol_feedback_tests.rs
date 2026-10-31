//! Synthetic follow-up to distinct RMC10/1 refusals; no private log replay.
//! Production Login/RVSec serialization and SQLite admission stay in the path.
use super::*;
use quazal::prudp::packet::StreamHandler;
use quazal::rmc::basic::{FromStream, ToStream};
use quazal::rmc::{Packet, Request, Response, RVSecHandler};

const ACCOUNT: &str = "protocol-feedback-fixture";

fn fixture() -> (Arc<Storage>, sqlx::SqlitePool, u32) {
    let (storage, pool) = crate::storage::release_test_support::fixture();
    let runtime = tokio::runtime::Builder::new_current_thread().enable_all().build().unwrap();
    runtime.block_on(storage.register_user_with_recovery_async(
        ACCOUNT, "fixture-only-password", Some("protocol-feedback-fixture-id"), None)).unwrap();
    let user = storage.find_user_id_by_name(ACCOUNT).unwrap().unwrap();
    (storage, pool, user)
}

fn count(pool: &sqlx::SqlitePool, sql: &str) -> i64 {
    tokio::runtime::Builder::new_current_thread().enable_all().build().unwrap()
        .block_on(sqlx::query_scalar(sql).fetch_one(pool)).unwrap()
}

fn request(call_id: u32) -> Vec<u8> {
    Request { protocol_id: 10, method_id: 1, call_id,
        parameters: LoginRequest { str_user_name: ACCOUNT.into() }.to_bytes() }.to_bytes()
}

fn handler(storage: Arc<Storage>, logger: &slog::Logger) -> RVSecHandler<()> {
    let mut handler = RVSecHandler::new(logger.clone());
    handler.register_protocol(new_protocol_with_source_policy(storage,
        quazal::source_policy::SourcePolicy::lan()));
    handler
}

fn invoke(handler: &RVSecHandler<()>, ci: &mut quazal::ClientInfo<()>, call_id: u32) -> Response {
    let logger = slog::Logger::root(slog::Discard, slog::o!());
    let context = Context::splinter_cell_blacklist();
    let registry = ClientRegistry::default();
    // The real handler signature requires a socket; this test never sends to it.
    let socket = std::net::UdpSocket::bind("127.0.0.1:0").unwrap();
    let bytes = handler.handle(&logger, &context, ci, &request(call_id), &registry, &socket).unwrap();
    let Packet::Response(response) = Packet::from_bytes(&bytes).unwrap() else {
        panic!("actual RVSec response required");
    };
    assert_eq!(response.protocol_id, 10);
    response
}

#[test]
fn protocol_feedback_distinct_plain_login_refusals_do_not_write_tickets_or_replace_identity() {
    let (storage, pool, _) = fixture();
    let logger = slog::Logger::root(slog::Discard, slog::o!());
    let handler = handler(storage, &logger);
    let mut fresh = quazal::ClientInfo::<()>::new("127.0.0.1:31001".parse().unwrap());
    let mut already_bound = quazal::ClientInfo::<()>::new("127.0.0.1:31002".parse().unwrap());
    already_bound.user_id = Some(123); // Synthetic pre-existing transport identity.
    for call_id in 1..=32 {
        for (client, expected) in [(&mut fresh, None), (&mut already_bound, Some(123))] {
            let error = invoke(&handler, client, call_id).result.unwrap_err();
            assert_eq!(error.call_id, call_id);
            assert_eq!(error.error_code, quazal::rmc::Error::InternalError.to_error_code());
            assert_eq!(client.user_id, expected);
            assert_eq!(count(&pool, "SELECT COUNT(*) FROM user_sessions"), 0);
        }
    }
    assert_eq!(count(&pool, "SELECT COUNT(*) FROM game_runs"), 0);
    assert_eq!(count(&pool, "SELECT COUNT(*) FROM users WHERE is_online<>0"), 0);
    println!("PROTOCOL_FEEDBACK distinct_calls=64 denied=64 ticket_rows=0 binding_preserved=true live_trace=false");
}

#[test]
fn protocol_feedback_plain_login_distinguishes_admitted_run_from_each_run_guard() {
    let (storage, pool, user) = fixture();
    assert_eq!(crate::api::maintenance_authenticated_game_login(storage.clone(), ACCOUNT,
        "scbl-lan/2/game/mod", "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"), user);
    let runtime = tokio::runtime::Builder::new_current_thread().enable_all().build().unwrap();
    let logger = slog::Logger::root(slog::Discard, slog::o!());
    let handler = handler(storage, &logger);
    let approved_release: String = runtime.block_on(sqlx::query_scalar(
        "SELECT release_identity FROM game_runs WHERE user_id=?").bind(user).fetch_one(&pool)).unwrap();
    let mut ci = quazal::ClientInfo::<()>::new("127.0.0.1:31003".parse().unwrap());
    let result = invoke(&handler, &mut ci, 1).result.unwrap();
    let response = LoginResponse::from_bytes(&result.data).unwrap();
    assert_eq!((result.call_id, result.method_id, response.pid_principal), (1, 1, user));
    assert_eq!(ci.user_id, Some(user));
    assert_eq!(count(&pool, "SELECT COUNT(*) FROM user_sessions"), 1);
    // One API-admitted run is modified only as a controlled negative fixture.
    // Each guard must refuse the next distinct request without another ticket.
    for (index, (name, sql)) in [
        ("source", "UPDATE game_runs SET source_ip='127.0.0.2' WHERE user_id=?"),
        ("stale", "UPDATE game_runs SET seen_at='2000-01-01 00:00:00' WHERE user_id=?"),
        ("retired", "UPDATE game_runs SET retired_at=CURRENT_TIMESTAMP WHERE user_id=?"),
    ].into_iter().enumerate() {
        runtime.block_on(sqlx::query(sql).bind(user).execute(&pool)).unwrap();
        let call_id = 2 + index as u32;
        let error = invoke(&handler, &mut ci, call_id).result.unwrap_err();
        assert_eq!(error.call_id, call_id);
        assert_eq!(error.error_code, quazal::rmc::Error::InternalError.to_error_code());
        assert_eq!(ci.user_id, Some(user));
        assert_eq!(count(&pool, "SELECT COUNT(*) FROM user_sessions"), 1);
        runtime.block_on(sqlx::query("UPDATE game_runs SET source_ip='127.0.0.1', release_identity=?, seen_at=CURRENT_TIMESTAMP, retired_at=NULL WHERE user_id=?")
            .bind(&approved_release).bind(user).execute(&pool)).unwrap();
        println!("PROTOCOL_FEEDBACK guard={name} denied=true ticket_rows_unchanged=true");
    }
    assert_eq!(invoke(&handler, &mut ci, 6).result.unwrap().call_id, 6);
    assert_eq!(count(&pool, "SELECT COUNT(*) FROM user_sessions"), 2,
        "a new admitted request issues a new ticket; it is not a packet retransmission");
    println!("PROTOCOL_FEEDBACK restored_run=true new_admitted_request=true ticket_rows=2 live_trace=false");
}

#[test]
fn protocol_feedback_wrong_release_run_cannot_issue_native_ticket() {
    let (storage, pool, user) = fixture();
    // A foreign historical row is a negative fixture. The immutable-release
    // trigger is retained: never mutate a successfully admitted row's identity.
    let runtime = tokio::runtime::Builder::new_current_thread().enable_all().build().unwrap();
    runtime.block_on(sqlx::query("INSERT INTO game_runs(user_id,instance,mode,source_ip,release_identity) VALUES(?,? ,1,'127.0.0.1','synthetic-other-release')")
        .bind(user).bind("abababababababababababababababab").execute(&pool)).unwrap();
    let logger = slog::Logger::root(slog::Discard, slog::o!());
    let handler = handler(storage, &logger);
    let mut ci = quazal::ClientInfo::<()>::new("127.0.0.1:31005".parse().unwrap());
    for call_id in 1..=3 {
        let error = invoke(&handler, &mut ci, call_id).result.unwrap_err();
        assert_eq!(error.call_id, call_id);
        assert_eq!(error.error_code, quazal::rmc::Error::InternalError.to_error_code());
        assert_eq!(ci.user_id, None);
        assert_eq!(count(&pool, "SELECT COUNT(*) FROM user_sessions"), 0);
    }
    println!("PROTOCOL_FEEDBACK guard=release denied=true ticket_rows=0 immutable_release_guard_retained=true");
}

#[cfg(feature = "maintenance-test-seams")]
#[test]
fn protocol_feedback_prudp_retransmission_reuses_exact_ticket_response_without_duplicate_write() {
    use quazal::prudp::fragment_boundary_capture::Capture;
    use quazal::prudp::packet::{PacketFlag, PacketType, QPacket, StreamHandlerRegistry, StreamType, VPort};
    use quazal::prudp::Server;
    type TestServer<'a> = Server<'a, fn(quazal::ClientInfo<()>), fn(quazal::ClientInfo<()>), ()>;

    sodiumoxide::init().unwrap();
    for admitted in [false, true] {
        let (storage, pool, user) = fixture();
        if admitted {
            assert_eq!(crate::api::maintenance_authenticated_game_login(storage.clone(), ACCOUNT,
                "scbl-lan/2/game/mod", "ffffffffffffffffffffffffffffffff"), user);
        }
        let logger = slog::Logger::root(slog::Discard, slog::o!());
        let context = Context::splinter_cell_blacklist();
        let port = VPort { stream_type: StreamType::RVSec, port: context.vport };
        let mut streams = StreamHandlerRegistry::new(logger.clone());
        streams.register(port, Box::new(handler(storage, &logger)));
        let mut server: TestServer<'_> = Server::new(logger, &context, streams);
        server.bind("127.0.0.1:0").unwrap();
        let address = "127.0.0.1:31004".parse().unwrap(); // Not contacted: send leaf captured.
        let signature = 0x1031_0001;
        server.maintenance_seed_connected(signature, quazal::ClientInfo::new(address), false);
        let capture = Capture::begin();
        let packet = QPacket { source: port, destination: port, packet_type: PacketType::Data,
            signature, session_id: 0, sequence: 77, fragment_id: Some(0), payload: request(77),
            ..Default::default() };
        let decode = |frames: Vec<Vec<u8>>| -> Vec<Vec<u8>> {
            let mut data = Vec::new();
            let mut acknowledgements = 0;
            for bytes in frames {
                let (packet, used) = QPacket::from_bytes(&context, &bytes).unwrap();
                assert_eq!(used as usize, bytes.len());
                packet.validate(&context, &bytes).unwrap();
                if packet.flags.contains(PacketFlag::Ack) { acknowledgements += 1; }
                else {
                    assert_eq!(packet.packet_type, PacketType::Data);
                    let Packet::Response(response) = Packet::from_bytes(&packet.payload).unwrap() else {
                        panic!("serialized ticket response required");
                    };
                    assert_eq!(response.protocol_id, 10);
                    assert_eq!(response.result.is_ok(), admitted);
                    data.push(bytes);
                }
            }
            assert_eq!(acknowledgements, 1);
            assert_eq!(data.len(), 1);
            data
        };
        server.maintenance_dispatch(packet.clone(), address);
        let original = decode(capture.take());
        let expected_rows = i64::from(admitted);
        assert_eq!(count(&pool, "SELECT COUNT(*) FROM user_sessions"), expected_rows);
        for _ in 0..8 {
            server.maintenance_dispatch(packet.clone(), address);
            assert_eq!(decode(capture.take()), original, "same-sequence retransmission preserves exact response bytes");
            assert_eq!(count(&pool, "SELECT COUNT(*) FROM user_sessions"), expected_rows);
        }
        println!("PROTOCOL_FEEDBACK admitted={admitted} retransmissions=8 exact_replay=true ticket_rows={expected_rows} actual_prudp_dispatch=true socket_send=false");
    }
}
