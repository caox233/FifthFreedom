//! Finite disposable-VM development probe, never a Public admission producer.
#[cfg(not(windows))]
fn main() { std::process::exit(2); }

#[cfg(windows)]
mod probe {
    use std::{collections::BTreeSet, fs::OpenOptions, io::Read, os::windows::fs::OpenOptionsExt,
        path::Path, time::{Duration, Instant}};
    use sha2::{Digest, Sha256};
    use tokio::io::{AsyncReadExt, AsyncWriteExt};
    use windows_sys::Win32::{Foundation::ERROR_SUCCESS,
        NetworkManagement::IpHelper::{FreeMibTable, GetIfTable2, MIB_IF_TABLE2},
        Storage::FileSystem::FILE_SHARE_READ};
    type Result<T> = std::result::Result<T, &'static str>;
    type Identity = (u128, u64, u32, String);
    const DLL_HASH: &str = "e5da8447dc2c320edc0fc52fa01885c103de8c118481f683643cacc3220dafce";

    fn snapshot() -> Result<BTreeSet<Identity>> {
        unsafe {
            let mut ptr: *mut MIB_IF_TABLE2 = std::ptr::null_mut();
            if GetIfTable2(&mut ptr) != ERROR_SUCCESS || ptr.is_null() { return Err("INVENTORY"); }
            struct Table(*mut MIB_IF_TABLE2);
            impl Drop for Table { fn drop(&mut self) { unsafe { FreeMibTable(self.0.cast()); } } }
            let table = Table(ptr);
            let count = (*table.0).NumEntries as usize;
            if count == 0 || count > 4096 { return Err("INVENTORY"); }
            let mut identities = BTreeSet::new();
            for row in std::slice::from_raw_parts((*table.0).Table.as_ptr(), count) {
                let g = row.InterfaceGuid;
                let guid = ((g.data1 as u128) << 96) | ((g.data2 as u128) << 80)
                    | ((g.data3 as u128) << 64) | u64::from_be_bytes(g.data4) as u128;
                let end = row.Alias.iter().position(|x| *x == 0).ok_or("INVENTORY")?;
                let alias = String::from_utf16(&row.Alias[..end]).map_err(|_| "INVENTORY")?;
                if guid == 0 || row.InterfaceLuid.Value == 0 || row.InterfaceIndex == 0 || alias.is_empty()
                    || !identities.insert((guid, row.InterfaceLuid.Value, row.InterfaceIndex, alias)) {
                    return Err("INVENTORY");
                }
            }
            Ok(identities)
        }
    }
    fn id(value: &tun::platform::windows::SessionIdentity) -> Identity {
        (value.guid, value.luid, value.index, value.name.clone())
    }
    fn require(condition: bool, code: &'static str) -> Result<()> {
        if condition { Ok(()) } else { Err(code) }
    }
    fn wait_absent(guid: u128, luid: u64) -> Result<()> {
        let deadline = Instant::now() + Duration::from_secs(10);
        loop {
            if snapshot()?.iter().all(|x| x.0 != guid && x.1 != luid) { return Ok(()); }
            if Instant::now() >= deadline { return Err("ADAPTER_RETAINED"); }
            std::thread::sleep(Duration::from_millis(100));
        }
    }
    fn create(path: &Path, guid: u128) -> Result<(tun::platform::Device,
        tun::platform::windows::SessionIdentityObserver, Identity)> {
        let name = format!("ff-{guid:032x}");
        require(snapshot()?.iter().all(|x| x.0 != guid && x.3 != name), "COLLISION")?;
        let mut config = tun::Configuration::default();
        config.tun_name(&name).platform_config(|p| {
            p.request_new_session(guid); p.skip_config(true);
            p.wintun_file(path.as_os_str()); p.ring_cap(Some(p.min_ring_cap()));
        });
        let device = tun::create(&config).map_err(|error| match error {
            tun::Error::Io(_) => "CREATE_IO",
            tun::Error::WintunError(_) => "CREATE_DRIVER",
            tun::Error::LibloadingError(_) => "CREATE_LOADER",
            tun::Error::String(_) => "CREATE_SIGNER_OR_STRING",
            _ => "CREATE_CONFIGURATION",
        })?;
        let observer = device.session_identity_observer().ok_or("OBSERVER")?;
        let identity = id(&observer.identity().map_err(|_| "IDENTITY")?);
        require(identity.0 == guid && identity.3 == name && snapshot()?.contains(&identity), "IDENTITY")?;
        Ok((device, observer, identity))
    }
    fn drop_case(path: &Path, guid: u128) -> Result<()> {
        let (device, observer, identity) = create(path, guid)?;
        let cloned = observer.clone();
        require(id(&cloned.identity().map_err(|_| "IDENTITY")?) == identity, "IDENTITY")?;
        drop(device);
        require(observer.identity().is_err() && cloned.identity().is_err(), "DROP_NOT_REVOKED")?;
        wait_absent(guid, identity.1)?;
        require(observer.identity().is_err() && cloned.identity().is_err(), "REVOCATION_NOT_STICKY")
    }
    async fn shutdown_case(path: &Path, guid: u128) -> Result<()> {
        let (device, observer, identity) = create(path, guid)?;
        let cloned = observer.clone();
        let mut device = tun::AsyncDevice::new(device).map_err(|_| "ASYNC_CREATE")?;
        // No write/send call: shutdown is the actual production owner operation.
        device.shutdown().await.map_err(|_| "SHUTDOWN")?;
        require(observer.identity().is_err() && cloned.identity().is_err(), "SHUTDOWN_NOT_REVOKED")?;
        // EOF proves the production receive worker released its sender. Merely
        // observing revoked state would not prove that its blocking wait ended.
        // The real backend copies a complete packet in poll_read. Allow its
        // maximum size so unsolicited input is a fixed failure, not a panic.
        let mut byte = [0u8; 65536];
        let read = tokio::time::timeout(Duration::from_secs(5), device.read(&mut byte)).await
            .map_err(|_| "WORKER_TIMEOUT")?.map_err(|_| "WORKER_READ")?;
        require(read == 0, "UNEXPECTED_PACKET")?;
        drop(device);
        wait_absent(guid, identity.1)?;
        require(observer.identity().is_err() && cloned.identity().is_err(), "REVOCATION_NOT_STICKY")
    }
    fn parse_guid(value: &str) -> Result<u128> {
        require(value.len() == 32 && value.bytes().all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b)), "ARGUMENTS")?;
        let guid = u128::from_str_radix(value, 16).map_err(|_| "ARGUMENTS")?;
        require(guid != 0, "ARGUMENTS")?; Ok(guid)
    }
    fn run(passed: &mut u32, unrelated: &mut bool) -> Result<()> {
        let args: Vec<_> = std::env::args_os().collect();
        require(args.len() == 4, "ARGUMENTS")?;
        let path = Path::new(&args[1]);
        let first = parse_guid(args[2].to_str().ok_or("ARGUMENTS")?)?;
        let second = parse_guid(args[3].to_str().ok_or("ARGUMENTS")?)?;
        require(first != second && path.is_absolute(), "ARGUMENTS")?;
        // Hold a read-only sharing handle through all loads, excluding ordinary
        // replacement/writes of the same owned DLL while signature/load run.
        let mut pinned = OpenOptions::new().read(true).share_mode(FILE_SHARE_READ).open(path).map_err(|_| "DLL")?;
        require(pinned.metadata().map_err(|_| "DLL")?.len() == 427552, "DLL")?;
        let mut bytes = Vec::new();
        pinned.by_ref().take(427553).read_to_end(&mut bytes).map_err(|_| "DLL")?;
        require(format!("{:x}", Sha256::digest(&bytes)) == DLL_HASH, "DLL")?;
        let before = snapshot()?;
        let result = (|| {
            drop_case(path, first)?; *passed += 1;
            let runtime = tokio::runtime::Builder::new_current_thread().enable_time().build().map_err(|_| "ASYNC_CREATE")?;
            runtime.block_on(shutdown_case(path, second))?; *passed += 1; Ok(())
        })();
        // Always audit identity after a completed case failure as well.
        *unrelated = snapshot()? == before;
        require(*unrelated, "IDENTITY_CHANGED")?;
        result
    }
    pub fn main() {
        std::panic::set_hook(Box::new(|_| {}));
        let mut passed = 0; let mut unrelated = false;
        let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| run(&mut passed, &mut unrelated)))
            .unwrap_or(Err("PANIC"));
        let code = result.err().unwrap_or("OK");
        println!("{{\"schema\":1,\"code\":\"{code}\",\"passed\":{passed},\"unrelated_identities_equal\":{unrelated},\"public_admission\":false,\"network_configured\":false,\"packets_sent\":false}}");
        std::process::exit(if code == "OK" { 0 } else { 1 });
    }
}
#[cfg(windows)]
fn main() { probe::main(); }
