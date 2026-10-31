//! Bounded local native-to-launcher context transport, not server authentication.
//! No token is Debug-formatted, persisted or accepted as an account UI token.
use std::net::Ipv4Addr;
use base64::{engine::general_purpose::URL_SAFE_NO_PAD, Engine};
use serde::{Deserialize, Serialize};
use crate::gameplay_contract::{valid_game_instance, ClientContract};
use crate::release_contract::ReleaseIdentity;

pub const DESCRIPTOR_ENV: &str = "FF_PUBLIC_RETURN_V1";
pub const MAX_FRAME: usize = 8192;
pub const MAX_TOKEN: usize = 4096;
pub const HEARTBEAT_SECONDS: u64 = 2;
pub const FRAME_TIMEOUT_SECONDS: u64 = 5;
pub const STARTUP_TIMEOUT_SECONDS: u64 = 10;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Invalid;
impl std::fmt::Display for Invalid {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result { f.write_str("PUBLIC_RETURN_INVALID") }
}
impl std::error::Error for Invalid {}

fn hex(value: &str, length: usize, nonzero: bool) -> bool {
    value.len() == length && value.bytes().all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
        && (!nonzero || value.bytes().any(|b| b != b'0'))
}

#[derive(Clone, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Descriptor {
    pub schema: u32,
    pub pipe: String,
    pub launch_id: String,
    pub launcher_pid: u32,
    pub launcher_creation_filetime: u64,
    pub launcher_path: String,
    pub launcher_sha256: String,
}
impl std::fmt::Debug for Descriptor {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result { f.write_str("PublicReturnDescriptor") }
}
impl Descriptor {
    pub fn parse(raw: &str) -> Result<Self, Invalid> {
        if raw.is_empty() || raw.len() > 4096 { return Err(Invalid); }
        let value: Self = serde_json::from_str(raw).map_err(|_| Invalid)?;
        let suffix = value.pipe.strip_prefix("ff-public-return-").ok_or(Invalid)?;
        let path = value.launcher_path.as_bytes();
        if value.schema != 1 || value.launcher_pid == 0 || value.launcher_creation_filetime == 0
            || !hex(suffix, 32, true) || !hex(&value.launch_id, 32, true)
            || !hex(&value.launcher_sha256, 64, false) || path.len() < 4 || path.len() > 2048
            || !path[0].is_ascii_alphabetic() || path[1] != b':' || path[2] != b'\\'
            || value.launcher_path.chars().any(char::is_control)
            || value.launcher_path.contains('/')
            || value.launcher_path[3..].split('\\').any(|p| p.is_empty() || p == "." || p == ".." || p.contains(':')) {
            return Err(Invalid);
        }
        Ok(value)
    }
}

#[derive(Clone, Deserialize, Serialize, PartialEq, Eq)]
#[serde(tag = "kind", rename_all = "snake_case", deny_unknown_fields)]
pub enum Frame {
    Challenge { schema: u32, launch_id: String, challenge: String },
    Hello { schema: u32, launch_id: String, challenge: String, game_instance: String },
    Invalidate { schema: u32, generation: u64 },
    Authenticated { schema: u32, generation: u64, token: String, account_id: String, username: String },
    Heartbeat { schema: u32, generation: u64 },
}
impl std::fmt::Debug for Frame {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result { f.write_str("PublicReturnFrame [redacted]") }
}
impl Frame {
    pub fn validate(&self) -> Result<(), Invalid> {
        let valid = match self {
            Self::Challenge { schema, launch_id, challenge } => *schema == 1 && hex(launch_id, 32, true) && hex(challenge, 64, true),
            Self::Hello { schema, launch_id, challenge, game_instance } => *schema == 1 && hex(launch_id, 32, true)
                && hex(challenge, 64, true) && valid_game_instance(game_instance),
            Self::Invalidate { schema, generation } | Self::Heartbeat { schema, generation } => *schema == 1 && *generation > 0,
            Self::Authenticated { schema, generation, token, account_id, username } => *schema == 1 && *generation > 0
                && valid_token_shape(token) && valid_account(account_id) && valid_username(username),
        };
        valid.then_some(()).ok_or(Invalid)
    }
    pub fn encode(&self) -> Result<Vec<u8>, Invalid> {
        self.validate()?;
        let bytes = serde_json::to_vec(self).map_err(|_| Invalid)?;
        if bytes.is_empty() || bytes.len() > MAX_FRAME { return Err(Invalid); }
        Ok(bytes)
    }
    pub fn decode(bytes: &[u8]) -> Result<Self, Invalid> {
        if bytes.is_empty() || bytes.len() > MAX_FRAME { return Err(Invalid); }
        let value: Self = serde_json::from_slice(bytes).map_err(|_| Invalid)?;
        value.validate()?;
        Ok(value)
    }
    pub fn hello(self, launch: &str, instance: &str) -> Result<Self, Invalid> {
        self.validate()?;
        match self {
            Self::Challenge { schema, launch_id, challenge } if launch_id == launch && valid_game_instance(instance) =>
                Ok(Self::Hello { schema, launch_id, challenge, game_instance: instance.to_owned() }),
            _ => Err(Invalid),
        }
    }
}

