// Numeric values exercise actual opaque protocol attributes. No inspected
// source/replay binds these property IDs to player rank; do not call this a
// real level-55/56/57/999 matchmaking replay or change any player's profile.
#[derive(Clone)]
struct FilterTraceDrain(std::sync::Arc<std::sync::Mutex<Vec<String>>>);
impl slog::Drain for FilterTraceDrain {
    type Ok = ();
    type Err = std::convert::Infallible;
    fn log(&self, record: &slog::Record, _: &slog::OwnedKVList) -> std::result::Result<(), Self::Err> {
        let message = record.msg().to_string();
        if message.starts_with("QuickMatchFilterSummary ") {
            assert_eq!(record.level(), slog::Level::Debug, "no extra routine INFO traffic");
            self.0.lock().unwrap().push(message);
        }
        Ok(())
    }
}

fn search_with_filter_trace(storage: &Arc<Storage>, client: &mut quazal::ClientInfo<()>,
    properties: Vec<Property>) -> (Vec<((u32, u32), u32, Vec<u32>)>, String) {
    use slog::Drain;
    let messages = Arc::new(std::sync::Mutex::new(Vec::new()));
    let logger = Logger::root(FilterTraceDrain(messages.clone()).fuse(), slog::o!());
    let protocol = crate::game_session_ex::new_protocol::<()>(storage.clone());
    let parameters = SearchSessionsRequest { game_session_query: GameSessionQuery {
        type_id: 1, query_id: 0, parameters: QList(properties) } }.to_bytes();
    let context = quazal::Context::splinter_cell_blacklist();
    let registry = quazal::prudp::ClientRegistry::<()>::default();
    let socket = std::net::UdpSocket::bind("127.0.0.1:0").unwrap();
    let bytes = protocol.handle(&logger, &context, client,
        &Request { protocol_id: 123, call_id: 1, method_id: 1, parameters }, &registry, &socket).unwrap();
    let response = SearchSessionsResponse::from_bytes(&bytes).unwrap();
    let mut rows = response.search_results.0.into_iter().map(|row| {
        let result = row.game_session_search_result;
        let mut members = row.participants.0.into_iter().map(|member| member.pid).collect::<Vec<_>>();
        members.sort_unstable();
        ((result.session_key.type_id, result.session_key.session_id), result.host_pid, members)
    }).collect::<Vec<_>>();
    rows.sort_by_key(|row| row.1);
    let records = messages.lock().unwrap();
    assert_eq!(records.len(), 1, "QUICK_MATCH_ONE_DEBUG_FILTER_SUMMARY");
    let record = records[0].clone();
    assert!(!record.contains("192.0.2.") && !record.contains("prudp:")
        && !record.contains("user_id") && !record.contains("session_id")
        && !record.contains("password") && !record.contains("ticket"));
    (rows, record)
}

