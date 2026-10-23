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
}

fn parse_document(raw: &[u8]) -> Result<DeploymentDocument, &'static str> {
    if raw.is_empty() || raw.len() as u64 > MAX_DOCUMENT { return Err(CONFIG_INVALID); }
    let document: DeploymentDocument = serde_json::from_slice(raw).map_err(|_| CONFIG_INVALID)?;
    let interface = document.overlay_interface.as_bytes();
    if document.schema != 1 || interface.is_empty() || interface.len() > 15
        || document.overlay_interface == "lo"
        || !interface.iter().all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'_' | b'-'))
        || !valid_overlay(document.overlay_ipv4, document.overlay_prefix) {
        return Err(CONFIG_INVALID);
    }
    Ok(document)
}

fn valid_overlay(address: Ipv4Addr, prefix: u8) -> bool {
    if !(8..=30).contains(&prefix) { return false; }
    let value = u32::from(address);
    let mask = u32::MAX << (32 - prefix);
    let network = value & mask;
    let broadcast = network | !mask;
    if value == network || value == broadcast { return false; }
    // The entire declared client network must remain in private/shared space.
    // This is configuration validation, not evidence it is an encrypted tunnel.
    [(0x0a00_0000, 8), (0xac10_0000, 12), (0xc0a8_0000, 16), (0x6440_0000, 10)]
        .iter().any(|&(base, parent_prefix)| prefix >= parent_prefix
            && network & (u32::MAX << (32 - parent_prefix)) == base)
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
pub(crate) fn check(path: &Path, executable: &Path) -> Result<serde_json::Value, &'static str> {
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
    let _auth = crate::api::AuthTokenConfig::from_startup(package.admitted_client().clone(), Some(&document.auth_key_file))
        .map_err(|_| "PUBLIC_PREFLIGHT_AUTH_CONFIG_INVALID")?;
    let _data = crate::auth_key_file::private_directory(&document.data_directory)
        .map_err(|_| "PUBLIC_PREFLIGHT_DATA_DIRECTORY_INVALID")?;
    if _data.metadata().map_err(|_| "PUBLIC_PREFLIGHT_DATA_DIRECTORY_INVALID")?.mode() & 0o7777 != 0o700 {
        return Err("PUBLIC_PREFLIGHT_DATA_DIRECTORY_INVALID");
    }
    let config = service_config(document.overlay_ipv4)?;
    validate_service_config(&config, document.overlay_ipv4)?;
    // No call to Storage::init, observer_policy::start, initialize, instance_lock
    // or any service start is permitted on this explicit inspection path.
    Ok(serde_json::json!({
        "schema":1, "status":"static_preflight_verified", "startup_allowed":false,
        "service_manifest_sha256":package.service_identity().manifest_sha256(),
        "admitted_client_storage_key":package.admitted_client().storage_key(),
        "shared_auth_key_validated":true, "data_directory_private":true,
        "data_lock_acquired":false, "observer_enabled":false,
        "native_listener_plan":{"count":5,"bind_scope":"declared_overlay_ipv4",
            "config_tcp":80,"content_tcp":8000,"auth_udp":21126,"secure_udp":21127,"api_tcp":50051},
        "blockers":["OWNED_ENCRYPTED_OVERLAY_UNVERIFIED","OVERLAY_LIFETIME_SUPERVISION_UNVERIFIED",
            "PUBLIC_SOCKET_CONFINEMENT_NOT_ACTIVATED","PUBLIC_SOURCE_POLICY_NOT_ACTIVATED"],
    }))
}

pub(crate) fn arguments_valid(args: &crate::Args) -> Result<(), &'static str> {
    if args.deployment_profile != crate::DeploymentProfile::Public || args.init_lan.is_some()
        || args.auth_key_file.is_some() || args.config_path.is_some() || args.data_dir.is_some()
        || args.smoke_ports || args.launcher {
        return Err(ARGUMENT_INVALID);
    }
    Ok(())
}

#[cfg(test)]
#[path = "public_preflight_tests.rs"]
mod tests;
