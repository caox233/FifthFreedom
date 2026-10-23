//! Offline integrity for a Linux service plus its admitted Windows client catalog.
//!
//! This verifies an explicitly selected, trusted, immutable deployment tree. It
//! is not publisher authentication or remote attestation. The Windows verifier
//! and its four-role layout remain unchanged; Linux bytes never replace a client
//! role. No configuration discovery, listener, extraction or writes occur here.
use std::fs::{self, File, Metadata};
use std::io::Read;
use std::os::unix::fs::MetadataExt;
use std::path::{Component, Path};

use serde::Deserialize;
use sha2::{Digest, Sha256};

use crate::release_catalog::{self, COMPAT_ID, RELEASE_ID, ROLE_PATHS, SERVER_MARKER};
use crate::release_contract::ReleaseIdentity;

pub const MANIFEST_PATH: &str = "package/PUBLIC-SERVER-CATALOG.json";
pub const SERVICE_PATH: &str = "bin/FifthFreedom.Server";
pub const CLIENT_ROOT: &str = "client";
pub const CLIENT_CATALOG_PATH: &str = "package/RELEASE-CATALOG.json";
pub const PLATFORM: &str = "linux-x86_64";
const MAX_MANIFEST_BYTES: usize = 16 * 1024;
const MAX_SERVICE_BYTES: u64 = 512 * 1024 * 1024;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PublicPackageError {
    InvalidPath,
    InvalidFile,
    InvalidManifest,
    ServiceMismatch,
    ServicePlatformMismatch,
    ServiceMarkerMismatch,
    ClientMismatch,
}
impl PublicPackageError {
    pub const fn reason(self) -> &'static str {
        match self {
            Self::InvalidPath => "PUBLIC_PACKAGE_PATH_INVALID",
            Self::InvalidFile => "PUBLIC_PACKAGE_FILE_INVALID",
            Self::InvalidManifest => "PUBLIC_PACKAGE_MANIFEST_INVALID",
            Self::ServiceMismatch => "PUBLIC_SERVICE_INTEGRITY_MISMATCH",
            Self::ServicePlatformMismatch => "PUBLIC_SERVICE_PLATFORM_MISMATCH",
            Self::ServiceMarkerMismatch => "PUBLIC_SERVICE_MARKER_MISMATCH",
            Self::ClientMismatch => "PUBLIC_CLIENT_CATALOG_MISMATCH",
        }
    }
}
impl std::fmt::Display for PublicPackageError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result { f.write_str(self.reason()) }
}
impl std::error::Error for PublicPackageError {}

