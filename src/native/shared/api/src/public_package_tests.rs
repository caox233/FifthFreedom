//! Synthetic inert file fixtures only. No real catalog, executable, DLL or save
//! is fabricated/published as a production candidate; these files are never run.
use super::*;
use std::path::PathBuf;
use std::os::unix::fs::symlink;
use std::sync::atomic::{AtomicU64, Ordering};

static NEXT: AtomicU64 = AtomicU64::new(0);

fn synthetic_elf(marker: &[u8]) -> Vec<u8> {
    let mut bytes = vec![0; 64];
    bytes[..7].copy_from_slice(b"\x7fELF\x02\x01\x01");
    bytes[16..18].copy_from_slice(&3_u16.to_le_bytes());
    bytes[18..20].copy_from_slice(&62_u16.to_le_bytes());
    bytes[20..24].copy_from_slice(&1_u32.to_le_bytes());
    bytes[52..54].copy_from_slice(&64_u16.to_le_bytes());
    bytes.extend_from_slice(b"SYNTHETIC TEST DATA; NOT EXECUTABLE\0");
    bytes.extend_from_slice(marker);
    bytes
}

fn synthetic_pe(role: &str) -> Vec<u8> {
    let mut bytes = vec![0; 128];
    bytes[..2].copy_from_slice(b"MZ");
    bytes[60..64].copy_from_slice(&64_u32.to_le_bytes());
    bytes[64..68].copy_from_slice(b"PE\0\0");
    bytes[68..70].copy_from_slice(&(if role == "server" { 0x8664_u16 } else { 0x14c_u16 }).to_le_bytes());
    bytes[84..86].copy_from_slice(&2_u16.to_le_bytes());
    bytes[88..90].copy_from_slice(&(if role == "server" { 0x20b_u16 } else { 0x10b_u16 }).to_le_bytes());
    bytes.extend_from_slice(b"SYNTHETIC TEST DATA; NOT EXECUTABLE\0");
    bytes.extend_from_slice(release_catalog::role_marker(role).unwrap().as_bytes());
    bytes
}

struct Fixture { root: PathBuf }
impl Fixture {
    fn new() -> Self {
        let root = std::env::temp_dir().join(format!("fifth-public-package-synthetic-{}-{}",
            std::process::id(), NEXT.fetch_add(1, Ordering::Relaxed)));
        fs::create_dir(&root).unwrap();
        let fixture = Self { root };
        let mut components = Vec::new();
        for (role, path) in ROLE_PATHS {
            let bytes = synthetic_pe(role);
            fixture.write(&format!("client/{path}"), &bytes);
            components.push(serde_json::json!({"role":role,"path":path,"release_id":RELEASE_ID,
                "compat_id":COMPAT_ID,"bytes":bytes.len(),"sha256":hex(&Sha256::digest(&bytes))}));
        }
        let client = serde_json::to_vec_pretty(&serde_json::json!({"schema":1,"release_id":RELEASE_ID,
            "compat_id":COMPAT_ID,"components":components})).unwrap();
        fixture.write("client/package/RELEASE-CATALOG.json", &client);
        let linux = synthetic_elf(SERVER_MARKER.as_bytes());
        fixture.write(SERVICE_PATH, &linux);
        let manifest = serde_json::json!({"schema":1,"platform":PLATFORM,
            "service":{"role":"server","path":SERVICE_PATH,"release_id":RELEASE_ID,"compat_id":COMPAT_ID,
                "bytes":linux.len(),"sha256":hex(&Sha256::digest(&linux))},
            "admitted_client":{"root":CLIENT_ROOT,"catalog":CLIENT_CATALOG_PATH,"release_id":RELEASE_ID,
                "compat_id":COMPAT_ID,"manifest_sha256":hex(&Sha256::digest(&client))}});
        fixture.write(MANIFEST_PATH, &serde_json::to_vec_pretty(&manifest).unwrap());
        fixture
    }
    fn write(&self, path: &str, bytes: &[u8]) {
        let path = self.root.join(path);
        fs::create_dir_all(path.parent().unwrap()).unwrap();
        fs::write(path, bytes).unwrap();
    }
    fn edit(&self, change: impl FnOnce(&mut serde_json::Value)) {
        let path = self.root.join(MANIFEST_PATH);
        let mut value = serde_json::from_slice(&fs::read(&path).unwrap()).unwrap();
        change(&mut value);
        fs::write(path, serde_json::to_vec_pretty(&value).unwrap()).unwrap();
    }
    fn service(&self, bytes: &[u8]) {
        self.write(SERVICE_PATH, bytes);
        self.edit(|value| { value["service"]["bytes"] = (bytes.len() as u64).into();
            value["service"]["sha256"] = hex(&Sha256::digest(bytes)).into(); });
    }
    fn repin_client(&self) {
        let bytes = fs::read(self.root.join("client/package/RELEASE-CATALOG.json")).unwrap();
        self.edit(|value| value["admitted_client"]["manifest_sha256"] = hex(&Sha256::digest(bytes)).into());
    }
    fn client_role(&self, index: usize, bytes: &[u8]) {
        self.write(&format!("client/{}", ROLE_PATHS[index].1), bytes);
        let path = self.root.join("client/package/RELEASE-CATALOG.json");
        let mut catalog: serde_json::Value = serde_json::from_slice(&fs::read(&path).unwrap()).unwrap();
        catalog["components"][index]["bytes"] = (bytes.len() as u64).into();
        catalog["components"][index]["sha256"] = hex(&Sha256::digest(bytes)).into();
        fs::write(path, serde_json::to_vec(&catalog).unwrap()).unwrap();
        self.repin_client();
    }
    fn load(&self) -> Result<VerifiedPublicPackage, PublicPackageError> {
        verify_public_server_package(&self.root.join(SERVICE_PATH))
    }
}
impl Drop for Fixture { fn drop(&mut self) { fs::remove_dir_all(&self.root).unwrap(); } }

