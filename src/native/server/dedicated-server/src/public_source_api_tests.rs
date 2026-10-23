//! Synthetic in-process credentials and kernel-peer metadata; no Public startup.
use super::*;
use server_api::gameplay_contract::{ACCOUNT_PRODUCT,GAME_INSTANCE_HEADER,GameplayMode};
use tonic::transport::server::TcpConnectInfo;
const RECOVERY: &str = "AAAAAAAAAAAAAAAAAAAAAAAA";
const RUN: &str = "0123456789abcdef0123456789abcdef";
fn policy() -> SourcePolicy { SourcePolicy::public_overlay("10.77.0.1".parse().unwrap(),24).unwrap() }
fn runtime() -> tokio::runtime::Runtime { tokio::runtime::Builder::new_current_thread().enable_all().build().unwrap() }
fn config() -> AuthTokenConfig { AuthTokenConfig {release:test_release_identity(),key:hmacsha256::Key([0x43;32]),ttl_seconds:86400} }
fn logger() -> Logger { Logger::root(slog::Discard,slog::o!()) }
fn source<T>(ip:&str, value:T) -> Request<T> {
    let mut request=Request::new(value);
    request.extensions_mut().insert(TcpConnectInfo {local_addr:Some("10.77.0.1:50051".parse().unwrap()),remote_addr:Some(SocketAddr::new(ip.parse().unwrap(),30500))});
    request.metadata_mut().insert("x-forwarded-for","10.77.0.2".parse().unwrap());
    request.metadata_mut().insert("x-real-ip","10.77.0.2".parse().unwrap());
    request
}
fn login(ip:&str, game:bool) -> Request<users::LoginRequest> {
    let mut request=source(ip,users::LoginRequest {client_product:if game {GameplayMode::Official.product().into()} else {ACCOUNT_PRODUCT.into()},
        username:"source-fixture".into(),password:"synthetic-password".into(),package_identity:Some(test_package_identity())});
    if game {request.metadata_mut().insert(GAME_INSTANCE_HEADER,RUN.parse().unwrap());}
    request
}
async fn seed(pool:&sqlx::SqlitePool) {
    sqlx::query("INSERT INTO users(id,username,password,ubi_id,recovery_code_hash,last_login) VALUES(1000,'source-fixture','synthetic-password','source-account',?,'2000-01-01 00:00:00')")
        .bind(hash_recovery_code(RECOVERY)).execute(pool).await.unwrap();
}
async fn snapshot(pool:&sqlx::SqlitePool)->((String,i64,Option<String>,Option<String>),i64,i64,i64) {
    let user=sqlx::query_as("SELECT password,auth_version,last_login,recovery_code_hash FROM users WHERE id=1000").fetch_one(pool).await.unwrap();
    let users=sqlx::query_scalar("SELECT COUNT(*) FROM users").fetch_one(pool).await.unwrap();
    let runs=sqlx::query_scalar("SELECT COUNT(*) FROM game_runs").fetch_one(pool).await.unwrap();
    let tickets=sqlx::query_scalar("SELECT COUNT(*) FROM user_sessions").fetch_one(pool).await.unwrap();
    (user,users,runs,tickets)
}

#[test]
fn forwarded_headers_missing_peer_and_ipv6_cannot_select_public_source() {
    let mut absent=Request::new(());
    absent.metadata_mut().insert("x-forwarded-for","10.77.0.2".parse().unwrap());
    assert!(request_source_ip(&absent,policy()).is_err());
    for ip in ["10.78.0.2","10.77.0.0","10.77.0.255","127.0.0.1","::ffff:10.77.0.2"] {
        assert!(request_source_ip(&source(ip,()),policy()).is_err());
    }
    let mut allowed=source("10.77.0.2",());
    allowed.metadata_mut().insert("x-forwarded-for","8.8.8.8".parse().unwrap());
    assert_eq!(request_source_ip(&allowed,policy()).unwrap(),"10.77.0.2".parse::<IpAddr>().unwrap());
}

