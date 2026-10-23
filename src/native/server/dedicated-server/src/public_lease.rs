//! A bounded capability from a trusted local deployment-owner parent.
//! Peer credentials prove local ownership, not Python code or overlay security.
//! Production admission remains the closed supervisor provider's responsibility.
#![cfg(target_os = "linux")]
use std::mem::{size_of, zeroed};
use std::net::Ipv4Addr;
use std::os::fd::{AsRawFd, FromRawFd, OwnedFd, RawFd};
use std::sync::{Arc, Mutex, atomic::{AtomicBool, AtomicU8, Ordering}};
use std::time::{Duration, Instant};
use serde::{Deserialize, Serialize};

const VERSION: &str = "ff-public-owner-lease-v1";
const MAX_FRAME: usize = 4096;
const HANDSHAKE: Duration = Duration::from_secs(3);
const READY_TIMEOUT: Duration = Duration::from_secs(10);
const REVOKED_EXIT: i32 = 92;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) struct LeaseError;
impl std::fmt::Display for LeaseError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result { f.write_str("PUBLIC_OWNER_LEASE_INVALID") }
}
impl std::error::Error for LeaseError {}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub(crate) struct Binding {
    pub config_sha256: String,
    pub service_manifest_sha256: String,
    pub admitted_client_storage_key: String,
    pub runtime_profile_sha256: String,
    pub runtime_executable_sha256: String,
    pub netns_device: u64,
    pub netns_inode: u64,
    pub interface_index: u32,
    pub overlay_ipv4: Ipv4Addr,
    pub overlay_prefix: u8,
}
pub(crate) fn digest_valid(value: &str) -> bool {
    value.len() == 64 && value.bytes().all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
}
impl Binding {
    fn valid(&self) -> bool {
        [&self.config_sha256,&self.service_manifest_sha256,&self.runtime_profile_sha256,&self.runtime_executable_sha256]
            .iter().all(|value| digest_valid(value)) && !self.admitted_client_storage_key.is_empty()
            && self.admitted_client_storage_key.len() <= 512 && self.netns_inode != 0 && self.interface_index != 0
            && quazal::source_policy::SourcePolicy::public_overlay(self.overlay_ipv4,self.overlay_prefix).is_ok()
    }
}

#[derive(Clone, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
struct Frame {
    version: String,
    kind: String,
    nonce: String,
    sequence: u64,
    binding: Binding,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    lease_ms: Option<u32>,
}

#[derive(Debug)]
enum ReceiveError { Invalid, Deadline }
impl From<LeaseError> for ReceiveError { fn from(_: LeaseError) -> Self { Self::Invalid } }
struct LeaseSocket { descriptor: OwnedFd, peer: libc::ucred }
impl LeaseSocket {
    fn send(&self, frame: &Frame) -> Result<(), LeaseError> {
        let bytes=serde_json::to_vec(frame).map_err(|_| LeaseError)?;
        if bytes.is_empty() || bytes.len()>MAX_FRAME { return Err(LeaseError); }
        // Seqpacket send is atomic; no unbounded wait on an unresponsive parent.
        let sent=unsafe { libc::send(self.descriptor.as_raw_fd(),bytes.as_ptr().cast(),bytes.len(),libc::MSG_NOSIGNAL|libc::MSG_DONTWAIT) };
        if usize::try_from(sent).ok()!=Some(bytes.len()) { return Err(LeaseError); }
        Ok(())
    }

