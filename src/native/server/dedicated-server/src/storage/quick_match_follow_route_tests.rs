// End-to-end server handler/state schedules, not the unknown retail matchmaking
// policy. Targets below always come from actual Search/accepted-invite responses.
use crate::api::quick_match_fixture::MatchingApi;
use crate::protocols::game_session_service::game_session_protocol::{
    DeleteSessionRequest, DeleteSessionResponse, SearchSessionsWithParticipantsRequest,
    SearchSessionsWithParticipantsResponse,
};

fn follow_route_fixture(mode: i64, name: &str) -> (Arc<Storage>, [quazal::ClientInfo<()>; 3], MatchingApi) {
    let storage = Arc::new(create_test_storage(&format!("quick-match-follow-route-{name}-{mode}")));
    insert_accounts(&storage, 3);
    for user in 1..=3 {
        maintenance_begin_run(&storage, user, &format!("{user:032x}"), mode);
        storage.register_urls(user, vec![format!("prudp:/address=192.0.2.{user};port=13000;sid={user};type=2")]).unwrap();
    }
    let mut clients = [maintenance_native_client(&storage, 1), maintenance_native_client(&storage, 2), maintenance_native_client(&storage, 3)];
    for client in &mut clients { assert!(discover_all_rooms(&storage, client).is_empty()); }
    let api = MatchingApi::new(storage.clone(), mode);
    // Real Create/accept/Join establishes a Party; there is still no game room.
    let request = CreateSessionRequest { game_session: NativeSession { type_id: 1,
        attributes: QList(vec![Property { id: 113, value: 1 }, Property { id: 3, value: 8 }, Property { id: 4, value: 0 }]) } }.to_bytes();
    let lobby = CreateSessionResponse::from_bytes(&call_room(&storage, &mut clients[0], 42, 1, request).unwrap()).unwrap().game_session_key;
    api.publish(1, lobby.session_id);
    let invite = run(storage.add_invite_async(1, 2, INVITE_KIND_LOBBY_PARTY, 1, lobby.session_id)).unwrap().unwrap().unwrap();
    assert!(api.accept(2, invite).unwrap().accepted);
    JoinSessionResponse::from_bytes(&call_room(&storage, &mut clients[1], 42, 22, join_request((1, lobby.session_id))).unwrap()).unwrap();
    for client in &mut clients { assert!(discover_all_rooms(&storage, client).is_empty()); }
    (storage, clients, api)
}
fn follow_row(storage: &Storage, member: u32, target: (u32, u32)) -> (i64, bool, bool) {
    run(sqlx::query_as("SELECT id,accepted_at IS NOT NULL,consumed_at IS NOT NULL FROM invites WHERE receiver=? AND kind=? AND session_type=? AND session_id=? ORDER BY id DESC LIMIT 1")
        .bind(member).bind(INVITE_KIND_PARTY_FOLLOW).bind(target.0).bind(target.1).fetch_one(&storage.pool)).unwrap().unwrap()
}
fn make_follow_delivery_due(storage: &Storage, invite: i64) {
    // Only the owned fixture clock fields move. Production delays and delivery
    // generation/authorization checks are still executed, without a sleep race.
    run(sqlx::query("UPDATE invites SET created=datetime('now','-4 seconds'),delivered_at=NULL WHERE id=?")
        .bind(invite).execute(&storage.pool)).unwrap().unwrap();
}
fn follow_participant_target(storage: &Arc<Storage>, client: &mut quazal::ClientInfo<()>, leader: u32) -> (u32, u32) {
    let request = SearchSessionsWithParticipantsRequest { game_session_type_id: 1, participant_ids: QList(vec![leader]) }.to_bytes();
    let found = SearchSessionsWithParticipantsResponse::from_bytes(&call_room(storage, client, 42, 24, request).unwrap()).unwrap();
    assert_eq!(found.search_results.0.len(), 1, "accepted follow must route to its exact live target");
    let row = &found.search_results.0[0];
    assert!(row.participant_ids.0.contains(&leader));
    let key = &row.game_session_search_result.session_key;
    (key.type_id, key.session_id)
}

