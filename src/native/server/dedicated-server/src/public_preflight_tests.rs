use super::*;
use argh::FromArgs;
use std::collections::BTreeMap;
use std::fs;
use std::os::unix::fs::{PermissionsExt, symlink};
use std::sync::atomic::{AtomicU64, Ordering};
use sodiumoxide::crypto::hash::sha256;
use server_api::release_catalog::{RELEASE_ID, COMPAT_ID, ROLE_PATHS};
static NEXT: AtomicU64 = AtomicU64::new(0);

fn digest(bytes: &[u8]) -> String { sodiumoxide::hex::encode(sha256::hash(bytes).0) }
fn synthetic_file(role: &str, linux: bool) -> Vec<u8> {
    let mut bytes = vec![0; 128];
    if linux {
        bytes[..7].copy_from_slice(b"\x7fELF\x02\x01\x01");
        bytes[16..18].copy_from_slice(&3_u16.to_le_bytes());
        bytes[18..20].copy_from_slice(&62_u16.to_le_bytes());
        bytes[20..24].copy_from_slice(&1_u32.to_le_bytes());
        bytes[52..54].copy_from_slice(&64_u16.to_le_bytes());
    } else {
        bytes[..2].copy_from_slice(b"MZ");
        bytes[60..64].copy_from_slice(&64_u32.to_le_bytes());
        bytes[64..68].copy_from_slice(b"PE\0\0");
        bytes[68..70].copy_from_slice(&0x14c_u16.to_le_bytes());
        bytes[84..86].copy_from_slice(&2_u16.to_le_bytes());
        bytes[88..90].copy_from_slice(&0x10b_u16.to_le_bytes());
    }
    bytes.extend_from_slice(b"SYNTHETIC INERT UNIT TEST DATA; DO NOT EXECUTE\0");
    bytes.extend_from_slice(server_api::release_catalog::role_marker(role).unwrap().as_bytes());
    bytes
}

pub(crate) struct Fixture { root: PathBuf, package: PathBuf, pub(crate) configuration: PathBuf, key: PathBuf, data: PathBuf }
impl Fixture {
    pub(crate) fn new() -> Self {
        sodiumoxide::init().unwrap();
        let root = std::env::temp_dir().join(format!("fifth-public-preflight-synthetic-{}-{}",
            std::process::id(), NEXT.fetch_add(1, Ordering::Relaxed)));
        fs::create_dir(&root).unwrap(); fs::set_permissions(&root, fs::Permissions::from_mode(0o700)).unwrap();
        let fixture = Self { package: root.join("package"), configuration: root.join("deployment.json"),
            key: root.join("issuer.key"), data: root.join("data"), root };
        fs::create_dir(&fixture.data).unwrap(); fs::set_permissions(&fixture.data, fs::Permissions::from_mode(0o700)).unwrap();
        fs::write(&fixture.key, [0x75; 32]).unwrap(); fs::set_permissions(&fixture.key, fs::Permissions::from_mode(0o600)).unwrap();
        let mut components = Vec::new();
        for (role, path) in ROLE_PATHS {
            let bytes = synthetic_file(role, false);
            fixture.package_file(&format!("client/{path}"), &bytes);
            components.push(serde_json::json!({"role":role,"path":path,"release_id":RELEASE_ID,
                "compat_id":COMPAT_ID,"bytes":bytes.len(),"sha256":digest(&bytes)}));
        }
        let catalog = serde_json::to_vec(&serde_json::json!({"schema":1,"release_id":RELEASE_ID,
            "compat_id":COMPAT_ID,"components":components})).unwrap();
        fixture.package_file("client/package/RELEASE-CATALOG.json", &catalog);
        let native = synthetic_file("server", true);
        fixture.package_file("bin/FifthFreedom.Server", &native);
        fixture.package_file("package/PUBLIC-SERVER-CATALOG.json", &serde_json::to_vec(&serde_json::json!({
            "schema":1,"platform":"linux-x86_64",
            "service":{"role":"server","path":"bin/FifthFreedom.Server","release_id":RELEASE_ID,
                "compat_id":COMPAT_ID,"bytes":native.len(),"sha256":digest(&native)},
            "admitted_client":{"root":"client","catalog":"package/RELEASE-CATALOG.json",
                "release_id":RELEASE_ID,"compat_id":COMPAT_ID,"manifest_sha256":digest(&catalog)},
        })).unwrap());
        fixture.document(serde_json::json!({"schema":1,"overlay_ipv4":"10.77.0.1","overlay_prefix":24,
            "overlay_interface":"ffpublic0","auth_key_file":fixture.key,"data_directory":fixture.data}));
        fixture
    }
    fn package_file(&self, path: &str, bytes: &[u8]) {
        let path = self.package.join(path); fs::create_dir_all(path.parent().unwrap()).unwrap(); fs::write(path, bytes).unwrap();
    }
    fn document(&self, value: serde_json::Value) {
        fs::write(&self.configuration, serde_json::to_vec(&value).unwrap()).unwrap();
        fs::set_permissions(&self.configuration, fs::Permissions::from_mode(0o600)).unwrap();
    }
    pub(crate) fn edit(&self, update: impl FnOnce(&mut serde_json::Value)) {
        let mut value = serde_json::from_slice(&fs::read(&self.configuration).unwrap()).unwrap();
        update(&mut value); self.document(value);
    }
    fn check(&self) -> Result<serde_json::Value, &'static str> { check(&self.configuration, &self.package.join("bin/FifthFreedom.Server")) }
    pub(crate) fn executable(&self) -> PathBuf { self.package.join("bin/FifthFreedom.Server") }
    pub(crate) fn prepare(&self) -> ValidatedDeployment { prepare(&self.configuration, &self.executable()).unwrap() }
}
impl Drop for Fixture { fn drop(&mut self) { fs::remove_dir_all(&self.root).unwrap(); } }