#[test]
fn rejected_public_users_methods_leave_real_credentials_runs_and_tickets_unchanged() {
    let(storage,pool)=crate::storage::release_test_support::fixture();
    runtime().block_on(async {
        seed(&pool).await;
        let users=MyUsers{logger:logger(),token_config:config(),storage,overlay_network:policy()};
        let before=snapshot(&pool).await;
        for ip in ["10.78.0.2","10.77.0.0","10.77.0.255","127.0.0.1","::ffff:10.77.0.2"] {
            for game in [false,true] { assert_eq!(users.login(login(ip,game)).await.unwrap_err().code(),tonic::Code::Unauthenticated); }
            let registration=source(ip,users::RegisterRequest {client_product:ACCOUNT_PRODUCT.into(),username:"never-created".into(),password:"fixture".into(),ubi_id:"never-created".into(),package_identity:Some(test_package_identity())});
            assert_eq!(users.register(registration).await.unwrap_err().code(),tonic::Code::Unauthenticated);
            let reset=source(ip,users::ResetPasswordRequest {username:"source-fixture".into(),recovery_code:RECOVERY.into(),new_password:"never-applied".into()});
            assert_eq!(users.reset_password(reset).await.unwrap_err().code(),tonic::Code::Unauthenticated);
            assert_eq!(users.get_capabilities(source(ip,users::CapabilitiesRequest{})).await.unwrap_err().code(),tonic::Code::Unauthenticated);
            assert_eq!(snapshot(&pool).await,before);
        }
    });
}

#[test]
fn admitted_public_game_keeps_release_token_and_current_run_source_binding() {
    let(storage,pool)=crate::storage::release_test_support::fixture();
    runtime().block_on(async {
        seed(&pool).await;
        let users=MyUsers{logger:logger(),token_config:config(),storage:storage.clone(),overlay_network:policy()};
        let token=users.login(login("10.77.0.2",true)).await.unwrap().into_inner().token;
        let request=|ip| {let mut r=source(ip,());r.metadata_mut().insert("authorization",token.parse().unwrap());r};
        for ip in ["10.78.0.2","10.77.0.3"] {
            let before=snapshot(&pool).await;
            assert_eq!(check_token(&logger(),&config(),&storage,policy(),request(ip)).await.unwrap_err().code(),tonic::Code::Unauthenticated);
            assert_eq!(snapshot(&pool).await,before);
        }
        let mut admitted=check_token(&logger(),&config(),&storage,policy(),request("10.77.0.2")).await.unwrap();
        assert_eq!(authenticated_game_mode(&storage,&admitted).await.unwrap(),1);
        assert_eq!(verified_source(&admitted).unwrap(),"10.77.0.2".parse::<IpAddr>().unwrap());
        admitted.extensions_mut().insert(TcpConnectInfo {local_addr:None,remote_addr:Some("10.77.0.3:30500".parse().unwrap())});
        assert_eq!(authenticated_game_mode(&storage,&admitted).await.unwrap_err().code(),tonic::Code::Unauthenticated);
        assert!(verified_source(&source("10.77.0.2",())).is_err());
    });
}

#[test]
fn public_incoming_drops_real_loopback_connection_before_handing_it_to_tonic() {
    use tokio_stream::StreamExt;
    use tokio::io::AsyncReadExt;
    runtime().block_on(async {
        let incoming=api_incoming(&quazal::socket_policy::SocketPolicy::lan(),"127.0.0.1:0".parse().unwrap()).unwrap();
        let address=incoming.local_addr().unwrap();
        let mut filtered=source_filtered_incoming(incoming,policy());
        let mut client=tokio::net::TcpStream::connect(address).await.unwrap();
        // Timeout cancels only the pending next accept after the rejected peer.
        assert!(tokio::time::timeout(std::time::Duration::from_millis(100),filtered.next()).await.is_err());
        let read=tokio::time::timeout(std::time::Duration::from_secs(2),client.read(&mut [0;1])).await.unwrap();
        assert!(matches!(read,Ok(0)) || read.is_err());
    });
}
