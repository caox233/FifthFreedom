//! A bounded, single-attempt observation protocol. No admission or live lease.
use serde::{Deserialize, Serialize};
use std::{io, net::Ipv4Addr};
use tokio::io::{AsyncRead, AsyncReadExt, AsyncWrite, AsyncWriteExt};

pub const ENVIRONMENT: &str = "FF_OWNED_CORE_READ_V1";
pub const MAX_FRAME: usize = 2048;
pub fn rejected() -> io::Error { io::Error::other("OWNED_CORE_READ_REJECTED") }
fn hex(value: &str, n: usize) -> bool {
    value.len() == n && value.bytes().any(|x| x != b'0') && value.bytes().all(|x| x.is_ascii_digit() || (b'a'..=b'f').contains(&x))
}
fn instance(value: &str) -> bool {
    uuid::Uuid::parse_str(value).is_ok_and(|x| !x.is_nil() && x.to_string() == value)
}
pub fn source(value: &str) -> io::Result<Ipv4Addr> {
    let ip: Ipv4Addr = value.parse().map_err(|_| rejected())?;
    let b = ip.octets();
    if ip.to_string() != value || b[0] == 0 || b[0] == 127 || b[0] >= 224 || b[0] == 169 && b[1] == 254 { return Err(rejected()); }
    Ok(ip)
}
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Descriptor {
    pub schema: u8, pub pipe: String, pub attempt: String, pub instance: String,
    pub parent_pid: u32, pub parent_birth: u64, pub parent_path: String, pub parent_sha256: String,
}
impl Descriptor {
    pub fn parse(value: &str, expected_instance: &str) -> io::Result<Self> {
        if value.len() > 4096 { return Err(rejected()); }
        let d: Self = serde_json::from_str(value).map_err(|_| rejected())?;
        if d.schema != 1 || !d.pipe.strip_prefix("ff-core-observe-").is_some_and(|x| hex(x,32)) || !hex(&d.attempt,32)
            || !instance(&d.instance) || d.instance != expected_instance || d.parent_pid == 0 || d.parent_birth == 0
            || d.parent_path.is_empty() || d.parent_path.len() > 2048 || d.parent_path.chars().any(char::is_control)
            || !hex(&d.parent_sha256,64) { return Err(rejected()); }
        Ok(d)
    }
}
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Challenge { schema: u8, kind: String, attempt: String, nonce: String, instance: String }
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Read { schema: u8, kind: String, attempt: String, pub nonce: String, instance: String, pub request: u64, pub source: String }
impl Challenge {
    pub fn validate(&self, d: &Descriptor) -> io::Result<()> {
        if self.schema != 1 || self.kind != "challenge" || self.attempt != d.attempt || self.instance != d.instance || !hex(&self.nonce,64) { return Err(rejected()); }
        Ok(())
    }
}
impl Read {
    pub fn validate(&self, d: &Descriptor, last: u64, previous_nonce: &str) -> io::Result<Ipv4Addr> {
        if self.schema != 1 || self.kind != "read" || self.attempt != d.attempt || self.instance != d.instance || !hex(&self.nonce,64)
            || self.nonce == previous_nonce || self.request == 0 || self.request != last.checked_add(1).ok_or_else(rejected)? { return Err(rejected()); }
        source(&self.source)
    }
}
#[derive(Serialize)]
pub struct Hello<'a> { schema: u8, kind: &'static str, attempt: &'a str, nonce: &'a str, instance: &'a str }
impl<'a> Hello<'a> { pub fn new(d: &'a Descriptor, c: &'a Challenge) -> Self { Self { schema: 1,kind:"hello",attempt:&d.attempt,nonce:&c.nonce,instance:&d.instance } } }
#[derive(Serialize)]
pub struct Observed<'a> {
    schema: u8, kind: &'static str, attempt: &'a str, nonce: &'a str, instance: &'a str, request: u64, source: &'a str,
    name: &'a str, guid: String, luid: String, index: u32,
}
impl<'a> Observed<'a> {
    pub fn new(d: &'a Descriptor, r: &'a Read, name: &'a str, guid: u128, luid: u64, index: u32) -> io::Result<Self> {
        if name.is_empty() || name.chars().count() > 128 || name.chars().any(char::is_control) || guid == 0 || luid == 0 || index == 0 { return Err(rejected()); }
        Ok(Self { schema:1,kind:"observed",attempt:&d.attempt,nonce:&r.nonce,instance:&d.instance,request:r.request,source:&r.source,name,
            guid:format!("{guid:032x}"),luid:format!("{luid:016x}"),index })
    }
}
#[derive(Serialize)]
pub struct Unavailable<'a> { schema:u8,kind:&'static str,attempt:&'a str,nonce:&'a str,instance:&'a str,request:u64,source:&'a str,code:&'static str }
impl<'a> Unavailable<'a> {
    pub fn new(d: &'a Descriptor,r: &'a Read) -> Self { Self { schema:1,kind:"unavailable",attempt:&d.attempt,nonce:&r.nonce,instance:&d.instance,request:r.request,source:&r.source,code:"NIC_UNAVAILABLE" } }
}
pub async fn read<T: for<'a> Deserialize<'a>>(stream: &mut (impl AsyncRead+Unpin)) -> io::Result<T> {
    let first=stream.read_u8().await?;
    read_after_first(stream,first).await
}
pub async fn read_after_first<T: for<'a> Deserialize<'a>>(stream: &mut (impl AsyncRead+Unpin),first:u8) -> io::Result<T> {
    let mut prefix=[first,0,0,0];stream.read_exact(&mut prefix[1..]).await?;
    let size=u32::from_le_bytes(prefix) as usize;
    if !(1..=MAX_FRAME).contains(&size) { return Err(rejected()); }
    let mut body = vec![0;size]; stream.read_exact(&mut body).await?;
    serde_json::from_slice(&body).map_err(|_| rejected())
}
pub async fn write(stream: &mut (impl AsyncWrite+Unpin), value: &impl Serialize) -> io::Result<()> {
    let body = serde_json::to_vec(value).map_err(|_| rejected())?;
    if body.is_empty() || body.len() > MAX_FRAME { return Err(rejected()); }
    stream.write_u32_le(body.len() as u32).await?; stream.write_all(&body).await?; stream.flush().await
}
