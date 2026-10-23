//! Windows-only, memory-only return channel for the exact owned launcher.
//! Authentication queues state synchronously; all pipe I/O runs off LOGIN_LOCK
//! and off DllMain. Missing/invalid Public configuration never creates a bridge.
use std::fs::File;
use std::io;
use std::os::windows::fs::OpenOptionsExt;
use std::os::windows::io::AsRawHandle;
use std::sync::{Arc, Condvar, Mutex, OnceLock};
use std::time::{Duration, Instant};
use server_api::public_return_contract::{Attempt, Descriptor, Frame, ReturnState, ReturnedAuthentication,
    DESCRIPTOR_ENV, FRAME_TIMEOUT_SECONDS, HEARTBEAT_SECONDS, MAX_FRAME, STARTUP_TIMEOUT_SECONDS};
use windows::core::PCWSTR;
use windows::Win32::Foundation::{CloseHandle, HANDLE, HMODULE, ERROR_IO_PENDING, WAIT_OBJECT_0, WAIT_TIMEOUT};
use windows::Win32::Storage::FileSystem::{ReadFile, WriteFile, FILE_FLAG_OVERLAPPED, SECURITY_IDENTIFICATION, SECURITY_SQOS_PRESENT};
use windows::Win32::System::IO::{CancelIoEx, GetOverlappedResult, OVERLAPPED};
use windows::Win32::System::LibraryLoader::{GetModuleHandleExW, GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS, GET_MODULE_HANDLE_EX_FLAG_PIN};
use windows::Win32::System::Threading::{CreateEventW, WaitForSingleObject};

static BRIDGE: OnceLock<Option<Arc<Bridge>>> = OnceLock::new();

fn invalid() -> io::Error { io::Error::new(io::ErrorKind::InvalidData, "PUBLIC_RETURN_UNAVAILABLE") }
fn lock<T>(value: &Mutex<T>) -> std::sync::MutexGuard<'_, T> {
    value.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
}

fn matches_start_anchor(owner: &hooks_config::launch_owner::LaunchOwner, descriptor: &Descriptor) -> bool {
    owner.alive() && owner.pid == descriptor.launcher_pid
        && owner.creation_filetime == descriptor.launcher_creation_filetime
        && owner.image.to_str().is_some_and(|path| path.eq_ignore_ascii_case(&descriptor.launcher_path))
}

struct Bridge {
    state: Mutex<ReturnState>,
    changed: Condvar,
    descriptor: Descriptor,
    owner: &'static hooks_config::launch_owner::LaunchOwner,
}

/// Called on normal login execution, before acquiring LOGIN_LOCK. It performs
/// local admission once; neither raw descriptor nor inner errors are logged.
pub(super) fn prepare() {
    BRIDGE.get_or_init(|| {
        let raw = std::env::var(DESCRIPTOR_ENV).ok()?;
        let descriptor = Descriptor::parse(&raw).ok()?;
        let owner = hooks_config::launch_owner::observed_launch_owner()?;
        if !matches_start_anchor(owner, &descriptor) {
            return None;
        }
        crate::release_identity::declaration().ok()?;
        crate::release_identity::verify_return_launcher(&owner.image, &descriptor.launcher_sha256).ok()?;
        // The worker may outlive an explicit FreeLibrary request. Public mode
        // pins this hooks module for the process lifetime; never execute code
        // from an unloaded DLL or join a worker while holding the loader lock.
        let mut module = HMODULE::default();
        unsafe { GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
            PCWSTR(worker as *const () as *const u16), &mut module) }.ok()?;
        let bridge = Arc::new(Bridge { state: Mutex::new(ReturnState::default()), changed: Condvar::new(), descriptor, owner });
        let owned = bridge.clone();
        std::thread::Builder::new().name("public-return".into()).spawn(move || worker(owned)).ok()?;
        Some(bridge)
    });
}

#[cfg(test)]
#[path = "public_return_tests.rs"]
mod tests;