fn snapshot(root: &Path) -> BTreeMap<PathBuf, String> {
    let mut files = BTreeMap::new();
    fn walk(root: &Path, path: &Path, files: &mut BTreeMap<PathBuf, String>) {
        let metadata = fs::symlink_metadata(path).unwrap();
        if metadata.is_dir() {
            files.insert(path.strip_prefix(root).unwrap().into(), format!("directory:{}", metadata.permissions().mode()));
            for entry in fs::read_dir(path).unwrap() { walk(root, &entry.unwrap().path(), files); }
        } else { files.insert(path.strip_prefix(root).unwrap().into(), digest(&fs::read(path).unwrap())); }
    }
    walk(root, root, &mut files); files
}

#[test]
fn actual_preflight_uses_both_identities_but_never_authorizes_startup_or_writes() {
    let fixture = Fixture::new();
    let before = snapshot(&fixture.root); let cwd = std::env::current_dir().unwrap();
    let report = fixture.check().unwrap();
    assert_eq!(report["status"], "static_preflight_verified"); assert_eq!(report["startup_allowed"], false);
    assert_eq!(report["data_lock_acquired"], false); assert_eq!(report["observer_enabled"], false);
    assert_eq!(report["native_listener_plan"]["count"], 5); assert_eq!(report["native_listener_plan"]["config_tcp"], 80);
    assert!(report["blockers"].as_array().unwrap().len() >= 4);
    let client = server_api::release_catalog::verify_package(&fixture.package.join("client")).unwrap();
    assert_eq!(report["admitted_client_storage_key"], client.storage_key());
    assert_ne!(report["service_manifest_sha256"], client.storage_key().split('|').last().unwrap());
    assert_eq!(snapshot(&fixture.root), before); assert_eq!(std::env::current_dir().unwrap(), cwd);
    assert!(!fixture.data.join("server.lock").exists());
    let rendered = report.to_string();
    assert!(!rendered.contains("10.77.0.1")); assert!(!rendered.contains(fixture.root.to_str().unwrap()));
    assert!(!rendered.contains(&"75".repeat(32)));
}

#[test]
fn public_observer_option_is_default_false_strict_bool_and_bound_to_raw_config() {
    let fixture = Fixture::new();
    let legacy = fixture.prepare();
    assert!(!legacy.observer_allowed());
    let mut previous_hash = legacy.configuration_sha256().to_owned();
    for allowed in [false, true] {
        fixture.edit(|value| value["observer_allowed"] = allowed.into());
        let before = snapshot(&fixture.root);
        let plan = fixture.prepare();
        assert_eq!(plan.observer_allowed(), allowed);
        assert_ne!(plan.configuration_sha256(), previous_hash);
        assert_eq!(plan.configuration_sha256(), digest(&fs::read(&fixture.configuration).unwrap()));
        previous_hash = plan.configuration_sha256().into();
        assert_eq!(fixture.check().unwrap()["observer_enabled"], false);
        assert_eq!(fixture.check().unwrap()["startup_allowed"], false);
        assert_eq!(snapshot(&fixture.root), before);
    }
    for invalid in [serde_json::Value::Null, "true".into(), 1.into(), serde_json::json!({"allowed":true})] {
        fixture.edit(|value| value["observer_allowed"] = invalid);
        let before = snapshot(&fixture.root);
        assert_eq!(fixture.check(), Err(CONFIG_INVALID));
        assert_eq!(snapshot(&fixture.root), before);
    }
    fixture.edit(|value| value["observer_allowed"] = true.into());
    let raw = String::from_utf8(fs::read(&fixture.configuration).unwrap()).unwrap();
    fs::write(&fixture.configuration, raw.replacen("\"observer_allowed\":true", "\"observer_allowed\":true,\"observer_allowed\":false", 1)).unwrap();
    assert_eq!(fixture.check(), Err(CONFIG_INVALID));
}

