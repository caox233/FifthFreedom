//! Real Windows pipes/process handles; never loads a game or prepares a package.
use super::*;
use std::os::windows::io::FromRawHandle;
use std::process::{Child, Command, Stdio};
use std::sync::atomic::{AtomicU64, Ordering};
use windows::Win32::Foundation::{ERROR_PIPE_CONNECTED, INVALID_HANDLE_VALUE};
use windows::Win32::Storage::FileSystem::{FILE_FLAG_FIRST_PIPE_INSTANCE, PIPE_ACCESS_DUPLEX};
use windows::Win32::System::Pipes::{ConnectNamedPipe, CreateNamedPipeW, PIPE_READMODE_BYTE, PIPE_REJECT_REMOTE_CLIENTS, PIPE_TYPE_BYTE, PIPE_WAIT};

static NEXT: AtomicU64 = AtomicU64::new(1);
const CHILD_ENV: &str = "FF_SYNTHETIC_RETURN_PIPE_TEST";
fn deadline() -> Instant { Instant::now() + Duration::from_secs(5) }
fn unique_name() -> String {
    format!("ff-public-return-{:016x}{:016x}", std::process::id(), NEXT.fetch_add(1, Ordering::Relaxed))
}
fn pipe_from(file: File) -> Pipe {
    Pipe { file, event: Event(unsafe { CreateEventW(None, true, false, PCWSTR::null()) }.unwrap()) }
}
fn server(name: &str) -> Pipe {
    let path: Vec<u16> = format!(r"\\.\pipe\{name}").encode_utf16().chain(Some(0)).collect();
    let handle = unsafe { CreateNamedPipeW(PCWSTR(path.as_ptr()),
        PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED | FILE_FLAG_FIRST_PIPE_INSTANCE,
        PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
        1, 16384, 16384, 0, None) };
    assert_ne!(handle, INVALID_HANDLE_VALUE);
    pipe_from(unsafe { File::from_raw_handle(handle.0 as _) })
}
fn accept(pipe: &Pipe) {
    let mut already_connected = false;
    let result = pipe.transfer(deadline(), |operation| {
        let result = unsafe { ConnectNamedPipe(pipe.handle(), Some(operation)) };
        already_connected = result.as_ref().is_err_and(|error| error.code() == ERROR_PIPE_CONNECTED.to_hresult());
        result
    });
    if already_connected {
        // Only the documented already-connected result bypasses overlapped
        // completion. Other errors, including timeouts, fail the test.
        let mut pid = 0;
        unsafe { windows::Win32::System::Pipes::GetNamedPipeClientProcessId(pipe.handle(), &mut pid) }.unwrap();
        assert!(pid > 0);
    } else { result.unwrap(); }
}

fn client(name: &str) -> File {
    let end = deadline();
    loop {
        if let Ok(file) = File::options().read(true).write(true)
            .custom_flags(FILE_FLAG_OVERLAPPED.0 | SECURITY_SQOS_PRESENT.0 | SECURITY_IDENTIFICATION.0)
            .open(format!(r"\\.\pipe\{name}")) { return file; }
        assert!(Instant::now() < end, "synthetic pipe connect deadline");
        std::thread::sleep(Duration::from_millis(10));
    }
}
fn pair() -> (Pipe, Pipe) {
    let name = unique_name();
    let server = server(&name);
    let task = std::thread::spawn(move || client(&name));
    accept(&server);
    (server, pipe_from(task.join().unwrap()))
}
fn observed(file: &File) -> &'static hooks_config::launch_owner::LaunchOwner {
    Box::leak(Box::new(hooks_config::launch_owner::observe_pipe_server(file.as_raw_handle()).unwrap()))
}
fn descriptor(owner: &hooks_config::launch_owner::LaunchOwner, pipe: String) -> Descriptor {
    Descriptor { schema: 1, pipe, launch_id: "11111111111111111111111111111111".into(),
        launcher_pid: owner.pid, launcher_creation_filetime: owner.creation_filetime,
        launcher_path: owner.image.to_str().unwrap().into(), launcher_sha256: "a".repeat(64) }
}
fn bridge(owner: &'static hooks_config::launch_owner::LaunchOwner, pipe: String) -> Arc<Bridge> {
    Arc::new(Bridge { state: Mutex::new(ReturnState::default()), changed: Condvar::new(),
        descriptor: descriptor(owner, pipe), owner })
}
fn payload() -> ReturnedAuthentication {
    // Shape-only synthetic bearer. No client-side authentication claim.
    ReturnedAuthentication::new(format!("v1.e30.{}", "A".repeat(43)), "synthetic-account".into(), "Synthetic".into()).unwrap()
}
struct OwnedTestChild(Child);
impl Drop for OwnedTestChild {
    fn drop(&mut self) { let _ = self.0.kill(); let _ = self.0.wait(); }
}
fn spawn_owned_child(name: &str) -> OwnedTestChild {
    OwnedTestChild(Command::new(std::env::current_exe().unwrap())
        .args(["--exact", "api::public_return::tests::owned_pipe_server", "--nocapture"])
        .env(CHILD_ENV, name).stdin(Stdio::null()).stdout(Stdio::null()).stderr(Stdio::null())
        .spawn().unwrap())
}