fn numeric_filter_trace_mixed_party_and_solo(mode: i64) {
    let storage = Arc::new(create_test_storage("quick-match-numeric-filter-trace"));
    insert_accounts(&storage, 9);
    for user in 1..=9 {
        maintenance_begin_run(&storage, user, &format!("{user:032x}"), if user == 8 { 3 - mode } else { mode });
        storage.register_urls(user, vec![format!("prudp:/address=192.0.2.{user};port=13000;sid={user};type=2")]).unwrap();
    }
    let mut leader = maintenance_native_client(&storage, 5);
    let mut member = maintenance_native_client(&storage, 6);
    let mut solo = maintenance_native_client(&storage, 7);
    assert_ne!(leader.ticket_fingerprint, member.ticket_fingerprint);
    assert_ne!(member.ticket_fingerprint, solo.ticket_fingerprint);
    let base = || vec![Property { id: 113, value: 0 }, Property { id: 103, value: 2_165_463_540 }];
    let (empty, trace) = search_with_filter_trace(&storage, &mut leader, base());
    assert!(empty.is_empty());
    assert!(trace.contains("candidates=0") && trace.contains("returned=0"));
    let lobby = storage.create_game_session(5, 1, "113 => 1;3 => 8;4 => 0".into()).unwrap();
    let invitation = run(storage.add_invite_async(5, 6, INVITE_KIND_LOBBY_PARTY, 1, lobby)).unwrap().unwrap().unwrap();
    assert!(run(storage.accept_invite_async(6, invitation)).unwrap().unwrap().is_some());
    assert!(storage.join_game_session_unbounded(6, 1, lobby).unwrap());
    let mut keys = Vec::new();
    for (user, numeric, private) in [(1, 55, false), (2, 56, false), (3, 57, false), (4, 999, false),
        (8, 55, false), (9, 55, true)] {
        let mut client = maintenance_native_client(&storage, user);
        let mut attributes = base();
        attributes.extend([Property { id: 3, value: if private { 0 } else { 8 } },
            Property { id: 4, value: if private { 8 } else { 0 } }]);
        attributes.extend((106..=110).map(|id| Property { id, value: numeric }));
        let parameters = CreateSessionRequest { game_session: NativeSession { type_id: 1,
            attributes: QList(attributes) } }.to_bytes();
        let key = CreateSessionResponse::from_bytes(&call_room(&storage, &mut client, 42, 1, parameters).unwrap()).unwrap().game_session_key;
        keys.push((key.type_id, key.session_id));
    }
    for id in 106..=110 {
        for (index, numeric) in [55, 56, 57, 999].into_iter().enumerate() {
            let mut properties = base();
            properties.push(Property { id, value: numeric });
            let (rows, trace) = search_with_filter_trace(&storage, &mut solo, properties);
            assert_eq!(rows, vec![(keys[index], index as u32 + 1, vec![index as u32 + 1])]);
            assert!(trace.contains("stage=pre_mode") && trace.contains("private=1"));
            assert!(trace.contains(&format!("{id}")));
            // The other-mode row is rejected by the existing outer wrapper.
            // The summary intentionally does not mislabel its own stage as final.
            assert!(trace.contains(if numeric == 55 { "returned=2" } else { "returned=1" }));
        }
    }
    for numeric in [55, 56, 57, 999] {
        let mut properties = base();
        properties.extend([Property { id: 105, value: numeric }, Property { id: 112, value: numeric }]);
        let (rows, _) = search_with_filter_trace(&storage, &mut solo, properties);
        assert_eq!(rows.iter().map(|row| row.0).collect::<Vec<_>>(), keys[..4]);
    }
    let query = |value| { let mut properties = base(); properties.push(Property { id: 109, value }); properties };
    let (leader_rows, _) = search_with_filter_trace(&storage, &mut leader, query(55));
    let (member_rows, _) = search_with_filter_trace(&storage, &mut member, query(57));
    let (solo_rows, _) = search_with_filter_trace(&storage, &mut solo, query(999));
    for (client, target) in [(&mut leader, leader_rows[0].0), (&mut member, member_rows[0].0), (&mut solo, solo_rows[0].0)] {
        JoinSessionResponse::from_bytes(&call_room(&storage, client, 42, 22, join_request(target)).unwrap()).unwrap();
    }
    assert_eq!(storage.game_session_diagnostics(1, keys[0].1).unwrap().unwrap().2, vec![1, 5]);
    assert_eq!(storage.game_session_diagnostics(1, keys[2].1).unwrap().unwrap().2, vec![3, 6]);
    assert_eq!(storage.game_session_diagnostics(1, keys[3].1).unwrap().unwrap().2, vec![4, 7]);
    let (party_leader, party) = run(storage.find_party_roster_async(6)).unwrap().unwrap().unwrap();
    let mut members = party.into_iter().map(|row| row.id).collect::<Vec<_>>(); members.sort_unstable();
    assert_eq!((party_leader, members), (5, vec![5, 6]));
    let mut many = base();
    many.extend((500..532).rev().map(|id| Property { id, value: 123456789 }));
    let (rows, trace) = search_with_filter_trace(&storage, &mut solo, many);
    assert!(rows.is_empty());
    assert!(trace.contains("ids_truncated=true"));
    // The social lobby is also a candidate before quick-search filters: its
    // missing game-mode attribute103 and lobby113=1 remain real rejections.
    assert!(trace.contains("mismatch_ids={103, 113, 500, 501, 502, 503, 504, 505, 506, 507, 508, 509, 510, 511, 512, 513}"), "{trace}");
    assert!(!trace.contains("123456789"), "never include property values");
    println!("QUICK_MATCH_NUMERIC_FILTER mode={mode} numeric_values=55,56,57,999 actual_rank_mapping=false production_protocol=true initial_rooms=0 mixed_party_and_solo=true different_queries_split_rooms=true capacity_exemptions_retained=true private_and_mode_guards_retained=true bounded_debug_summary=true live_game=false");
    run(storage.pool.close()).unwrap();
}

#[test]
fn quick_match_protocol_official_numeric_filter_trace_mixed_party_and_solo() { numeric_filter_trace_mixed_party_and_solo(1); }
#[test]
fn quick_match_protocol_mod_numeric_filter_trace_mixed_party_and_solo() { numeric_filter_trace_mixed_party_and_solo(2); }
