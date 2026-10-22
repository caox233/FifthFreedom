//! This module defines and implements the gRPC services for the dedicated server,
//! including Friends, Users, Misc, UsersAdmin, and GamesAdmin services.

use std::collections::HashMap;
use std::collections::HashSet;
use std::future::Future;
use std::net::IpAddr;
use std::net::Ipv4Addr;
use std::net::SocketAddr;
use std::sync::Arc;
use std::time::SystemTime;
use std::time::UNIX_EPOCH;

use quazal::rmc::types::Property;
use quazal::rmc::types::QList;
use quazal::rmc::types::StationURL;
use serde::Deserialize;
use serde::Serialize;
use server_api::friends;
use server_api::friends::friends_server::Friends;
use server_api::friends::friends_server::FriendsServer;
use server_api::friends::Friend;
use server_api::games;
use server_api::games::games_admin_server::GamesAdmin;
use server_api::games::games_admin_server::GamesAdminServer;
use server_api::misc;
use server_api::misc::misc_server::Misc;
use server_api::misc::misc_server::MiscServer;
use server_api::users;
use server_api::users::users_admin_server::UsersAdmin;
use server_api::users::users_admin_server::UsersAdminServer;
use server_api::users::users_server::Users;
use server_api::users::users_server::UsersServer;
use server_api::users::User;
use slog::Logger;
use sodiumoxide::base64;
use sodiumoxide::crypto::auth::hmacsha256;
use sodiumoxide::crypto::hash::sha256;
use sodiumoxide::crypto::secretbox;
use sodiumoxide::randombytes;
use tokio::sync::RwLock;
use tonic::transport::Server;
use tonic::Request;
use tonic::Response;
use tonic::Status;

use crate::config::DebugConfig;
use crate::storage::AuthIdentity;
use crate::storage::GameSession;
use crate::storage::InvitableGameSession;
use crate::storage::LoginError;
use crate::storage::Storage;
use crate::storage::INVITE_KIND_LOBBY_PARTY;
use crate::storage::INVITE_KIND_LOBBY_RESTORE;
use crate::storage::INVITE_KIND_PARTY_FOLLOW;
use crate::storage::INVITE_KIND_PRIVATE_ROOM;

const MAX_UPLAY_GAME_SESSION_DATA: usize = 16 * 1024;
const RECOVERY_CODE_BYTES: usize = 15;
const RECOVERY_CODE_LENGTH: usize = 24;
const AUTH_TOKEN_VERSION: &str = "v1";
const DEFAULT_AUTH_TOKEN_TTL_SECONDS: i64 = 24 * 60 * 60;
const MIN_AUTH_TOKEN_TTL_SECONDS: i64 = 5 * 60;
const MAX_AUTH_TOKEN_TTL_SECONDS: i64 = 7 * 24 * 60 * 60;
const MAX_AUTH_TOKEN_FUTURE_SKEW_SECONDS: i64 = 60;
const MAX_AUTH_TOKEN_LENGTH: usize = 4096;
const DEFAULT_VIRTUAL_NET: &str = "10.66.0.0/24";
const DEFAULT_SERVER_IP: &str = "10.66.0.1";

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
struct OverlayClientNetwork {
    network: u32,
    broadcast: u32,
    server_ip: Option<u32>,
}

impl OverlayClientNetwork {
    fn from_env() -> Result<Self, std::io::Error> {
        let network = match std::env::var("SCBL_VIRTUAL_NET") {
            Ok(value) => value,
            Err(std::env::VarError::NotPresent) => String::from(DEFAULT_VIRTUAL_NET),
            Err(std::env::VarError::NotUnicode(_)) => return Err(invalid_virtual_network("SCBL_VIRTUAL_NET is not valid Unicode")),
        };
        let server_ip = match std::env::var("SCBL_SERVER_IP") {
            Ok(value) => Some(value),
            Err(std::env::VarError::NotPresent) if network == DEFAULT_VIRTUAL_NET => Some(String::from(DEFAULT_SERVER_IP)),
            Err(std::env::VarError::NotPresent) => None,
            Err(std::env::VarError::NotUnicode(_)) => return Err(invalid_virtual_network("SCBL_SERVER_IP is not valid Unicode")),
        };
        Self::parse(&network, server_ip.as_deref())
    }

    fn parse(cidr: &str, server_ip: Option<&str>) -> Result<Self, std::io::Error> {
        let (address, prefix) = cidr
            .split_once('/')
            .ok_or_else(|| invalid_virtual_network("SCBL_VIRTUAL_NET must use IPv4 CIDR notation"))?;
        let address = address
            .parse::<Ipv4Addr>()
            .map_err(|_| invalid_virtual_network("SCBL_VIRTUAL_NET must contain an IPv4 address"))?;
        let prefix = prefix
            .parse::<u32>()
            .ok()
            .filter(|prefix| (1..=30).contains(prefix))
            .ok_or_else(|| invalid_virtual_network("SCBL_VIRTUAL_NET prefix must be between 1 and 30"))?;
        let mask = u32::MAX << (32 - prefix);
        let network = u32::from(address) & mask;
        let broadcast = network | !mask;
        let server_ip = server_ip
            .map(|value| {
                value
                    .parse::<Ipv4Addr>()
                    .map(u32::from)
                    .map_err(|_| invalid_virtual_network("SCBL_SERVER_IP must contain an IPv4 address"))
            })
            .transpose()?;
        if server_ip.is_some_and(|server_ip| server_ip <= network || server_ip >= broadcast) {
            return Err(invalid_virtual_network("SCBL_SERVER_IP must be a usable address inside SCBL_VIRTUAL_NET"));
        }
        Ok(Self { network, broadcast, server_ip })
    }

    fn contains_client(self, ip: IpAddr) -> bool {
        let IpAddr::V4(ip) = ip else {
            return false;
        };
        let ip = u32::from(ip);
        ip > self.network && ip < self.broadcast && self.server_ip != Some(ip)
    }
}

fn invalid_virtual_network(message: &str) -> std::io::Error {
    std::io::Error::new(std::io::ErrorKind::InvalidInput, message)
}

#[cfg(test)]
mod overlay_client_network_tests {
    use super::*;

    #[test]
    fn default_network_preserves_existing_client_range() {
        let network = OverlayClientNetwork::parse(DEFAULT_VIRTUAL_NET, Some(DEFAULT_SERVER_IP)).unwrap();
        assert!(!network.contains_client("10.66.0.0".parse().unwrap()));
        assert!(!network.contains_client("10.66.0.1".parse().unwrap()));
        assert!(network.contains_client("10.66.0.2".parse().unwrap()));
        assert!(network.contains_client("10.66.0.254".parse().unwrap()));
        assert!(!network.contains_client("10.66.0.255".parse().unwrap()));
    }

    #[test]
    fn configured_network_replaces_the_hardcoded_subnet() {
        let network = OverlayClientNetwork::parse("10.77.8.0/24", Some("10.77.8.1")).unwrap();
        assert!(network.contains_client("10.77.8.42".parse().unwrap()));
        assert!(!network.contains_client("10.66.0.42".parse().unwrap()));
        assert!(!network.contains_client("10.77.8.1".parse().unwrap()));
    }

    #[test]
    fn invalid_or_non_ipv4_networks_fail_closed() {
        assert!(OverlayClientNetwork::parse("not-a-network", None).is_err());
        assert!(OverlayClientNetwork::parse("2001:db8::/64", None).is_err());
        assert!(OverlayClientNetwork::parse("10.77.8.0/31", None).is_err());
        assert!(OverlayClientNetwork::parse("10.77.8.0/24", Some("10.88.0.1")).is_err());
    }
}

#[derive(Clone)]
struct AuthTokenConfig {
    key: hmacsha256::Key,
    ttl_seconds: i64,
}

#[derive(Clone, Debug, Deserialize, PartialEq, Eq, Serialize)]
struct AuthTokenClaims {
    user_id: u32,
    username: String,
    source_ip: String,
    issued_at: i64,
    expires_at: i64,
    auth_version: i64,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum AuthTokenError {
    Invalid,
    Expired,
    FutureIssued,
    WrongSource,
    Revoked,
    UnknownUser,
}

impl AuthTokenConfig {
    fn from_env() -> Result<Self, String> {
        let raw_key = std::env::var("SCBL_AUTH_TOKEN_KEY").map_err(|_| String::from("SCBL_AUTH_TOKEN_KEY is required"))?;
        let key_bytes = decode_hex_key(&raw_key).ok_or_else(|| String::from("SCBL_AUTH_TOKEN_KEY must contain exactly 64 hexadecimal characters"))?;
        let key = hmacsha256::Key::from_slice(&key_bytes).ok_or_else(|| String::from("SCBL_AUTH_TOKEN_KEY has an invalid length"))?;
        let ttl_seconds = match std::env::var("SCBL_AUTH_TOKEN_TTL_SECONDS") {
            Ok(value) => value
                .parse::<i64>()
                .ok()
                .filter(|ttl| (MIN_AUTH_TOKEN_TTL_SECONDS..=MAX_AUTH_TOKEN_TTL_SECONDS).contains(ttl))
                .ok_or_else(|| format!("SCBL_AUTH_TOKEN_TTL_SECONDS must be between {MIN_AUTH_TOKEN_TTL_SECONDS} and {MAX_AUTH_TOKEN_TTL_SECONDS}"))?,
            Err(std::env::VarError::NotPresent) => DEFAULT_AUTH_TOKEN_TTL_SECONDS,
            Err(std::env::VarError::NotUnicode(_)) => return Err(String::from("SCBL_AUTH_TOKEN_TTL_SECONDS is invalid")),
        };
        Ok(Self { key, ttl_seconds })
    }
}

fn decode_hex_key(value: &str) -> Option<[u8; 32]> {
    if value.len() != 64 || !value.bytes().all(|byte| byte.is_ascii_hexdigit()) {
        return None;
    }
    let mut output = [0_u8; 32];
    for (index, pair) in value.as_bytes().chunks_exact(2).enumerate() {
        let high = hex_nibble(pair[0])?;
        let low = hex_nibble(pair[1])?;
        output[index] = (high << 4) | low;
    }
    Some(output)
}

fn hex_nibble(value: u8) -> Option<u8> {
    match value {
        b'0'..=b'9' => Some(value - b'0'),
        b'a'..=b'f' => Some(value - b'a' + 10),
        b'A'..=b'F' => Some(value - b'A' + 10),
        _ => None,
    }
}

fn unix_time_seconds() -> Result<i64, Status> {
    let seconds = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_err(|_| Status::internal("System clock is before the Unix epoch"))?
        .as_secs();
    i64::try_from(seconds).map_err(|_| Status::internal("System clock is out of range"))
}

fn request_source_ip<T>(request: &Request<T>, overlay_network: OverlayClientNetwork) -> Result<IpAddr, Status> {
    let ip = request
        .remote_addr()
        .map(|address| address.ip())
        .ok_or_else(|| Status::unauthenticated("Missing client source address"))?;
    if !overlay_network.contains_client(ip) {
        return Err(Status::unauthenticated("Invalid client source address"));
    }
    Ok(ip)
}

fn encode_base64url(value: &[u8]) -> String {
    base64::encode(value, base64::Variant::UrlSafeNoPadding)
}

fn decode_base64url(value: &str) -> Result<Vec<u8>, AuthTokenError> {
    if value.is_empty() || !value.bytes().all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'_')) {
        return Err(AuthTokenError::Invalid);
    }
    let decoded = base64::decode(value, base64::Variant::UrlSafeNoPadding).map_err(|()| AuthTokenError::Invalid)?;
    if encode_base64url(&decoded) != value {
        return Err(AuthTokenError::Invalid);
    }
    Ok(decoded)
}