#[test]
fn owned_pipe_server() {
    if let Ok(raw) = std::env::var("FF_SYNTHETIC_RETURN_INTEROP_V1") {
        if super::interop::run(&raw).is_err() {
            println!("\nNATIVE_INTEROP_REJECTED");
            panic!("synthetic interop rejected");
        }
        return;
    }
    let Ok(name) = std::env::var(CHILD_ENV) else { return; };
    assert!(name.starts_with("ff-public-return-"));
    let server = server(&name);
    accept(&server);
    let mut byte = [0];
    let _ = server.read_exact(&mut byte, deadline());
}

#[test]
fn observed_owner_requires_exact_birth_path_and_real_pipe_server() {
    let (_server, client) = pair();
    let owner = observed(&client.file);
    let mut anchor = descriptor(owner, unique_name());
    assert_eq!(owner.pid, std::process::id());
    assert!(owner.alive() && owner.matches_pipe(client.file.as_raw_handle()));
    assert!(matches_start_anchor(owner, &anchor));
    anchor.launcher_creation_filetime += 1;
    assert!(!matches_start_anchor(owner, &anchor));
    anchor.launcher_creation_filetime = owner.creation_filetime;
    anchor.launcher_pid += 1;
    assert!(!matches_start_anchor(owner, &anchor));
    anchor.launcher_pid = owner.pid;
    anchor.launcher_path.push_str(".wrong");
    assert!(!matches_start_anchor(owner, &anchor));
}

#[test]
fn actual_owned_child_server_is_rejected_and_death_revokes_pinned_owner() {
    let (_server, initial) = pair();
    let expected = observed(&initial.file);
    let name = unique_name();
    let mut child = spawn_owned_child(&name);
    let actual = client(&name);
    let observed_child = observed(&actual);
    assert_eq!(observed_child.pid, child.0.id());
    assert_ne!(observed_child.pid, expected.pid);
    assert!(!expected.matches_pipe(actual.as_raw_handle()));
    drop(actual);
    // Use a second actual owned child to exercise production connect rejection.
    let name = unique_name();
    let _other = spawn_owned_child(&name);
    let bridge = bridge(expected, name);
    lock(&bridge.state).begin().unwrap();
    assert!(Pipe::connect(&bridge, deadline()).is_err());
    // The helper may already have observed EOF; only this owned handle is used.
    let _ = child.0.kill();
    child.0.wait().unwrap();
    assert!(!observed_child.alive());
}

#[test]
fn overlapped_timeout_cancels_and_drains_before_buffer_reuse() {
    let (server, client) = pair();
    let start = Instant::now();
    let mut byte = [0u8; 1];
    assert!(server.read_exact(&mut byte, start + Duration::from_millis(75)).is_err());
    assert!(start.elapsed() < Duration::from_secs(2));
    client.write_all(&[42], deadline()).unwrap();
    server.read_exact(&mut byte, deadline()).unwrap();
    assert_eq!(byte, [42]);
}

#[test]
fn bounded_frames_reject_oversize_partial_eof_and_expired_deadline() {
    let (server, client) = pair();
    client.write_all(&((MAX_FRAME + 1) as u32).to_le_bytes(), deadline()).unwrap();
    assert!(server.read_frame(deadline()).is_err());
    let (server, client) = pair();
    client.write_all(&3u32.to_le_bytes(), deadline()).unwrap();
    client.write_all(b"{}", deadline()).unwrap();
    drop(client);
    assert!(server.read_frame(deadline()).is_err());
    assert!(server.read_frame(Instant::now()).is_err());
}