fn party_and_solo_follow_from_empty(mode: i64) {
    let (storage, [mut leader, mut member, mut solo], api) = follow_route_fixture(mode, "ordered");
    let target = create_public_room(&storage, &mut leader);
    let follow = follow_row(&storage, 2, target);
    assert_eq!((follow.1, follow.2), (false, false));
    // A real publication is already available from the leader's lobby. The
    // follow transport intentionally preserves that payload's published ID.
    let solo_found = discover_all_rooms(&storage, &mut solo);
    assert_eq!(solo_found.len(), 1);
    JoinSessionResponse::from_bytes(&call_room(&storage, &mut solo, 42, 22, join_request(solo_found[0].key)).unwrap()).unwrap();
    make_follow_delivery_due(&storage, follow.0);
    let delivered = api.event(2).invite.unwrap();
    assert_eq!((delivered.id, delivered.kind, delivered.force_join), (follow.0, 3, true));
    assert!(delivered.game_session.as_ref().is_some_and(|session| session.data == vec![0x5a; 496]));
    assert!(api.accept(2, delivered.id).unwrap().accepted);
    let returned_target = follow_participant_target(&storage, &mut member, 1);
    assert_eq!(returned_target, target);
    JoinSessionResponse::from_bytes(&call_room(&storage, &mut member, 42, 22, join_request(returned_target)).unwrap()).unwrap();
    assert_eq!(storage.game_session_diagnostics(target.0, target.1).unwrap().unwrap().2, vec![1, 2, 3]);
    assert_eq!(follow_row(&storage, 2, target), (follow.0, true, true));
    assert!(api.event(2).invite.is_none());
    let roster = api.event(2).party.unwrap();
    assert_eq!(roster.members.len(), 2);
    println!("QUICK_MATCH_FOLLOW_ROUTE mode={mode} schedule=ordered initial_game_rooms=0 actual_misc_event_accept=true actual_search_join=true party_plus_solo_final_members=3 native_event_delivery=false retail_order_verified=false live_game=false");
    run(storage.pool.close()).unwrap();
}