fn decoded(value: &str) -> Result<Vec<u8>, Invalid> {
    if value.is_empty() || !value.bytes().all(|b| b.is_ascii_alphanumeric() || matches!(b, b'_' | b'-')) { return Err(Invalid); }
    let bytes = URL_SAFE_NO_PAD.decode(value).map_err(|_| Invalid)?;
    if URL_SAFE_NO_PAD.encode(&bytes) != value { return Err(Invalid); }
    Ok(bytes)
}
fn token_parts(token: &str) -> Result<(&str, &str), Invalid> {
    if token.is_empty() || token.len() > MAX_TOKEN { return Err(Invalid); }
    let mut parts = token.split('.');
    if parts.next() != Some("v1") { return Err(Invalid); }
    let (payload, signature) = (parts.next().ok_or(Invalid)?, parts.next().ok_or(Invalid)?);
    if parts.next().is_some() { return Err(Invalid); }
    Ok((payload, signature))
}
fn valid_token_shape(token: &str) -> bool {
    token_parts(token).is_ok_and(|(payload, signature)| decoded(payload).is_ok()
        && decoded(signature).is_ok_and(|bytes| bytes.len() == 32))
}
fn valid_username(value: &str) -> bool {
    !value.is_empty() && value.len() <= 64 && value.bytes().all(|b| b.is_ascii_alphanumeric() || matches!(b, b'_' | b'-' | b'.'))
}
fn valid_account(value: &str) -> bool {
    !value.is_empty() && value.len() <= 64 && !value.chars().any(char::is_control)
}

/// Account fields must come from the same successful LoginResponse.user. They
/// are not derived from claims; the launcher compares its independent login.
#[derive(Clone, PartialEq, Eq)]
pub struct ReturnedAuthentication { token: String, account_id: String, username: String }
impl std::fmt::Debug for ReturnedAuthentication {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result { f.write_str("ReturnedAuthentication [redacted]") }
}
impl ReturnedAuthentication {
    pub fn new(token: String, account_id: String, username: String) -> Result<Self, Invalid> {
        if !valid_token_shape(&token) || !valid_account(&account_id) || !valid_username(&username) { return Err(Invalid); }
        Ok(Self { token, account_id, username })
    }
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct GameRun { product: String, instance: String }
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Claims {
    release: ReleaseIdentity,
    user_id: u32,
    username: String,
    source_ip: String,
    issued_at: i64,
    expires_at: i64,
    auth_version: i64,
    game_run: GameRun,
    #[serde(default)]
    observer_admitted_run: Option<String>,
}

/// Expectations from the actual native login owner, never from token claims.
pub struct ContextExpectation<'a> {
    pub username: &'a str,
    pub source_ip: Ipv4Addr,
    pub product: &'a str,
    pub instance: &'a str,
    pub release: &'a ReleaseIdentity,
    pub observer_requested: bool,
}