#[test]
fn every_retained_listener_and_advertised_endpoint_has_one_exact_address_authority() {
    sodiumoxide::init().unwrap();
    for address in ["10.77.0.1", "172.22.0.1", "192.168.90.1", "100.80.0.1"] {
        let address: Ipv4Addr = address.parse().unwrap();
        let config = service_config(address).unwrap();
        validate_service_config(&config, address).unwrap();
        let serialized = toml::to_string(&config).unwrap();
        assert!(!serialized.contains("0.0.0.0")); assert!(!serialized.contains("127.0.0.1"));
        assert_eq!(config.quazal.into_services().unwrap().len(), 4);
    }
}

#[test]
fn future_config_drift_cannot_add_wildcards_redirects_exfiltration_or_debug_policy() {
    sodiumoxide::init().unwrap(); let address = "10.77.0.1".parse().unwrap();
    for case in 0..8 {
        let mut config = service_config(address).unwrap();
        match case {
            0 => config.api_server = "0.0.0.0:50051".parse().unwrap(),
            1 => config.debug.force_joins = true,
            2 => { config.quazal.services.remove("content"); },
            3 => { if let quazal::Service::Config(value) = config.quazal.service.get_mut("onlineconfig").unwrap() { value.listen.set_port(18080); } },
            4 => { if let quazal::Service::Authentication(value) = config.quazal.service.get_mut("sc_bl_auth").unwrap() { value.secure_server_addr = Some("203.0.113.1:21127".parse().unwrap()); } },
            5 => { if let quazal::Service::Secure(value) = config.quazal.service.get_mut("sc_bl_secure").unwrap() { value.settings.insert("storage_host".into(), "203.0.113.1:8000".into()); } },
            6 => { if let quazal::Service::Content(value) = config.quazal.service.get_mut("content").unwrap() { value.files.insert("/private".into(), PathBuf::from("/private-file")); } },
            _ => { if let quazal::Service::Config(value) = config.quazal.service.get_mut("onlineconfig").unwrap() { value.set_ips(IpAddr::V4(address), "203.0.113.1".parse().unwrap()); } },
        }
        assert_eq!(validate_service_config(&config, address), Err("PUBLIC_PREFLIGHT_NATIVE_CONFIG_INVALID"));
    }
}

#[test]
fn public_unicast_addresses_and_overwide_or_broadcast_networks_are_not_overlay_proof() {
    for (ip, prefix) in [("0.0.0.0",24),("127.0.0.1",24),("169.254.0.1",24),("203.0.113.1",24),
        ("224.0.0.1",24),("10.77.0.0",24),("10.77.0.255",24),("10.77.0.1",31),
        ("172.22.0.1",8),("192.168.90.1",8),("100.80.0.1",8)] {
        assert!(!valid_overlay(ip.parse().unwrap(), prefix), "{ip}/{prefix}");
    }
}

#[test]
fn private_operator_document_is_bounded_strict_and_never_reports_untrusted_text() {
    let fixture = Fixture::new();
    let original = fs::read(&fixture.configuration).unwrap();
    for bytes in [b"{}".to_vec(), b"not-json-sensitive-canary".to_vec(),
        String::from_utf8(original.clone()).unwrap().replace("\"schema\":1", "\"schema\":1,\"schema\":1").into_bytes(),
        { let mut large = original.clone(); large.resize(MAX_DOCUMENT as usize + 1, b' '); large }] {
        fs::write(&fixture.configuration, bytes).unwrap();
        assert_eq!(fixture.check(), Err(CONFIG_INVALID));
    }
    fs::write(&fixture.configuration, &original).unwrap();
    fixture.edit(|value| value["untrusted-sensitive-canary"] = "bad".into()); assert_eq!(fixture.check(), Err(CONFIG_INVALID));
    fs::write(&fixture.configuration, &original).unwrap();
    for interface in ["", "lo", "../interface", "interface.with.dot", "abcdefghijklmnop"] {
        fixture.edit(|value| value["overlay_interface"] = interface.into()); assert_eq!(fixture.check(), Err(CONFIG_INVALID));
    }
}

