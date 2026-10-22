use std::ffi::c_char;
use std::io::Write;
use std::os::windows::ffi::OsStringExt;
use std::path::PathBuf;
use std::sync::Mutex;

use hooks_plugin_api::host_api_compatible;
use hooks_plugin_api::plugin_config_compatible;
use hooks_plugin_api::HostApiV1;
use hooks_plugin_api::PluginConfigV1;
use hooks_plugin_api::EVENT_NATIVE_SAVE_OPENED;
use hooks_plugin_api::LOG_INFO;
use tracing_subscriber::fmt::MakeWriter;
use tracing_subscriber::util::SubscriberInitExt;

mod extended_loadouts;
mod gameplay_features;
mod player_capacity;
mod weapon_probe;

static HOST_LOG: Mutex<Option<hooks_plugin_api::HostLogFn>> = Mutex::new(None);
static LOADOUT_SIDECAR_PATH: Mutex<Option<PathBuf>> = Mutex::new(None);

pub(crate) fn host_log(level: u32, message: &str) {
    let callback = HOST_LOG.lock().ok().and_then(|guard| *guard);
    let Some(callback) = callback else {
        return;
    };
    let Ok(length) = u32::try_from(message.len()) else {
        return;
    };
    unsafe { callback(level, message.as_ptr().cast::<c_char>(), length) };
}

struct HostWriter;

impl Write for HostWriter {
    fn write(&mut self, buffer: &[u8]) -> std::io::Result<usize> {
        let message = String::from_utf8_lossy(buffer);
        let message = message.trim();
        if !message.is_empty() {
            host_log(LOG_INFO, message);
        }
        Ok(buffer.len())
    }

    fn flush(&mut self) -> std::io::Result<()> {
        Ok(())
    }
}

struct HostMakeWriter;

impl<'a> MakeWriter<'a> for HostMakeWriter {
    type Writer = HostWriter;

    fn make_writer(&'a self) -> Self::Writer {
        HostWriter
    }
}

fn init_tracing() {
    let _ = tracing_subscriber::fmt().with_ansi(false).without_time().with_writer(HostMakeWriter).finish().try_init();
}

pub(crate) unsafe fn writemem(pointer: *mut u8, data: &[u8]) {
    if let Ok(_handle) = region::protect_with_handle(pointer, data.len(), region::Protection::READ_WRITE) {
        std::ptr::copy(data.as_ptr(), pointer, data.len());
    }
}

pub(crate) fn extended_loadout_sidecar_path() -> Option<PathBuf> {
    LOADOUT_SIDECAR_PATH.lock().ok().and_then(|path| path.clone())
}

#[no_mangle]
pub unsafe extern "system" fn scbl_plugin_initialize(host: *const HostApiV1, config: *const PluginConfigV1) -> i32 {
    let Some(host) = host.as_ref() else {
        return -1;
    };
    let Some(config) = config.as_ref() else {
        return -2;
    };
    if !host_api_compatible(host) || !plugin_config_compatible(config) {
        return -3;
    }
    if let Ok(mut callback) = HOST_LOG.lock() {
        *callback = host.log;
    } else {
        return -4;
    }
    init_tracing();
    if config.loadout_sidecar_path_len > 0 && !config.loadout_sidecar_path.is_null() {
        let Ok(length) = usize::try_from(config.loadout_sidecar_path_len) else {
            return -5;
        };
        let path = std::slice::from_raw_parts(config.loadout_sidecar_path, length);
        if let Ok(mut stored) = LOADOUT_SIDECAR_PATH.lock() {
            *stored = Some(PathBuf::from(std::ffi::OsString::from_wide(path)));
        } else {
            return -6;
        }
    }
    host_log(
        LOG_INFO,
        &format!("SCBL_GAMEPLAY_PLUGIN initialized abi={} feature_bits=0x{:016X}", config.abi_version, config.feature_bits),
    );
    gameplay_features::init(config.world_raycast as usize);
    if let Err(reason) = player_capacity::init() {
        host_log(hooks_plugin_api::LOG_ERROR, &format!("SCBL_MOD_PLAYER_CAPACITY enabled=false reason={reason}"));
    }
    extended_loadouts::init(config.world_raycast as usize);
    weapon_probe::init(config.world_raycast as usize);
    0
}

#[no_mangle]
pub extern "system" fn scbl_plugin_shutdown() {
    unsafe {
        player_capacity::deinit();
        extended_loadouts::deinit();
        weapon_probe::deinit();
        gameplay_features::deinit();
    }
    host_log(LOG_INFO, "SCBL_GAMEPLAY_PLUGIN shutdown");
    if let Ok(mut callback) = HOST_LOG.lock() {
        *callback = None;
    }
    if let Ok(mut path) = LOADOUT_SIDECAR_PATH.lock() {
        *path = None;
    }
}

#[no_mangle]
pub extern "system" fn scbl_plugin_handle_event(event: u32) {
    if event == EVENT_NATIVE_SAVE_OPENED {
        extended_loadouts::request_sidecar_reload();
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rejects_missing_host_and_config() {
        assert_eq!(unsafe { scbl_plugin_initialize(std::ptr::null(), std::ptr::null()) }, -1);
    }
}