fn issue_auth_token(config: &AuthTokenConfig, claims: &AuthTokenClaims) -> Result<String, Status> {
    let payload = serde_json::to_vec(claims).map_err(|_| Status::internal("Token payload encoding failed"))?;
    let payload = encode_base64url(&payload);
    let signing_input = format!("{AUTH_TOKEN_VERSION}.{payload}");
    let signature = hmacsha256::authenticate(signing_input.as_bytes(), &config.key);
    Ok(format!("{signing_input}.{}", encode_base64url(signature.as_ref())))
}

fn verify_auth_token(config: &AuthTokenConfig, token: &str, source_ip: IpAddr, now: i64) -> Result<AuthTokenClaims, AuthTokenError> {
    if token.len() > MAX_AUTH_TOKEN_LENGTH {
        return Err(AuthTokenError::Invalid);
    }
    let mut parts = token.split('.');
    let version = parts.next().ok_or(AuthTokenError::Invalid)?;
    let payload_segment = parts.next().ok_or(AuthTokenError::Invalid)?;
    let signature_segment = parts.next().ok_or(AuthTokenError::Invalid)?;
    if parts.next().is_some() || version != AUTH_TOKEN_VERSION {
        return Err(AuthTokenError::Invalid);
    }
    let signature = decode_base64url(signature_segment)?;
    let signature = hmacsha256::Tag::from_slice(&signature).ok_or(AuthTokenError::Invalid)?;
    let signing_input = format!("{version}.{payload_segment}");
    if !hmacsha256::verify(&signature, signing_input.as_bytes(), &config.key) {
        return Err(AuthTokenError::Invalid);
    }
    let payload = decode_base64url(payload_segment)?;
    let claims: AuthTokenClaims = serde_json::from_slice(&payload).map_err(|_| AuthTokenError::Invalid)?;
    if claims.user_id == 0 || claims.username.is_empty() || claims.username.len() > 64 || claims.auth_version < 1 {
        return Err(AuthTokenError::Invalid);
    }
    if claims.source_ip != source_ip.to_string() {
        return Err(AuthTokenError::WrongSource);
    }
    if claims.issued_at > now + MAX_AUTH_TOKEN_FUTURE_SKEW_SECONDS {
        return Err(AuthTokenError::FutureIssued);
    }
    if claims.expires_at <= now {
        return Err(AuthTokenError::Expired);
    }
    let lifetime = claims.expires_at.checked_sub(claims.issued_at).ok_or(AuthTokenError::Invalid)?;
    if lifetime < MIN_AUTH_TOKEN_TTL_SECONDS || lifetime > MAX_AUTH_TOKEN_TTL_SECONDS {
        return Err(AuthTokenError::Invalid);
    }
    Ok(claims)
}

fn validate_auth_identity(claims: &AuthTokenClaims, identity: Option<&AuthIdentity>) -> Result<(), AuthTokenError> {
    let identity = identity.ok_or(AuthTokenError::UnknownUser)?;
    if claims.user_id != identity.user_id || claims.username != identity.username || claims.auth_version != identity.auth_version {
        return Err(AuthTokenError::Revoked);
    }
    Ok(())
}

#[cfg(test)]
mod auth_token_tests {
    use std::net::Ipv4Addr;

    use super::*;

    const NOW: i64 = 1_700_000_000;

    fn config() -> AuthTokenConfig {
        AuthTokenConfig {
            key: hmacsha256::Key([0x11; 32]),
            ttl_seconds: DEFAULT_AUTH_TOKEN_TTL_SECONDS,
        }
    }

    fn claims() -> AuthTokenClaims {
        AuthTokenClaims {
            user_id: 42,
            username: String::from("CanonicalPlayer"),
            source_ip: String::from("10.66.0.42"),
            issued_at: NOW,
            expires_at: NOW + DEFAULT_AUTH_TOKEN_TTL_SECONDS,
            auth_version: 3,
        }
    }

    fn source_ip() -> IpAddr {
        IpAddr::V4(Ipv4Addr::new(10, 66, 0, 42))
    }

    #[test]
    fn fixed_token_vector_is_valid() {
        let token = issue_auth_token(&config(), &claims()).unwrap();
        assert_eq!(
            token,
            "v1.eyJ1c2VyX2lkIjo0MiwidXNlcm5hbWUiOiJDYW5vbmljYWxQbGF5ZXIiLCJzb3VyY2VfaXAiOiIxMC42Ni4wLjQyIiwiaXNzdWVkX2F0IjoxNzAwMDAwMDAwLCJleHBpcmVzX2F0IjoxNzAwMDg2NDAwLCJhdXRoX3ZlcnNpb24iOjN9.5GGWk0JTcbhyPcXHWHH5msYvAv9RLkxjyPknzbGcaHQ"
        );
        let verified = verify_auth_token(&config(), &token, source_ip(), NOW).unwrap();
        assert_eq!(verified, claims());
    }

    #[test]
    fn tampered_token_is_rejected() {
        let token = issue_auth_token(&config(), &claims()).unwrap();
        let mut bytes = token.into_bytes();
        let last = bytes.last_mut().unwrap();
        *last = if *last == b'A' { b'B' } else { b'A' };
        let tampered = String::from_utf8(bytes).unwrap();
        assert_eq!(verify_auth_token(&config(), &tampered, source_ip(), NOW), Err(AuthTokenError::Invalid));
    }

    #[test]
    fn oversized_token_is_rejected() {
        let oversized = "A".repeat(MAX_AUTH_TOKEN_LENGTH + 1);
        assert_eq!(verify_auth_token(&config(), &oversized, source_ip(), NOW), Err(AuthTokenError::Invalid));
    }

    #[test]
    fn expired_and_future_tokens_are_rejected() {
        let expired = issue_auth_token(&config(), &claims()).unwrap();
        assert_eq!(
            verify_auth_token(&config(), &expired, source_ip(), NOW + DEFAULT_AUTH_TOKEN_TTL_SECONDS),
            Err(AuthTokenError::Expired)
        );

        let mut future_claims = claims();
        future_claims.issued_at = NOW + MAX_AUTH_TOKEN_FUTURE_SKEW_SECONDS + 1;
        future_claims.expires_at = future_claims.issued_at + DEFAULT_AUTH_TOKEN_TTL_SECONDS;
        let future = issue_auth_token(&config(), &future_claims).unwrap();
        assert_eq!(verify_auth_token(&config(), &future, source_ip(), NOW), Err(AuthTokenError::FutureIssued));
    }

    #[test]
    fn wrong_source_ip_is_rejected() {
        let token = issue_auth_token(&config(), &claims()).unwrap();
        let wrong_ip = IpAddr::V4(Ipv4Addr::new(10, 66, 0, 43));
        assert_eq!(verify_auth_token(&config(), &token, wrong_ip, NOW), Err(AuthTokenError::WrongSource));
    }

    #[test]
    fn revoked_and_unknown_users_are_rejected() {
        let claims = claims();
        let revoked = AuthIdentity {
            user_id: claims.user_id,
            username: claims.username.clone(),
            account_id: String::from("account-42"),
            auth_version: claims.auth_version + 1,
        };
        assert_eq!(validate_auth_identity(&claims, Some(&revoked)), Err(AuthTokenError::Revoked));
        assert_eq!(validate_auth_identity(&claims, None), Err(AuthTokenError::UnknownUser));
    }

    #[test]
    fn control_auth_key_requires_exact_hex() {
        assert_eq!(decode_hex_key(&"11".repeat(32)), Some([0x11; 32]));
        assert_eq!(decode_hex_key("11"), None);
        assert_eq!(decode_hex_key(&"zz".repeat(32)), None);
    }
}

fn normalize_recovery_code(value: &str) -> String {
    value.chars().filter(|ch| ch.is_ascii_alphanumeric()).map(|ch| ch.to_ascii_uppercase()).collect()
}

fn hash_recovery_code(value: &str) -> String {
    let normalized = normalize_recovery_code(value);
    sha256::hash(normalized.as_bytes()).0.iter().map(|byte| format!("{byte:02x}")).collect()
}