#[test]
fn both_distinct_identities_bind_original_client_and_linux_service() {
    let fixture = Fixture::new();
    let verified = fixture.load().unwrap();
    let client = release_catalog::verify_package(&fixture.root.join("client")).unwrap();
    assert_eq!(verified.admitted_client(), &client);
    assert_eq!(verified.service_identity().platform(), "linux-x86_64");
    assert_eq!(verified.service_identity().release_id(), RELEASE_ID);
    assert_eq!(verified.service_identity().compat_id(), COMPAT_ID);
    assert_eq!(verified.service_identity().manifest_sha256(), hex(&Sha256::digest(fs::read(fixture.root.join(MANIFEST_PATH)).unwrap())));
    assert_eq!(verified.service_identity().executable_sha256(), hex(&Sha256::digest(fs::read(fixture.root.join(SERVICE_PATH)).unwrap())));
    assert!(!fixture.root.join("client/DefaultSaves").exists());
}

#[test]
fn linux_service_bytes_never_replace_the_original_windows_server_role() {
    let fixture = Fixture::new();
    let linux = fs::read(fixture.root.join(SERVICE_PATH)).unwrap();
    let client_path = fixture.root.join("client/runtime/FifthFreedom.Server.exe");
    fs::write(&client_path, &linux).unwrap();
    let catalog_path = fixture.root.join("client/package/RELEASE-CATALOG.json");
    let mut catalog: serde_json::Value = serde_json::from_slice(&fs::read(&catalog_path).unwrap()).unwrap();
    catalog["components"][3]["bytes"] = (linux.len() as u64).into();
    catalog["components"][3]["sha256"] = hex(&Sha256::digest(&linux)).into();
    fs::write(catalog_path, serde_json::to_vec(&catalog).unwrap()).unwrap();
    fixture.repin_client();
    // Existing verifier is intentionally format-neutral. The new public boundary
    // additionally rejects a Linux file masquerading as the Windows client role.
    assert!(release_catalog::verify_package(&fixture.root.join("client")).is_ok());
    assert_eq!(fixture.load(), Err(PublicPackageError::ClientMismatch));
}

#[test]
fn every_client_role_is_really_verified_and_required() {
    for (_, relative) in ROLE_PATHS {
        for missing in [true, false] {
            let fixture = Fixture::new();
            let path = fixture.root.join("client").join(relative);
            if missing { fs::remove_file(path).unwrap(); }
            else { let mut bytes = fs::read(&path).unwrap(); bytes.push(b'x'); fs::write(path, bytes).unwrap(); }
            assert!(fixture.load().is_err(), "{relative}, missing={missing}");
        }
    }
}