#[test]
fn missing_or_insecure_key_and_data_never_fall_back_to_random_key_or_created_directory() {
    let fixture = Fixture::new();
    fs::set_permissions(&fixture.key, fs::Permissions::from_mode(0o644)).unwrap();
    assert_eq!(fixture.check(), Err("PUBLIC_PREFLIGHT_AUTH_CONFIG_INVALID"));
    fs::set_permissions(&fixture.key, fs::Permissions::from_mode(0o600)).unwrap();
    fs::set_permissions(&fixture.data, fs::Permissions::from_mode(0o755)).unwrap();
    assert_eq!(fixture.check(), Err("PUBLIC_PREFLIGHT_DATA_DIRECTORY_INVALID"));
    fs::set_permissions(&fixture.data, fs::Permissions::from_mode(0o500)).unwrap();
    assert_eq!(fixture.check(), Err("PUBLIC_PREFLIGHT_DATA_DIRECTORY_INVALID"));
    fs::set_permissions(&fixture.data, fs::Permissions::from_mode(0o700)).unwrap();
    let missing = fixture.root.join("missing-data"); fixture.edit(|value| value["data_directory"] = serde_json::json!(missing));
    assert_eq!(fixture.check(), Err("PUBLIC_PREFLIGHT_DATA_DIRECTORY_INVALID")); assert!(!missing.exists());
    fixture.edit(|value| value["auth_key_file"] = serde_json::json!(fixture.root.join("missing-key")));
    assert_eq!(fixture.check(), Err("PUBLIC_PREFLIGHT_AUTH_CONFIG_INVALID"));
}

#[test]
fn secret_config_and_data_are_never_admitted_inside_the_immutable_artifact_tree() {
    for field in ["auth_key_file", "data_directory"] {
        let fixture = Fixture::new(); fixture.edit(|value| value[field] = serde_json::json!(fixture.package.join("private-state")));
        assert_eq!(fixture.check(), Err("PUBLIC_PREFLIGHT_STATE_INSIDE_PACKAGE"));
        assert!(!fixture.package.join("private-state").exists());
    }
}

#[test]
fn changed_client_bytes_fail_before_authentication_or_state_validation() {
    let fixture = Fixture::new();
    fixture.package_file("client/runtime/scbl_gameplay.dll", b"SYNTHETIC TAMPER; NEVER EXECUTABLE");
    fs::remove_file(&fixture.key).unwrap();
    fs::remove_dir(&fixture.data).unwrap();
    let before = snapshot(&fixture.root);
    assert_eq!(fixture.check(), Err("PUBLIC_PREFLIGHT_PACKAGE_INVALID"));
    assert_eq!(snapshot(&fixture.root), before);
    assert!(!fixture.key.exists()); assert!(!fixture.data.exists());
}

#[test]
fn symlinked_operator_document_or_data_and_nonprivate_config_are_rejected() {
    let fixture = Fixture::new();
    fs::set_permissions(&fixture.configuration, fs::Permissions::from_mode(0o644)).unwrap(); assert_eq!(fixture.check(), Err(CONFIG_INVALID));
    fs::set_permissions(&fixture.configuration, fs::Permissions::from_mode(0o600)).unwrap();
    let alias = fixture.root.join("document-link"); symlink(&fixture.configuration, &alias).unwrap();
    assert_eq!(check(&alias, &fixture.package.join("bin/FifthFreedom.Server")), Err(CONFIG_INVALID));
    let alias = fixture.root.join("data-link"); symlink(&fixture.data, &alias).unwrap();
    fixture.edit(|value| value["data_directory"] = serde_json::json!(alias));
    assert_eq!(fixture.check(), Err("PUBLIC_PREFLIGHT_DATA_DIRECTORY_INVALID"));
}

#[test]
fn explicit_check_arguments_cannot_be_mixed_with_lan_initialization_or_other_config_authorities() {
    let parse = |args: &[&str]| crate::Args::from_args(&["FifthFreedom.Server"], args).unwrap();
    assert!(arguments_valid(&parse(&["--deployment-profile", "public", "--check-public-deployment", "/private/config"])).is_ok());
    for extra in [vec![], vec!["--init-lan", "127.0.0.1"], vec!["--auth-key-file", "/another/key"],
        vec!["--config", "/another/config"], vec!["--data-dir", "/another/data"], vec!["--smoke-ports"], vec!["--launcher"]] {
        let mut args = vec!["--check-public-deployment", "/private/config"];
        if !extra.is_empty() { args.extend(["--deployment-profile", "public"]); args.extend(extra); }
        assert_eq!(arguments_valid(&parse(&args)), Err(ARGUMENT_INVALID));
    }
    assert_eq!(crate::DeploymentProfile::Public.startup_plan(), Err(crate::deployment_profile::ProfileError::PublicNotIntegrated));
}