pub(super) struct AuthenticationPublication {
    bridge: Arc<Bridge>,
    owner: Attempt,
    completed: bool,
}
impl AuthenticationPublication {
    pub(super) fn publish(mut self, value: ReturnedAuthentication) {
        let _ = lock(&self.bridge.state).publish(self.owner, value);
        self.completed = true;
        self.bridge.changed.notify_all();
    }
}
impl Drop for AuthenticationPublication {
    fn drop(&mut self) {
        if !self.completed {
            let _ = lock(&self.bridge.state).invalidate(self.owner);
            self.bridge.changed.notify_all();
        }
    }
}
pub(super) fn begin() -> Option<AuthenticationPublication> {
    let bridge = BRIDGE.get()?.as_ref()?.clone();
    let owner = lock(&bridge.state).begin().ok()?;
    bridge.changed.notify_all();
    Some(AuthenticationPublication { bridge, owner, completed: false })
}
pub(super) fn stop() {
    if let Some(Some(bridge)) = BRIDGE.get() {
        lock(&bridge.state).close();
        bridge.changed.notify_all();
    }
}

struct Event(HANDLE);
impl Drop for Event { fn drop(&mut self) { unsafe { let _ = CloseHandle(self.0); } } }
struct Pipe { file: File, event: Event }
impl Pipe {
    fn connect(bridge: &Bridge, deadline: Instant) -> io::Result<Self> {
        let path = format!(r"\\.\pipe\{}", bridge.descriptor.pipe);
        let file = loop {
            if !bridge.owner.alive() || lock(&bridge.state).is_closed() { return Err(invalid()); }
            match File::options().read(true).write(true).custom_flags(FILE_FLAG_OVERLAPPED.0
                | SECURITY_SQOS_PRESENT.0 | SECURITY_IDENTIFICATION.0).open(&path) {
                Ok(file) => break file,
                Err(_) if Instant::now() < deadline => std::thread::sleep(Duration::from_millis(25)),
                Err(_) => return Err(invalid()),
            }
        };
        if !bridge.owner.matches_pipe(file.as_raw_handle()) { return Err(invalid()); }
        let event = unsafe { CreateEventW(None, true, false, PCWSTR::null()) }.map_err(|_| invalid())?;
        Ok(Self { file, event: Event(event) })
    }
    fn handle(&self) -> HANDLE { HANDLE(self.file.as_raw_handle() as isize) }
    fn transfer(&self, deadline: Instant, start: impl FnOnce(*mut OVERLAPPED) -> windows::core::Result<()>) -> io::Result<usize> {
        let remaining = || deadline.checked_duration_since(Instant::now()).filter(|value| !value.is_zero())
            .map(|value| ((value.as_nanos() + 999_999) / 1_000_000).min(u128::from(u32::MAX - 1)) as u32).ok_or_else(invalid);
        remaining()?;
        let mut operation = OVERLAPPED { hEvent: self.event.0, ..OVERLAPPED::default() };
        match start(&mut operation) {
            Ok(()) => {}
            Err(error) if error.code() == ERROR_IO_PENDING.to_hresult() => {
                let wait = remaining().map(|ms| unsafe { WaitForSingleObject(self.event.0, ms) }).unwrap_or(WAIT_TIMEOUT);
                if wait != WAIT_OBJECT_0 {
                    unsafe {
                        // Cancellation alone does not release the kernel's
                        // references. Drain completion before buffers/OVERLAPPED
                        // or their event are dropped, even on a raced timeout.
                        let _ = CancelIoEx(self.handle(), Some(&operation));
                        let mut completed = 0;
                        let _ = GetOverlappedResult(self.handle(), &operation, &mut completed, true);
                    }
                    return Err(invalid());
                }
            }
            Err(_) => return Err(invalid()),
        }
        let mut completed = 0;
        unsafe { GetOverlappedResult(self.handle(), &operation, &mut completed, false) }.map_err(|_| invalid())?;
        Ok(completed as usize)
    }
    fn read_exact(&self, mut bytes: &mut [u8], deadline: Instant) -> io::Result<()> {
        while !bytes.is_empty() {
            let count = self.transfer(deadline, |operation| unsafe { ReadFile(self.handle(), Some(bytes), None, Some(operation)) })?;
            if count == 0 || count > bytes.len() { return Err(invalid()); }
            bytes = &mut bytes[count..];
        }
        Ok(())
    }
    fn write_all(&self, mut bytes: &[u8], deadline: Instant) -> io::Result<()> {
        while !bytes.is_empty() {
            let count = self.transfer(deadline, |operation| unsafe { WriteFile(self.handle(), Some(bytes), None, Some(operation)) })?;
            if count == 0 || count > bytes.len() { return Err(invalid()); }
            bytes = &bytes[count..];
        }
        Ok(())
    }
    fn read_frame(&self, deadline: Instant) -> io::Result<Frame> {
        let mut length = [0u8; 4]; self.read_exact(&mut length, deadline)?;
        let length = u32::from_le_bytes(length) as usize;
        if length == 0 || length > MAX_FRAME { return Err(invalid()); }
        let mut bytes = vec![0u8; length]; self.read_exact(&mut bytes, deadline)?;
        Frame::decode(&bytes).map_err(|_| invalid())
    }
    fn write_frame(&self, frame: &Frame) -> io::Result<()> {
        let deadline = Instant::now() + Duration::from_secs(FRAME_TIMEOUT_SECONDS);
        self.write_frame_until(frame, deadline)
    }
    fn write_frame_until(&self, frame: &Frame, deadline: Instant) -> io::Result<()> {
        let bytes = frame.encode().map_err(|_| invalid())?;
        self.write_all(&(bytes.len() as u32).to_le_bytes(), deadline)?;
        self.write_all(&bytes, deadline)
    }
}

