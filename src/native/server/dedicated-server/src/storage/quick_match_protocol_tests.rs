// Exercise the actual serialized room protocols with independent authenticated
// clients. This is a server-side convergence contract, not a replay of the user's
// game requests or proof that native P2P matchmaking succeeds.
// A compatible room is already advertised: empty-lobby find/create races and
// native client selection are outside this test's scope.
mod quick_match_protocol {
    use super::*;
    use crate::protocols::game_session_ex_service::game_session_ex_protocol::{SearchSessionsRequest, SearchSessionsResponse};
    use crate::protocols::game_session_service::game_session_protocol::{CreateSessionRequest, CreateSessionResponse, JoinSessionRequest, JoinSessionResponse, LeaveSessionRequest, LeaveSessionResponse};
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

    // Construct the actual production wrapper on the calling thread. Protocol
    // trait objects are not Send; sharing Storage is the production state seam.
    fn call_room(storage: &Arc<Storage>, client: &mut quazal::ClientInfo<()>, protocol_id: u16, method_id: u32, parameters: Vec<u8>) -> std::result::Result<Vec<u8>, Error> {
        let protocol = match protocol_id {
            42 => crate::game_session::new_protocol::<()>(storage.clone()),
            123 => crate::game_session_ex::new_protocol::<()>(storage.clone()),
            _ => panic!("unexpected room protocol"),
        };
        let logger = Logger::root(slog::Discard, slog::o!());
        let context = quazal::Context::splinter_cell_blacklist();
        let registry = quazal::prudp::ClientRegistry::<()>::default();
        let socket = std::net::UdpSocket::bind("127.0.0.1:0").unwrap();
        protocol.handle(&logger, &context, client,
            &Request { protocol_id, call_id: 1, method_id, parameters }, &registry, &socket)
    }

    fn search_room(storage: &Arc<Storage>, client: &mut quazal::ClientInfo<()>) -> ((u32, u32), Vec<u32>) {
        let parameters = SearchSessionsRequest { game_session_query: GameSessionQuery { type_id: 1, query_id: 0,
            parameters: QList(vec![Property { id: 113, value: 0 }, Property { id: 103, value: 2_165_463_540 }]) } }.to_bytes();
        let response = SearchSessionsResponse::from_bytes(&call_room(storage, client, 123, 1, parameters).unwrap()).unwrap();
        assert_eq!(response.search_results.0.len(), 1);
        let row = &response.search_results.0[0];
        assert_eq!(row.game_session_search_result.host_pid, 1);
        assert_eq!(row.game_session_search_result.host_urls.0.len(), 1);
        let mut members = row.participants.0.iter().map(|peer| peer.pid).collect::<Vec<_>>();
        members.sort_unstable();
        let key = &row.game_session_search_result.session_key;
        ((key.type_id, key.session_id), members)
    }

    fn join_request(target: (u32, u32)) -> Vec<u8> {
        JoinSessionRequest { game_session_key: GameSessionKey { type_id: target.0, session_id: target.1 } }.to_bytes()
    }

