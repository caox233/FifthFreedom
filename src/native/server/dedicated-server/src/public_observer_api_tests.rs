//! Invoked only by the isolated real owner-lease test child after ADMIT.
//! Private synthetic SQLite and package inputs; no network/game/bridge startup.
use super::*;
use server_api::gameplay_contract::{ACCOUNT_PRODUCT, GAME_INSTANCE_HEADER, GameplayMode};
use crate::observer_policy::{self, OwnedPublicPolicy};

fn source<T>(value:T) -> Request<T> {
    let mut request=Request::new(value);
    request.extensions_mut().insert(tonic::transport::server::TcpConnectInfo {
        local_addr:Some("10.77.0.1:50051".parse().unwrap()),remote_addr:Some("10.77.0.2:30500".parse().unwrap())});
    request
}
fn package(config:&AuthTokenConfig) -> users::PackageIdentity {
    let key=config.release.storage_key();let fields:Vec<_>=key.split('|').collect();
    let role=Some(users::ReleaseComponent{release_id:fields[1].into(),compat_id:fields[2].into()});
    users::PackageIdentity{schema:1,release_id:fields[1].into(),compat_id:fields[2].into(),manifest_sha256:fields[3].into(),
        launcher:role.clone(),main_dll:role.clone(),gameplay_dll:role.clone(),server:role}
}
fn login(config:&AuthTokenConfig,product:&str,nonce:&str,observer:bool) -> Request<users::LoginRequest> {
    let mut request=source(users::LoginRequest{client_product:product.into(),username:"observer-fixture".into(),password:"synthetic-password".into(),package_identity:Some(package(config))});
    if product!=ACCOUNT_PRODUCT {request.metadata_mut().insert(GAME_INSTANCE_HEADER,nonce.parse().unwrap());}
    if observer {request.metadata_mut().insert(observer_policy::HEADER,"1".parse().unwrap());}
    request
}
async fn snapshot(pool:&sqlx::SqlitePool) -> (Option<String>,i64,i64) {
    (sqlx::query_scalar("SELECT last_login FROM users WHERE id=1000").fetch_one(pool).await.unwrap(),
     sqlx::query_scalar("SELECT COUNT(*) FROM game_runs").fetch_one(pool).await.unwrap(),
     sqlx::query_scalar("SELECT COUNT(*) FROM user_sessions").fetch_one(pool).await.unwrap())
}

pub(crate) fn exercise(config:AuthTokenConfig,allowed:bool,policy:OwnedPublicPolicy) {
    let logger=Logger::root(slog::Discard,slog::o!());
    let storage=Arc::new(Storage::init(logger.clone(),config.release.clone()).unwrap());
    let network=SourcePolicy::public_overlay("10.77.0.1".parse().unwrap(),24).unwrap();
    let users=MyUsers{logger:logger.clone(),storage:storage.clone(),token_config:config.clone(),overlay_network:network};
    tokio::runtime::Builder::new_current_thread().enable_all().build().unwrap().block_on(async {
        let pool=sqlx::SqlitePool::connect("sqlite://5th-echelon.db").await.unwrap();
        sqlx::query("INSERT INTO users(id,username,password,ubi_id,last_login) VALUES(1000,'observer-fixture','synthetic-password','observer-account','2000-01-01 00:00:00')").execute(&pool).await.unwrap();
        assert_eq!(users.get_capabilities(source(users::CapabilitiesRequest{})).await.unwrap().get_ref().observer_allowed,allowed);
        let before=snapshot(&pool).await;
        let error=users.login(login(&config,ACCOUNT_PRODUCT,"",true)).await.unwrap_err();
        assert_eq!(error.code(),tonic::Code::InvalidArgument);assert_eq!(snapshot(&pool).await,before);
        for product in [GameplayMode::Official.product(),GameplayMode::Mod.product()] {
            let request=login(&config,product,"11111111111111111111111111111111",true);
            if !allowed {
                // This is the same request a Python bridge/client can send;
                // neither requested OB nor a valid package changes owner policy.
                assert_eq!(users.login(request).await.unwrap_err().code(),tonic::Code::PermissionDenied);
                assert_eq!(snapshot(&pool).await,before);
            }
        }
        let ordinary=users.login(login(&config,ACCOUNT_PRODUCT,"",false)).await.unwrap();
        let ordinary=verify_auth_token(&config,&ordinary.get_ref().token,"10.77.0.2".parse().unwrap(),unix_time_seconds().unwrap()).unwrap();
        assert_eq!(ordinary.observer_admitted_run,None);assert_eq!(ordinary.game_run,None);
        if allowed {
            let nonce="22222222222222222222222222222222";
            let response=users.login(login(&config,GameplayMode::Mod.product(),nonce,true)).await.unwrap();
            assert_eq!(response.metadata().get(server_api::observer_contract::ADMITTED_RUN_HEADER).unwrap(),nonce);
            let claims=verify_auth_token(&config,&response.get_ref().token,"10.77.0.2".parse().unwrap(),unix_time_seconds().unwrap()).unwrap();
            assert_eq!(claims.observer_admitted_run.as_deref(),Some(nonce));
            assert_eq!(claims.game_run.as_ref().unwrap().instance,nonce);
            let mut authenticated=source(());authenticated.metadata_mut().insert("authorization",response.get_ref().token.parse().unwrap());
            let admitted=check_token(&logger,&config,&storage,network,authenticated).await.unwrap();
            assert_eq!(authenticated_game_mode(&storage,&admitted).await.unwrap(),2);
            // Existing LAN-style in-flight revocation guards are shared by this
            // path. No owner config reload is introduced by these test controls.
            let before_runs=snapshot(&pool).await.1;
            storage.set_login_checkpoint("publication",Box::new(move||drop(policy)));
            assert_eq!(users.login(login(&config,GameplayMode::Mod.product(),"33333333333333333333333333333333",true)).await.unwrap_err().code(),tonic::Code::PermissionDenied);
            assert_eq!(snapshot(&pool).await.1,before_runs);
            let policy=observer_policy::owned_public(true);
            storage.set_login_checkpoint("observer-publication",Box::new(move||drop(policy)));
            // A constructed signed token must still not be returned once the
            // final host check closes; this does not promise DB rollback.
            assert_eq!(users.login(login(&config,GameplayMode::Mod.product(),"44444444444444444444444444444444",true)).await.unwrap_err().code(),tonic::Code::PermissionDenied);
        } else {drop(policy);}
        assert!(!observer_policy::allowed());
        assert!(!users.get_capabilities(source(users::CapabilitiesRequest{})).await.unwrap().get_ref().observer_allowed);
        let player=users.login(login(&config,GameplayMode::Official.product(),"55555555555555555555555555555555",false)).await.unwrap();
        assert!(player.metadata().get(server_api::observer_contract::ADMITTED_RUN_HEADER).is_none());
        assert_eq!(verify_auth_token(&config,&player.get_ref().token,"10.77.0.2".parse().unwrap(),unix_time_seconds().unwrap()).unwrap().observer_admitted_run,None);
        pool.close().await;
    });
}