fn generate_recovery_code() -> String {
    let compact = base32(&randombytes::randombytes(RECOVERY_CODE_BYTES));
    compact
        .as_bytes()
        .chunks(4)
        .map(|chunk| std::str::from_utf8(chunk).expect("base32 output is ASCII"))
        .collect::<Vec<_>>()
        .join("-")
}

#[cfg(test)]
mod recovery_code_tests {
    use super::*;

    #[test]
    fn generated_code_has_120_bits_in_readable_groups() {
        let code = generate_recovery_code();
        assert_eq!(code.len(), 29);
        assert_eq!(code.matches('-').count(), 5);
        assert_eq!(normalize_recovery_code(&code).len(), RECOVERY_CODE_LENGTH);
    }

    #[test]
    fn hashing_ignores_case_and_separators() {
        assert_eq!(hash_recovery_code("0ABC-1DEF-2GHI-3JKL-4MNO-5PQR"), hash_recovery_code("0abc 1def 2ghi 3jkl 4mno 5pqr"));
    }
}

fn invite_requires_uplay_game_session(kind: i32) -> bool {
    matches!(kind, INVITE_KIND_PRIVATE_ROOM | INVITE_KIND_PARTY_FOLLOW)
}

fn invite_forces_join(kind: i32) -> bool {
    matches!(kind, INVITE_KIND_PARTY_FOLLOW | INVITE_KIND_LOBBY_RESTORE)
}

fn validate_uplay_game_session(data: &[u8]) -> bool {
    !data.is_empty() && data.len() <= MAX_UPLAY_GAME_SESSION_DATA
}

fn invite_projects_exact_uplay_target(kind: i32) -> bool {
    kind == INVITE_KIND_PRIVATE_ROOM
}

fn invite_uplay_payload_owner(kind: i32, session_host_id: Option<u32>) -> Option<u32> {
    invite_requires_uplay_game_session(kind).then_some(session_host_id).flatten()
}

/// Return a response copy of the inviter-published Uplay GameSession for the
/// private invitation transports that consume it. Lobby invitations always
/// use Friends accepted -> participant search -> NAT -> JoinSession and must
/// not attach or gate acceptance on a Uplay GameSession payload.
fn project_invite_uplay_game_session(kind: i32, invite_session_id: u32, published: Option<misc::UplayGameSession>) -> Option<misc::UplayGameSession> {
    if !invite_requires_uplay_game_session(kind) {
        return None;
    }
    let mut session = published?;
    if invite_projects_exact_uplay_target(kind) {
        session.id = u64::from(invite_session_id);
    }
    Some(session)
}

fn project_party_roster(roster: Option<(u32, Vec<crate::storage::User>)>) -> Option<misc::PartyRoster> {
    let (leader_id, members) = roster?;
    let leader = members.iter().find(|member| member.id == leader_id)?;
    Some(misc::PartyRoster {
        leader: Some(User {
            id: leader.ubi_id.clone(),
            username: leader.username.clone(),
            ips: Vec::new(),
        }),
        members: members
            .into_iter()
            .map(|member| User {
                id: member.ubi_id,
                username: member.username,
                ips: Vec::new(),
            })
            .collect(),
    })
}

#[cfg(test)]
mod private_game_direct_route_tests {
    use super::*;

    fn published() -> misc::UplayGameSession {
        misc::UplayGameSession {
            id: 465,
            data: vec![1, 2, 3, 4],
            flags: 7,
            invite_only: true,
        }
    }

    #[test]
    fn direct_private_projects_only_top_level_target_id() {
        let projected = project_invite_uplay_game_session(INVITE_KIND_PRIVATE_ROOM, 466, Some(published())).unwrap();
        assert_eq!(projected.id, 466);
        assert_eq!(projected.data, vec![1, 2, 3, 4]);
        assert_eq!(projected.flags, 7);
        assert!(projected.invite_only);
    }

    #[test]
    fn party_follow_keeps_host_published_target_id() {
        let projected = project_invite_uplay_game_session(INVITE_KIND_PARTY_FOLLOW, 466, Some(published())).unwrap();
        assert_eq!(projected.id, 465);
        assert_eq!(projected.data, vec![1, 2, 3, 4]);
    }

    #[test]
    fn lobby_party_never_attaches_uplay_game_session_payload() {
        let mut host_publication = published();
        host_publication.data = vec![0x5a; 496];
        assert!(project_invite_uplay_game_session(INVITE_KIND_LOBBY_PARTY, 460, Some(host_publication)).is_none());
    }

    #[test]
    fn missing_publication_stays_missing() {
        assert!(project_invite_uplay_game_session(INVITE_KIND_PRIVATE_ROOM, 466, None).is_none());
    }
}

fn session_invite_kind(session: &GameSession) -> Option<i32> {
    let attributes = session.attributes.parse::<QList<Property>>().ok()?;
    attributes.0.iter().find_map(|property| {
        if property.id != 113 {
            return None;
        }
        match property.value {
            0 => Some(INVITE_KIND_PRIVATE_ROOM),
            1 => Some(INVITE_KIND_LOBBY_PARTY),
            _ => None,
        }
    })
}

fn session_is_cooperative(session: &GameSession) -> bool {
    session
        .attributes
        .parse::<QList<Property>>()
        .ok()
        .is_some_and(|attributes| attributes.0.iter().any(|property| property.id == 105 && property.value == 2))
}

fn session_attributes_are_public_game(session_attributes: &str) -> bool {
    let Ok(attributes) = session_attributes.parse::<QList<Property>>() else {
        return false;
    };
    let values = attributes.0.iter().map(|property| (property.id, property.value)).collect::<HashMap<_, _>>();
    values.get(&113) == Some(&0) && values.get(&3).is_some_and(|slots| *slots > 0)
}

fn session_is_public_game(session: &GameSession) -> bool {
    session_attributes_are_public_game(&session.attributes)
}

#[derive(Debug)]
struct InvitableSessionSelection {
    session: GameSession,
    kind: i32,
}

#[derive(Default)]
struct UplayGameSessionRegistry {
    current: HashMap<u32, misc::UplayGameSession>,
    last_public: HashMap<u32, misc::UplayGameSession>,
}

impl UplayGameSessionRegistry {
    fn current(&self, user_id: u32) -> Option<&misc::UplayGameSession> {
        self.current.get(&user_id)
    }

    /// Retail clears the newly elected host's current Uplay publication while
    /// completing native match migration. Its most recent public descriptor is
    /// still the validated transport payload for that same running client and
    /// can be projected onto the migrated match Session just as the original
    /// host's lobby descriptor was projected before migration.
    fn public_for_active_match(&self, user_id: u32) -> Option<&misc::UplayGameSession> {
        match self.current.get(&user_id) {
            Some(publication) if !publication.invite_only => Some(publication),
            Some(_) => None,
            None => self.last_public.get(&user_id).filter(|publication| !publication.invite_only),
        }
    }

    fn publish(&mut self, user_id: u32, session: misc::UplayGameSession) {
        if !session.invite_only {
            self.last_public.insert(user_id, session.clone());
        }
        self.current.insert(user_id, session);
    }

    fn clear_current(&mut self, user_id: u32) -> bool {
        self.current.remove(&user_id).is_some()
    }
}

fn invite_host_publication<'a>(registry: &'a UplayGameSessionRegistry, kind: i32, session_attributes: &str, host_id: u32) -> Option<&'a misc::UplayGameSession> {
    if kind == INVITE_KIND_PRIVATE_ROOM && session_attributes_are_public_game(session_attributes) {
        registry.public_for_active_match(host_id)
    } else {
        registry.current(host_id)
    }
}

fn select_invitable_session(
    sender_id: u32,
    sessions: impl IntoIterator<Item = InvitableGameSession>,
    uplay_game_sessions: &UplayGameSessionRegistry,
) -> Option<InvitableSessionSelection> {
    let mut private_room = None;
    let mut public_match = None;
    let mut party_lobby = None;

    for candidate in sessions {
        let session = candidate.session;
        let Some(kind) = session_invite_kind(&session) else {
            continue;
        };
        match kind {
            INVITE_KIND_PRIVATE_ROOM => {
                let host_publication = uplay_game_sessions.current(session.creator_id);
                let public_match_publication = uplay_game_sessions.public_for_active_match(session.creator_id);
                let sender_publication = uplay_game_sessions.current(sender_id);
                let current_private_room = !session_is_public_game(&session)
                    && host_publication.is_some_and(|published| published.invite_only)
                    && (candidate.direct_member || sender_publication.is_some_and(|published| published.invite_only));
                let current_public_match = session_is_public_game(&session)
                    && public_match_publication
                        .is_some_and(|published| candidate.direct_member || sender_publication.is_some_and(|sender| !sender.invite_only && sender.id == published.id));
                if current_private_room && private_room.is_none() {
                    private_room = Some(session);
                } else if current_public_match && public_match.is_none() {
                    public_match = Some(session);
                }
            }
            INVITE_KIND_LOBBY_PARTY => {
                if uplay_game_sessions
                    .current(sender_id)
                    .is_some_and(|published| !published.invite_only && published.id == u64::from(session.session_id))
                    && party_lobby.is_none()
                {
                    party_lobby = Some(session);
                }
            }
            _ => {}
        }
    }

    if let Some(session) = private_room {
        return Some(InvitableSessionSelection {
            session,
            kind: INVITE_KIND_PRIVATE_ROOM,
        });
    }
    if let Some(match_session) = public_match {
        return Some(InvitableSessionSelection {
            session: match_session,
            kind: INVITE_KIND_PRIVATE_ROOM,
        });
    }
    party_lobby.map(|session| InvitableSessionSelection {
        session,
        kind: INVITE_KIND_LOBBY_PARTY,
    })
}