fn worker_start(bridge: &Arc<Bridge>) -> (std::thread::JoinHandle<()>, std::sync::mpsc::Receiver<()>) {
    let owned = bridge.clone();
    let (send, receive) = std::sync::mpsc::channel();
    let task = std::thread::spawn(move || { worker(owned); let _ = send.send(()); });
    (task, receive)
}
fn finish(task: std::thread::JoinHandle<()>, done: std::sync::mpsc::Receiver<()>, bridge: &Bridge) {
    lock(&bridge.state).close();
    bridge.changed.notify_all();
    assert!(done.recv_timeout(Duration::from_secs(7)).is_ok(), "synthetic worker did not drain");
    task.join().unwrap();
    assert!(lock(&bridge.state).is_closed());
}
fn challenge(bridge: &Bridge) -> Frame {
    Frame::Challenge { schema: 1, launch_id: bridge.descriptor.launch_id.clone(), challenge: "b".repeat(64) }
}
fn next_state(server: &Pipe, expected_generation: u64) -> Frame {
    let end = deadline();
    loop {
        let frame = server.read_frame(end).unwrap();
        match frame {
            Frame::Heartbeat { generation, .. } if generation < expected_generation => continue,
            _ => return frame,
        }
    }
}

#[test]
fn actual_worker_returns_run_authentication_invalidation_heartbeat_and_closes_on_eof() {
    let (_config, initial) = pair();
    let bridge = bridge(observed(&initial.file), unique_name());
    let owner = lock(&bridge.state).begin().unwrap();
    let server = server(&bridge.descriptor.pipe);
    let (task, done) = worker_start(&bridge);
    accept(&server);
    server.write_frame(&challenge(&bridge)).unwrap();
    let hello = server.read_frame(deadline()).unwrap();
    assert_eq!(hello, challenge(&bridge).hello(&bridge.descriptor.launch_id, super::super::current_game_instance().unwrap()).unwrap());
    if let Frame::Hello { game_instance, .. } = hello { assert_ne!(game_instance, bridge.descriptor.launch_id); } else { panic!("expected hello"); }
    assert_eq!(server.read_frame(deadline()).unwrap(), Frame::Invalidate { schema: 1, generation: 1 });
    assert!(lock(&bridge.state).publish(owner, payload()).unwrap());
    bridge.changed.notify_all();
    assert!(matches!(next_state(&server, 2), Frame::Authenticated { generation: 2, .. }));
    {
        let mut state = lock(&bridge.state);
        let next = state.begin().unwrap();
        assert!(state.invalidate(next).unwrap());
    }
    bridge.changed.notify_all();
    assert_eq!(next_state(&server, 4), Frame::Invalidate { schema: 1, generation: 4 });
    assert_eq!(server.read_frame(deadline()).unwrap(), Frame::Heartbeat { schema: 1, generation: 4 });
    drop(server);
    // EOF is discovered on the next bounded heartbeat write; no reconnect.
    assert!(done.recv_timeout(Duration::from_secs(7)).is_ok());
    task.join().unwrap();
    assert!(lock(&bridge.state).is_closed());
    assert!(lock(&bridge.state).snapshot().is_none());
}

#[test]
fn wrong_launch_challenge_permanently_closes_worker_without_hello() {
    let (_config, initial) = pair();
    let bridge = bridge(observed(&initial.file), unique_name());
    lock(&bridge.state).begin().unwrap();
    let server = server(&bridge.descriptor.pipe);
    let (task, done) = worker_start(&bridge);
    accept(&server);
    server.write_frame(&Frame::Challenge { schema: 1, launch_id: "2".repeat(32), challenge: "b".repeat(64) }).unwrap();
    assert!(server.read_frame(deadline()).is_err());
    finish(task, done, &bridge);
}

#[test]
fn authentication_guard_drop_invalidates_and_stale_success_cannot_publish() {
    let (_config, initial) = pair();
    let bridge = bridge(observed(&initial.file), unique_name());
    let owner = lock(&bridge.state).begin().unwrap();
    drop(AuthenticationPublication { bridge: bridge.clone(), owner, completed: false });
    assert_eq!(lock(&bridge.state).snapshot().unwrap(), Frame::Invalidate { schema: 1, generation: 2 });
    let old = lock(&bridge.state).begin().unwrap();
    let current = lock(&bridge.state).begin().unwrap();
    AuthenticationPublication { bridge: bridge.clone(), owner: old, completed: false }.publish(payload());
    assert_eq!(lock(&bridge.state).snapshot().unwrap(), Frame::Invalidate { schema: 1, generation: 4 });
    AuthenticationPublication { bridge: bridge.clone(), owner: current, completed: false }.publish(payload());
    assert!(matches!(lock(&bridge.state).snapshot(), Some(Frame::Authenticated { generation: 5, .. })));
}