fn worker(bridge: Arc<Bridge>) {
    // Every exit permanently closes this channel for the process. No old token
    // can be replayed by an automatic reconnect; restart the owned launch.
    let _ = serve(&bridge);
    lock(&bridge.state).close();
    bridge.changed.notify_all();
}

fn serve(bridge: &Bridge) -> io::Result<()> {
    {
        let mut state = lock(&bridge.state);
        while state.generation() == 0 && !state.is_closed() {
            state = bridge.changed.wait_timeout(state, Duration::from_secs(1)).map_err(|_| invalid())?.0;
            if !bridge.owner.alive() { return Err(invalid()); }
        }
        if state.is_closed() { return Err(invalid()); }
    }
    let handshake_deadline = Instant::now() + Duration::from_secs(STARTUP_TIMEOUT_SECONDS);
    let pipe = Pipe::connect(bridge, handshake_deadline)?;
    let challenge = pipe.read_frame(handshake_deadline.min(Instant::now() + Duration::from_secs(FRAME_TIMEOUT_SECONDS)))?;
    let instance = super::current_game_instance().map_err(|_| invalid())?;
    let hello = challenge.hello(&bridge.descriptor.launch_id, instance).map_err(|_| invalid())?;
    if !bridge.owner.matches_pipe(pipe.file.as_raw_handle()) || lock(&bridge.state).is_closed() { return Err(invalid()); }
    pipe.write_frame_until(&hello, handshake_deadline.min(Instant::now() + Duration::from_secs(FRAME_TIMEOUT_SECONDS)))?;
    let mut sent: Option<Frame> = None;
    loop {
        if !bridge.owner.alive() { return Err(invalid()); }
        let snapshot = lock(&bridge.state).snapshot().ok_or_else(invalid)?;
        let frame = if sent.as_ref() == Some(&snapshot) {
            let generation = match &snapshot {
                Frame::Authenticated { generation, .. } | Frame::Invalidate { generation, .. } => *generation,
                _ => return Err(invalid()),
            };
            Frame::Heartbeat { schema: 1, generation }
        } else { snapshot.clone() };
        if !lock(&bridge.state).is_current(&frame) { continue; }
        pipe.write_frame(&frame)?;
        // A newer auth attempt can invalidate while an overlapped write is
        // pending. Never mark that late completion as the current sent state.
        let state = lock(&bridge.state);
        if !state.is_current(&frame) { continue; }
        sent = Some(snapshot);
        let _ = bridge.changed.wait_timeout(state, Duration::from_secs(HEARTBEAT_SECONDS)).map_err(|_| invalid())?;
    }
}
