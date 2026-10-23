//! Test-executable entry for the managed/native Windows interoperability probe.
//! No game / DLL loading / network login. Real package prepare is intentionally
//! bypassed: the independently pinned fixture image is not a release proof.
use super::*;
use std::io::{Read, Write};
use base64::{Engine, engine::general_purpose::URL_SAFE_NO_PAD};
use serde::Deserialize;
use sha2::{Digest, Sha256};

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Fixture {
    launcher_path: String,
    launcher_sha256: String,
    release_id: String,
    compat_id: String,
    manifest_sha256: String,
}
fn marker(text: &str) -> io::Result<()> {
    // libtest may have written an unterminated test-name prefix. Keep protocol
    // markers on their own line without forwarding any diagnostics or frame.
    println!("\n{text}");
    io::stdout().flush().map_err(|_| invalid())
}
fn command(input: &mut impl Read) -> io::Result<Option<String>> {
    let mut value = Vec::with_capacity(16);
    loop {
        let mut byte = [0];
        if input.read(&mut byte).map_err(|_| invalid())? == 0 {
            return if value.is_empty() { Ok(None) } else { Err(invalid()) };
        }
        if byte[0] == b'\n' {
            if value.last() == Some(&b'\r') { value.pop(); }
            return String::from_utf8(value).map(Some).map_err(|_| invalid());
        }
        if value.len() >= 16 || !byte[0].is_ascii() { return Err(invalid()); }
        value.push(byte[0]);
    }
}

pub(super) fn run(raw: &str) -> io::Result<()> {
    if raw.is_empty() || raw.len() > 4096 { return Err(invalid()); }
    let fixture: Fixture = serde_json::from_str(raw).map_err(|_| invalid())?;
    let release = server_api::release_contract::ReleaseIdentity::new(1, &fixture.release_id,
        &fixture.compat_id, &fixture.manifest_sha256).map_err(|_| invalid())?;
    let declaration = server_api::users::PackageIdentity { schema: 1, release_id: fixture.release_id,
        compat_id: fixture.compat_id, manifest_sha256: fixture.manifest_sha256, ..Default::default() };
    let descriptor = Descriptor::parse(&std::env::var(DESCRIPTOR_ENV).map_err(|_| invalid())?).map_err(|_| invalid())?;
    // The parent owns the exact helper handle; stdin EOF releases this helper.
    // A separate hard bound covers a broken synthetic parent/test harness.
    std::thread::spawn(|| { std::thread::sleep(Duration::from_secs(45)); std::process::exit(90); });
    let executable = std::env::current_exe().map_err(|_| invalid())?;
    let reference = executable.with_file_name("scbl-lan.toml");
    if !std::fs::symlink_metadata(&reference).map_err(|_| invalid())?.is_file() { return Err(invalid()); }
    // This is the retained production config reader and ACK path. No game or
    // adapter probing occurs: explicit IPv4 is required below.
    let config = hooks_config::get_or_load(&executable).map_err(|_| invalid())?;
    if config.user.username != "synthetic-client" || config.user.account_id != "synthetic-account"
        || !config.user.password.is_empty() || config.gameplay_mode != hooks_config::GameplayMode::Mod
        || config.networking.ip_address != Some(std::net::Ipv4Addr::new(10, 66, 0, 10))
        || config.networking.adapter.is_some() { return Err(invalid()); }
    let owner = hooks_config::launch_owner::observed_launch_owner().ok_or_else(invalid)?;
    if !matches_start_anchor(owner, &descriptor)
        || !owner.image.to_str().is_some_and(|path| path.eq_ignore_ascii_case(&fixture.launcher_path))
        || fixture.launcher_sha256 != descriptor.launcher_sha256 { return Err(invalid()); }
    // Fixture consistency only: this is intentionally independent of the launch
    // descriptor, but does not replace prepare's real package / loaded-DLL gate.
    let mut image = File::open(&owner.image).map_err(|_| invalid())?;
    let size = image.metadata().map_err(|_| invalid())?.len();
    if size == 0 || size > 512 * 1024 * 1024 { return Err(invalid()); }
    let mut digest = Sha256::new();
    let mut bytes = [0; 16384];
    let mut count = 0u64;
    loop {
        let read = image.read(&mut bytes).map_err(|_| invalid())?;
        if read == 0 { break; }
        count += read as u64;
        if count > size { return Err(invalid()); }
        digest.update(&bytes[..read]);
    }
    if count != size || format!("{:x}", digest.finalize()) != fixture.launcher_sha256 || !owner.alive() {
        return Err(invalid());
    }
    let instance = super::super::current_game_instance().map_err(|_| invalid())?;
    let claims = serde_json::json!({"release":release,"user_id":1000,"username":"synthetic-client",
        "source_ip":"10.66.0.10","issued_at":1,"expires_at":2,"auth_version":1,
        "game_run":{"product":"scbl-lan/2/game/mod","instance":instance}});
    let token = format!("v1.{}.{}", URL_SAFE_NO_PAD.encode(serde_json::to_vec(&claims).map_err(|_| invalid())?),
        URL_SAFE_NO_PAD.encode([0u8;32]));
    let response = server_api::users::LoginResponse { error: String::new(), token,
        user: Some(server_api::users::User { id: "synthetic-account".into(), username: "synthetic-client".into(), ips: vec!["10.66.0.10".into()] }) };
    let returned = super::super::return_authentication(&response, "synthetic-client", hooks_config::GameplayMode::Mod,
        instance, &declaration, false).map_err(|_| invalid())?;
    let bridge = Arc::new(Bridge { state: Mutex::new(ReturnState::default()), changed: Condvar::new(), descriptor, owner });
    lock(&bridge.state).begin().map_err(|_| invalid())?;
    let owned = bridge.clone();
    let (send, done) = std::sync::mpsc::channel();
    let task = std::thread::spawn(move || { worker(owned); let _ = send.send(()); });
    let result = (|| {
        marker("READY")?;
        let mut input = io::stdin().lock();
        while let Some(command) = command(&mut input)? {
            match command.as_str() {
                "AUTH" => {
                    let owner = lock(&bridge.state).begin().map_err(|_| invalid())?;
                    AuthenticationPublication { bridge: bridge.clone(), owner, completed: false }.publish(returned.clone());
                    if !matches!(lock(&bridge.state).snapshot(), Some(Frame::Authenticated { .. })) { return Err(invalid()); }
                    marker("AUTHENTICATED")?;
                }
                "INVALIDATE" => {
                    let owner = lock(&bridge.state).begin().map_err(|_| invalid())?;
                    drop(AuthenticationPublication { bridge: bridge.clone(), owner, completed: false });
                    marker("INVALIDATED")?;
                }
                "EXIT" => break,
                _ => return Err(invalid()),
            }
        }
        Ok(())
    })();
    lock(&bridge.state).close();
    bridge.changed.notify_all();
    done.recv_timeout(Duration::from_secs(7)).map_err(|_| invalid())?;
    task.join().map_err(|_| invalid())?;
    result?;
    marker("STOPPED")
}
