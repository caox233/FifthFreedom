use std::collections::HashMap;
use std::future::Future;
use std::sync::Mutex;
use std::sync::OnceLock;

use server_api::friends::friends_client::FriendsClient;
use server_api::friends::InviteRequest;
use server_api::friends::ListRequest;
use server_api::misc::misc_client::MiscClient;
use server_api::misc::AcceptInviteRequest;
use server_api::misc::ClearGameSessionRequest;
use server_api::misc::EventRequest;
use server_api::misc::EventResponse;
use server_api::misc::SetGameSessionRequest;
use server_api::misc::UplayGameSession;
use server_api::users::users_client::UsersClient;
use server_api::users::LoginRequest;
use tonic::metadata::Ascii;
use tonic::metadata::MetadataValue;
use tracing::debug;
use tracing::error;
use tracing::info;
use tracing::instrument;

static TOKEN: Mutex<Option<MetadataValue<Ascii>>> = Mutex::new(None);
static CREDS: Mutex<Option<(String, String)>> = Mutex::new(None);

#[derive(Debug, Default)]
struct GameSessionPublishState {
    desired: Option<UplayGameSession>,
    desired_generation: u64,
    applied_generation: u64,
}

impl GameSessionPublishState {
    fn update(&mut self, desired: Option<UplayGameSession>) {
        self.desired = desired;
        self.desired_generation = self.desired_generation.saturating_add(1);
    }

    fn force_republish(&mut self) {
        if self.desired.is_some() {
            self.desired_generation = self.desired_generation.saturating_add(1);
        }
    }

    fn pending(&self) -> Option<(u64, Option<UplayGameSession>)> {
        (self.applied_generation != self.desired_generation).then(|| (self.desired_generation, self.desired.clone()))
    }

    fn mark_applied(&mut self, generation: u64) {
        self.applied_generation = self.applied_generation.max(generation);
    }
}

static GAME_SESSION_PUBLISH_STATE: Mutex<GameSessionPublishState> = Mutex::new(GameSessionPublishState {
    desired: None,
    desired_generation: 0,
    applied_generation: 0,
});
static GAME_SESSION_PUBLISH_LOCK: OnceLock<tokio::sync::Mutex<()>> = OnceLock::new();

struct InviteIdState {
    next_local_id: i64,
    server_to_current: HashMap<i64, (i64, bool)>,
    local_to_server: HashMap<i64, i64>,
}

impl InviteIdState {
    fn new() -> Self {
        Self {
            next_local_id: -1,
            server_to_current: HashMap::new(),
            local_to_server: HashMap::new(),
        }
    }

    fn allocate_local_id(&mut self) -> i64 {
        while self.local_to_server.contains_key(&self.next_local_id) {
            self.next_local_id = self.next_local_id.saturating_sub(1);
        }
        let local_id = self.next_local_id;
        self.next_local_id = self.next_local_id.saturating_sub(1);
        local_id
    }

    fn map_server_event(&mut self, server_id: i64) -> i64 {
        if let Some((local_id, retired)) = self.server_to_current.get(&server_id).copied() {
            if !retired {
                return local_id;
            }
        }

        let local_id = self.allocate_local_id();
        self.server_to_current.insert(server_id, (local_id, false));
        self.local_to_server.insert(local_id, server_id);
        local_id
    }

    fn server_id_for_local(&self, local_id: i64) -> i64 {
        self.local_to_server.get(&local_id).copied().unwrap_or(local_id)
    }

    fn retire_local(&mut self, local_id: i64) {
        let Some(server_id) = self.local_to_server.get(&local_id).copied() else {
            return;
        };
        if let Some((current_local_id, retired)) = self.server_to_current.get_mut(&server_id) {
            if *current_local_id == local_id {
                *retired = true;
            }
        }
    }
}

static INVITE_ID_STATE: OnceLock<Mutex<InviteIdState>> = OnceLock::new();

fn invite_id_state() -> &'static Mutex<InviteIdState> {
    INVITE_ID_STATE.get_or_init(|| Mutex::new(InviteIdState::new()))
}