fn invite_publication_is_current(kind: i32, session_id: u32, session_attributes: &str, publication: Option<&misc::UplayGameSession>) -> bool {
    match kind {
        INVITE_KIND_PRIVATE_ROOM => publication.is_some_and(|published| {
            if session_attributes_are_public_game(session_attributes) {
                !published.invite_only
            } else {
                published.invite_only
            }
        }),
        INVITE_KIND_LOBBY_PARTY if session_attributes_are_public_game(session_attributes) => true,
        INVITE_KIND_LOBBY_PARTY => publication.is_some_and(|published| !published.invite_only && published.id == u64::from(session_id)),
        _ => true,
    }
}

#[cfg(test)]
mod invite_target_selection_tests {
    use super::*;

    fn candidate(session: GameSession, direct_member: bool) -> InvitableGameSession {
        InvitableGameSession { session, direct_member }
    }

    fn session(session_id: u32, creator_id: u32, room: bool) -> InvitableGameSession {
        candidate(
            GameSession {
                session_type: 1,
                session_id,
                creator_id,
                attributes: if room {
                    String::from("113 => 0;3 => 0;4 => 8")
                } else {
                    String::from("113 => 1;3 => 8;4 => 0")
                },
                participants: Vec::new(),
            },
            false,
        )
    }

    fn public_game(session_id: u32, creator_id: u32) -> InvitableGameSession {
        candidate(
            GameSession {
                session_type: 1,
                session_id,
                creator_id,
                attributes: String::from("113 => 0;3 => 12;4 => 0;103 => 4008726265"),
                participants: Vec::new(),
            },
            false,
        )
    }

    fn publication(id: u64, invite_only: bool) -> misc::UplayGameSession {
        misc::UplayGameSession {
            id,
            data: vec![0x5a; 496],
            flags: 0,
            invite_only,
        }
    }

    fn registry(current: HashMap<u32, misc::UplayGameSession>) -> UplayGameSessionRegistry {
        UplayGameSessionRegistry {
            current,
            last_public: HashMap::new(),
        }
    }

    #[test]
    fn clearing_current_publication_preserves_only_the_last_public_transport() {
        let mut publications = UplayGameSessionRegistry::default();
        publications.publish(1005, publication(1659, false));
        assert!(publications.clear_current(1005));
        assert_eq!(publications.public_for_active_match(1005).map(|session| session.id), Some(1659));

        publications.publish(1005, publication(1660, true));
        assert!(publications.public_for_active_match(1005).is_none());
    }

    #[test]
    fn current_private_publication_keeps_private_room_authoritative() {
        let sessions = vec![session(1330, 1003, true), session(1327, 1005, false)];
        let publications = registry(HashMap::from([(1003, publication(1327, true))]));

        let selected = select_invitable_session(1003, sessions, &publications).unwrap();
        assert_eq!(selected.session.session_id, 1330);
        assert_eq!(selected.kind, INVITE_KIND_PRIVATE_ROOM);
    }

    #[test]
    fn direct_private_room_member_uses_the_hosts_publication() {
        let sessions = vec![candidate(session(1472, 1003, true).session, true)];
        let publications = registry(HashMap::from([(1003, publication(1472, true))]));

        let selected = select_invitable_session(1021, sessions, &publications).unwrap();
        assert_eq!(selected.session.session_id, 1472);
        assert_eq!(selected.kind, INVITE_KIND_PRIVATE_ROOM);
    }

    #[test]
    fn non_member_without_a_sender_publication_cannot_use_the_hosts_room() {
        let sessions = vec![session(1472, 1003, true)];
        let publications = registry(HashMap::from([(1003, publication(1472, true))]));

        assert!(select_invitable_session(1005, sessions, &publications).is_none());
    }

    #[test]
    fn returned_lobby_skips_stale_private_room_and_uses_shared_party() {
        let sessions = vec![session(1330, 1003, true), session(1327, 1005, false), session(1331, 1003, false)];
        let publications = registry(HashMap::from([(1003, publication(1327, false)), (1005, publication(1327, false))]));

        let selected = select_invitable_session(1003, sessions, &publications).unwrap();
        assert_eq!(selected.session.session_id, 1327);
        assert_eq!(selected.kind, INVITE_KIND_LOBBY_PARTY);
    }

    #[test]
    fn active_public_match_invitation_targets_the_joinable_match_directly() {
        let sessions = vec![public_game(1467, 1005), session(1464, 1005, false)];
        let publications = registry(HashMap::from([(1005, publication(1464, false))]));

        let selected = select_invitable_session(1005, sessions, &publications).unwrap();
        assert_eq!(selected.session.session_id, 1467);
        assert_eq!(selected.kind, INVITE_KIND_PRIVATE_ROOM);
    }

    #[test]
    fn active_public_match_accepts_another_current_party_member_as_inviter() {
        let sessions = vec![public_game(1467, 1005), session(1464, 1005, false)];
        let publications = registry(HashMap::from([(1005, publication(1464, false)), (1021, publication(1464, false))]));

        let selected = select_invitable_session(1021, sessions, &publications).unwrap();
        assert_eq!(selected.session.session_id, 1467);
        assert_eq!(selected.kind, INVITE_KIND_PRIVATE_ROOM);
    }

    #[test]
    fn active_public_match_without_a_current_party_lobby_keeps_direct_fallback() {
        let sessions = vec![public_game(1467, 1005)];
        let publications = registry(HashMap::from([(1005, publication(1464, false))]));

        let selected = select_invitable_session(1005, sessions, &publications).unwrap();
        assert_eq!(selected.session.session_id, 1467);
        assert_eq!(selected.kind, INVITE_KIND_PRIVATE_ROOM);
    }

    #[test]
    fn migrated_public_match_reuses_the_new_hosts_last_validated_public_transport() {
        let sessions = vec![candidate(public_game(1661, 1005).session, true)];
        let publications = UplayGameSessionRegistry {
            current: HashMap::new(),
            last_public: HashMap::from([(1005, publication(1659, false))]),
        };

        let selected = select_invitable_session(1005, sessions, &publications).unwrap();
        assert_eq!(selected.session.session_id, 1661);
        assert_eq!(selected.kind, INVITE_KIND_PRIVATE_ROOM);
        let projected = project_invite_uplay_game_session(
            selected.kind,
            selected.session.session_id,
            invite_host_publication(&publications, selected.kind, &selected.session.attributes, selected.session.creator_id).cloned(),
        )
        .unwrap();
        assert_eq!(projected.id, 1661);
        assert_eq!(projected.data.len(), 496);
    }

    #[test]
    fn migrated_public_match_without_a_validated_transport_fails_closed() {
        let sessions = vec![candidate(public_game(1661, 1005).session, true)];

        assert!(select_invitable_session(1005, sessions, &UplayGameSessionRegistry::default()).is_none());
    }

    #[test]
    fn migrated_public_match_fallback_rejects_a_non_member() {
        let sessions = vec![public_game(1661, 1005)];
        let publications = UplayGameSessionRegistry {
            current: HashMap::new(),
            last_public: HashMap::from([(1005, publication(1659, false))]),
        };

        assert!(select_invitable_session(1021, sessions, &publications).is_none());
    }

    #[test]
    fn unrelated_publication_cannot_route_into_an_active_public_match() {
        let sessions = vec![public_game(1467, 1005), session(1469, 1021, false)];
        let publications = registry(HashMap::from([(1005, publication(1464, false)), (1021, publication(1469, false))]));

        let selected = select_invitable_session(1021, sessions, &publications).unwrap();
        assert_eq!(selected.session.session_id, 1469);
        assert_eq!(selected.kind, INVITE_KIND_LOBBY_PARTY);
    }

    #[test]
    fn missing_sender_publication_fails_closed() {
        let sessions = vec![session(1330, 1003, true), session(1327, 1005, false)];

        assert!(select_invitable_session(1003, sessions, &UplayGameSessionRegistry::default()).is_none());
    }

    #[test]
    fn current_lobby_publication_outranks_a_preserved_solo_fallback() {
        let sessions = vec![session(1347, 1021, false), session(1335, 1005, false)];
        let publications = registry(HashMap::from([(1021, publication(1335, false)), (1005, publication(1335, false))]));

        let selected = select_invitable_session(1021, sessions, &publications).unwrap();
        assert_eq!(selected.session.session_id, 1335);
        assert_eq!(selected.kind, INVITE_KIND_LOBBY_PARTY);
    }

    #[test]
    fn stale_solo_lobby_is_not_a_fallback_when_current_lobby_is_absent() {
        let sessions = vec![session(1347, 1021, false)];
        let publications = registry(HashMap::from([(1021, publication(1335, false))]));

        assert!(select_invitable_session(1021, sessions, &publications).is_none());
    }

    #[test]
    fn private_delivery_requires_current_invite_only_publication() {
        assert!(invite_publication_is_current(
            INVITE_KIND_PRIVATE_ROOM,
            1330,
            "113 => 0;3 => 0;4 => 8",
            Some(&publication(1327, true))
        ));
        assert!(!invite_publication_is_current(
            INVITE_KIND_PRIVATE_ROOM,
            1330,
            "113 => 0;3 => 0;4 => 8",
            Some(&publication(1327, false))
        ));
        assert!(!invite_publication_is_current(INVITE_KIND_PRIVATE_ROOM, 1330, "113 => 0;3 => 0;4 => 8", None));
        assert!(invite_publication_is_current(
            INVITE_KIND_PRIVATE_ROOM,
            1330,
            "113 => 0;3 => 12;4 => 0",
            Some(&publication(1327, false))
        ));
        assert!(!invite_publication_is_current(
            INVITE_KIND_PRIVATE_ROOM,
            1330,
            "113 => 0;3 => 12;4 => 0",
            Some(&publication(1327, true))
        ));
    }

    #[test]
    fn lobby_delivery_requires_the_hosts_exact_current_publication() {
        assert!(invite_publication_is_current(
            INVITE_KIND_LOBBY_PARTY,
            1335,
            "113 => 1;3 => 8;4 => 0",
            Some(&publication(1335, false))
        ));
        assert!(!invite_publication_is_current(
            INVITE_KIND_LOBBY_PARTY,
            1347,
            "113 => 1;3 => 8;4 => 0",
            Some(&publication(1335, false))
        ));
        assert!(!invite_publication_is_current(
            INVITE_KIND_LOBBY_PARTY,
            1335,
            "113 => 1;3 => 8;4 => 0",
            Some(&publication(1335, true))
        ));
        assert!(!invite_publication_is_current(INVITE_KIND_LOBBY_PARTY, 1335, "113 => 1;3 => 8;4 => 0", None));
    }
}