/// Local purpose/context screening only. Numeric user_id is returned as metadata;
/// no signature/expiry/revocation authentication is performed by the client.
pub fn screen_token(token: &str, expected: &ContextExpectation<'_>) -> Result<u32, Invalid> {
    let (payload, signature) = token_parts(token)?;
    if decoded(signature)?.len() != 32 { return Err(Invalid); }
    let claims: Claims = serde_json::from_slice(&decoded(payload)?).map_err(|_| Invalid)?;
    if claims.user_id == 0 || claims.username != expected.username || claims.username.is_empty() || claims.username.len() > 64
        || !claims.username.bytes().all(|b| b.is_ascii_alphanumeric() || matches!(b, b'_' | b'-' | b'.'))
        || claims.source_ip != expected.source_ip.to_string() || claims.release != *expected.release
        || claims.game_run.product != expected.product || claims.game_run.instance != expected.instance
        || !valid_game_instance(expected.instance)
        || !matches!(ClientContract::parse(expected.product), Some(ClientContract::Game(_)))
        || claims.auth_version <= 0 || claims.expires_at < claims.issued_at
        || claims.observer_admitted_run.as_deref().is_some_and(|value| value != expected.instance)
        || expected.observer_requested && claims.observer_admitted_run.as_deref() != Some(expected.instance) {
        return Err(Invalid);
    }
    Ok(claims.user_id)
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Attempt(u64);

/// A bounded latest-state slot. Caller serializes it with a short mutex; no I/O
/// belongs under that mutex or under the native LOGIN_LOCK.
pub struct ReturnState {
    generation: u64,
    owner: Option<Attempt>,
    token: Option<ReturnedAuthentication>,
    closed: bool,
}
impl std::fmt::Debug for ReturnState {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result { f.write_str("PublicReturnState [redacted]") }
}
impl Default for ReturnState {
    fn default() -> Self { Self { generation: 0, owner: None, token: None, closed: false } }
}
impl ReturnState {
    fn advance(&mut self) -> Result<u64, Invalid> {
        self.token = None;
        match self.generation.checked_add(1) {
            Some(value) if !self.closed => { self.generation = value; Ok(value) }
            _ => { self.owner = None; self.closed = true; Err(Invalid) }
        }
    }
    pub fn begin(&mut self) -> Result<Attempt, Invalid> {
        let attempt = Attempt(self.advance()?);
        self.owner = Some(attempt);
        Ok(attempt)
    }
    pub fn invalidate(&mut self, owner: Attempt) -> Result<bool, Invalid> {
        if self.closed || self.owner != Some(owner) { return Ok(false); }
        self.owner = None;
        self.advance()?;
        Ok(true)
    }
    pub fn publish(&mut self, owner: Attempt, token: ReturnedAuthentication) -> Result<bool, Invalid> {
        if self.closed || self.owner != Some(owner) { return Ok(false); }
        self.advance()?;
        self.owner = None; // Success can be published only once for this attempt.
        self.token = Some(token);
        Ok(true)
    }
    pub fn close(&mut self) {
        let _ = self.advance();
        self.owner = None;
        self.closed = true;
    }
    pub fn snapshot(&self) -> Option<Frame> {
        if self.closed || self.generation == 0 { return None; }
        Some(match &self.token {
            Some(value) => Frame::Authenticated { schema: 1, generation: self.generation,
                token: value.token.clone(), account_id: value.account_id.clone(), username: value.username.clone() },
            None => Frame::Invalidate { schema: 1, generation: self.generation },
        })
    }
    pub fn is_current(&self, frame: &Frame) -> bool {
        !self.closed && match frame {
            Frame::Authenticated { generation, token, account_id, username, .. } => *generation == self.generation
                && self.token.as_ref().is_some_and(|value| value.token == *token && value.account_id == *account_id && value.username == *username),
            Frame::Invalidate { generation, .. } => *generation == self.generation && self.token.is_none(),
            Frame::Heartbeat { generation, .. } => *generation == self.generation,
            _ => false,
        }
    }
    pub fn generation(&self) -> u64 { self.generation }
    pub fn is_closed(&self) -> bool { self.closed }
}

#[cfg(test)]
#[path = "public_return_contract_tests.rs"]
mod tests;