#[derive(Debug, thiserror::Error)]
pub enum Error {
    #[error("I/O error: {0}")]
    IO(#[from] std::io::Error),
    #[error("Missing URL for the api server")]
    MissingUrl,
    #[error("Transport error: {0}")]
    Transport(#[from] tonic::transport::Error),
    #[error("gRPC error: {0}")]
    GRPCStatus(#[from] tonic::Status),
    #[error("Login failure")]
    LoginFailure,
    #[error("Login failure")]
    InvalidToken(#[from] tonic::metadata::errors::InvalidMetadataValue),
}

// A Channel normally reconnects by itself, but Windows sleep can leave the
// underlying HTTP/2 transport alive-looking while requests are still routed to
// a dead socket. Keep the cached handle resettable so the polling loop can force
// a fresh TCP connection after resume.
static CONNECTION: Mutex<Option<tonic::transport::Channel>> = Mutex::new(None);

async fn create_channel() -> std::result::Result<tonic::transport::Channel, Error> {
    if let Some(channel) = CONNECTION.lock().unwrap().clone() {
        tracing::debug!("Reusing connection {channel:?}");
        return Ok(channel);
    }

    let Some(url) = crate::config::URL.get() else {
        return Err(Error::MissingUrl);
    };
    tracing::debug!("Connecting to {url}");
    let channel = tonic::transport::Channel::from_shared(url.as_str())
        .unwrap()
        .connect_timeout(std::time::Duration::from_secs(1))
        .timeout(std::time::Duration::from_secs(10))
        .connect()
        .await?;
    tracing::debug!("Connected to {url}");
    *CONNECTION.lock().unwrap() = Some(channel.clone());
    Ok(channel)
}

pub fn reset_connection(reason: &str) {
    let removed = CONNECTION.lock().unwrap().take().is_some();
    tracing::warn!("API connection reset: reason={reason}, had_cached_channel={removed}");
}

macro_rules! connect {
    ($client:ident) => {{
        let channel = create_channel().await?;
        $client::with_interceptor(channel, move |mut req: tonic::Request<_>| {
            let guard = TOKEN.lock().unwrap();
            if let Some(token) = (*guard).as_ref() {
                tracing::debug!("Adding auth token");
                req.metadata_mut().insert("authorization", token.clone());
            }
            Ok(req)
        })
    }};
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Friend {
    pub id: String,
    pub username: String,
    pub is_online: bool,
}

static RUNTIME: OnceLock<tokio::runtime::Runtime> = OnceLock::new();

pub fn runtime() -> Result<&'static tokio::runtime::Runtime, Error> {
    if let Some(runtime) = RUNTIME.get() {
        return Ok(runtime);
    }
    let _ = RUNTIME.set(tokio::runtime::Runtime::new()?);
    Ok(RUNTIME.get().unwrap())
}

fn run<T>(future: impl Future<Output = Result<T, Error>>) -> Result<T, Error> {
    runtime()?.block_on(future)
}

fn invite_connection_retryable(error: &Error) -> bool {
    match error {
        Error::Transport(_) => true,
        Error::GRPCStatus(status) => status.code() == tonic::Code::Unavailable,
        _ => false,
    }
}

async fn invite_friend_remote(id: &str) -> Result<(), Error> {
    let mut client = connect!(FriendsClient);
    let request = tonic::Request::new(InviteRequest { id: id.into() });
    client.invite(request).await?;
    Ok(())
}

pub fn invite_friend(id: &str) -> Result<(), Error> {
    run(async {
        let first = invite_friend_remote(id).await;
        if !first.as_ref().is_err_and(invite_connection_retryable) {
            return first;
        }
        tracing::warn!("Invite transport unavailable; resetting the cached channel and retrying once");
        reset_connection("invite transport unavailable");
        tokio::time::sleep(std::time::Duration::from_millis(100)).await;
        invite_friend_remote(id).await
    })
}

async fn accept_server_invite(server_id: i64) -> Result<bool, Error> {
    let mut client = connect!(MiscClient);
    let request = tonic::Request::new(AcceptInviteRequest { id: server_id });
    Ok(client.accept_invite(request).await?.into_inner().accepted)
}

fn invite_accept_result_is_terminal(result: &Result<bool, Error>) -> bool {
    result.is_ok() || matches!(result, Err(Error::GRPCStatus(status)) if status.code() == tonic::Code::NotFound)
}

pub fn accept_invite(local_id: i64) -> Result<(bool, i64), Error> {
    let server_id = invite_id_state().lock().unwrap().server_id_for_local(local_id);
    let result = run(accept_server_invite(server_id));
    // A server NotFound is just as terminal as an accepted/rejected response:
    // the row was replaced, expired or consumed and must not keep the local
    // generation alive if SQLite ever reuses the numeric server id.
    if invite_accept_result_is_terminal(&result) {
        invite_id_state().lock().unwrap().retire_local(local_id);
    }
    result.map(|accepted| (accepted, server_id))
}

async fn publish_game_session_remote(session: UplayGameSession) -> Result<(), Error> {
    let mut client = connect!(MiscClient);
    client
        .set_game_session(tonic::Request::new(SetGameSessionRequest {
            id: session.id,
            data: session.data,
            flags: session.flags,
            invite_only: session.invite_only,
        }))
        .await?;
    Ok(())
}

async fn clear_game_session_remote() -> Result<(), Error> {
    let mut client = connect!(MiscClient);
    client.clear_game_session(tonic::Request::new(ClearGameSessionRequest {})).await?;
    Ok(())
}

async fn synchronize_game_session(reason: &'static str) {
    let publish_lock = GAME_SESSION_PUBLISH_LOCK.get_or_init(|| tokio::sync::Mutex::new(()));
    let _guard = publish_lock.lock().await;

    loop {
        let Some((generation, desired)) = GAME_SESSION_PUBLISH_STATE.lock().unwrap().pending() else {
            return;
        };
        let result = match desired {
            Some(session) => publish_game_session_remote(session).await,
            None => clear_game_session_remote().await,
        };
        match result {
            Ok(()) => {
                GAME_SESSION_PUBLISH_STATE.lock().unwrap().mark_applied(generation);
                info!("UplayGameSessionSyncSucceeded generation={generation} reason={reason}");
            }
            Err(error) => {
                tracing::warn!("UplayGameSessionSyncDeferred generation={generation} reason={reason} error={error}");
                return;
            }
        }
    }
}

fn schedule_game_session_sync(reason: &'static str) {
    match runtime() {
        Ok(runtime) => {
            runtime.spawn(synchronize_game_session(reason));
        }
        Err(error) => tracing::warn!("UplayGameSessionSyncRuntimeUnavailable reason={reason} error={error}"),
    }
}

pub fn publish_game_session(id: u64, data: Vec<u8>, flags: u32, invite_only: bool) {
    let session = UplayGameSession { id, data, flags, invite_only };
    GAME_SESSION_PUBLISH_STATE.lock().unwrap().update(Some(session));
    schedule_game_session_sync("set");
}

pub fn clear_game_session() {
    GAME_SESSION_PUBLISH_STATE.lock().unwrap().update(None);
    schedule_game_session_sync("clear");
}

async fn republish_local_game_session() {
    GAME_SESSION_PUBLISH_STATE.lock().unwrap().force_republish();
    synchronize_game_session("login").await;
}

pub async fn list_friends_async() -> Result<Vec<Friend>, Error> {
    let mut client = connect!(FriendsClient);
    let request = tonic::Request::new(ListRequest {});
    let response = client.list(request).await?.into_inner();
    Ok(response
        .friends
        .into_iter()
        .map(|friend| Friend {
            id: friend.id,
            username: friend.username,
            is_online: friend.is_online,
        })
        .collect())
}

pub fn list_friends() -> Result<Vec<Friend>, Error> {
    run(list_friends_async())
}

async fn login_async(username: &str, password: &str) -> Result<(), Error> {
    {
        let mut guard = CREDS.lock().unwrap();
        *guard = Some((String::from(username), String::from(password)));
    }

    let mut client = connect!(UsersClient);
    let request = tonic::Request::new(LoginRequest {
        username: String::from(username),
        password: String::from(password),
    });

    debug!("logging in");
    let response = client.login(request).await?.into_inner();
    if !response.error.is_empty() {
        error!("Login error: {}", response.error);
        return Err(Error::LoginFailure);
    }
    if !response.token.is_empty() {
        info!("Login successful");
        let mut guard = TOKEN.lock().unwrap();
        *guard = Some(response.token.parse()?);
    }
    republish_local_game_session().await;
    Ok(())
}

#[instrument(skip(password))]
pub fn login(username: &str, password: &str) -> Result<(), Error> {
    let result = run(login_async(username, password));
    if let Err(error) = &result {
        error!("Initial login failed: {error}");
    }
    result
}

#[instrument]
pub async fn event() -> Result<EventResponse, Error> {
    let mut client = connect!(MiscClient);
    let request = tonic::Request::new(EventRequest {});
    let mut response = client.event(request).await?.into_inner();

    // Party-follow invitations must reach the overlay. The server marks them
    // force_join, and the overlay accepts them once before injecting the same
    // proven game-invitation event used by direct room and lobby invitations.
    if let Some(invite) = response.invite.as_mut() {
        let server_id = invite.id;
        let local_id = invite_id_state().lock().unwrap().map_server_event(server_id);
        // This is the first client-side proof after the server returns an
        // invitation. Keep it identity-free but visible at the normal log
        // level so a missed card can be separated from missed delivery.
        info!("InvitePollDelivered server_id={server_id} local_id={local_id} kind={}", invite.kind);
        invite.id = local_id;
    }
    Ok(response)
}

#[instrument]
pub async fn relogin() -> bool {
    let (username, password) = {
        let Some((username, password)) = CREDS.lock().unwrap().clone() else {
            error!("Relogin skipped because credentials are unavailable");
            return false;
        };
        (username, password)
    };
    match login_async(&username, &password).await {
        Ok(()) => {
            info!("Relogin successful");
            true
        }
        Err(error) => {
            error!("Relogin failed: {error}");
            false
        }
    }
}

#[instrument]
pub async fn reconnect_and_relogin() -> bool {
    reset_connection("polling failure or system resume");
    relogin().await
}

#[cfg(test)]
mod tests {
    use server_api::misc::UplayGameSession;

    use super::invite_accept_result_is_terminal;
    use super::invite_connection_retryable;
    use super::Error;
    use super::GameSessionPublishState;
    use super::InviteIdState;

    #[test]
    fn invite_retry_is_limited_to_connection_failures() {
        assert!(invite_connection_retryable(&Error::GRPCStatus(tonic::Status::unavailable("connect error"))));
        assert!(!invite_connection_retryable(&Error::GRPCStatus(tonic::Status::not_found("friend"))));
        assert!(!invite_connection_retryable(&Error::GRPCStatus(tonic::Status::failed_precondition("room"))));
        assert!(!invite_connection_retryable(&Error::GRPCStatus(tonic::Status::already_exists("room"))));
    }

    #[test]
    fn repeated_delivery_reuses_local_id_until_acceptance() {
        let mut state = InviteIdState::new();
        let first = state.map_server_event(1);
        assert_eq!(first, state.map_server_event(1));
        assert_eq!(state.server_id_for_local(first), 1);
    }

    #[test]
    fn reused_server_id_gets_new_local_id_after_acceptance() {
        let mut state = InviteIdState::new();
        let first = state.map_server_event(1);
        state.retire_local(first);
        let second = state.map_server_event(1);
        assert_ne!(first, second);
        assert_eq!(state.server_id_for_local(second), 1);
        assert_eq!(second, state.map_server_event(1));
    }

    #[test]
    fn only_terminal_accept_results_retire_the_local_generation() {
        assert!(invite_accept_result_is_terminal(&Ok(true)));
        assert!(invite_accept_result_is_terminal(&Ok(false)));
        assert!(invite_accept_result_is_terminal(&Err(Error::GRPCStatus(tonic::Status::not_found("expired")))));
        assert!(!invite_accept_result_is_terminal(&Err(Error::GRPCStatus(tonic::Status::unavailable("retry")))));
    }

    #[test]
    fn set_clear_set_coalesces_to_the_latest_session_generation() {
        let mut state = GameSessionPublishState::default();
        state.update(Some(UplayGameSession {
            id: 10,
            data: vec![1],
            flags: 2,
            invite_only: false,
        }));
        state.update(None);
        state.update(Some(UplayGameSession {
            id: 11,
            data: vec![3],
            flags: 4,
            invite_only: true,
        }));

        let (generation, desired) = state.pending().unwrap();
        assert_eq!(generation, 3);
        assert_eq!(desired.unwrap().id, 11);
    }

    #[test]
    fn completing_an_older_request_does_not_hide_a_newer_desired_state() {
        let mut state = GameSessionPublishState::default();
        state.update(Some(UplayGameSession {
            id: 10,
            data: Vec::new(),
            flags: 0,
            invite_only: false,
        }));
        let old_generation = state.pending().unwrap().0;
        state.update(None);
        state.mark_applied(old_generation);

        let (pending_generation, desired) = state.pending().unwrap();
        assert!(pending_generation > old_generation);
        assert!(desired.is_none());
    }

    #[test]
    fn relogin_republishes_only_when_a_local_session_exists() {
        let mut empty = GameSessionPublishState::default();
        empty.force_republish();
        assert!(empty.pending().is_none());

        let mut populated = GameSessionPublishState::default();
        populated.update(Some(UplayGameSession {
            id: 10,
            data: Vec::new(),
            flags: 0,
            invite_only: false,
        }));
        let first_generation = populated.pending().unwrap().0;
        populated.mark_applied(first_generation);
        populated.force_republish();
        assert!(populated.pending().unwrap().0 > first_generation);
    }
}
