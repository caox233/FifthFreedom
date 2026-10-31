//! Synthetic storage/ClientInfo inputs exercise the actual native ticket cores.
use super::*;
use quazal::rmc::basic::ToStream;
use quazal::rmc::types::{Any,Data};
fn login_ex() -> LoginExRequest {
    let data=UbiAuthenticationLoginCustomData {data:Data,user_name:"source-ticket".into(),online_key:"synthetic".into(),password:"synthetic-password".into()};
    LoginExRequest {str_user_name:"source-ticket".into(),o_extra_data:Any::new("UbiAuthenticationLoginCustomData".into(),data.to_bytes())}
}

#[test]
fn public_ticket_cores_deny_outside_peer_before_writes_and_preserve_run_source_binding() {
    let(storage,pool)=crate::storage::release_test_support::fixture();
    let runtime=tokio::runtime::Builder::new_current_thread().enable_all().build().unwrap();
    runtime.block_on(sqlx::query("INSERT INTO users(id,username,password,ubi_id,last_login) VALUES(1000,'source-ticket','synthetic-password','source-ticket-id','2000-01-01 00:00:00')").execute(&pool)).unwrap();
    let logger=slog::Logger::root(slog::Discard,slog::o!());
    let ctx=Context::splinter_cell_blacklist();
    let service=TicketGrantingProtocolServerImpl {storage:storage.clone(),source_policy:quazal::source_policy::SourcePolicy::public_overlay("10.77.0.1".parse().unwrap(),24).unwrap()};
    let snapshot=|| runtime.block_on(async {
        let last:Option<String>=sqlx::query_scalar("SELECT last_login FROM users WHERE id=1000").fetch_one(&pool).await.unwrap();
        let tickets:i64=sqlx::query_scalar("SELECT COUNT(*) FROM user_sessions").fetch_one(&pool).await.unwrap();
        (last,tickets)
    });
    let before=snapshot();
    for address in ["10.78.0.2:30500","10.77.0.0:30500","10.77.0.255:30500","127.0.0.1:30500","[::ffff:10.77.0.2]:30500"] {
        let mut client=quazal::ClientInfo::<()>::new(address.parse().unwrap());
        assert!(matches!(service.login_core(&logger,&ctx,&mut client,LoginRequest{str_user_name:"source-ticket".into()}),Err(quazal::rmc::Error::AccessDenied)));
        assert!(matches!(service.login_ex_core(&logger,&ctx,&mut client,login_ex()),Err(quazal::rmc::Error::AccessDenied)));
        assert_eq!(client.user_id,None);
        client.user_id=Some(1000);
        assert!(matches!(service.request_ticket_core(&logger,&ctx,&mut client,RequestTicketRequest{id_source:1000,id_target:SERVER_PID}),Err(quazal::rmc::Error::AccessDenied)));
        assert_eq!(client.user_id,Some(1000));
        assert_eq!(snapshot(),before);
    }
    // Being in the CIDR still does not authorize a ticket without the admitted
    // game run, exact source, package identity and current account credentials.
    runtime.block_on(storage.begin_game_run_with_release(1000,"0123456789abcdef0123456789abcdef",1,
        "10.77.0.2".parse().unwrap(),&crate::api::test_release_identity())).unwrap();
    let mut wrong=quazal::ClientInfo::<()>::new("10.77.0.3:30500".parse().unwrap());
    assert!(service.login_ex_core(&logger,&ctx,&mut wrong,login_ex()).is_err());
    assert_eq!(wrong.user_id,None);
    let mut admitted=quazal::ClientInfo::<()>::new("10.77.0.2:30500".parse().unwrap());
    assert!(service.login_ex_core(&logger,&ctx,&mut admitted,login_ex()).is_ok());
    assert_eq!(admitted.user_id,Some(1000));
    assert!(service.request_ticket_core(&logger,&ctx,&mut admitted,RequestTicketRequest{id_source:1000,id_target:SERVER_PID}).is_ok());
}
