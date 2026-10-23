//! Read-only executable preflight, not permission to start public listeners.
//! The ordinary public startup gate remains independent and closed.
#![cfg(target_os = "linux")]
use std::net::{IpAddr, Ipv4Addr, SocketAddr};
use std::path::{Path, PathBuf};
use serde::Deserialize;
use crate::config::Config;

const MAX_DOCUMENT: u64 = 8192;
const CONFIG_INVALID: &str = "PUBLIC_PREFLIGHT_CONFIG_INVALID";
const ARGUMENT_INVALID: &str = "PUBLIC_PREFLIGHT_ARGUMENT_INVALID";

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct DeploymentDocument {
    schema: u32,
    overlay_ipv4: Ipv4Addr,
    overlay_prefix: u8,
    overlay_interface: String,
    auth_key_file: PathBuf,
    data_directory: PathBuf,
    #[serde(default)]
    runtime_profile_sha256: Option<String>,
    #[serde(default)]
    runtime_executable_sha256: Option<String>,
}

fn parse_document(raw: &[u8]) -> Result<DeploymentDocument, &'static str> {
    if raw.is_empty() || raw.len() as u64 > MAX_DOCUMENT { return Err(CONFIG_INVALID); }
    let document: DeploymentDocument = serde_json::from_slice(raw).map_err(|_| CONFIG_INVALID)?;
    let interface = document.overlay_interface.as_bytes();
    if document.schema != 1 || interface.is_empty() || interface.len() > 15
        || document.overlay_interface == "lo"
        || !interface.iter().all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'_' | b'-'))
        || !valid_overlay(document.overlay_ipv4, document.overlay_prefix)
        || [&document.runtime_profile_sha256, &document.runtime_executable_sha256].iter()
            .any(|value| value.as_ref().is_some_and(|value| !crate::public_lease::digest_valid(value))) {
        return Err(CONFIG_INVALID);
    }
    Ok(document)
}

fn valid_overlay(address: Ipv4Addr, prefix: u8) -> bool {
    quazal::source_policy::SourcePolicy::public_overlay(address, prefix).is_ok()
}

fn endpoint(address: Ipv4Addr, port: u16) -> SocketAddr { SocketAddr::from((address, port)) }

/// Construct the actual retained configuration without saving it. There is one
/// address authority and no arbitrary TOML/default-config fallback path.
fn service_config(address: Ipv4Addr) -> Result<Config, &'static str> {
    let mut config = Config::default();
    config.api_server = endpoint(address, 50051);
    for (name, service) in &mut config.quazal.service {
        match (name.as_str(), service) {
            ("onlineconfig", quazal::Service::Config(online)) => {
                online.set_ips(IpAddr::V4(address), IpAddr::V4(address));
            }
            ("content", quazal::Service::Content(content)) => {
                content.listen = endpoint(address, 8000);
                content.files.clear();
                content.files.insert("/mp_balancing.ini".into(), PathBuf::from("data/mp_balancing.ini"));
            }
            ("sc_bl_auth", quazal::Service::Authentication(auth)) => {
                auth.listen = endpoint(address, 21126);
                auth.secure_server_addr = Some(endpoint(address, 21127));
            }
            ("sc_bl_secure", quazal::Service::Secure(secure)) => {
                secure.listen = endpoint(address, 21127);
                secure.settings.insert("storage_host".into(), endpoint(address, 8000).to_string());
                secure.settings.insert("storage_path".into(), "/mp_balancing.ini".into());
            }
            _ => return Err("PUBLIC_PREFLIGHT_NATIVE_CONFIG_INVALID"),
        }
    }
    validate_service_config(&config, address)?;
    Ok(config)
}

fn validate_service_config(config: &Config, address: Ipv4Addr) -> Result<(), &'static str> {
    const INVALID: &str = "PUBLIC_PREFLIGHT_NATIVE_CONFIG_INVALID";
    let names = ["onlineconfig", "content", "sc_bl_auth", "sc_bl_secure"];
    if config.api_server != endpoint(address, 50051) || config.debug.mark_all_as_online || config.debug.force_joins
        || config.quazal.services.len() != 4 || config.quazal.service.len() != 4
        || !names.iter().all(|name| config.quazal.services.contains(*name)) { return Err(INVALID); }
    let Some(quazal::Service::Config(online)) = config.quazal.service.get("onlineconfig") else { return Err(INVALID); };
    let Some(quazal::Service::Content(content)) = config.quazal.service.get("content") else { return Err(INVALID); };
    let Some(quazal::Service::Authentication(auth)) = config.quazal.service.get("sc_bl_auth") else { return Err(INVALID); };
    let Some(quazal::Service::Secure(secure)) = config.quazal.service.get("sc_bl_secure") else { return Err(INVALID); };
    let online_expected = serde_json::json!([
        {"Name":"SandboxUrl","Values":[format!("prudp:/address={address};port=21126")]},
        {"Name":"SandboxUrlWS","Values":[endpoint(address, 21126).to_string()]},
    ]);
    let online_actual: serde_json::Value = serde_json::from_str(&online.content()).map_err(|_| INVALID)?;
    if online.listen != endpoint(address, 80) || online_actual != online_expected
        || content.listen != endpoint(address, 8000) || content.files.len() != 1
        || content.files.get("/mp_balancing.ini") != Some(&PathBuf::from("data/mp_balancing.ini"))
        || auth.listen != endpoint(address, 21126) || auth.secure_server_addr != Some(endpoint(address, 21127))
        || secure.listen != endpoint(address, 21127) || secure.secure_server_addr.is_some()
        || auth.vport != 1 || secure.vport != 1 || auth.ticket_key != secure.ticket_key
        || auth.access_key != b"yl4NG7qZ" || secure.access_key != auth.access_key
        || auth.crypto_key != b"CD&ML" || secure.crypto_key != auth.crypto_key
        || !auth.settings.is_empty() || secure.settings.len() != 2
        || secure.settings.get("storage_host") != Some(&endpoint(address, 8000).to_string())
        || secure.settings.get("storage_path").map(String::as_str) != Some("/mp_balancing.ini") {
        return Err(INVALID);
    }
    Ok(())
}