    fn concurrent_existing_room(mode: i64, close_old_ticket: bool) {
        let storage = Arc::new(create_test_storage("quick-match-concurrent-protocol"));
        insert_accounts(&storage, 3);
        for user in 1..=3 {
            maintenance_begin_run(&storage, user, &format!("{user:032x}"), mode);
            storage.register_urls(user, vec![format!("prudp:/address=192.0.2.{user};port=13000;sid={user};type=2")]).unwrap();
        }
        let mut host = maintenance_native_client(&storage, 1);
        let parameters = CreateSessionRequest { game_session: NativeSession { type_id: 1,
            attributes: QList(vec![Property { id: 113, value: 0 }, Property { id: 103, value: 2_165_463_540 },
                Property { id: 3, value: 8 }, Property { id: 4, value: 0 }]) } }.to_bytes();
        let created = CreateSessionResponse::from_bytes(&call_room(&storage, &mut host, 42, 1, parameters).unwrap()).unwrap().game_session_key;
        let client_a = maintenance_native_client(&storage, 2);
        let client_b = maintenance_native_client(&storage, 3);
        let old_fingerprint = client_b.ticket_fingerprint;
        assert_ne!(client_a.ticket_fingerprint, old_fingerprint);

        let (mut healthy, mut old_client, replacement) = std::thread::scope(|scope| {
            let (discovered, found) = std::sync::mpsc::channel();
            let mut permits = Vec::new();
            let mut workers = Vec::new();
            for mut client in [client_a, client_b] {
                let state = storage.clone();
                let discovered = discovered.clone();
                let (proceed, permit) = std::sync::mpsc::channel();
                permits.push(proceed);
                workers.push(scope.spawn(move || {
                    let snapshot = search_room(&state, &mut client);
                    discovered.send((client.user_id.unwrap(), snapshot.clone())).unwrap();
                    // This timeout is a failure watchdog, not a timing assumption.
                    permit.recv_timeout(Duration::from_secs(10)).expect("join permit missing");
                    let result = call_room(&state, &mut client, 42, 22, join_request(snapshot.0));
                    (client, result)
                }));
            }
            let mut searches = (0..2).map(|_| found.recv_timeout(Duration::from_secs(10)).expect("search worker did not finish")).collect::<Vec<_>>();
            searches.sort_by_key(|(user, _)| *user);
            assert_eq!(searches[0], (2, ((created.type_id, created.session_id), vec![1])));
            assert_eq!(searches[1], (3, searches[0].1.clone()));
            let replacement = if close_old_ticket {
                // A replacement connection exists before the old one closes.
                // Exercise the same exact-ticket transaction as secure close,
                // not the cfg(test) whole-user compatibility helper.
                let fresh = maintenance_native_client(&storage, 3);
                assert_ne!(fresh.ticket_fingerprint, old_fingerprint);
                assert!(matches!(storage.try_close_secure_ticket(3, old_fingerprint).unwrap(), SecureCloseAttempt::Committed { preserved: true }));
                assert!(storage.native_game_run(3, old_fingerprint, "127.0.0.1".parse().unwrap()).unwrap().is_none());
                assert!(storage.native_game_run(3, fresh.ticket_fingerprint, fresh.address().ip()).unwrap().is_some());
                Some(fresh)
            } else { None };
            // Both real calls are scheduled from independent threads; no sleep
            // or copied SQL/filter algorithm chooses their target or result.
            for permit in permits { permit.send(()).unwrap(); }
            let mut results = workers.into_iter().map(|worker| worker.join().unwrap()).collect::<Vec<_>>();
            let (b, b_result) = results.pop().unwrap();
            let (a, a_result) = results.pop().unwrap();
            JoinSessionResponse::from_bytes(&a_result.unwrap()).unwrap();
            if close_old_ticket { assert!(matches!(b_result, Err(Error::AccessDenied))); }
            else { JoinSessionResponse::from_bytes(&b_result.unwrap()).unwrap(); }
            (a, b, replacement)
        });
        let target = (created.type_id, created.session_id);
        if close_old_ticket {
            assert_eq!(search_room(&storage, &mut healthy), (target, vec![1, 2]), "closed caller must not be persisted");
        }
        let mut remaining = if let Some(mut fresh) = replacement {
            assert_eq!(search_room(&storage, &mut fresh), (target, vec![1, 2]));
            JoinSessionResponse::from_bytes(&call_room(&storage, &mut fresh, 42, 22, join_request(target)).unwrap()).unwrap();
            assert!(matches!(call_room(&storage, &mut old_client, 42, 22, join_request(target)), Err(Error::AccessDenied)));
            fresh
        } else { old_client };
        assert_eq!(search_room(&storage, &mut healthy), (target, vec![1, 2, 3]));
        assert_eq!(search_room(&storage, &mut remaining), (target, vec![1, 2, 3]));
        // The protocol has LeaveSession, not a server matchmaking-queue Cancel.
        // A client's leave must not remove the other client or the room host.
        let leave = LeaveSessionRequest { game_session_key: GameSessionKey { type_id: target.0, session_id: target.1 } }.to_bytes();
        LeaveSessionResponse::from_bytes(&call_room(&storage, &mut healthy, 42, 5, leave).unwrap()).unwrap();
        assert_eq!(search_room(&storage, &mut remaining), (target, vec![1, 3]));
        assert_eq!(storage.game_session_diagnostics(target.0, target.1).unwrap().unwrap().2, vec![1, 3]);
        println!("QUICK_MATCH_CONCURRENT mode={mode} scheduled_join_threads=2 closed_old_ticket={close_old_ticket} replacement_control={close_old_ticket} same_returned_key=true serialized_rosters_verified=true leave_preserved_other_client=true queue_cancel=false real_udp_dispatch=false notification_delivery=false empty_lobby_convergence=false live_game=false user_report_reproduced=false");
        run(storage.pool.close()).unwrap();
    }

    #[test]
    fn quick_match_protocol_official_concurrent_existing_room_and_leave() { concurrent_existing_room(1, false); }
    #[test]
    fn quick_match_protocol_mod_concurrent_existing_room_and_leave() { concurrent_existing_room(2, false); }
    #[test]
    fn quick_match_protocol_official_closed_ticket_cannot_join_replacement_can() { concurrent_existing_room(1, true); }
    #[test]
    fn quick_match_protocol_mod_closed_ticket_cannot_join_replacement_can() { concurrent_existing_room(2, true); }
}
