//! Test-only bridge to the actual Misc handlers. Owned synthetic signatures and
//! source metadata model the verified interceptor boundary; no HTTP listener,
//! retail payload, player account or native game is started by this fixture.
use super::*;

pub(crate) struct MatchingApi {
    service: MyMisc,
    runtime: tokio::runtime::Runtime,
    mode: i64,
}
impl MatchingApi {
    pub(crate) fn new(storage: Arc<Storage>, mode: i64) -> Self {
        assert!(matches!(mode, 1 | 2));
        Self {
            service: MyMisc {
                logger: Logger::root(slog::Discard, slog::o!()), storage,
                debug_config: Arc::new(DebugConfig::default()),
                uplay_game_sessions: Arc::new(RwLock::new(UplayGameSessionRegistry::default())),
                socket_policy: quazal::socket_policy::SocketPolicy::lan(),
            },
            // SQLx may schedule connection cleanup after the handler returns.
            // Keep an owned worker alive while synchronous protocol calls and
            // final pool.close run, just as the existing Misc fixtures do.
            runtime: tokio::runtime::Builder::new_multi_thread().worker_threads(2).enable_all().build().unwrap(), mode,
        }
    }
    fn request<T>(&self, user: u32, value: T) -> Request<T> {
        let now = unix_time_seconds().unwrap();
        let config = AuthTokenConfig { release: test_release_identity(), key: hmacsha256::Key([0x51; 32]), ttl_seconds: 86400 };
        let claims = AuthTokenClaims {
            release: Some(test_release_identity()), user_id: user, username: format!("user-{user}"),
            source_ip: "127.0.0.1".into(), issued_at: now, expires_at: now + 86400, auth_version: 1,
            game_run: Some(GameRunClaim {
                product: format!("scbl-lan/2/game/{}", if self.mode == 1 { "official" } else { "mod" }),
                instance: format!("{user:032x}"),
            }), observer_admitted_run: None,
        };
        let token = issue_auth_token(&config, &claims).unwrap();
        let verified = verify_auth_token(&config, &token, "127.0.0.1".parse().unwrap(), now).unwrap();
        let mut request = Request::new(value);
        request.extensions_mut().insert(tonic::transport::server::TcpConnectInfo {
            local_addr: Some("127.0.0.1:50051".parse().unwrap()), remote_addr: Some("127.0.0.1:30500".parse().unwrap()),
        });
        apply_verified_identity(&mut request, &verified);
        request.extensions_mut().insert(VerifiedSource { policy: SourcePolicy::lan(), ip: "127.0.0.1".parse().unwrap() });
        request
    }
    pub(crate) fn publish(&self, user: u32, session: u32) {
        self.runtime.block_on(self.service.set_game_session(self.request(user, misc::SetGameSessionRequest {
            id: u64::from(session), data: vec![0x5a; 496], flags: 7, invite_only: false,
        }))).unwrap();
    }
    pub(crate) fn event(&self, user: u32) -> misc::EventResponse {
        self.runtime.block_on(self.service.event(self.request(user, misc::EventRequest {}))).unwrap().into_inner()
    }
    pub(crate) fn accept(&self, user: u32, id: i64) -> Result<misc::AcceptInviteResponse, tonic::Status> {
        self.runtime.block_on(self.service.accept_invite(self.request(user, misc::AcceptInviteRequest { id })))
            .map(Response::into_inner)
    }
}