fn authenticated_user_id<T>(request: &Request<T>) -> Result<u32, Status> {
    request
        .metadata()
        .get("user_id")
        .ok_or_else(|| Status::unauthenticated("Missing authenticated user"))?
        .to_str()
        .map_err(|_| Status::unauthenticated("Invalid authenticated user"))?
        .parse()
        .map_err(|_| Status::unauthenticated("Invalid authenticated user"))
}

#[cfg(test)]
mod authenticated_user_tests {
    use super::*;

    #[test]
    fn accepts_valid_interceptor_metadata() {
        let mut request = Request::new(());
        request.metadata_mut().insert("user_id", 42_u32.into());
        assert_eq!(authenticated_user_id(&request).unwrap(), 42);
    }

    #[test]
    fn rejects_missing_or_malformed_metadata_without_panicking() {
        let missing = authenticated_user_id(&Request::new(())).unwrap_err();
        assert_eq!(missing.code(), tonic::Code::Unauthenticated);

        let mut malformed = Request::new(());
        malformed.metadata_mut().insert("user_id", "not-a-number".parse().unwrap());
        let malformed = authenticated_user_id(&malformed).unwrap_err();
        assert_eq!(malformed.code(), tonic::Code::Unauthenticated);
    }
}

/// Implements the `Friends` gRPC service.
pub struct MyFriends {
    logger: Logger,
    storage: Arc<Storage>,
    debug_config: Arc<DebugConfig>,
    uplay_game_sessions: Arc<RwLock<UplayGameSessionRegistry>>,
}

#[tonic::async_trait]
impl Friends for MyFriends {
    /// Handles friend invitation requests.
    ///
    /// Extracts sender and receiver IDs, adds the invite to storage, and returns a response.
    async fn invite(&self, request: Request<friends::InviteRequest>) -> Result<Response<friends::InviteResponse>, Status> {
        let sender = authenticated_user_id(&request)?;
        debug!(self.logger, "Invite request: {:?} from {}", request, sender);
        let receiver = request.into_inner().id;

        let Some(receiver_id) = self
            .storage
            .find_user_id_by_ubi_id_async(&receiver)
            .await
            .map_err(|e| Status::internal(format!("Couldn't add invite: {e:?}")))?
        else {
            return Err(Status::not_found("User not found"));
        };
        if receiver_id == sender {
            return Err(Status::invalid_argument("Cannot invite yourself"));
        }

        let invitable_sessions = self
            .storage
            .find_invitable_session_candidates_async(sender)
            .await
            .map_err(|e| Status::internal(format!("Couldn't resolve invite target: {e:?}")))?;
        // Storage deliberately keeps a private room first during the short
        // lobby-to-room participant-capture window. It may also retain that
        // room while native host migration settles. Only the host's current
        // invite-only Uplay publication proves the room is still the active
        // invitation target; after everyone returns to a lobby, skip the stale
        // room and use the remaining Party lobby instead.
        let selected = {
            let publications = self.uplay_game_sessions.read().await;
            select_invitable_session(sender, invitable_sessions, &publications)
        };
        let Some(selected) = selected else {
            return Err(Status::failed_precondition("No active lobby or private room to invite into"));
        };
        let InvitableSessionSelection { session, kind } = selected;
        if !session_is_cooperative(&session) {
            let sender_user = self
                .storage
                .find_user_by_id_async(sender)
                .await
                .map_err(|e| Status::internal(format!("Couldn't resolve sender mode: {e:?}")))?
                .ok_or_else(|| Status::not_found("Sender not found"))?;
            let receiver_user = self
                .storage
                .find_user_by_id_async(receiver_id)
                .await
                .map_err(|e| Status::internal(format!("Couldn't resolve receiver mode: {e:?}")))?
                .ok_or_else(|| Status::not_found("User not found"))?;
            let context = if kind == INVITE_KIND_PRIVATE_ROOM { "private-room" } else { "lobby-party" };
            crate::gameplay_modes::ensure_compatible(&sender_user.username, &receiver_user.username, context).map_err(Status::failed_precondition)?;
        }
        if self
            .storage
            .invite_target_already_contains_async(receiver_id, kind, session.session_id)
            .await
            .map_err(|e| Status::internal(format!("Couldn't inspect invite target: {e:?}")))?
        {
            return Err(Status::already_exists(if kind == INVITE_KIND_PRIVATE_ROOM {
                "Already in the same room"
            } else {
                "Already in the same party"
            }));
        }

        let Some(invite_id) = self
            .storage
            .add_invite_async(sender, receiver_id, kind, session.session_type, session.session_id)
            .await
            .map_err(|e| Status::internal(format!("Couldn't add invite: {e:?}")))?
        else {
            return Err(Status::failed_precondition("Invite target is no longer joinable"));
        };
        info!(
            self.logger,
            "InviteBound";
            "invite_id" => invite_id,
            "kind" => kind,
            "sender_id" => sender,
            "receiver_id" => receiver_id,
            "session_type" => session.session_type,
            "session_id" => session.session_id,
        );
        Ok(Response::new(friends::InviteResponse {}))
    }

    /// Handles requests to list friends.
    ///
    /// Retrieves all users from storage and marks them as online based on debug configuration.
    async fn list(&self, request: Request<friends::ListRequest>) -> Result<Response<friends::ListResponse>, Status> {
        let user_id = authenticated_user_id(&request)?;
        debug!(self.logger, "Friendlist request: {:?} from {}", request, user_id);
        let users = self.storage.list_game_ready_users_async().await.map_err(|e| Status::internal(format!("{e}")))?;
        let friends = users
            .into_iter()
            .filter(|user| user.id != user_id)
            .map(|u| Friend {
                id: u.ubi_id,
                username: u.username,
                is_online: u.is_online || self.debug_config.mark_all_as_online,
            })
            .collect();
        let resp = friends::ListResponse { friends };
        Ok(Response::new(resp))
    }
}

/// Authenticates a gRPC request using the same bounded, source-bound token
/// accepted by the sidecar control plane.
async fn check_token<T>(
    logger: &Logger,
    config: &AuthTokenConfig,
    storage: &Arc<Storage>,
    overlay_network: OverlayClientNetwork,
    mut req: Request<T>,
) -> Result<Request<T>, Status> {
    let source_ip = request_source_ip(&req, overlay_network)?;
    let token = req.metadata().get("authorization").ok_or(Status::unauthenticated("Missing authorization"))?;
    let token = token.to_str().map_err(|_| Status::unauthenticated("Invalid token"))?;
    let claims = verify_auth_token(config, token, source_ip, unix_time_seconds()?).map_err(|_| Status::unauthenticated("Invalid token"))?;
    let identity = storage
        .find_auth_identity_by_user_id_async(claims.user_id)
        .await
        .map_err(|_| Status::unauthenticated("Invalid token"))?;
    validate_auth_identity(&claims, identity.as_ref()).map_err(|_| Status::unauthenticated("Invalid token"))?;
    debug!(logger, "Valid token for user {}", claims.user_id);
    storage
        .touch_api_presence_async(claims.user_id)
        .await
        .map_err(|error| Status::internal(format!("Presence heartbeat failed: {error}")))?;
    req.metadata_mut().remove("authorization");
    req.metadata_mut().insert("user_id", claims.user_id.into());

    Ok(req)
}

/// Implements the `Users` gRPC service.
pub struct MyUsers {
    logger: Logger,
    token_config: AuthTokenConfig,
    storage: Arc<Storage>,
    overlay_network: OverlayClientNetwork,
}

#[tonic::async_trait]
impl Users for MyUsers {
    /// Handles user login requests.
    ///
    /// Authenticates the user against the storage and generates an authorization token upon successful login.
    async fn login(&self, request: Request<users::LoginRequest>) -> Result<Response<users::LoginResponse>, Status> {
        let source_ip = request_source_ip(&request, self.overlay_network)?;
        let request = request.into_inner();
        let username = request.username;
        let password = request.password;

        let maybe_user = self
            .storage
            .login_user_async(&username, &password)
            .await
            .map_err(|e| Status::internal(format!("Login error: {e:?}")))?;

        let user_id = maybe_user.map_err(|err| match err {
            LoginError::InvalidPassword => Status::unauthenticated("Invalid login"),
            LoginError::NotFound => Status::not_found("Unknown user"),
        })?;

        let identity = self
            .storage
            .find_auth_identity_by_user_id_async(user_id)
            .await
            .map_err(|error| Status::internal(format!("Login identity lookup failed: {error}")))?
            .ok_or_else(|| Status::unauthenticated("Invalid login"))?;
        let issued_at = unix_time_seconds()?;
        let expires_at = issued_at
            .checked_add(self.token_config.ttl_seconds)
            .ok_or_else(|| Status::internal("Token expiry is out of range"))?;
        let token = issue_auth_token(
            &self.token_config,
            &AuthTokenClaims {
                user_id: identity.user_id,
                username: identity.username.clone(),
                source_ip: source_ip.to_string(),
                issued_at,
                expires_at,
                auth_version: identity.auth_version,
            },
        )?;

        info!(self.logger, "Login successful for {}", identity.username);
        Ok(Response::new(users::LoginResponse {
            error: String::new(),
            token,
            user: Some(User {
                id: identity.account_id,
                username: identity.username,
                ips: vec![source_ip.to_string()],
            }),
        }))
    }