#[test]
fn client_digest_is_the_exact_catalog_bytes_and_admission_does_not_upgrade() {
    let fixture = Fixture::new();
    let original = fixture.load().unwrap();
    let path = fixture.root.join("client/package/RELEASE-CATALOG.json");
    let mut bytes = fs::read(&path).unwrap(); bytes.push(b'\n'); fs::write(&path, bytes).unwrap();
    assert_eq!(fixture.load(), Err(PublicPackageError::ClientMismatch));
    fixture.repin_client();
    let repinned = fixture.load().unwrap();
    assert_ne!(original.service_identity(), repinned.service_identity());
    assert_eq!(original.admitted_client().admit(Some(repinned.admitted_client())),
        Err(crate::release_contract::AdmissionError::CatalogMismatch));
}

#[test]
fn public_manifest_digest_is_distinct_from_unchanged_client_identity() {
    let fixture = Fixture::new();
    let before = fixture.load().unwrap();
    let path = fixture.root.join(MANIFEST_PATH);
    let mut bytes = fs::read(&path).unwrap(); bytes.push(b'\n'); fs::write(&path, bytes).unwrap();
    let after = fixture.load().unwrap();
    assert_eq!(before.admitted_client(), after.admitted_client());
    assert_ne!(before.service_identity(), after.service_identity());
}

#[test]
fn malformed_duplicate_unknown_and_unbounded_manifest_fail_closed() {
    for bytes in [Vec::new(), b"{}".to_vec(), b"null".to_vec(), b"{broken".to_vec(),
        b"{\"schema\":1,\"schema\":1}".to_vec(), vec![b' '; MAX_MANIFEST_BYTES + 1], vec![0xff; 100]] {
        let fixture = Fixture::new(); fixture.write(MANIFEST_PATH, &bytes); assert!(fixture.load().is_err());
    }
    for location in ["top", "service", "client"] {
        let fixture = Fixture::new();
        fixture.edit(|value| match location {
            "top" => value["unknown"] = true.into(),
            "service" => value["service"]["unknown"] = true.into(),
            _ => value["admitted_client"]["unknown"] = true.into(),
        });
        assert_eq!(fixture.load(), Err(PublicPackageError::InvalidManifest));
    }
    for (needle, replacement) in [("\"schema\":1", "\"schema\":1,\"schema\":1"),
        ("\"role\":\"server\"", "\"role\":\"server\",\"role\":\"server\""),
        ("\"root\":\"client\"", "\"root\":\"client\",\"root\":\"client\"")] {
        let fixture = Fixture::new();
        let value: serde_json::Value = serde_json::from_slice(&fs::read(fixture.root.join(MANIFEST_PATH)).unwrap()).unwrap();
        let valid = serde_json::to_string(&value).unwrap();
        assert!(valid.contains(needle));
        fixture.write(MANIFEST_PATH, valid.replacen(needle, replacement, 1).as_bytes());
        assert_eq!(fixture.load(), Err(PublicPackageError::InvalidManifest));
    }
    let fixture = Fixture::new();
    let mut valid_but_unbounded = fs::read(fixture.root.join(MANIFEST_PATH)).unwrap();
    valid_but_unbounded.resize(MAX_MANIFEST_BYTES + 1, b' ');
    assert!(serde_json::from_slice::<Manifest>(&valid_but_unbounded).is_ok());
    fixture.write(MANIFEST_PATH, &valid_but_unbounded);
    assert_eq!(fixture.load(), Err(PublicPackageError::InvalidManifest));
}

#[test]
fn rehashed_windows_roles_reject_malformed_pe_headers_and_unsupported_architectures() {
    for (index, offset, value) in [(0, 64, b'X'), (0, 60, 0xff), (0, 84, 0), (0, 88, 0),
        (0, 68, 0x64), (3, 68, 0xaa)] {
        let fixture = Fixture::new();
        let mut bytes = synthetic_pe(ROLE_PATHS[index].0); bytes[offset] = value;
        fixture.client_role(index, &bytes);
        assert!(release_catalog::verify_package(&fixture.root.join("client")).is_ok());
        assert_eq!(fixture.load(), Err(PublicPackageError::ClientMismatch));
    }
    let fixture = Fixture::new();
    let mut x86_server = synthetic_pe("server");
    x86_server[68..70].copy_from_slice(&0x14c_u16.to_le_bytes());
    x86_server[88..90].copy_from_slice(&0x10b_u16.to_le_bytes());
    fixture.client_role(3, &x86_server);
    assert!(fixture.load().is_ok());
}

