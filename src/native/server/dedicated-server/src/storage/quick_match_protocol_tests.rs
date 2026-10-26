// Exercise the actual serialized room protocols with independent authenticated
// clients. This is a server-side convergence contract, not a replay of the user's
// game requests or proof that native P2P matchmaking succeeds.
// A compatible room is already advertised: empty-lobby find/create races and
// native client selection are outside this test's scope.
mod quick_match_protocol {
    use super::*;
    use crate::protocols::game_session_ex_service::game_session_ex_protocol::{SearchSessionsRequest, SearchSessionsResponse};
    use crate::protocols::game_session_service::game_session_protocol::{CreateSessionRequest, CreateSessionResponse, JoinSessionRequest, JoinSessionResponse};
    use crate::protocols::game_session_service::types::{GameSession as NativeSession, GameSessionKey, GameSessionQuery};
    use quazal::rmc::basic::{FromStream, ToStream};
    use quazal::rmc::types::{Property, QList};
    use quazal::rmc::{Error, Protocol, Request};

    fn same_room(mode: i64) {
        let storage = Arc::new(create_test_storage("quick-match-two-client-protocol"));
        insert_accounts(&storage, 5);
        for user in 1..=5 {
            maintenance_begin_run(&storage, user, &format!("{user:032x}"), if user == 5 { 3 - mode } else { mode });
            storage.register_urls(user, vec![format!("prudp:/address=192.0.2.{user};port=13000;sid={user};type=2")]).unwrap();
        }
        // Seed a genuine social Party through the existing invitation/admission
        // methods. User 1 is an independent match host; 2 leads Party [2,3].
        let lobby = storage.create_game_session(2, 1, "113 => 1;3 => 8;4 => 0".into()).unwrap();
        let invitation = run(storage.add_invite_async(2, 3, INVITE_KIND_LOBBY_PARTY, 1, lobby)).unwrap().unwrap().unwrap();
        assert!(run(storage.accept_invite_async(3, invitation)).unwrap().unwrap().is_some());
        assert!(storage.join_game_session_unbounded(3, 1, lobby).unwrap());

        let rooms = crate::game_session::new_protocol::<()>(storage.clone());
        let search = crate::game_session_ex::new_protocol::<()>(storage.clone());
        let logger = Logger::root(slog::Discard, slog::o!());
        let context = quazal::Context::splinter_cell_blacklist();
        let registry = quazal::prudp::ClientRegistry::<()>::default();
        let socket = std::net::UdpSocket::bind("127.0.0.1:0").unwrap();
        let call = |protocol: &dyn Protocol<()>, client: &mut quazal::ClientInfo<()>, method_id, parameters| {
            protocol.handle(&logger, &context, client,
                &Request { protocol_id: protocol.id(), call_id: 1, method_id, parameters }, &registry, &socket)
        };
        let create = |user, private| {
            let mut client = maintenance_native_client(&storage, user);
            let parameters = CreateSessionRequest { game_session: NativeSession { type_id: 1,
                attributes: QList(vec![Property { id: 113, value: 0 }, Property { id: 3, value: if private { 0 } else { 8 } },
                    Property { id: 4, value: if private { 8 } else { 0 } }, Property { id: 103, value: 2_165_463_540 },
                    Property { id: 105, value: 0 }, Property { id: 112, value: 8 }]) } }.to_bytes();
            CreateSessionResponse::from_bytes(&call(rooms.as_ref(), &mut client, 1, parameters).unwrap()).unwrap().game_session_key.session_id
        };
        let public = create(1, false);
        let private = create(4, true);
        let incompatible = create(5, false);
        let mut leader = maintenance_native_client(&storage, 2);
        let mut follower = maintenance_native_client(&storage, 3);
        assert_ne!(leader.ticket_fingerprint, follower.ticket_fingerprint);

        let discover = |client: &mut quazal::ClientInfo<()>| {
            // Deliberately use a concrete supported query. Retail capture is
            // absent; this does not assume all quick-match UI requests use it.
            let parameters = SearchSessionsRequest { game_session_query: GameSessionQuery { type_id: 1, query_id: 0,
                parameters: QList(vec![Property { id: 113, value: 0 }, Property { id: 103, value: 2_165_463_540 },
                    Property { id: 105, value: 0 }, Property { id: 112, value: 2 }]) } }.to_bytes();
            let response = SearchSessionsResponse::from_bytes(&call(search.as_ref(), client, 1, parameters).unwrap()).unwrap();
            assert_eq!(response.search_results.0.len(), 1, "only the compatible public room must be discoverable");
            let found = &response.search_results.0[0];
            let key = &found.game_session_search_result.session_key;
            assert_ne!(key.session_id, private);
            assert_ne!(key.session_id, incompatible);
            assert_eq!(found.game_session_search_result.host_pid, 1);
            assert_eq!(found.game_session_search_result.host_urls.0.len(), 1);
            assert_eq!(found.game_session_search_result.host_urls.0[0].address, "192.0.2.1");
            assert!(found.participants.0.iter().any(|peer| peer.pid == 1));
            (key.type_id, key.session_id)
        };
        // Both clients search before either joins: membership must converge on
        // the actual returned keys rather than a test-supplied room selection.
        let leader_target = discover(&mut leader);
        let follower_target = discover(&mut follower);
        assert_eq!(leader_target, follower_target);
        assert_eq!(leader_target, (1, public));
        let join = |client: &mut quazal::ClientInfo<()>, target: (u32, u32)| {
            call(rooms.as_ref(), client, 22, JoinSessionRequest { game_session_key: GameSessionKey {
                type_id: target.0, session_id: target.1 } }.to_bytes())
        };
        assert!(matches!(join(&mut leader, (1, incompatible)), Err(Error::AccessDenied)));
        JoinSessionResponse::from_bytes(&join(&mut leader, leader_target).unwrap()).unwrap();
        let queued: Vec<(u32, u32, u32, u32)> = run(sqlx::query_as(
            "SELECT sender,receiver,session_type,session_id FROM invites WHERE kind=? AND consumed_at IS NULL ORDER BY receiver")
            .bind(INVITE_KIND_PARTY_FOLLOW).fetch_all(&storage.pool)).unwrap().unwrap();
        assert_eq!(queued, vec![(2, 3, leader_target.0, leader_target.1)], "Party follow must target the same discovered match");
        JoinSessionResponse::from_bytes(&join(&mut follower, follower_target).unwrap()).unwrap();
        // A repeated native acknowledgment cannot duplicate or split members.
        JoinSessionResponse::from_bytes(&join(&mut follower, follower_target).unwrap()).unwrap();
        assert_eq!(storage.game_session_diagnostics(1, public).unwrap().unwrap().2, vec![1, 2, 3]);
        assert_eq!(storage.game_session_diagnostics(1, incompatible).unwrap().unwrap().2, vec![5]);
        assert_eq!(storage.game_session_diagnostics(1, private).unwrap().unwrap().2, vec![4]);
        assert_eq!(discover(&mut leader), leader_target);
        assert_eq!(discover(&mut follower), follower_target);
        let (party_leader, roster) = run(storage.find_party_roster_async(3)).unwrap().unwrap().unwrap();
        let mut members = roster.into_iter().map(|row| row.id).collect::<Vec<_>>();
        members.sort_unstable();
        assert_eq!((party_leader, members), (2, vec![2, 3]), "joining another host must preserve the social Party");
        let pending: i64 = run(sqlx::query_scalar("SELECT COUNT(*) FROM invites WHERE receiver=3 AND kind=? AND consumed_at IS NULL")
            .bind(INVITE_KIND_PARTY_FOLLOW).fetch_one(&storage.pool)).unwrap().unwrap();
        assert_eq!(pending, 0, "actual follower join must consume the queued fallback");
        println!("QUICK_MATCH_PROTOCOL mode={mode} independent_clients=2 same_returned_key=true final_native_roster=3 party_preserved=true private_hidden=true incompatible_hidden_and_join_denied=true follow_target_verified=true real_udp_dispatch=false notification_delivery=false live_game=false user_report_reproduced=false");
        run(storage.pool.close()).unwrap();
    }

    #[test]
    fn quick_match_protocol_official_two_clients_same_returned_session() { same_room(1); }
    #[test]
    fn quick_match_protocol_mod_two_clients_same_returned_session() { same_room(2); }
}