    /// Handles user registration requests.
    ///
    /// Registers a new user in the storage, handling potential conflicts like duplicate usernames or Ubisoft IDs.
    async fn register(&self, request: Request<users::RegisterRequest>) -> Result<Response<users::RegisterResponse>, Status> {
        let request = request.into_inner();
        let username = request.username;
        let password = request.password;
        let ubi_id = request.ubi_id;

        let recovery_code = generate_recovery_code();
        let recovery_code_hash = hash_recovery_code(&recovery_code);
        let error = if let Err(err) = self
            .storage
            .register_user_with_recovery_async(&username, &password, Some(&ubi_id), Some(&recovery_code_hash))
            .await
        {
            match err.downcast::<sqlx::Error>() {
                Ok(sqlx::Error::Database(db_err)) => {
                    if db_err.is_unique_violation() {
                        return Err(Status::already_exists(String::from("Username already taken or Ubisoft ID already registered")));
                    }
                    return Err(Status::internal(db_err.to_string()));
                }
                Ok(err) => return Err(Status::internal(err.to_string())),
                Err(err) => err.to_string(),
            }
        } else {
            String::new()
        };
        info!(self.logger, "New user {username} ({ubi_id}) registered");
        Ok(Response::new(users::RegisterResponse { error, user: None, recovery_code }))
    }

    /// Resets a password using a one-time recovery code. A successful reset
    /// rotates the code so replaying a leaked or previously used code fails.
    async fn reset_password(&self, request: Request<users::ResetPasswordRequest>) -> Result<Response<users::ResetPasswordResponse>, Status> {
        let request = request.into_inner();
        let username = request.username.trim();
        let normalized_code = normalize_recovery_code(&request.recovery_code);
        if username.is_empty() || username.len() > 64 || normalized_code.len() != RECOVERY_CODE_LENGTH || request.new_password.len() < 6 || request.new_password.len() > 128 {
            return Err(Status::invalid_argument("Invalid recovery request"));
        }

        let supplied_hash = hash_recovery_code(&normalized_code);
        let replacement_code = generate_recovery_code();
        let replacement_hash = hash_recovery_code(&replacement_code);
        let reset = self
            .storage
            .reset_password_with_recovery_async(username, &supplied_hash, &request.new_password, &replacement_hash)
            .await
            .map_err(|error| Status::internal(format!("Password reset failed: {error}")))?;

        if !reset {
            // Keep account existence and recovery-code state private.
            return Err(Status::unauthenticated("Invalid recovery credentials"));
        }

        info!(self.logger, "Password reset completed for {username}");
        Ok(Response::new(users::ResetPasswordResponse { recovery_code: replacement_code }))
    }
}

/// Implements the `Misc` gRPC service.
pub struct MyMisc {
    logger: Logger,
    storage: Arc<Storage>,
    debug_config: Arc<DebugConfig>,
    uplay_game_sessions: Arc<RwLock<UplayGameSessionRegistry>>,
}

#[tonic::async_trait]
impl Misc for MyMisc {
    /// Handles event requests, primarily for retrieving pending friend invites.
    async fn event(&self, request: Request<misc::EventRequest>) -> Result<Response<misc::EventResponse>, Status> {
        let user_id = authenticated_user_id(&request)?;
        if self.storage.lobby_restore_split_cleanup_due() {
            let storage = Arc::clone(&self.storage);
            let resolved = tokio::task::spawn_blocking(move || storage.expire_lobby_restore_split_transitions())
                .await
                .map_err(|error| Status::internal(format!("Couldn't join lobby-restore split worker: {error:?}")))?
                .map_err(|error| Status::internal(format!("Couldn't expire lobby-restore split transition: {error:?}")))?;
            if resolved > 0 {
                info!(self.logger, "LobbyRestoreDeferredRemovalCompleted"; "resolved_members" => resolved);
            }
        }
        if self.storage.native_host_migration_cleanup_due() {
            let storage = Arc::clone(&self.storage);
            let resolved = tokio::task::spawn_blocking(move || storage.expire_native_host_migrations())
                .await
                .map_err(|error| Status::internal(format!("Couldn't join host-migration fallback worker: {error:?}")))?
                .map_err(|error| Status::internal(format!("Couldn't expire host migration: {error:?}")))?;
            if resolved > 0 {
                info!(self.logger, "NativeHostMigrationFallbackCompleted"; "resolved_sessions" => resolved);
            }
        }
        if self.storage.pending_quick_match_member_leave_cleanup_due() {
            let storage = Arc::clone(&self.storage);
            let resolved = tokio::task::spawn_blocking(move || storage.expire_pending_quick_match_member_leaves())
                .await
                .map_err(|error| Status::internal(format!("Couldn't join quick-match member-leave worker: {error:?}")))?
                .map_err(|error| Status::internal(format!("Couldn't expire quick-match member leave: {error:?}")))?;
            if resolved > 0 {
                info!(self.logger, "QuickMatchMemberLeaveFallbackCompleted"; "resolved_members" => resolved);
            }
        }
        let party = project_party_roster(
            self.storage
                .find_party_roster_async(user_id)
                .await
                .map_err(|error| Status::internal(format!("Couldn't resolve Party roster: {error:?}")))?,
        );
        let Some(invite) = self.storage.take_invite_async(user_id).await.map_err(|e| {
            error!(self.logger, "Error getting latest invite for user: {e}");
            Status::internal(format!("{e:?}"))
        })?
        else {
            return Ok(Response::new(misc::EventResponse { invite: None, party }));
        };

        let Some(sender) = self.storage.find_user_by_id_async(invite.sender).await.map_err(|e| {
            error!(self.logger, "Error getting ubi id for user: {e}");
            Status::internal(format!("{e:?}"))
        })?
        else {
            return Err(Status::not_found(""));
        };
        let session_target = self
            .storage
            .find_active_session_invite_target_async(invite.session_type, invite.session_id)
            .await
            .map_err(|error| Status::internal(format!("Couldn't resolve invite session host: {error:?}")))?;
        let session_host_id = session_target.as_ref().map(|(creator_id, _)| *creator_id);
        let session_attributes = session_target.as_ref().map_or("", |(_, attributes)| attributes.as_str());
        let host_publication = if let Some(host_id) = session_host_id {
            let publications = self.uplay_game_sessions.read().await;
            invite_host_publication(&publications, invite.kind, session_attributes, host_id).cloned()
        } else {
            None
        };
        let published_game_session = if invite_uplay_payload_owner(invite.kind, session_host_id).is_some() {
            host_publication.clone()
        } else {
            None
        };
        if !invite_publication_is_current(invite.kind, invite.session_id, session_attributes, host_publication.as_ref()) {
            self.storage
                .consume_invite_async(user_id, invite.id)
                .await
                .map_err(|error| Status::internal(format!("Couldn't retire stale invitation: {error:?}")))?;
            warn!(
                self.logger,
                "StaleInviteSuppressed";
                "invite_id" => invite.id,
                "sender_id" => invite.sender,
                "session_host_id" => session_host_id.unwrap_or(0),
                "receiver_id" => invite.receiver,
                "session_id" => invite.session_id,
            );
            return Ok(Response::new(misc::EventResponse { invite: None, party }));
        }
        let published_uplay_game_session_id = published_game_session.as_ref().map_or(0, |session| session.id);
        let game_session = project_invite_uplay_game_session(invite.kind, invite.session_id, published_game_session);
        if invite.kind == INVITE_KIND_PRIVATE_ROOM {
            if let Some(projected) = game_session.as_ref() {
                info!(
                    self.logger,
                    "PrivateInviteUplayTargetProjected";
                    "invite_id" => invite.id,
                    "sender_id" => invite.sender,
                    "session_host_id" => session_host_id.unwrap_or(0),
                    "receiver_id" => invite.receiver,
                    "session_type" => invite.session_type,
                    "private_session_id" => invite.session_id,
                    "published_uplay_game_session_id" => published_uplay_game_session_id,
                    "projected_uplay_game_session_id" => projected.id,
                    "payload_bytes" => projected.data.len(),
                    "payload_unchanged" => true,
                    "flags_unchanged" => true,
                    "invite_only_unchanged" => true,
                    "response_only" => true,
                );
            }
        }
        if invite_requires_uplay_game_session(invite.kind) && game_session.is_none() {
            warn!(
                self.logger,
                "InviteUplayGameSessionUnavailable";
                "invite_id" => invite.id,
                "kind" => invite.kind,
                "sender_id" => invite.sender,
                "session_host_id" => session_host_id.unwrap_or(0),
                "receiver_id" => invite.receiver,
                "session_id" => invite.session_id,
            );
        }
        info!(
            self.logger,
            "InviteEventDelivered";
            "invite_id" => invite.id,
            "delivery_count" => invite.delivery_count,
            "kind" => invite.kind,
            "sender_id" => invite.sender,
            "session_host_id" => session_host_id.unwrap_or(0),
            "receiver_id" => invite.receiver,
            "session_type" => invite.session_type,
            "session_id" => invite.session_id,
            "uplay_game_session_present" => game_session.is_some(),
            "uplay_game_session_published_id" => published_uplay_game_session_id,
            "uplay_game_session_id" => game_session.as_ref().map_or(0, |session| session.id),
            "uplay_target_projected" => invite_projects_exact_uplay_target(invite.kind) && game_session.is_some(),
            "uplay_game_session_bytes" => game_session.as_ref().map_or(0, |session| session.data.len()),
            "uplay_game_session_flags" => game_session.as_ref().map_or(0, |session| session.flags),
            "uplay_game_session_invite_only" => game_session.as_ref().is_some_and(|session| session.invite_only),
        );
        Ok(Response::new(misc::EventResponse {
            invite: Some(misc::InviteEvent {
                id: invite.id,
                sender: Some(User {
                    id: sender.ubi_id,
                    username: sender.username,
                    ips: vec![],
                }),
                force_join: self.debug_config.force_joins || invite_forces_join(invite.kind),
                kind: invite.kind,
                game_session,
            }),
            party,
        }))
    }