/// No files are created, no lock acquired, no database/log opened, no process or
/// socket started, and no system policy changed. The only successful result is
/// explicitly static: activation still lacks independent runtime proof.
pub(crate) fn prepare(path: &Path, executable: &Path) -> Result<ValidatedDeployment, &'static str> {
    use std::os::unix::fs::MetadataExt;
    let raw = crate::auth_key_file::read_private_document(path, MAX_DOCUMENT).map_err(|_| CONFIG_INVALID)?;
    let document = parse_document(&raw)?;
    let package = server_api::public_package::verify_public_server_package(executable)
        .map_err(|_| "PUBLIC_PREFLIGHT_PACKAGE_INVALID")?;
    let package_root = executable.ancestors().nth(2).ok_or("PUBLIC_PREFLIGHT_PACKAGE_INVALID")?;
    if [path, document.auth_key_file.as_path(), document.data_directory.as_path()]
        .iter().any(|path| path.starts_with(package_root)) {
        return Err("PUBLIC_PREFLIGHT_STATE_INSIDE_PACKAGE");
    }
    sodiumoxide::init().map_err(|_| "PUBLIC_PREFLIGHT_CRYPTO_UNAVAILABLE")?;
    // Pass the admitted original Windows identity to the real native auth config.
    // Never use the Linux service-manifest digest for client login or storage.
    let auth = crate::api::AuthTokenConfig::from_startup(package.admitted_client().clone(), Some(&document.auth_key_file))
        .map_err(|_| "PUBLIC_PREFLIGHT_AUTH_CONFIG_INVALID")?;
    let data = crate::auth_key_file::private_directory(&document.data_directory)
        .map_err(|_| "PUBLIC_PREFLIGHT_DATA_DIRECTORY_INVALID")?;
    if data.metadata().map_err(|_| "PUBLIC_PREFLIGHT_DATA_DIRECTORY_INVALID")?.mode() & 0o7777 != 0o700 {
        return Err("PUBLIC_PREFLIGHT_DATA_DIRECTORY_INVALID");
    }
    validate_existing_state(&data)?;
    let config = service_config(document.overlay_ipv4)?;
    validate_service_config(&config, document.overlay_ipv4)?;
    Ok(ValidatedDeployment { config, auth, data, document,
        config_sha256: sodiumoxide::hex::encode(sodiumoxide::crypto::hash::sha256::hash(&raw).0),
        service_manifest_sha256: package.service_identity().manifest_sha256().to_owned(),
        admitted_client: package.admitted_client().clone() })
}

/// Only the fixed retained state paths are inspected. A private directory is
/// not permission to follow a pre-existing imported symlink/hardlink outside it.
/// The trusted owner must not concurrently mutate this tree during startup.
fn validate_existing_state(directory: &std::fs::File) -> Result<(), &'static str> {
    use std::os::fd::{AsRawFd, FromRawFd};
    use std::os::unix::fs::MetadataExt;
    const INVALID: &str = "PUBLIC_PREFLIGHT_DATA_DIRECTORY_INVALID";
    fn inspect(parent: &std::fs::File, name: &std::ffi::CStr, is_directory: bool) -> Result<Option<std::fs::File>, &'static str> {
        let fd = unsafe { libc::openat(parent.as_raw_fd(), name.as_ptr(), libc::O_RDONLY | libc::O_NOFOLLOW | libc::O_CLOEXEC | libc::O_NONBLOCK
            | if is_directory { libc::O_DIRECTORY } else { 0 }) };
        if fd < 0 {
            return if std::io::Error::last_os_error().raw_os_error() == Some(libc::ENOENT) { Ok(None) } else { Err(INVALID) };
        }
        let file = unsafe { std::fs::File::from_raw_fd(fd) };
        let metadata = file.metadata().map_err(|_| INVALID)?;
        if metadata.uid() != unsafe { libc::geteuid() } || metadata.mode() & 0o7022 != 0
            || metadata.mode() & 0o600 != 0o600
            || (is_directory && (!metadata.is_dir() || metadata.mode() & 0o100 == 0))
            || (!is_directory && (!metadata.is_file() || metadata.nlink() != 1)) { return Err(INVALID); }
        Ok(Some(file))
    }
    for name in [c"5th-echelon.db", c"5th-echelon.db-wal", c"5th-echelon.db-shm", c"5th-echelon.db-journal", c"server.lock"] {
        let _ = inspect(directory, name, false)?;
    }
    if let Some(content) = inspect(directory, c"data", true)? {
        let _ = inspect(&content, c"mp_balancing.ini", false)?;
    }
    Ok(())
}