    fn receive(&self, parent: Option<RawFd>, deadline: Instant) -> Result<Frame, ReceiveError> {
        loop {
            let remaining=deadline.checked_duration_since(Instant::now()).ok_or(ReceiveError::Deadline)?;
            let millis=i32::try_from(remaining.as_millis().max(1)).map_err(|_| LeaseError)?;
            let mut descriptors=[libc::pollfd{fd:self.descriptor.as_raw_fd(),events:libc::POLLIN|libc::POLLRDHUP,revents:0},
                libc::pollfd{fd:parent.unwrap_or(-1),events:libc::POLLIN,revents:0}];
            let ready=unsafe { libc::poll(descriptors.as_mut_ptr(),descriptors.len() as libc::nfds_t,millis) };
            if ready<0 && std::io::Error::last_os_error().kind()==std::io::ErrorKind::Interrupted { continue; }
            if ready==0 { return Err(ReceiveError::Deadline); }
            if ready<0 || descriptors[1].revents!=0
                || descriptors[0].revents & (libc::POLLERR|libc::POLLHUP|libc::POLLRDHUP|libc::POLLNVAL)!=0 { return Err(ReceiveError::Invalid); }
            if descriptors[0].revents & libc::POLLIN!=0 { break; }
        }
        let mut bytes=[0_u8;MAX_FRAME];
        // u64 storage aligns cmsghdr. Extra capacity permits closing received
        // forbidden SCM_RIGHTS before rejection, including truncated messages.
        let mut control=[0_u64;32];
        let mut vector=libc::iovec{iov_base:bytes.as_mut_ptr().cast(),iov_len:bytes.len()};
        let mut message:libc::msghdr=unsafe {zeroed()};
        message.msg_iov=&mut vector;message.msg_iovlen=1;
        message.msg_control=control.as_mut_ptr().cast();message.msg_controllen=size_of_val(&control);
        let count=unsafe {libc::recvmsg(self.descriptor.as_raw_fd(),&mut message,libc::MSG_DONTWAIT|libc::MSG_CMSG_CLOEXEC)};
        let mut credentials=None;let mut invalid=false;
        let mut header=unsafe {libc::CMSG_FIRSTHDR(&message)};
        while !header.is_null() {
            let value=unsafe {&*header};
            let base=unsafe {libc::CMSG_LEN(0)} as usize;
            if value.cmsg_len<base {invalid=true;break;}
            let length=value.cmsg_len-base;
            if value.cmsg_level==libc::SOL_SOCKET && value.cmsg_type==libc::SCM_RIGHTS {
                invalid=true;
                for offset in (0..length/size_of::<RawFd>()).map(|index| index*size_of::<RawFd>()) {
                    let descriptor=unsafe {std::ptr::read_unaligned(libc::CMSG_DATA(header).add(offset).cast::<RawFd>())};
                    unsafe {libc::close(descriptor)};
                }
            } else if value.cmsg_level==libc::SOL_SOCKET && value.cmsg_type==libc::SCM_CREDENTIALS
                && length==size_of::<libc::ucred>() && credentials.is_none() {
                credentials=Some(unsafe {std::ptr::read_unaligned(libc::CMSG_DATA(header).cast::<libc::ucred>())});
            } else { invalid=true; }
            header=unsafe {libc::CMSG_NXTHDR(&message,header)};
        }
        let credentials=credentials.ok_or(LeaseError)?;
        if invalid || message.msg_flags & (libc::MSG_TRUNC|libc::MSG_CTRUNC)!=0 || count<=0
            || credentials.pid!=self.peer.pid || credentials.uid!=self.peer.uid || credentials.gid!=self.peer.gid {
            return Err(ReceiveError::Invalid);
        }
        let count=usize::try_from(count).map_err(|_| LeaseError)?;
        if count>MAX_FRAME {return Err(ReceiveError::Invalid);}
        let frame = serde_json::from_slice(&bytes[..count]).map_err(|_| LeaseError)?;
        if Instant::now() >= deadline { return Err(ReceiveError::Invalid); }
        Ok(frame)
    }
    fn duplicate(&self) -> Result<Self,LeaseError> {
        let fd=unsafe {libc::fcntl(self.descriptor.as_raw_fd(),libc::F_DUPFD_CLOEXEC,3)};
        if fd<0 {return Err(LeaseError);}
        Ok(Self{descriptor:unsafe {OwnedFd::from_raw_fd(fd)},peer:self.peer})
    }
}

