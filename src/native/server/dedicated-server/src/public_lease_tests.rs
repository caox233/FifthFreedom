//! Real credential sockets/owned children; all admission metadata is synthetic.
use super::*;
use std::{fs, io::{Read,Write}, net::{TcpListener,TcpStream,UdpSocket}, process::{Child,Command,Stdio}};
use std::os::unix::{fs::PermissionsExt,process::CommandExt};
use std::sync::atomic::AtomicU64;
static NEXT:AtomicU64=AtomicU64::new(0);
const CHILD:&str="public_lease::tests::owned_child_entry";
fn binding()->Binding { Binding{config_sha256:"1".repeat(64),service_manifest_sha256:"2".repeat(64),admitted_client_storage_key:"synthetic-only".into(),runtime_profile_sha256:"3".repeat(64),runtime_executable_sha256:"4".repeat(64),netns_device:7,netns_inode:8,interface_index:9,overlay_ipv4:"10.77.0.1".parse().unwrap(),overlay_prefix:24} }
fn pair()->(OwnedFd,OwnedFd) {
    let mut fds=[-1;2];assert_eq!(unsafe{libc::socketpair(libc::AF_UNIX,libc::SOCK_SEQPACKET|libc::SOCK_CLOEXEC,0,fds.as_mut_ptr())},0);
    for fd in fds {let enabled=1_i32;assert_eq!(unsafe{libc::setsockopt(fd,libc::SOL_SOCKET,libc::SO_PASSCRED,(&enabled as *const i32).cast(),size_of::<i32>() as _)},0);}
    unsafe{(OwnedFd::from_raw_fd(fds[0]),OwnedFd::from_raw_fd(fds[1]))}
}
struct Worker {child:Child,socket:Option<LeaseSocket>,root:std::path::PathBuf}
impl Worker {
    fn start(mode:&str)->Self {
        let root=std::env::temp_dir().join(format!("ff-owned-lease-synthetic-{}-{}",std::process::id(),NEXT.fetch_add(1,Ordering::Relaxed)));
        fs::create_dir(&root).unwrap();fs::set_permissions(&root,fs::Permissions::from_mode(0o700)).unwrap();
        let (parent,childfd)=pair();let raw=childfd.as_raw_fd();
        let mut cmd=Command::new(std::env::current_exe().unwrap());
        cmd.args(["--exact",CHILD,"--nocapture"]).env("FF_LEASE_SYNTHETIC_MODE",mode).env("FF_LEASE_SYNTHETIC_FD",raw.to_string()).env("FF_LEASE_SYNTHETIC_ROOT",&root).stdin(Stdio::null()).stdout(Stdio::null()).stderr(Stdio::null());
        // Only async-signal-safe fcntl in the post-fork hook.
        unsafe{cmd.pre_exec(move||{if libc::fcntl(raw,libc::F_SETFD,0)<0{return Err(std::io::Error::last_os_error());}Ok(())});}
        let child=cmd.spawn().unwrap();drop(childfd);
        let peer=libc::ucred{pid:child.id() as _,uid:unsafe{libc::geteuid()},gid:unsafe{libc::getegid()}};
        Self{child,socket:Some(LeaseSocket{descriptor:parent,peer}),root}
    }
    fn hello(&self)->Frame {
        let hello=self.socket.as_ref().unwrap().receive(None,Instant::now()+HANDSHAKE).unwrap();
        assert_eq!(hello.version,VERSION);assert_eq!(hello.kind,"hello");assert_eq!(hello.sequence,0);assert!(digest_valid(&hello.nonce));assert!(hello.binding==binding());assert_eq!(hello.lease_ms,None);hello
    }
    fn grant(&self,hello:&Frame,ms:u32) {let mut frame=hello.clone();frame.kind="admit".into();frame.sequence=1;frame.lease_ms=Some(ms);self.socket.as_ref().unwrap().send(&frame).unwrap();}
    fn wait(&mut self,code:i32) {
        let deadline=Instant::now()+Duration::from_secs(5);
        loop {if let Some(status)=self.child.try_wait().unwrap(){assert_eq!(status.code(),Some(code));break;}assert!(Instant::now()<deadline,"owned child exceeded deadline");std::thread::sleep(Duration::from_millis(10));}
    }
    fn raw(&self,bytes:&[u8]) {assert_eq!(unsafe{libc::send(self.socket.as_ref().unwrap().descriptor.as_raw_fd(),bytes.as_ptr().cast(),bytes.len(),libc::MSG_NOSIGNAL)},bytes.len() as isize);}
}
impl Drop for Worker {fn drop(&mut self){if self.child.try_wait().ok().flatten().is_none(){let _=self.child.kill();}let _=self.child.wait();let _=fs::remove_dir_all(&self.root);}}
#[test]
fn owned_child_entry() {
    let Ok(mode)=std::env::var("FF_LEASE_SYNTHETIC_MODE") else{return;};sodiumoxide::init().unwrap();
    let fd=std::env::var("FF_LEASE_SYNTHETIC_FD").unwrap().parse().unwrap();
    let root=std::path::PathBuf::from(std::env::var_os("FF_LEASE_SYNTHETIC_ROOT").unwrap());
    let result=UnadmittedLease::inherited(fd).and_then(|lease|lease.admit(binding()));
    if mode=="reject" {assert!(result.is_err());assert_eq!(fs::read_dir(root).unwrap().count(),0);return;}
    let active=result.unwrap();if mode=="accept"{return;}
    if mode=="expired"{std::thread::sleep(Duration::from_millis(175));assert!(active.activate(||true).is_err());return;}
    let reporter=active.activate(||true).unwrap();
    if mode=="wait"{fs::write(root.join("synthetic-lease-active"),b"active").unwrap();loop{std::thread::park();}}
    if mode=="partial"{for bit in [1,2,4,8]{reporter.bound(bit);}loop{std::thread::park();}}
    if mode=="late-ready"{for bit in [1,2,4,8]{reporter.bound(bit);}std::thread::sleep(Duration::from_millis(9250));reporter.bound(16);loop{std::thread::park();}}
    assert_eq!(mode,"services");
    crate::lan_runtime::register_public_readiness(move|bit|reporter.bound(bit)).unwrap();
    std::env::set_current_dir(&root).unwrap();let logger=slog::Logger::root(slog::Discard,slog::o!());
    let release=server_api::release_contract::ReleaseIdentity::new(1,server_api::release_catalog::RELEASE_ID,server_api::release_catalog::COMPAT_ID,&"a".repeat(64)).unwrap();
    let storage=Arc::new(crate::Storage::init(logger.clone(),release.clone()).unwrap());
    let token=crate::api::AuthTokenConfig::from_startup(release,None).unwrap();let mut config=crate::Config::default();
    // Actual five retained services, loopback only; this is not Public admission.
    let tcp:Vec<_>=(0..3).map(|_|TcpListener::bind("127.0.0.1:0").unwrap()).collect();
    let udp:Vec<_>=(0..2).map(|_|UdpSocket::bind("127.0.0.1:0").unwrap()).collect();
    let ports=[tcp[0].local_addr().unwrap().port(),tcp[1].local_addr().unwrap().port(),udp[0].local_addr().unwrap().port(),udp[1].local_addr().unwrap().port(),tcp[2].local_addr().unwrap().port()];
    config.api_server=(std::net::Ipv4Addr::LOCALHOST,ports[4]).into();
    for svc in config.quazal.service.values_mut(){let (addr,index)=match svc{quazal::Service::Config(s)=>(&mut s.listen,0),quazal::Service::Content(s)=>(&mut s.listen,1),quazal::Service::Authentication(s)=>(&mut s.listen,2),quazal::Service::Secure(s)=>(&mut s.listen,3)};*addr=(std::net::Ipv4Addr::LOCALHOST,ports[index]).into();}
    fs::write("synthetic-ports.json",serde_json::to_vec(&ports).unwrap()).unwrap();drop(tcp);drop(udp);
    crate::run_services(logger,storage,config,token,quazal::socket_policy::SocketPolicy::lan(),quazal::source_policy::SourcePolicy::lan()).unwrap();panic!("service unexpectedly returned");
}
#[test]
fn actual_credential_handshake_accepts_exact_binding_without_state(){let mut w=Worker::start("accept");let h=w.hello();w.grant(&h,5000);w.wait(0);assert_eq!(fs::read_dir(&w.root).unwrap().count(),0);}
#[test]
fn invalid_nonce_identity_profile_config_namespace_and_sequence_have_no_state(){
    for case in 0..10{let mut w=Worker::start("reject");let mut f=w.hello();f.kind="admit".into();f.sequence=1;f.lease_ms=Some(5000);
        match case{0=>f.nonce="a".repeat(64),1=>f.binding.config_sha256="b".repeat(64),2=>f.binding.runtime_profile_sha256="b".repeat(64),3=>f.binding.runtime_executable_sha256="b".repeat(64),4=>f.binding.service_manifest_sha256="b".repeat(64),5=>f.binding.admitted_client_storage_key="foreign".into(),6=>f.binding.interface_index+=1,7=>f.binding.netns_inode+=1,8=>f.sequence=2,_=>f.kind="renew".into()}
        w.socket.as_ref().unwrap().send(&f).unwrap();w.wait(0);assert_eq!(fs::read_dir(&w.root).unwrap().count(),0);
    }
}
#[test]
fn malformed_duplicate_unknown_and_oversized_packets_are_bounded(){
    for case in 0..4{let mut w=Worker::start("reject");let mut f=w.hello();f.kind="admit".into();f.sequence=1;f.lease_ms=Some(5000);let raw=serde_json::to_string(&f).unwrap();
        let bytes=match case{0=>b"{sensitive-untrusted-text".to_vec(),1=>raw.replacen('{',"{\"sequence\":1,",1).into_bytes(),2=>raw.replacen('{',"{\"unknown\":true,",1).into_bytes(),_=>{let mut v=raw.into_bytes();v.resize(MAX_FRAME+1,b' ');v}};w.raw(&bytes);w.wait(0);
    }
}
fn rights(fd:RawFd,bytes:&[u8],count:usize){
    let raw=unsafe{libc::memfd_create(c"ff-lease-rights-sentinel".as_ptr(),libc::MFD_CLOEXEC)};assert!(raw>=0);let file=unsafe{OwnedFd::from_raw_fd(raw)};let mut vector=libc::iovec{iov_base:bytes.as_ptr().cast_mut().cast(),iov_len:bytes.len()};
    let size=unsafe{libc::CMSG_SPACE((count*size_of::<RawFd>()) as _)} as usize;let mut control=vec![0_u64;size.div_ceil(8)];let mut msg:libc::msghdr=unsafe{zeroed()};msg.msg_iov=&mut vector;msg.msg_iovlen=1;msg.msg_control=control.as_mut_ptr().cast();msg.msg_controllen=size;
    unsafe{let h=libc::CMSG_FIRSTHDR(&msg);(*h).cmsg_level=libc::SOL_SOCKET;(*h).cmsg_type=libc::SCM_RIGHTS;(*h).cmsg_len=libc::CMSG_LEN((count*size_of::<RawFd>()) as _) as usize;for i in 0..count{std::ptr::write_unaligned(libc::CMSG_DATA(h).cast::<RawFd>().add(i),file.as_raw_fd());}assert_eq!(libc::sendmsg(fd,&msg,libc::MSG_NOSIGNAL),bytes.len() as isize);}
}
#[test]
fn forbidden_rights_and_ancillary_truncation_close_received_fds(){
    // A unique sentinel avoids concurrent tests' ordinary /dev/null descriptors.
    for count in [1,100]{let (a,b)=pair();let socket=LeaseSocket{descriptor:b,peer:libc::ucred{pid:unsafe{libc::getpid()},uid:unsafe{libc::geteuid()},gid:unsafe{libc::getegid()}}};let frame=Frame{version:VERSION.into(),kind:"admit".into(),nonce:"a".repeat(64),sequence:1,binding:binding(),lease_ms:Some(5000)};
        rights(a.as_raw_fd(),&serde_json::to_vec(&frame).unwrap(),count);assert!(socket.receive(None,Instant::now()+HANDSHAKE).is_err());
        assert!(!fs::read_dir("/proc/self/fd").unwrap().filter_map(Result::ok).any(|entry|fs::read_link(entry.path()).ok().is_some_and(|path|path.to_string_lossy().contains("ff-lease-rights-sentinel"))));
    }
}
#[test]
fn missing_wrong_kernel_credentials_and_already_expired_queued_packet_fail(){
    for case in 0..3{let(a,b)=pair();if case==0{let value=0_i32;assert_eq!(unsafe{libc::setsockopt(b.as_raw_fd(),libc::SOL_SOCKET,libc::SO_PASSCRED,(&value as *const i32).cast(),size_of::<i32>() as _)},0);}
        let peer=libc::ucred{pid:unsafe{libc::getpid()}+if case==1{1}else{0},uid:unsafe{libc::geteuid()},gid:unsafe{libc::getegid()}};let sender=LeaseSocket{descriptor:a,peer};let receiver=LeaseSocket{descriptor:b,peer};let frame=Frame{version:VERSION.into(),kind:"admit".into(),nonce:"a".repeat(64),sequence:1,binding:binding(),lease_ms:Some(5000)};sender.send(&frame).unwrap();let deadline=if case==2{Instant::now()-Duration::from_millis(1)}else{Instant::now()+HANDSHAKE};assert!(receiver.receive(None,deadline).is_err());
    }
}
#[test]
fn invalid_fd_non_socket_and_non_parent_creator_are_rejected(){
    use std::os::fd::IntoRawFd;assert!(UnadmittedLease::inherited(-1).is_err());assert!(UnadmittedLease::inherited(0).is_err());let(a,_b)=pair();assert!(UnadmittedLease::inherited(a.into_raw_fd()).is_err());let f=fs::File::open("/dev/null").unwrap();assert!(UnadmittedLease::inherited(f.into_raw_fd()).is_err());
}
#[test]
fn expired_admission_cannot_arm_watchdog_or_create_state(){let mut w=Worker::start("expired");let h=w.hello();w.grant(&h,100);w.wait(0);assert_eq!(fs::read_dir(&w.root).unwrap().count(),0);}
#[test]
fn eof_expiry_and_replayed_renewal_revoke_owned_child(){
    for case in 0..3{let mut w=Worker::start("wait");let h=w.hello();w.grant(&h,300);if case==0{let deadline=Instant::now()+Duration::from_millis(250);while !w.root.join("synthetic-lease-active").exists(){assert!(Instant::now()<deadline);std::thread::sleep(Duration::from_millis(1));}drop(w.socket.take());}if case==2{let mut f=h;f.kind="renew".into();f.sequence=2;f.lease_ms=Some(300);w.socket.as_ref().unwrap().send(&f).unwrap();w.socket.as_ref().unwrap().send(&f).unwrap();}w.wait(REVOKED_EXIT);}
}
#[test]
fn readiness_requires_five_actual_binds_and_revoke_closes_live_sockets(){
    let mut w=Worker::start("services");let h=w.hello();w.grant(&h,5000);let ready=w.socket.as_ref().unwrap().receive(None,Instant::now()+Duration::from_secs(5)).unwrap();assert_eq!(ready.kind,"ready");assert_eq!(ready.sequence,1);assert_eq!(ready.nonce,h.nonce);assert!(ready.binding==h.binding);assert_eq!(ready.lease_ms,None);
    let ports:[u16;5]=serde_json::from_slice(&fs::read(w.root.join("synthetic-ports.json")).unwrap()).unwrap();
    for i in [0,1,4]{assert!(TcpStream::connect((std::net::Ipv4Addr::LOCALHOST,ports[i])).is_ok());}for i in [2,3]{assert!(UdpSocket::bind((std::net::Ipv4Addr::LOCALHOST,ports[i])).is_err());}
    let mut stream=TcpStream::connect((std::net::Ipv4Addr::LOCALHOST,ports[0])).unwrap();stream.write_all(b"GET ").unwrap();stream.set_read_timeout(Some(Duration::from_secs(2))).unwrap();drop(w.socket.take());w.wait(REVOKED_EXIT);assert!(match stream.read(&mut [0;1]) { Ok(0)=>true, Err(error)=>matches!(error.kind(),std::io::ErrorKind::ConnectionReset|std::io::ErrorKind::ConnectionAborted|std::io::ErrorKind::BrokenPipe), _=>false });
    for i in [2,3]{assert!(UdpSocket::bind((std::net::Ipv4Addr::LOCALHOST,ports[i])).is_ok());}for i in [0,1,4]{assert!(TcpStream::connect((std::net::Ipv4Addr::LOCALHOST,ports[i])).is_err());}
}
#[test]
fn four_callbacks_never_emit_ready_and_skipped_renewal_sequence_revokes(){let mut w=Worker::start("partial");let h=w.hello();w.grant(&h,5000);assert!(w.socket.as_ref().unwrap().receive(None,Instant::now()+Duration::from_millis(100)).is_err());let mut f=h;f.kind="renew".into();f.sequence=3;f.lease_ms=Some(5000);w.socket.as_ref().unwrap().send(&f).unwrap();w.wait(REVOKED_EXIT);}
#[test]
fn exact_owned_pidfd_death_revokes_even_when_socket_peer_is_open(){
    let mut child=Command::new("/bin/sh").args(["-c","exit 0"]).stdin(Stdio::null()).stdout(Stdio::null()).stderr(Stdio::null()).spawn().unwrap();
    let fd=unsafe{libc::syscall(libc::SYS_pidfd_open,child.id(),0)};assert!(fd>=0);let pidfd=unsafe{OwnedFd::from_raw_fd(fd as _)};
    child.wait().unwrap();
    let (_peer,receive)=pair();let socket=LeaseSocket{descriptor:receive,peer:libc::ucred{pid:unsafe{libc::getpid()},uid:unsafe{libc::geteuid()},gid:unsafe{libc::getegid()}}};
    let before=Instant::now();assert!(socket.receive(Some(pidfd.as_raw_fd()),before+HANDSHAKE).is_err());assert!(before.elapsed()<Duration::from_millis(500));
}
#[test]
fn unanswered_handshake_is_bounded_and_valid_renewals_extend_only_current_lease(){
    let mut reject=Worker::start("reject");let _hello=reject.hello();reject.wait(0);assert_eq!(fs::read_dir(&reject.root).unwrap().count(),0);
    let mut w=Worker::start("wait");let mut frame=w.hello();w.grant(&frame,300);
    frame.kind="renew".into();frame.lease_ms=Some(300);
    for sequence in 2..7{std::thread::sleep(Duration::from_millis(100));frame.sequence=sequence;w.socket.as_ref().unwrap().send(&frame).unwrap();assert!(w.child.try_wait().unwrap().is_none());}
    drop(w.socket.take());w.wait(REVOKED_EXIT);
}
#[test]
fn readiness_deadline_is_not_extended_by_healthy_renewals(){
    let mut w=Worker::start("partial");let mut frame=w.hello();w.grant(&frame,5000);frame.kind="renew".into();frame.lease_ms=Some(5000);
    let start=Instant::now();let mut sequence=2;
    while start.elapsed()<Duration::from_secs(12){
        if let Some(status)=w.child.try_wait().unwrap(){assert_eq!(status.code(),Some(REVOKED_EXIT));assert!(start.elapsed()>=Duration::from_secs(9));return;}
        frame.sequence=sequence;sequence+=1;let _=w.socket.as_ref().unwrap().send(&frame);std::thread::sleep(Duration::from_millis(200));
    }
    panic!("readiness deadline was extended by renewals");
}

#[test]
fn valid_fifth_callback_clears_only_the_readiness_deadline() {
    let mut w=Worker::start("late-ready");let mut frame=w.hello();w.grant(&frame,5000);
    frame.kind="renew".into();frame.lease_ms=Some(5000);
    for sequence in 2..22 {frame.sequence=sequence;w.socket.as_ref().unwrap().send(&frame).unwrap();std::thread::sleep(Duration::from_millis(450));}
    let ready=w.socket.as_ref().unwrap().receive(None,Instant::now()+Duration::from_secs(2)).unwrap();assert_eq!(ready.kind,"ready");assert_eq!(ready.sequence,1);
    std::thread::sleep(Duration::from_millis(1200));assert!(w.child.try_wait().unwrap().is_none());
    drop(w.socket.take());w.wait(REVOKED_EXIT);
}