    async fn accept_invite(&self, request: Request<misc::AcceptInviteRequest>) -> Result<Response<misc::AcceptInviteResponse>, Status> {
        let user_id = authenticated_user_id(&request)?;
        let invite_id = request.into_inner().id;
        let Some(invite) = self
            .storage
            .accept_invite_async(user_id, invite_id)
            .await
            .map_err(|error| Status::internal(format!("Couldn't accept invite: {error:?}")))?
        else {
            return Err(Status::not_found("Invitation expired or unavailable"));
        };
        if matches!(invite.kind, INVITE_KIND_PRIVATE_ROOM | INVITE_KIND_LOBBY_PARTY) {
            let session_target = self
                .storage
                .find_active_session_invite_target_async(invite.session_type, invite.session_id)
                .await
                .map_err(|error| Status::internal(format!("Couldn't resolve invite session host: {error:?}")))?;
            let session_host_id = session_target.as_ref().map(|(creator_id, _)| *creator_id);
            let session_attributes = session_target.as_ref().map_or("", |(_, attributes)| attributes.as_str());
            let publication = if let Some(host_id) = session_host_id {
                let publications = self.uplay_game_sessions.read().await;
                invite_host_publication(&publications, invite.kind, session_attributes, host_id).cloned()
            } else {
                None
            };
            if !invite_publication_is_current(invite.kind, invite.session_id, session_attributes, publication.as_ref()) {
                self.storage
                    .consume_invite_async(user_id, invite.id)
                    .await
                    .map_err(|error| Status::internal(format!("Couldn't retire stale invitation: {error:?}")))?;
                warn!(
                    self.logger,
                    "StaleInviteAcceptanceRejected";
                    "invite_id" => invite.id,
                    "sender_id" => invite.sender,
                    "session_host_id" => session_host_id.unwrap_or(0),
                    "receiver_id" => invite.receiver,
                    "session_id" => invite.session_id,
                );
                return Err(Status::failed_precondition("Invitation target is no longer active"));
            }
        }
        info!(
            self.logger,
            "InviteAccepted";
            "invite_id" => invite.id,
            "kind" => invite.kind,
            "sender_id" => invite.sender,
            "receiver_id" => invite.receiver,
            "session_type" => invite.session_type,
            "session_id" => invite.session_id,
        );
        Ok(Response::new(misc::AcceptInviteResponse {
            accepted: true,
            kind: invite.kind,
        }))
    }

    async fn set_game_session(&self, request: Request<misc::SetGameSessionRequest>) -> Result<Response<misc::SetGameSessionResponse>, Status> {
        let user_id = authenticated_user_id(&request)?;
        let request = request.into_inner();
        if !validate_uplay_game_session(&request.data) {
            return Err(Status::invalid_argument("Uplay GameSession data is empty or too large"));
        }
        let session = misc::UplayGameSession {
            id: request.id,
            data: request.data,
            flags: request.flags,
            invite_only: request.invite_only,
        };
        info!(
            self.logger,
            "UplayGameSessionPublished";
            "user_id" => user_id,
            "game_session_id" => session.id,
            "data_bytes" => session.data.len(),
            "flags" => session.flags,
            "invite_only" => session.invite_only,
        );
        self.storage.record_published_game_session(user_id, session.id);
        self.uplay_game_sessions.write().await.publish(user_id, session);
        Ok(Response::new(misc::SetGameSessionResponse {}))
    }

    async fn clear_game_session(&self, request: Request<misc::ClearGameSessionRequest>) -> Result<Response<misc::ClearGameSessionResponse>, Status> {
        let user_id = authenticated_user_id(&request)?;
        self.storage.clear_published_game_session(user_id);
        let removed = self.uplay_game_sessions.write().await.clear_current(user_id);
        info!(self.logger, "UplayGameSessionCleared"; "user_id" => user_id, "removed" => removed);
        Ok(Response::new(misc::ClearGameSessionResponse {}))
    }

    /// Handles P2P testing requests.
    ///
    /// Attempts to establish a UDP connection with the client and exchanges a challenge.
    async fn test_p2p(&self, request: Request<misc::TestP2pRequest>) -> Result<Response<misc::TestP2pResponse>, Status> {
        let mut client_addr = request.remote_addr().ok_or(Status::failed_precondition("no client address"))?;
        client_addr.set_port(13_000);
        let request = request.into_inner();
        let mut resp_data = b"P2P Test - ".to_vec();
        resp_data.extend(request.challenge);

        let buf = tokio::time::timeout(std::time::Duration::from_secs(5), async {
            let socket = tokio::net::UdpSocket::bind("0.0.0.0:0").await?;
            socket.connect(client_addr).await?;
            socket.send(&resp_data).await?;
            let mut buf = [0; 1024];
            let len = socket.recv(&mut buf).await?;
            Ok(buf[..len].to_vec())
        });
        let Ok(challenge) = buf.await else {
            return Err(Status::deadline_exceeded("client didn't response in time"));
        };
        let Ok(challenge): std::io::Result<Vec<u8>> = challenge else {
            return Err(Status::unknown(format!("P2P communication failed: {}", challenge.unwrap_err())));
        };

        Ok(Response::new(misc::TestP2pResponse { challenge }))
    }
}

/// Implements the `UsersAdmin` gRPC service for administrative user management.
pub struct MyUsersAdmin {
    logger: Logger,
    storage: Arc<Storage>,
}

#[tonic::async_trait]
impl UsersAdmin for MyUsersAdmin {
    /// Handles requests to list all users.
    ///
    /// Retrieves user information and their associated IP addresses from storage.
    async fn list(&self, request: Request<users::ListRequest>) -> Result<Response<users::ListResponse>, Status> {
        let _request = request.into_inner();
        let Ok(db_users) = self.storage.list_users_async().await else {
            return Err(Status::internal("Error listing users"));
        };

        let mut users = vec![];
        for user in db_users {
            let urls = self.storage.list_urls(user.id).await.map_err(|e| Status::internal(format!("{e:?}")))?;
            let ips = urls
                .into_iter()
                .map(|u| u.parse::<StationURL>())
                .filter_map(Result::ok)
                .map(|u| u.address)
                .collect::<HashSet<_>>()
                .into_iter()
                .collect();
            users.push(User {
                id: user.ubi_id,
                username: user.username,
                ips,
            });
        }

        let resp = users::ListResponse {
            #[allow(clippy::cast_possible_wrap, clippy::cast_possible_truncation)]
            total: users.len() as i32,
            users,
        };
        Ok(Response::new(resp))
    }

    /// Handles requests to retrieve a single user's details.
    ///
    /// Retrieves user information and their associated IP addresses from storage based on the provided user ID.
    async fn get(&self, request: Request<users::GetRequest>) -> Result<Response<users::GetResponse>, Status> {
        let request = request.into_inner();
        let user_id = request.id;
        let user_id: u32 = user_id.parse().map_err(|_| Status::invalid_argument("Invalid ID"))?;
        let Ok(user) = self.storage.find_user_by_id_async(user_id).await else {
            return Err(Status::internal("Error retrieving user"));
        };

        let Some(user) = user else {
            return Err(Status::not_found("User not found"));
        };

        let urls = self.storage.list_urls(user.id).await.map_err(|e| Status::internal(format!("{e:?}")))?;
        let ips = urls
            .into_iter()
            .map(|u| u.parse::<StationURL>())
            .filter_map(Result::ok)
            .map(|u| u.address)
            .collect::<HashSet<_>>()
            .into_iter()
            .collect();

        let resp = users::GetResponse {
            user: Some(User {
                id: user.ubi_id,
                username: user.username,
                ips,
            }),
        };
        Ok(Response::new(resp))
    }

    /// Handles requests to delete a user.
    ///
    /// Deletes a user from storage based on their Ubisoft ID.
    async fn delete(&self, request: Request<users::DeleteRequest>) -> Result<Response<users::DeleteResponse>, Status> {
        let request = request.into_inner();
        let Some(user) = self
            .storage
            .find_user_by_ubi_id_async(&request.id)
            .await
            .map_err(|_| Status::invalid_argument("Invalid ID"))?
        else {
            return Err(Status::not_found("User not found"));
        };
        match self.storage.delete_user_async(user.id).await {
            Ok(()) => {
                warn!(self.logger, "Deleted user {user:?}");
                Ok(Response::new(users::DeleteResponse {}))
            }
            Err(e) => Err(Status::internal(format!("{e:?}"))),
        }
    }
}

/// Implements the `GamesAdmin` gRPC service for administrative game session management.
struct MyGamesAdmin {
    logger: Logger,
    storage: Arc<Storage>,
}

#[tonic::async_trait]
impl GamesAdmin for MyGamesAdmin {
    /// Handles requests to list all active game sessions.
    ///
    /// Retrieves game session information from storage and parses game-specific attributes.
    async fn list(&self, request: Request<games::ListRequest>) -> Result<Response<games::ListResponse>, Status> {
        let _request = request.into_inner();
        let sessions = self.storage.list_game_sessions_async().await.map_err(|e| {
            error!(self.logger, "Error listing games: {e}");
            Status::internal(format!("{e:?}"))
        })?;

        Ok(Response::new(games::ListResponse {
            games: sessions
                .into_iter()
                .filter_map(|s| {
                    let attributes: QList<Property> = s
                        .attributes
                        .parse()
                        .inspect_err(|e| {
                            error!(self.logger, "Error parsing game type: {e}");
                        })
                        .unwrap_or_default();
                    let attributes: HashMap<u32, u32> = attributes.0.into_iter().map(|p| (p.id, p.value)).collect();

                    // just guessing that 105 gives me what I want... ¯\_(ツ)_/¯
                    let game_type = match attributes.get(&105) {
                        None => String::from("Lobby"),
                        Some(&1) => String::from("SvM"),
                        Some(&2) => String::from("Coop"),
                        Some(v) => format!("Unknown({v})"),
                    };

                    let Some(creator) = s.participants.iter().find(|participant| participant.user_id == s.creator_id) else {
                        error!(self.logger, "Skipping inconsistent game session without creator"; "session_id" => s.session_id, "creator_id" => s.creator_id);
                        return None;
                    };
                    let creator = creator.name.clone();

                    Some(games::Game {
                        id: s.session_id,
                        creator,
                        participants: s.participants.into_iter().filter(|p| p.user_id != s.creator_id).map(|p| p.name).collect(),
                        game_type,
                    })
                })
                .collect(),
        }))
    }