/// Deliberately distinct from ReleaseIdentity: this cannot be passed as the
/// Windows client's login/package admission identity by accident.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct PublicServiceIdentity {
    release_id: String,
    compat_id: String,
    manifest_sha256: String,
    executable_sha256: String,
}
impl PublicServiceIdentity {
    pub fn release_id(&self) -> &str { &self.release_id }
    pub fn compat_id(&self) -> &str { &self.compat_id }
    pub const fn platform(&self) -> &'static str { PLATFORM }
    /// Digest of the raw public manifest, which binds the Linux executable and
    /// the raw, independently verified original Windows client catalog.
    pub fn manifest_sha256(&self) -> &str { &self.manifest_sha256 }
    pub fn executable_sha256(&self) -> &str { &self.executable_sha256 }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct VerifiedPublicPackage {
    service: PublicServiceIdentity,
    admitted_client: ReleaseIdentity,
}
impl VerifiedPublicPackage {
    pub fn service_identity(&self) -> &PublicServiceIdentity { &self.service }
    pub fn admitted_client(&self) -> &ReleaseIdentity { &self.admitted_client }
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Manifest {
    schema: u32,
    platform: String,
    service: Service,
    admitted_client: Client,
}
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Service {
    role: String,
    path: String,
    release_id: String,
    compat_id: String,
    bytes: u64,
    sha256: String,
}
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Client {
    root: String,
    catalog: String,
    release_id: String,
    compat_id: String,
    manifest_sha256: String,
}

fn hex(bytes: &[u8]) -> String { bytes.iter().map(|byte| format!("{byte:02x}")).collect() }
fn digest_valid(value: &str) -> bool {
    value.len() == 64 && value.bytes().all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
}

fn normalized_absolute(path: &Path) -> bool {
    use std::os::unix::ffi::OsStrExt;
    path.is_absolute() && path.components().all(|part| matches!(part, Component::RootDir | Component::Normal(_)))
        && !path.as_os_str().as_bytes().split(|byte| *byte == b'/').skip(1)
            .any(|part| part.is_empty() || part == b"." || part == b"..")
}

fn regular_file(path: &Path) -> Result<Metadata, PublicPackageError> {
    if !normalized_absolute(path) { return Err(PublicPackageError::InvalidPath); }
    for ancestor in path.ancestors() {
        let metadata = fs::symlink_metadata(ancestor).map_err(|_| PublicPackageError::InvalidFile)?;
        if metadata.file_type().is_symlink() { return Err(PublicPackageError::InvalidPath); }
    }
    let metadata = fs::symlink_metadata(path).map_err(|_| PublicPackageError::InvalidFile)?;
    if !metadata.is_file() || metadata.nlink() != 1 { return Err(PublicPackageError::InvalidFile); }
    Ok(metadata)
}

fn unchanged(left: &Metadata, right: &Metadata) -> bool {
    (left.dev(), left.ino(), left.len(), left.mode(), left.uid(), left.gid(), left.nlink(), left.mtime(), left.mtime_nsec(), left.ctime(), left.ctime_nsec())
        == (right.dev(), right.ino(), right.len(), right.mode(), right.uid(), right.gid(), right.nlink(), right.mtime(), right.mtime_nsec(), right.ctime(), right.ctime_nsec())
}

fn open_regular(path: &Path) -> Result<(File, Metadata), PublicPackageError> {
    let before = regular_file(path)?;
    let file = File::open(path).map_err(|_| PublicPackageError::InvalidFile)?;
    let opened = file.metadata().map_err(|_| PublicPackageError::InvalidFile)?;
    if !unchanged(&before, &opened) { return Err(PublicPackageError::InvalidFile); }
    Ok((file, opened))
}

fn bounded_manifest(path: &Path) -> Result<Vec<u8>, PublicPackageError> {
    let (mut file, before) = open_regular(path)?;
    if before.len() == 0 || before.len() > MAX_MANIFEST_BYTES as u64 { return Err(PublicPackageError::InvalidManifest); }
    let mut raw = Vec::with_capacity(before.len() as usize);
    (&mut file).take((MAX_MANIFEST_BYTES + 1) as u64).read_to_end(&mut raw).map_err(|_| PublicPackageError::InvalidFile)?;
    if raw.len() > MAX_MANIFEST_BYTES || raw.len() as u64 != before.len()
        || !unchanged(&before, &file.metadata().map_err(|_| PublicPackageError::InvalidFile)?) {
        return Err(PublicPackageError::InvalidFile);
    }
    Ok(raw)
}

/// ELF header checks identify the intended platform; they are not a loader or
/// proof that the file is safe/executable or that its dynamic dependencies exist.
fn linux_x86_64_header(header: &[u8]) -> bool {
    header.len() >= 64 && &header[..7] == b"\x7fELF\x02\x01\x01"
        && matches!(header[7], 0 | 3)
        && matches!(u16::from_le_bytes([header[16], header[17]]), 2 | 3)
        && u16::from_le_bytes([header[18], header[19]]) == 62
        && u32::from_le_bytes(header[20..24].try_into().unwrap()) == 1
        && u16::from_le_bytes([header[52], header[53]]) == 64
}

fn verify_windows_role(path: &Path, role: &str) -> Result<(), PublicPackageError> {
    let (mut file, before) = open_regular(path)?;
    let mut header = Vec::new();
    (&mut file).take(65536).read_to_end(&mut header).map_err(|_| PublicPackageError::InvalidFile)?;
    if header.len() < 64 || &header[..2] != b"MZ" { return Err(PublicPackageError::ClientMismatch); }
    let offset = u32::from_le_bytes(header[60..64].try_into().unwrap()) as usize;
    let end = offset.checked_add(26).ok_or(PublicPackageError::ClientMismatch)?;
    if offset < 64 || end > header.len() || &header[offset..offset + 4] != b"PE\0\0" {
        return Err(PublicPackageError::ClientMismatch);
    }
    let machine = u16::from_le_bytes(header[offset + 4..offset + 6].try_into().unwrap());
    let optional_size = u16::from_le_bytes(header[offset + 20..offset + 22].try_into().unwrap()) as usize;
    let optional_magic = u16::from_le_bytes(header[offset + 24..offset + 26].try_into().unwrap());
    let supported = (machine == 0x14c && optional_magic == 0x10b)
        || (role == "server" && machine == 0x8664 && optional_magic == 0x20b);
    if !supported || optional_size < 2 || offset + 24 + optional_size > header.len()
        || !unchanged(&before, &file.metadata().map_err(|_| PublicPackageError::InvalidFile)?) {
        return Err(PublicPackageError::ClientMismatch);
    }
    Ok(())
}

fn complete_marker(bytes: &[u8], wide: bool) -> Option<String> {
    let mut text = String::new();
    let stride = if wide { 2 } else { 1 };
    for index in (0..bytes.len().min(400)).step_by(stride) {
        let byte = bytes[index];
        if !byte.is_ascii() || byte == 0 || (wide && bytes.get(index + 1) != Some(&0)) { return None; }
        text.push(char::from(byte));
        if text.ends_with("|END_SCBl") {
            let parts: Vec<_> = text.split('|').collect();
            return (parts.len() == 5 && parts[0] == "SCBL_RELEASE_V1").then_some(text);
        }
    }
    None
}

fn verify_service(path: &Path, service: &Service) -> Result<(), PublicPackageError> {
    let (mut file, before) = open_regular(path)?;
    if before.len() != service.bytes { return Err(PublicPackageError::ServiceMismatch); }
    let expected = release_catalog::role_marker("server").ok_or(PublicPackageError::ServiceMarkerMismatch)?;
    // Derive rather than embedding an incomplete own-role marker literal in the
    // real binary being verified. A malformed second own-role prefix must fail
    // exactly as it does in the retained four-role verifier.
    let own_prefix = expected.split('|').take(2).collect::<Vec<_>>().join("|") + "|";
    let wide_own_prefix: Vec<_> = own_prefix.encode_utf16().flat_map(u16::to_le_bytes).collect();
    let wide_expected: Vec<_> = expected.encode_utf16().flat_map(u16::to_le_bytes).collect();
    let prefix = b"SCBL_RELEASE_V1|";
    let wide_prefix: Vec<_> = "SCBL_RELEASE_V1|".encode_utf16().flat_map(u16::to_le_bytes).collect();
    let mut buffer = [0_u8; 65536];
    let mut tail = Vec::new();
    let mut size = 0_u64;
    let mut digest = Sha256::new();
    let mut markers = 0;
    loop {
        let count = file.read(&mut buffer).map_err(|_| PublicPackageError::InvalidFile)?;
        if size == 0 && !linux_x86_64_header(&buffer[..count]) { return Err(PublicPackageError::ServicePlatformMismatch); }
        size += count as u64;
        if size > service.bytes { return Err(PublicPackageError::ServiceMismatch); }
        digest.update(&buffer[..count]);
        tail.extend_from_slice(&buffer[..count]);
        let scan = if count == 0 { tail.len() } else { tail.len().saturating_sub(512) };
        for index in 0..scan {
            for (own, expected_bytes) in [(own_prefix.as_bytes(), expected.as_bytes()),
                (wide_own_prefix.as_slice(), wide_expected.as_slice())] {
                if tail[index..].starts_with(own) && !tail[index..].starts_with(expected_bytes) {
                    return Err(PublicPackageError::ServiceMarkerMismatch);
                }
            }
            for (prefix, wide) in [(prefix.as_slice(), false), (wide_prefix.as_slice(), true)] {
                if tail[index..].starts_with(prefix) {
                    if let Some(marker) = complete_marker(&tail[index..], wide) {
                        if marker != expected { return Err(PublicPackageError::ServiceMarkerMismatch); }
                        markers += 1;
                    }
                }
            }
        }
        if count == 0 { break; }
        tail.drain(..scan);
    }
    if markers != 1 { return Err(PublicPackageError::ServiceMarkerMismatch); }
    if size != service.bytes || hex(&digest.finalize()) != service.sha256
        || !unchanged(&before, &file.metadata().map_err(|_| PublicPackageError::InvalidFile)?) {
        return Err(PublicPackageError::ServiceMismatch);
    }
    Ok(())
}

/// Verify an explicit package root as data only. Its owner must keep the tree
/// immutable during verification and use; local hashes do not authenticate its
/// publisher. The client subtree is admission evidence, not a complete client
/// distribution: default-save/privacy gates remain separate.
pub fn verify_public_package(root: &Path) -> Result<VerifiedPublicPackage, PublicPackageError> {
    if !normalized_absolute(root) { return Err(PublicPackageError::InvalidPath); }
    let raw = bounded_manifest(&root.join(MANIFEST_PATH))?;
    let manifest: Manifest = serde_json::from_slice(&raw).map_err(|_| PublicPackageError::InvalidManifest)?;
    if manifest.schema != 1 || manifest.platform != PLATFORM
        || manifest.service.role != "server" || manifest.service.path != SERVICE_PATH
        || manifest.service.release_id != RELEASE_ID || manifest.service.compat_id != COMPAT_ID
        || manifest.service.bytes == 0 || manifest.service.bytes > MAX_SERVICE_BYTES
        || !digest_valid(&manifest.service.sha256)
        || manifest.admitted_client.root != CLIENT_ROOT || manifest.admitted_client.catalog != CLIENT_CATALOG_PATH {
        return Err(PublicPackageError::InvalidManifest);
    }
    let pinned_client = ReleaseIdentity::new(1, &manifest.admitted_client.release_id,
        &manifest.admitted_client.compat_id, &manifest.admitted_client.manifest_sha256)
        .map_err(|_| PublicPackageError::InvalidManifest)?;
    // This exact production verifier reads and hashes all original four roles.
    // The additional preflight rejects hardlinks in the whole public tree.
    let client_root = root.join(CLIENT_ROOT);
    regular_file(&client_root.join(CLIENT_CATALOG_PATH))?;
    for (role, path) in ROLE_PATHS { verify_windows_role(&client_root.join(path), role)?; }
    let verified_client = release_catalog::verify_package(&client_root).map_err(|_| PublicPackageError::ClientMismatch)?;
    if verified_client != pinned_client { return Err(PublicPackageError::ClientMismatch); }
    verify_service(&root.join(SERVICE_PATH), &manifest.service)?;
    Ok(VerifiedPublicPackage {
        service: PublicServiceIdentity {
            release_id: manifest.service.release_id,
            compat_id: manifest.service.compat_id,
            manifest_sha256: hex(&Sha256::digest(&raw)),
            executable_sha256: manifest.service.sha256,
        },
        admitted_client: verified_client,
    })
}

/// Bind future native startup to the fixed executable position. No upward
/// catalog search or alternate role/path is accepted. Public main does not call
/// this yet; its existing readiness gate remains in place.
pub fn verify_public_server_package(executable: &Path) -> Result<VerifiedPublicPackage, PublicPackageError> {
    if !normalized_absolute(executable) { return Err(PublicPackageError::InvalidPath); }
    let root = executable.ancestors().nth(2).ok_or(PublicPackageError::InvalidPath)?;
    if executable != root.join(SERVICE_PATH) { return Err(PublicPackageError::InvalidPath); }
    std::hint::black_box(SERVER_MARKER);
    verify_public_package(root)
}

#[cfg(test)]
#[path = "public_package_tests.rs"]
mod tests;