#[test]
fn runtime_hashes_are_optional_for_inspection_but_never_permission_to_activate() {
    let fixture=Fixture::new();let original=snapshot(&fixture.root);
    let plan=prepare(&fixture.configuration,&fixture.package.join("bin/FifthFreedom.Server")).unwrap();
    assert!(plan.activation_binding().is_err());assert_eq!(snapshot(&fixture.root),original);
    fixture.edit(|value|{value["runtime_profile_sha256"]="a".repeat(64).into();value["runtime_executable_sha256"]="b".repeat(64).into();});
    let before=snapshot(&fixture.root);
    let plan=prepare(&fixture.configuration,&fixture.package.join("bin/FifthFreedom.Server")).unwrap();
    assert_eq!(plan.report()["startup_allowed"],false);
    assert!(plan.activation_binding().is_err()); // no verified ffpublic0 interface
    assert_eq!(snapshot(&fixture.root),before);
    for field in ["runtime_profile_sha256","runtime_executable_sha256"] {
        fixture.edit(|value|value[field]="PRIVATE-INVALID-CANARY".into());
        assert_eq!(fixture.check(),Err(CONFIG_INVALID));
        fixture.edit(|value|value[field]="a".repeat(64).into());
    }
}

#[test]
fn inherited_activation_and_static_inspection_cannot_mix_authorities() {
    for extra in [vec!["--owned-public-lease-fd","3"],vec!["--public-deployment-config","/private/second"]] {
        let mut args=vec!["--deployment-profile","public","--check-public-deployment","/private/first"];args.extend(extra);
        let args=crate::Args::from_args(&["FifthFreedom.Server"],&args).unwrap();
        assert_eq!(arguments_valid(&args),Err(ARGUMENT_INVALID));
    }
    for args in [vec!["--owned-public-lease-fd","3"],vec!["--deployment-profile","public","--owned-public-lease-fd","3"],vec!["--deployment-profile","public","--public-deployment-config","/private/one"]] {
        let args=crate::Args::from_args(&["FifthFreedom.Server"],&args).unwrap();
        assert_eq!(crate::run_owned_public(&args),Err("PUBLIC_ACTIVATION_ARGUMENT_INVALID"));
    }
}

#[test]
fn existing_state_aliases_are_rejected_before_lock_database_or_content_writes() {
    for case in 0..5 {
        let fixture=Fixture::new();let outside=fixture.root.join("preserved-original");
        fs::write(&outside,b"preserve unique evidence").unwrap();
        let database=fixture.data.join("5th-echelon.db");
        match case {
            0=>symlink(&outside,&database).unwrap(),
            1=>fs::hard_link(&outside,&database).unwrap(),
            2=>{fs::write(&database,b"owned synthetic file").unwrap();fs::set_permissions(&database,fs::Permissions::from_mode(0o666)).unwrap();},
            3=>symlink(&fixture.root,fixture.data.join("data")).unwrap(),
            _=>{fs::create_dir(fixture.data.join("data")).unwrap();symlink(&outside,fixture.data.join("data/mp_balancing.ini")).unwrap();},
        }
        assert_eq!(fixture.check(),Err("PUBLIC_PREFLIGHT_DATA_DIRECTORY_INVALID"));
        assert_eq!(fs::read(&outside).unwrap(),b"preserve unique evidence");
        assert!(!fixture.data.join("server.lock").exists());
    }
}

#[test]
fn ordinary_existing_owned_state_is_inspected_without_opening_database_or_rewriting_content() {
    let fixture=Fixture::new();
    for name in ["5th-echelon.db","5th-echelon.db-wal","5th-echelon.db-shm","5th-echelon.db-journal","server.lock"] {
        let file=fixture.data.join(name);fs::write(&file,b"").unwrap();
        fs::set_permissions(&file,fs::Permissions::from_mode(0o600)).unwrap();
    }
    let content=fixture.data.join("data");fs::create_dir(&content).unwrap();
    fs::write(content.join("mp_balancing.ini"),b"synthetic existing retained content").unwrap();
    let before=snapshot(&fixture.root);assert_eq!(fixture.check().unwrap()["startup_allowed"],false);
    assert_eq!(snapshot(&fixture.root),before);
}