    /// Handles requests to delete a game session.
    ///
    /// Deletes a game session from storage based on its session ID.
    async fn delete(&self, request: Request<games::DeleteRequest>) -> Result<Response<games::DeleteResponse>, Status> {
        let request = request.into_inner();
        let session_id = request.id;
        match self.storage.delete_game_session_by_id_async(session_id).await {
            Ok(()) => {
                warn!(self.logger, "Deleted game session {session_id:?}");
                Ok(Response::new(games::DeleteResponse {}))
            }
            Err(e) => Err(Status::internal(format!("{e:?}"))),
        }
    }
}

/// Creates an authenticated gRPC service.
///
/// This function wraps a gRPC service with an interceptor that validates
/// an authorization token present in the request metadata.
fn authenticated<S>(
    service: S,
    logger: Logger,
    token_config: AuthTokenConfig,
    storage: Arc<Storage>,
    overlay_network: OverlayClientNetwork,
) -> tonic_async_interceptor::AsyncInterceptedService<S, impl tonic_async_interceptor::AsyncInterceptor<Future = impl Future<Output = Result<Request<()>, Status>> + Send> + Clone>
{
    tonic_async_interceptor::AsyncInterceptedService::new(service, move |req: Request<()>| {
        let storage = Arc::clone(&storage);
        let logger = logger.clone();
        let token_config = token_config.clone();
        async move {
            let this = check_token(&logger, &token_config, &storage, overlay_network, req).await;
            if let Err(ref e) = this {
                error!(logger, "Auth failure: {e}");
            }
            this
        }
    })
}

/// Creates a gRPC service with preshared key authentication.
///
/// This function wraps a gRPC service with an interceptor that validates
/// a preshared key present in the "authorization" metadata of the request.
fn preshared_authentication<S>(service: S, key: String) -> tonic::service::interceptor::InterceptedService<S, impl tonic::service::Interceptor + Clone> {
    tonic::service::interceptor::InterceptedService::new(service, move |req: Request<()>| {
        let header_value = req.metadata().get("authorization").ok_or(Status::unauthenticated("Missing authorization"))?;
        let token = header_value.to_str().map_err(|_| Status::unauthenticated("Invalid token"))?;

        if token == key {
            Ok(req)
        } else {
            Err(Status::permission_denied("Invalid token"))
        }
    })
}

/// Encodes a byte slice into a Base32 string.
fn base32(data: &[u8]) -> String {
    let mut s = String::new();
    for chunk in data.chunks(5) {
        let mut value = 0u64;
        let mut i = 0;
        for c in chunk {
            value <<= 8;
            value |= u64::from(*c);
            i += 1;
        }
        value <<= 8 * (5 - i);
        if i == 8 {
            i = 0;
        } else {
            i = 8 - (i * 8 + 4) / 5;
        }
        for i in (i..8).rev() {
            let ch = match ((value >> (5 * i)) & 0b11111) as u8 {
                b @ 0..=9 => b'0' + b,
                b @ 10..=31 => b'A' + b - 10,
                b => unreachable!("{b:?}"),
            };
            s.push(ch as char);
        }
    }
    s
}

/// Starts the gRPC server, binding to the specified address and registering services.
///
/// This function initializes the server, sets up reflection services, and registers
/// the Friends, Users, and Misc gRPC services. Optionally, it enables and registers
/// administrative services (UsersAdmin and GamesAdmin) if `enable_admin_services` is true.
pub async fn start_server(
    logger: Logger,
    storage: Arc<Storage>,
    server_addr: SocketAddr,
    debug_config: Arc<DebugConfig>,
    enable_admin_services: bool,
) -> Result<(), Box<dyn std::error::Error>> {
    let token_config = AuthTokenConfig::from_env()?;
    let overlay_network = OverlayClientNetwork::from_env()?;
    let uplay_game_sessions = Arc::new(RwLock::new(UplayGameSessionRegistry::default()));
    info!(logger, "Listening on {server_addr}");
    let builder = Server::builder()
        .add_service(
            tonic_reflection::server::Builder::configure()
                .register_encoded_file_descriptor_set(users::FILE_DESCRIPTOR_SET)
                .register_encoded_file_descriptor_set(friends::FILE_DESCRIPTOR_SET)
                .register_encoded_file_descriptor_set(misc::FILE_DESCRIPTOR_SET)
                .build_v1alpha()
                .unwrap(),
        )
        .add_service(
            tonic_reflection::server::Builder::configure()
                .register_encoded_file_descriptor_set(users::FILE_DESCRIPTOR_SET)
                .register_encoded_file_descriptor_set(friends::FILE_DESCRIPTOR_SET)
                .register_encoded_file_descriptor_set(misc::FILE_DESCRIPTOR_SET)
                .build_v1()
                .unwrap(),
        )
        .add_service(authenticated(
            FriendsServer::new(MyFriends {
                logger: logger.clone(),
                storage: Arc::clone(&storage),
                debug_config: Arc::clone(&debug_config),
                uplay_game_sessions: Arc::clone(&uplay_game_sessions),
            }),
            logger.clone(),
            token_config.clone(),
            Arc::clone(&storage),
            overlay_network,
        ))
        .add_service(authenticated(
            MiscServer::new(MyMisc {
                logger: logger.clone(),
                storage: Arc::clone(&storage),
                debug_config,
                uplay_game_sessions: Arc::clone(&uplay_game_sessions),
            }),
            logger.clone(),
            token_config.clone(),
            Arc::clone(&storage),
            overlay_network,
        ))
        .add_service(UsersServer::new(MyUsers {
            logger: logger.clone(),
            storage: Arc::clone(&storage),
            token_config,
            overlay_network,
        }));

    let builder = if enable_admin_services {
        warn!(logger, "Enabling admin services");
        let preshared = base32(&secretbox::gen_key().0);
        println!("Admin Key: {preshared}");
        builder
            .add_service(preshared_authentication(
                UsersAdminServer::new(MyUsersAdmin {
                    logger: logger.clone(),
                    storage: Arc::clone(&storage),
                }),
                preshared.clone(),
            ))
            .add_service(preshared_authentication(GamesAdminServer::new(MyGamesAdmin { logger, storage }), preshared))
    } else {
        builder
    };

    builder.serve(server_addr).await?;
    Ok(())
}

#[cfg(test)]
mod invite_target_tests {
    use super::*;

    fn session(attributes: &str) -> GameSession {
        GameSession {
            session_type: 1,
            session_id: 1,
            creator_id: 1,
            attributes: attributes.to_string(),
            participants: Vec::new(),
        }
    }

    #[test]
    fn classifies_private_room_and_lobby_party_sessions() {
        assert_eq!(session_invite_kind(&session("113 => 0;103 => 0;112 => 4")), Some(INVITE_KIND_PRIVATE_ROOM));
        assert_eq!(session_invite_kind(&session("113 => 1;3 => 8;4 => 0")), Some(INVITE_KIND_LOBBY_PARTY));
        assert_eq!(session_invite_kind(&session("not valid attributes")), None);
    }

    #[test]
    fn only_private_and_follow_invites_require_uplay_game_session_data() {
        assert!(invite_requires_uplay_game_session(INVITE_KIND_PRIVATE_ROOM));
        assert!(invite_requires_uplay_game_session(INVITE_KIND_PARTY_FOLLOW));
        assert!(!invite_requires_uplay_game_session(INVITE_KIND_LOBBY_PARTY));
        assert!(!invite_requires_uplay_game_session(INVITE_KIND_LOBBY_RESTORE));
    }

    #[test]
    fn member_invitation_uses_the_real_session_hosts_uplay_payload() {
        assert_eq!(invite_uplay_payload_owner(INVITE_KIND_PRIVATE_ROOM, Some(41)), Some(41));
        assert_eq!(invite_uplay_payload_owner(INVITE_KIND_PARTY_FOLLOW, Some(41)), Some(41));
        assert_eq!(invite_uplay_payload_owner(INVITE_KIND_LOBBY_PARTY, Some(41)), None);
        assert_eq!(invite_uplay_payload_owner(INVITE_KIND_PRIVATE_ROOM, None), None);
    }

    #[test]
    fn party_roster_projection_preserves_leader_and_all_members() {
        let roster = project_party_roster(Some((
            2,
            vec![
                crate::storage::User {
                    id: 2,
                    username: String::from("second"),
                    ubi_id: String::from("account-two"),
                    is_online: true,
                },
                crate::storage::User {
                    id: 3,
                    username: String::from("third"),
                    ubi_id: String::from("account-three"),
                    is_online: true,
                },
            ],
        )))
        .unwrap();
        assert_eq!(roster.leader.as_ref().unwrap().id, "account-two");
        assert_eq!(roster.members.len(), 2);
        assert_eq!(roster.members[1].id, "account-three");
    }

    #[test]
    fn follow_and_lobby_restore_invites_force_join() {
        assert!(invite_forces_join(INVITE_KIND_PARTY_FOLLOW));
        assert!(invite_forces_join(INVITE_KIND_LOBBY_RESTORE));
        assert!(!invite_forces_join(INVITE_KIND_PRIVATE_ROOM));
        assert!(!invite_forces_join(INVITE_KIND_LOBBY_PARTY));
    }

    #[test]
    fn validates_uplay_game_session_blob_boundaries() {
        assert!(!validate_uplay_game_session(&[]));
        assert!(validate_uplay_game_session(&[1]));
        assert!(validate_uplay_game_session(&vec![0; MAX_UPLAY_GAME_SESSION_DATA]));
        assert!(!validate_uplay_game_session(&vec![0; MAX_UPLAY_GAME_SESSION_DATA + 1]));
    }
}