pub(crate) struct UnadmittedLease { socket:LeaseSocket, parent:OwnedFd, parent_pid:libc::pid_t }
impl UnadmittedLease {
    /// Takes ownership only of the explicitly inherited descriptor. No Unix
    /// socket path, discovery, environment override or listener is created.
    pub(crate) fn inherited(fd:RawFd) -> Result<Self,LeaseError> {
        if fd<3 || unsafe {libc::fcntl(fd,libc::F_GETFD)}<0 {return Err(LeaseError);}
        let descriptor=unsafe {OwnedFd::from_raw_fd(fd)};
        for (option,expected) in [(libc::SO_TYPE,libc::SOCK_SEQPACKET),(libc::SO_DOMAIN,libc::AF_UNIX)] {
            let mut actual=0_i32;let mut length=size_of::<i32>() as libc::socklen_t;
            if unsafe {libc::getsockopt(fd,libc::SOL_SOCKET,option,(&mut actual as *mut i32).cast(),&mut length)}!=0
                || length as usize!=size_of::<i32>() || actual!=expected {return Err(LeaseError);}
        }
        let mut peer:libc::ucred=unsafe {zeroed()};let mut length=size_of::<libc::ucred>() as libc::socklen_t;
        if unsafe {libc::getsockopt(fd,libc::SOL_SOCKET,libc::SO_PEERCRED,(&mut peer as *mut libc::ucred).cast(),&mut length)}!=0
            || length as usize!=size_of::<libc::ucred>() || peer.pid<=0 || peer.pid!=unsafe {libc::getppid()}
            || peer.uid!=unsafe {libc::geteuid()} {return Err(LeaseError);}
        let flags=unsafe {libc::fcntl(fd,libc::F_GETFD)};
        let enabled=1_i32;
        if flags<0 || unsafe {libc::fcntl(fd,libc::F_SETFD,flags|libc::FD_CLOEXEC)}<0
            || unsafe {libc::setsockopt(fd,libc::SOL_SOCKET,libc::SO_PASSCRED,(&enabled as *const i32).cast(),size_of::<i32>() as libc::socklen_t)}!=0 {
            return Err(LeaseError);
        }
        let parent=unsafe {libc::syscall(libc::SYS_pidfd_open,peer.pid,0)};
        if parent<0 {return Err(LeaseError);}
        let parent=unsafe {OwnedFd::from_raw_fd(parent as RawFd)};
        if unsafe {libc::getppid()}!=peer.pid {return Err(LeaseError);}
        Ok(Self{socket:LeaseSocket{descriptor,peer},parent,parent_pid:peer.pid})
    }

    pub(crate) fn admit(self,binding:Binding) -> Result<ActiveLease,LeaseError> {
        if !binding.valid() || unsafe {libc::getppid()}!=self.parent_pid {return Err(LeaseError);}
        let nonce=sodiumoxide::randombytes::randombytes(32).iter().map(|byte|format!("{byte:02x}")).collect();
        let hello=Frame{version:VERSION.into(),kind:"hello".into(),nonce,sequence:0,binding,lease_ms:None};
        let deadline=Instant::now()+HANDSHAKE;
        self.socket.send(&hello)?;
        let grant=self.socket.receive(Some(self.parent.as_raw_fd()),deadline).map_err(|_| LeaseError)?;
        let duration=validate_frame(&grant,&hello,"admit",1)?;
        if unsafe {libc::getppid()}!=self.parent_pid {return Err(LeaseError);}
        Ok(ActiveLease{owner:self,hello,expires:Instant::now()+duration,sequence:1})
    }
}

fn validate_frame(frame:&Frame,hello:&Frame,kind:&str,sequence:u64) -> Result<Duration,LeaseError> {
    if frame.version!=VERSION || frame.kind!=kind || frame.nonce!=hello.nonce || frame.binding!=hello.binding || frame.sequence!=sequence {
        return Err(LeaseError);
    }
    let millis=frame.lease_ms.ok_or(LeaseError)?;
    if !(100..=5000).contains(&millis) {return Err(LeaseError);}
    Ok(Duration::from_millis(u64::from(millis)))
}