#[test]
fn manifest_cannot_redirect_paths_roles_release_or_platform() {
    for (field, value) in [
        ("schema", serde_json::json!(2)), ("platform", serde_json::json!("windows-x86_64")),
        ("role", serde_json::json!("main")), ("path", serde_json::json!("../server")),
        ("release_id", serde_json::json!("other")), ("compat_id", serde_json::json!("other")),
        ("bytes", serde_json::json!(0)), ("bytes", serde_json::json!(MAX_SERVICE_BYTES + 1)),
        ("sha256", serde_json::json!("A".repeat(64))),
        ("client-root", serde_json::json!("../client")),
        ("client-catalog", serde_json::json!("package/other.json")),
    ] {
        let fixture = Fixture::new();
        fixture.edit(|manifest| match field {
            "schema" | "platform" => manifest[field] = value,
            "client-root" => manifest["admitted_client"]["root"] = value,
            "client-catalog" => manifest["admitted_client"]["catalog"] = value,
            _ => manifest["service"][field] = value,
        });
        assert_eq!(fixture.load(), Err(PublicPackageError::InvalidManifest), "{field}");
    }
    for field in ["release_id", "compat_id", "manifest_sha256"] {
        let fixture = Fixture::new();
        fixture.edit(|manifest| manifest["admitted_client"][field] = if field == "manifest_sha256" { "0".repeat(64).into() } else { "other".into() });
        assert_eq!(fixture.load(), Err(PublicPackageError::ClientMismatch));
    }
}

#[test]
fn service_hash_and_size_are_checked_without_rewriting_inputs() {
    let fixture = Fixture::new();
    let original = fs::read(fixture.root.join(SERVICE_PATH)).unwrap();
    let mut changed = original.clone(); *changed.last_mut().unwrap() ^= 1;
    fixture.write(SERVICE_PATH, &changed); assert!(fixture.load().is_err());
    assert_eq!(fs::read(fixture.root.join(SERVICE_PATH)).unwrap(), changed);
    fixture.write(SERVICE_PATH, &original);
    fixture.edit(|value| value["service"]["sha256"] = "0".repeat(64).into());
    assert_eq!(fixture.load(), Err(PublicPackageError::ServiceMismatch));
    fixture.service(&original);
    fixture.edit(|value| value["service"]["bytes"] = (original.len() as u64 + 1).into());
    assert_eq!(fixture.load(), Err(PublicPackageError::ServiceMismatch));
}

#[test]
fn rehashed_wrong_elf_class_endianness_machine_type_and_header_are_rejected() {
    for (index, value) in [(0, b'M'), (4, 1), (5, 2), (6, 0), (7, 255), (16, 1), (18, 3), (20, 2), (52, 63)] {
        let fixture = Fixture::new();
        let mut bytes = synthetic_elf(SERVER_MARKER.as_bytes()); bytes[index] = value;
        fixture.service(&bytes);
        assert_eq!(fixture.load(), Err(PublicPackageError::ServicePlatformMismatch), "byte {index}");
    }
}

#[test]
fn rehashed_old_foreign_missing_and_duplicate_service_markers_are_rejected() {
    for marker in [String::new(), format!("SCBL_RELEASE_V1|server|old|{COMPAT_ID}|END_SCBl"),
        release_catalog::role_marker("gameplay").unwrap(), SERVER_MARKER.repeat(2)] {
        let fixture = Fixture::new(); fixture.service(&synthetic_elf(marker.as_bytes()));
        assert_eq!(fixture.load(), Err(PublicPackageError::ServiceMarkerMismatch));
    }
    for wide in [false, true] {
        let fixture = Fixture::new();
        let foreign = release_catalog::role_marker("main").unwrap();
        let mut bytes = synthetic_elf(SERVER_MARKER.as_bytes());
        if wide { bytes.extend(foreign.encode_utf16().flat_map(u16::to_le_bytes)); }
        else { bytes.extend_from_slice(foreign.as_bytes()); }
        fixture.service(&bytes);
        assert_eq!(fixture.load(), Err(PublicPackageError::ServiceMarkerMismatch));
    }
}