fn competing_member_join_consumes_pending_follow(mode: i64) {
    let (storage, [mut leader, mut member, mut solo], api) = follow_route_fixture(mode, "competing");
    // Both creators have already received an empty real search above. The test
    // explicitly supplies this overlapping order; it is not a retail replay.
    let leader_room = create_public_room(&storage, &mut leader);
    let other_room = create_public_room(&storage, &mut solo);
    assert_ne!(leader_room, other_room);
    let follow = follow_row(&storage, 2, leader_room);
    let member_found = discover_all_rooms(&storage, &mut member);
    assert_eq!(member_found.len(), 2);
    let alternate = member_found.iter().find(|room| room.host == 3).unwrap().key;
    JoinSessionResponse::from_bytes(&call_room(&storage, &mut member, 42, 22, join_request(alternate)).unwrap()).unwrap();
    assert_eq!(follow_row(&storage, 2, leader_room), (follow.0, false, true),
        "ordinary join consumes the pending follow even when selecting a different room");
    assert_eq!(storage.game_session_diagnostics(1, leader_room.1).unwrap().unwrap().2, vec![1]);
    assert_eq!(storage.game_session_diagnostics(1, other_room.1).unwrap().unwrap().2, vec![2, 3]);
    assert_eq!(api.event(2).party.unwrap().members.len(), 2, "same Party does not prove same game room");
    assert!(api.event(2).invite.is_none(), "no unconsumed follow remains to deliver");
    assert!(api.accept(2, follow.0).is_err(), "stale acknowledgement cannot restore a consumed generation");
    // A new explicit leader Join requeues a new generation. No background host
    // election or room merge is invented by this test.
    JoinSessionResponse::from_bytes(&call_room(&storage, &mut leader, 42, 22, join_request(leader_room)).unwrap()).unwrap();
    let replacement = follow_row(&storage, 2, leader_room);
    assert_ne!(replacement.0, follow.0);
    make_follow_delivery_due(&storage, replacement.0);
    let delivered = api.event(2).invite.unwrap();
    assert_eq!(delivered.id, replacement.0);
    assert!(api.accept(2, delivered.id).unwrap().accepted);
    let target = follow_participant_target(&storage, &mut member, 1);
    JoinSessionResponse::from_bytes(&call_room(&storage, &mut member, 42, 22, join_request(target)).unwrap()).unwrap();
    assert_eq!(storage.game_session_diagnostics(1, leader_room.1).unwrap().unwrap().2, vec![1, 2]);
    assert_eq!(storage.game_session_diagnostics(1, other_room.1).unwrap().unwrap().2, vec![2, 3],
        "Join to the leader does not retire another room membership automatically");
    let leave = LeaveSessionRequest { game_session_key: GameSessionKey { type_id: alternate.0, session_id: alternate.1 } }.to_bytes();
    LeaveSessionResponse::from_bytes(&call_room(&storage, &mut member, 42, 5, leave).unwrap()).unwrap();
    let solo_found = discover_all_rooms(&storage, &mut solo);
    let target = solo_found.iter().find(|room| room.host == 1).unwrap().key;
    JoinSessionResponse::from_bytes(&call_room(&storage, &mut solo, 42, 22, join_request(target)).unwrap()).unwrap();
    let leave = LeaveSessionRequest { game_session_key: GameSessionKey { type_id: other_room.0, session_id: other_room.1 } }.to_bytes();
    LeaveSessionResponse::from_bytes(&call_room(&storage, &mut solo, 42, 5, leave).unwrap()).unwrap();
    assert_eq!(storage.game_session_diagnostics(1, leader_room.1).unwrap().unwrap().2, vec![1, 2, 3]);
    assert!(storage.game_session_diagnostics(1, other_room.1).unwrap().is_none());
    println!("QUICK_MATCH_FOLLOW_ROUTE mode={mode} schedule=competing_ordinary_join pending_follow_consumed=true party_retained=true rooms_split=true leader_repeat_join_required_by_test=true new_follow_generation=true explicit_leave_required_by_test=true final_members=3 retail_order_verified=false live_issue_reproduced=false");
    run(storage.pool.close()).unwrap();
}

fn stale_target_after_search_does_not_join(mode: i64) {
    let (storage, [mut leader, mut member, mut solo], api) = follow_route_fixture(mode, "stale");
    let room = create_public_room(&storage, &mut leader);
    let follow = follow_row(&storage, 2, room);
    let found = discover_all_rooms(&storage, &mut solo);
    assert_eq!(found.len(), 1);
    let request = DeleteSessionRequest { game_session_key: GameSessionKey { type_id: room.0, session_id: room.1 } }.to_bytes();
    DeleteSessionResponse::from_bytes(&call_room(&storage, &mut leader, 42, 3, request).unwrap()).unwrap();
    assert!(matches!(call_room(&storage, &mut solo, 42, 22, join_request(found[0].key)), Err(Error::AccessDenied)));
    assert!(api.accept(2, follow.0).is_err());
    assert!(api.event(2).invite.is_none());
    assert!(discover_all_rooms(&storage, &mut member).is_empty());
    println!("QUICK_MATCH_FOLLOW_ROUTE mode={mode} schedule=target_destroyed_after_search join_denied=true follow_denied=true search_not_admission=true live_game=false");
    run(storage.pool.close()).unwrap();
}

#[test]
fn quick_match_protocol_official_party_plus_solo_follow_from_empty() { party_and_solo_follow_from_empty(1); }
#[test]
fn quick_match_protocol_mod_party_plus_solo_follow_from_empty() { party_and_solo_follow_from_empty(2); }
#[test]
fn quick_match_protocol_official_competing_member_join_consumes_pending_follow() { competing_member_join_consumes_pending_follow(1); }
#[test]
fn quick_match_protocol_mod_competing_member_join_consumes_pending_follow() { competing_member_join_consumes_pending_follow(2); }
#[test]
fn quick_match_protocol_official_stale_target_after_search_does_not_join() { stale_target_after_search_does_not_join(1); }
#[test]
fn quick_match_protocol_mod_stale_target_after_search_does_not_join() { stale_target_after_search_does_not_join(2); }