pub(crate) struct ActiveLease { owner:UnadmittedLease, hello:Frame, expires:Instant, sequence:u64 }
#[derive(Clone)]
pub(crate) struct ReadyReporter { inner:Arc<ReadyState> }
struct ReadyState {socket:Mutex<LeaseSocket>,hello:Frame,bits:AtomicU8,sent:AtomicBool,expires:Mutex<Instant>,ready_deadline:Instant}
impl ReadyReporter {
    pub(crate) fn bound(&self,bit:u8) {
        if ![1,2,4,8,16].contains(&bit) {std::process::exit(REVOKED_EXIT);}
        let before=self.inner.bits.fetch_or(bit,Ordering::SeqCst);
        if before!=31 && (before|bit)==31 {
            let now = Instant::now();
            if now >= self.inner.ready_deadline || self.inner.expires.lock().map_or(true, |expires| now >= *expires) {
                std::process::exit(REVOKED_EXIT);
            }
            let mut ready=self.inner.hello.clone();ready.kind="ready".into();ready.sequence=1;
            if self.inner.socket.lock().ok().and_then(|socket|socket.send(&ready).ok()).is_none() {std::process::exit(REVOKED_EXIT);}
            self.inner.sent.store(true,Ordering::SeqCst);
        }
    }
}
impl ActiveLease {
    /// The watchdog is armed before any lock/database/listener side effects.
    /// Process exit closes existing accepted sockets and blocking service loops;
    /// it does not signal or clean up any unrelated process or interface.
    pub(crate) fn activate(mut self, boundary_current: impl Fn() -> bool + Send + 'static) -> Result<ReadyReporter,LeaseError> {
        if Instant::now() >= self.expires || !boundary_current() { return Err(LeaseError); }
        let ready_deadline = Instant::now()+READY_TIMEOUT;
        let reporter=ReadyReporter{inner:Arc::new(ReadyState{socket:Mutex::new(self.owner.socket.duplicate()?),hello:self.hello.clone(),bits:AtomicU8::new(0),sent:AtomicBool::new(false),expires:Mutex::new(self.expires),ready_deadline})};
        let state=reporter.inner.clone();
        std::thread::Builder::new().name("owned-public-lease".into()).spawn(move || {
            loop {
                if unsafe {libc::getppid()}!=self.owner.parent_pid || !boundary_current() {break;}
                let deadline=if state.sent.load(Ordering::SeqCst) {self.expires} else {self.expires.min(ready_deadline)};
                let frame=match self.owner.socket.receive(Some(self.owner.parent.as_raw_fd()),deadline) {
                    Ok(frame) => frame,
                    Err(ReceiveError::Invalid) => break,
                    // A fifth bind may finish while poll is waiting for the
                    // earlier readiness deadline. Only that deadline may clear;
                    // lease expiry or an invalid packet can never be revived.
                    Err(ReceiveError::Deadline) => {
                        if Instant::now() >= self.expires || !state.sent.load(Ordering::SeqCst) { break; }
                        continue;
                    }
                };
                let Some(sequence)=self.sequence.checked_add(1) else {break;};
                let Ok(duration)=validate_frame(&frame,&self.hello,"renew",sequence) else {break;};
                self.sequence=sequence;self.expires=Instant::now()+duration;
                let Ok(mut expires)=state.expires.lock() else {break;}; *expires=self.expires;
            }
            // Fail closed independently of a worker blocked in storage/accept.
            // SQLite's normal crash recovery applies to any in-flight transaction.
            std::process::exit(REVOKED_EXIT);
        }).map_err(|_|LeaseError)?;
        Ok(reporter)
    }
}

/// Kernel confinement is checked separately from the trusted parent's runtime
/// health declaration. Replacements cannot reuse a name with a different index.
pub(crate) fn boundary_current(binding: &Binding, interface: &str) -> bool {
    use std::os::unix::fs::MetadataExt;
    let Ok(namespace) = std::fs::metadata("/proc/self/ns/net") else { return false; };
    let Ok(name) = std::ffi::CString::new(interface) else { return false; };
    namespace.dev() == binding.netns_device && namespace.ino() == binding.netns_inode
        && unsafe { libc::if_nametoindex(name.as_ptr()) } == binding.interface_index
        && quazal::socket_policy::SocketPolicy::interface_bound(binding.overlay_ipv4, interface).is_ok()
        && unsafe { libc::if_nametoindex(name.as_ptr()) } == binding.interface_index
}

#[cfg(test)]
#[path="public_lease_tests.rs"]
mod tests;