#[test]
fn chunk_split_and_wide_single_service_markers_are_counted_exactly_once() {
    for wide in [false, true] {
        let fixture = Fixture::new();
        let mut bytes = synthetic_elf(b""); bytes.resize(65536 - 15, b'x');
        if wide { bytes.extend(SERVER_MARKER.encode_utf16().flat_map(u16::to_le_bytes)); }
        else { bytes.extend_from_slice(SERVER_MARKER.as_bytes()); }
        fixture.service(&bytes); assert!(fixture.load().is_ok());
    }
}

#[test]
fn malformed_or_truncated_second_own_marker_cannot_hide_beside_a_valid_marker() {
    for wide in [false, true] {
        for suffix in ["SCBL_RELEASE_V1|server|", "SCBL_RELEASE_V1|server|old|broken", "SCBL_RELEASE_V1|server|?|"] {
            let fixture = Fixture::new();
            let mut bytes = synthetic_elf(SERVER_MARKER.as_bytes());
            if wide { bytes.extend(suffix.encode_utf16().flat_map(u16::to_le_bytes)); }
            else { bytes.extend_from_slice(suffix.as_bytes()); }
            fixture.service(&bytes);
            assert_eq!(fixture.load(), Err(PublicPackageError::ServiceMarkerMismatch));
        }
    }
    let fixture = Fixture::new();
    let mut bytes = synthetic_elf(SERVER_MARKER.as_bytes()); bytes.extend_from_slice(b" SCBL_RELEASE_V1| ");
    fixture.service(&bytes);
    assert!(fixture.load().is_ok(), "a generic scanner prefix is not an embedded identity");
}

#[test]
fn production_executable_is_fixed_and_no_parent_search_or_path_normalization_is_used() {
    let fixture = Fixture::new();
    for path in [fixture.root.join("FifthFreedom.Server"), fixture.root.join("bin/other"),
        fixture.root.join("nested/bin/FifthFreedom.Server"),
        PathBuf::from(format!("{}/./bin/FifthFreedom.Server", fixture.root.display())),
        PathBuf::from(format!("{}//bin/FifthFreedom.Server", fixture.root.display())),
        fixture.root.join("bin/../bin/FifthFreedom.Server"), PathBuf::from("bin/FifthFreedom.Server")] {
        assert!(verify_public_server_package(&path).is_err());
    }
    assert!(verify_public_package(&fixture.root.join("..")).is_err());
}

#[test]
fn symlinks_and_hardlinks_are_rejected_for_service_catalogs_and_every_client_role() {
    let paths: Vec<_> = [MANIFEST_PATH.to_owned(), SERVICE_PATH.to_owned(), "client/package/RELEASE-CATALOG.json".into()]
        .into_iter().chain(ROLE_PATHS.into_iter().map(|(_, path)| format!("client/{path}"))).collect();
    for path in paths {
        for hard in [false, true] {
            let fixture = Fixture::new();
            let original = fixture.root.join(&path);
            let evidence = fixture.root.join("preserved-original");
            fs::rename(&original, &evidence).unwrap();
            let before = fs::read(&evidence).unwrap();
            if hard { fs::hard_link(&evidence, original).unwrap(); }
            else { symlink(&evidence, original).unwrap(); }
            assert!(fixture.load().is_err(), "{path}, hard={hard}");
            assert_eq!(fs::read(evidence).unwrap(), before);
        }
    }
}

#[test]
fn symlinked_ancestor_cannot_escape_the_explicit_package_root() {
    let fixture = Fixture::new();
    let external = Fixture::new();
    fs::remove_dir_all(fixture.root.join("client")).unwrap();
    symlink(external.root.join("client"), fixture.root.join("client")).unwrap();
    assert!(fixture.load().is_err());
    let alias = fixture.root.join("root-alias");
    symlink(&external.root, &alias).unwrap();
    assert!(verify_public_package(&alias).is_err());
}
