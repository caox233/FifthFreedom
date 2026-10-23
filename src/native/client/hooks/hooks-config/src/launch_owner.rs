//! Observation of the actual original configuration-pipe server. This is not an
//! authorization by itself; Public return also requires the independent launch
//! descriptor and the separately verified package launcher role.
use std::os::windows::ffi::OsStringExt;
use std::os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle};
use std::path::PathBuf;
use std::sync::OnceLock;
use windows::core::PWSTR;
use windows::Win32::Foundation::{FILETIME, HANDLE, WAIT_TIMEOUT};
use windows::Win32::System::Pipes::GetNamedPipeServerProcessId;
use windows::Win32::System::Threading::{GetProcessTimes, OpenProcess, QueryFullProcessImageNameW,
    WaitForSingleObject, PROCESS_NAME_WIN32, PROCESS_QUERY_LIMITED_INFORMATION, PROCESS_SYNCHRONIZE};

static OWNER: OnceLock<LaunchOwner> = OnceLock::new();

pub struct LaunchOwner {
    process: OwnedHandle,
    pub pid: u32,
    pub creation_filetime: u64,
    pub image: PathBuf,
}
impl std::fmt::Debug for LaunchOwner {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result { f.write_str("ObservedLaunchOwner") }
}
impl LaunchOwner {
    pub fn alive(&self) -> bool {
        unsafe { WaitForSingleObject(HANDLE(self.process.as_raw_handle() as isize), 0) == WAIT_TIMEOUT }
    }
    pub fn matches_pipe(&self, pipe: std::os::windows::io::RawHandle) -> bool {
        let mut actual = 0;
        self.alive() && unsafe { GetNamedPipeServerProcessId(HANDLE(pipe as isize), &mut actual) }.is_ok()
            && actual == self.pid && self.alive()
    }
}

pub(crate) fn observe(pipe: HANDLE) -> Option<LaunchOwner> {
    observe_pipe_server(pipe.0 as _)
}

/// Pins the actual kernel pipe-server process. This observation grants no
/// package or launch authorization; callers must compare independent anchors.
pub fn observe_pipe_server(pipe: std::os::windows::io::RawHandle) -> Option<LaunchOwner> {
    let pipe = HANDLE(pipe as isize);
    let mut pid = 0;
    unsafe { GetNamedPipeServerProcessId(pipe, &mut pid) }.ok()?;
    if pid == 0 { return None; }
    let handle = unsafe { OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SYNCHRONIZE, false, pid) }.ok()?;
    let process = unsafe { OwnedHandle::from_raw_handle(handle.0 as _) };
    let (mut created, mut exited, mut kernel, mut user) = (FILETIME::default(), FILETIME::default(), FILETIME::default(), FILETIME::default());
    unsafe { GetProcessTimes(handle, &mut created, &mut exited, &mut kernel, &mut user) }.ok()?;
    let mut path = vec![0u16; 32768];
    let mut length = path.len() as u32;
    unsafe { QueryFullProcessImageNameW(handle, PROCESS_NAME_WIN32, PWSTR(path.as_mut_ptr()), &mut length) }.ok()?;
    if length == 0 || length as usize >= path.len() { return None; }
    let image = PathBuf::from(std::ffi::OsString::from_wide(&path[..length as usize]));
    let creation_filetime = (u64::from(created.dwHighDateTime) << 32) | u64::from(created.dwLowDateTime);
    let owner = LaunchOwner { process, pid, creation_filetime, image };
    (owner.creation_filetime != 0 && owner.image.is_absolute() && owner.alive()).then_some(owner)
}

pub(crate) fn remember(owner: Option<LaunchOwner>) {
    if let Some(owner) = owner { let _ = OWNER.set(owner); }
}

pub fn observed_launch_owner() -> Option<&'static LaunchOwner> { OWNER.get() }