/// One real plan is consumed by both inspection and owner-leased startup. The
/// held private directory FD keeps chdir/lock tied to the inspected directory.
pub(crate) struct ValidatedDeployment {
    pub config: Config,
    pub auth: crate::api::AuthTokenConfig,
    pub data: std::fs::File,
    pub admitted_client: server_api::release_contract::ReleaseIdentity,
    document: DeploymentDocument,
    config_sha256: String,
    service_manifest_sha256: String,
}
impl ValidatedDeployment {
    pub(crate) fn activation_binding(&self) -> Result<(crate::public_lease::Binding, quazal::socket_policy::SocketPolicy, quazal::source_policy::SourcePolicy), &'static str> {
        use std::os::unix::fs::MetadataExt;
        const INVALID: &str = "PUBLIC_ACTIVATION_BOUNDARY_INVALID";
        let document = &self.document;
        let profile = document.runtime_profile_sha256.as_ref().ok_or(INVALID)?;
        let runtime = document.runtime_executable_sha256.as_ref().ok_or(INVALID)?;
        let name = std::ffi::CString::new(document.overlay_interface.as_str()).map_err(|_| INVALID)?;
        let index = unsafe { libc::if_nametoindex(name.as_ptr()) };
        if index == 0 { return Err(INVALID); }
        let socket = quazal::socket_policy::SocketPolicy::interface_bound(document.overlay_ipv4, &document.overlay_interface).map_err(|_| INVALID)?;
        let namespace = std::fs::metadata("/proc/self/ns/net").map_err(|_| INVALID)?;
        if index != unsafe { libc::if_nametoindex(name.as_ptr()) } { return Err(INVALID); }
        let binding = crate::public_lease::Binding {
            config_sha256: self.config_sha256.clone(), service_manifest_sha256: self.service_manifest_sha256.clone(),
            admitted_client_storage_key: self.admitted_client.storage_key(), runtime_profile_sha256: profile.clone(),
            runtime_executable_sha256: runtime.clone(), netns_device: namespace.dev(), netns_inode: namespace.ino(),
            interface_index: index, overlay_ipv4: document.overlay_ipv4, overlay_prefix: document.overlay_prefix,
        };
        let source = quazal::source_policy::SourcePolicy::public_overlay(document.overlay_ipv4, document.overlay_prefix).map_err(|_| INVALID)?;
        Ok((binding, socket, source))
    }
    pub(crate) fn interface_name(&self) -> String { self.document.overlay_interface.clone() }
    fn report(&self) -> serde_json::Value {
        serde_json::json!({
            "schema":1, "status":"static_preflight_verified", "startup_allowed":false,
            "service_manifest_sha256":self.service_manifest_sha256,
            "admitted_client_storage_key":self.admitted_client.storage_key(),
            "shared_auth_key_validated":true, "data_directory_private":true,
            "data_lock_acquired":false, "observer_enabled":false,
            "native_listener_plan":{"count":5,"bind_scope":"declared_overlay_ipv4",
                "config_tcp":80,"content_tcp":8000,"auth_udp":21126,"secure_udp":21127,"api_tcp":50051},
            "blockers":["OWNED_ENCRYPTED_OVERLAY_UNVERIFIED","OVERLAY_LIFETIME_SUPERVISION_UNVERIFIED",
                "PUBLIC_SOCKET_CONFINEMENT_NOT_ACTIVATED","PUBLIC_SOURCE_POLICY_NOT_ACTIVATED"],
        })
    }
}

pub(crate) fn check(path: &Path, executable: &Path) -> Result<serde_json::Value, &'static str> {
    Ok(prepare(path, executable)?.report())
}

pub(crate) fn arguments_valid(args: &crate::Args) -> Result<(), &'static str> {
    if args.deployment_profile != crate::DeploymentProfile::Public || args.init_lan.is_some()
        || args.auth_key_file.is_some() || args.config_path.is_some() || args.data_dir.is_some()
        || args.smoke_ports || args.launcher || args.owned_public_lease_fd.is_some() || args.public_deployment_config.is_some() {
        return Err(ARGUMENT_INVALID);
    }
    Ok(())
}

#[cfg(test)]
#[path = "public_preflight_tests.rs"]
mod tests;
