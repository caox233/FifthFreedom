//! Explicit socket creation for retained LAN and future interface-confined use.
//!
//! An interface-bound policy proves only kernel socket confinement. It does NOT
//! establish interface/process ownership, encrypted overlay health or permission
//! to enable the native Public deployment profile. Those are startup/lifecycle
//! prerequisites owned by the caller. No environment switch or fallback exists.
use std::io;
use std::net::{IpAddr, Ipv4Addr, SocketAddr, TcpListener, ToSocketAddrs, UdpSocket};

#[derive(Clone, Debug)]
pub struct SocketPolicy {
    policy: Policy,
}

#[derive(Clone, Debug)]
enum Policy {
    Lan,
    #[cfg(target_os = "linux")]
    InterfaceBound(linux::InterfaceBinding),
}

impl SocketPolicy {
    /// Preserve the established std socket creation semantics, including LAN's
    /// ephemeral P2P wildcard source selection. This choice is always explicit.
    #[must_use]
    pub fn lan() -> Self {
        Self { policy: Policy::Lan }
    }

    /// Pin a concrete interface generation and IPv4 address. Never substitute a
    /// wildcard, another interface or a newly created same-name generation.
    pub fn interface_bound(address: Ipv4Addr, device: &str) -> io::Result<Self> {
        #[cfg(target_os = "linux")]
        {
            Ok(Self { policy: Policy::InterfaceBound(linux::InterfaceBinding::new(address, device)?) })
        }
        #[cfg(not(target_os = "linux"))]
        {
            let _ = (address, device);
            Err(io::Error::new(io::ErrorKind::Unsupported, "INTERFACE_BOUND_SOCKETS_UNSUPPORTED"))
        }
    }

    pub fn bind_tcp(&self, address: SocketAddr) -> io::Result<TcpListener> {
        match &self.policy {
            Policy::Lan => TcpListener::bind(address),
            #[cfg(target_os = "linux")]
            Policy::InterfaceBound(binding) => binding.bind_tcp(address),
        }
    }

    pub fn bind_udp(&self, address: SocketAddr) -> io::Result<UdpSocket> {
        match &self.policy {
            Policy::Lan => UdpSocket::bind(address),
            #[cfg(target_os = "linux")]
            Policy::InterfaceBound(binding) => binding.bind_udp(address),
        }
    }

    /// All temporary P2P/NAT sockets inherit the same immutable source/device
    /// constraint as the listeners. LAN retains its original wildcard behavior.
    pub fn bind_ephemeral_udp(&self) -> io::Result<UdpSocket> {
        let address = match &self.policy {
            Policy::Lan => Ipv4Addr::UNSPECIFIED,
            #[cfg(target_os = "linux")]
            Policy::InterfaceBound(binding) => binding.address,
        };
        self.bind_udp(SocketAddr::new(IpAddr::V4(address), 0))
    }
}

// Compatibility for PRUDP's existing generic bind API. Production startup uses
// bind_with_policy and a concrete SocketAddr. This does not expose a generic
// multi-address resolver/fallback on an interface-bound policy.
pub(crate) fn bind_lan_udp<A: ToSocketAddrs>(addresses: A) -> io::Result<UdpSocket> {
    UdpSocket::bind(addresses)
}

#[cfg(target_os = "linux")]
mod linux {
    use super::*;
    use std::ffi::{CStr, CString};
    use std::mem::{size_of, zeroed};
    use std::os::fd::{AsRawFd, FromRawFd, OwnedFd};
    use std::ptr;

    #[derive(Clone, Debug)]
    pub(super) struct InterfaceBinding {
        pub(super) address: Ipv4Addr,
        name: CString,
        index: u32,
    }

    fn denied(code: &'static str) -> io::Error {
        io::Error::new(io::ErrorKind::PermissionDenied, code)
    }

    impl InterfaceBinding {
        pub(super) fn new(address: Ipv4Addr, name: &str) -> io::Result<Self> {
            if address.is_unspecified() || address.is_multicast() || address.is_broadcast()
                || address.octets()[0] == 0 || address.octets()[0] >= 224
                || name.is_empty() || name.len() >= libc::IFNAMSIZ
                || !name.bytes().all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'_' | b'-' | b'.'))
            {
                return Err(io::Error::new(io::ErrorKind::InvalidInput, "SOCKET_POLICY_INVALID"));
            }
            let name = CString::new(name).map_err(|_| io::Error::new(io::ErrorKind::InvalidInput, "SOCKET_POLICY_INVALID"))?;
            // SAFETY: the CString is NUL terminated and lives through the call.
            let index = unsafe { libc::if_nametoindex(name.as_ptr()) };
            if index == 0 {
                return Err(denied("SOCKET_INTERFACE_UNAVAILABLE"));
            }
            let binding = Self { address, name, index };
            binding.validate_current()?;
            Ok(binding)
        }

        fn validate_current(&self) -> io::Result<()> {
            // SAFETY: the CString remains valid and the lookup has no side effects.
            if unsafe { libc::if_nametoindex(self.name.as_ptr()) } != self.index {
                return Err(denied("SOCKET_INTERFACE_CHANGED"));
            }
            let mut head = ptr::null_mut();
            // SAFETY: getifaddrs initializes the output list; every traversal is
            // bounded by its next pointers and the owning guard frees it once.
            if unsafe { libc::getifaddrs(&mut head) } != 0 {
                return Err(denied("SOCKET_INTERFACE_UNAVAILABLE"));
            }
            struct Addresses(*mut libc::ifaddrs);
            impl Drop for Addresses {
                fn drop(&mut self) {
                    // SAFETY: this list was returned by getifaddrs and is owned.
                    unsafe { libc::freeifaddrs(self.0) };
                }
            }
            let _owned = Addresses(head);
            let mut current = head;
            let mut found = false;
            while !current.is_null() {
                // SAFETY: current belongs to the still-owned getifaddrs list.
                let entry = unsafe { &*current };
                if !entry.ifa_name.is_null() && !entry.ifa_addr.is_null()
                    // SAFETY: names are NUL-terminated in the OS-returned list.
                    && unsafe { CStr::from_ptr(entry.ifa_name) } == self.name.as_c_str()
                    && entry.ifa_flags & libc::IFF_UP as u32 != 0
                    // SAFETY: the non-null OS sockaddr has a readable family.
                    && unsafe { (*entry.ifa_addr).sa_family } == libc::AF_INET as u16
                {
                    // SAFETY: AF_INET identifies the complete sockaddr_in layout.
                    let address = unsafe { &*entry.ifa_addr.cast::<libc::sockaddr_in>() };
                    found |= address.sin_addr.s_addr.to_ne_bytes() == self.address.octets();
                }
                current = entry.ifa_next;
            }
            if !found {
                return Err(denied("SOCKET_INTERFACE_ADDRESS_UNAVAILABLE"));
            }
            // A same-name replacement during address enumeration cannot silently
            // replace the generation captured by the immutable policy.
            if unsafe { libc::if_nametoindex(self.name.as_ptr()) } != self.index {
                return Err(denied("SOCKET_INTERFACE_CHANGED"));
            }
            Ok(())
        }

        fn bind(&self, address: SocketAddr, kind: libc::c_int) -> io::Result<OwnedFd> {
            if address.ip() != IpAddr::V4(self.address) {
                return Err(io::Error::new(io::ErrorKind::InvalidInput, "SOCKET_ADDRESS_MISMATCH"));
            }
            self.validate_current()?;
            // SAFETY: creating one owned IPv4 socket; no borrowed descriptor.
            let raw = unsafe { libc::socket(libc::AF_INET, kind | libc::SOCK_CLOEXEC, 0) };
            if raw < 0 {
                return Err(io::Error::last_os_error());
            }
            // SAFETY: raw is a fresh descriptor; OwnedFd closes all failure paths.
            let socket = unsafe { OwnedFd::from_raw_fd(raw) };
            let bytes = self.name.as_bytes_with_nul();
            // Apply ingress/egress device confinement before bind/listen. EPERM,
            // unsupported options or disappeared device are errors, never LAN.
            if unsafe { libc::setsockopt(socket.as_raw_fd(), libc::SOL_SOCKET, libc::SO_BINDTODEVICE,
                bytes.as_ptr().cast(), bytes.len() as libc::socklen_t) } != 0
            {
                return Err(denied("SOCKET_DEVICE_BIND_DENIED"));
            }
            let mut device = [0u8; libc::IFNAMSIZ];
            let mut length = device.len() as libc::socklen_t;
            if unsafe { libc::getsockopt(socket.as_raw_fd(), libc::SOL_SOCKET, libc::SO_BINDTODEVICE,
                device.as_mut_ptr().cast(), &mut length) } != 0
                || usize::try_from(length).ok() != Some(bytes.len()) || &device[..bytes.len()] != bytes
            {
                return Err(denied("SOCKET_DEVICE_BIND_UNVERIFIED"));
            }
            // A name can be renamed/replaced and restored between the lookup
            // and setsockopt. Verify the actual socket's stored interface index,
            // not only a later name lookup that could refer to another device.
            let mut bound_index: libc::c_int = 0;
            let mut index_length = size_of::<libc::c_int>() as libc::socklen_t;
            if unsafe { libc::getsockopt(socket.as_raw_fd(), libc::SOL_SOCKET, libc::SO_BINDTOIFINDEX,
                ptr::from_mut(&mut bound_index).cast(), &mut index_length) } != 0
                || index_length != size_of::<libc::c_int>() as libc::socklen_t
                || u32::try_from(bound_index).ok() != Some(self.index)
            {
                return Err(denied("SOCKET_DEVICE_INDEX_UNVERIFIED"));
            }
            self.validate_current()?;
            if kind == libc::SOCK_STREAM {
                let enabled: libc::c_int = 1;
                if unsafe { libc::setsockopt(socket.as_raw_fd(), libc::SOL_SOCKET, libc::SO_REUSEADDR,
                    ptr::from_ref(&enabled).cast(), size_of::<libc::c_int>() as libc::socklen_t) } != 0
                {
                    return Err(io::Error::last_os_error());
                }
            }
            // SAFETY: zero is a valid initial representation of sockaddr_in.
            let mut native: libc::sockaddr_in = unsafe { zeroed() };
            native.sin_family = libc::AF_INET as u16;
            native.sin_port = address.port().to_be();
            native.sin_addr.s_addr = u32::from_ne_bytes(self.address.octets());
            if unsafe { libc::bind(socket.as_raw_fd(), ptr::from_ref(&native).cast(),
                size_of::<libc::sockaddr_in>() as libc::socklen_t) } != 0
            {
                return Err(io::Error::last_os_error());
            }
            self.validate_current()?;
            Ok(socket)
        }

        pub(super) fn bind_tcp(&self, address: SocketAddr) -> io::Result<TcpListener> {
            let socket = self.bind(address, libc::SOCK_STREAM)?;
            if unsafe { libc::listen(socket.as_raw_fd(), 128) } != 0 {
                return Err(io::Error::last_os_error());
            }
            self.validate_current()?;
            Ok(TcpListener::from(socket))
        }

        pub(super) fn bind_udp(&self, address: SocketAddr) -> io::Result<UdpSocket> {
            Ok(UdpSocket::from(self.bind(address, libc::SOCK_DGRAM)?))
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::{Read, Write};
    use std::time::Duration;

    #[test]
    fn lan_tcp_udp_and_ephemeral_keep_existing_loopback_bytes_and_source_behavior() {
        let policy = SocketPolicy::lan();
        let udp = policy.bind_udp("127.0.0.1:0".parse().unwrap()).unwrap();
        udp.set_read_timeout(Some(Duration::from_secs(2))).unwrap();
        let sender = policy.bind_ephemeral_udp().unwrap();
        assert_eq!(sender.local_addr().unwrap().ip(), Ipv4Addr::UNSPECIFIED);
        sender.send_to(b"synthetic-udp", udp.local_addr().unwrap()).unwrap();
        let mut buffer = [0u8; 32];
        let (count, peer) = udp.recv_from(&mut buffer).unwrap();
        assert_eq!(&buffer[..count], b"synthetic-udp");
        assert_eq!(peer.ip(), Ipv4Addr::LOCALHOST);
        let tcp = policy.bind_tcp("127.0.0.1:0".parse().unwrap()).unwrap();
        let target = tcp.local_addr().unwrap();
        let client = std::thread::spawn(move || {
            let mut client = std::net::TcpStream::connect(target).unwrap();
            client.write_all(b"synthetic-tcp").unwrap();
        });
        let (mut received, _) = tcp.accept().unwrap();
        received.set_read_timeout(Some(Duration::from_secs(2))).unwrap();
        let mut bytes = [0; 13];
        received.read_exact(&mut bytes).unwrap();
        assert_eq!(&bytes, b"synthetic-tcp");
        client.join().unwrap();
    }

    #[cfg(target_os = "linux")]
    #[test]
    fn interface_policy_rejects_bad_identity_absent_device_and_unassigned_address() {
        for name in ["", "lo\0bad", "../lo", "lo:alias", "sixteenlettersxx"] {
            assert!(SocketPolicy::interface_bound(Ipv4Addr::LOCALHOST, name).is_err());
        }
        assert!(SocketPolicy::interface_bound(Ipv4Addr::LOCALHOST, "ff_missing0").is_err());
        for address in [Ipv4Addr::UNSPECIFIED, Ipv4Addr::BROADCAST, Ipv4Addr::new(224, 1, 1, 1), Ipv4Addr::new(10, 77, 0, 17)] {
            assert!(SocketPolicy::interface_bound(address, "lo").is_err());
        }
        let policy = SocketPolicy::interface_bound(Ipv4Addr::LOCALHOST, "lo").unwrap();
        for address in ["0.0.0.0:0", "127.0.0.2:0", "[::1]:0"] {
            assert_eq!(policy.bind_udp(address.parse().unwrap()).unwrap_err().kind(), io::ErrorKind::InvalidInput);
            assert_eq!(policy.bind_tcp(address.parse().unwrap()).unwrap_err().kind(), io::ErrorKind::InvalidInput);
        }
    }

    #[cfg(target_os = "linux")]
    #[test]
    fn real_interface_bound_sockets_succeed_or_fail_closed_without_privilege_fallback() {
        use std::os::fd::AsRawFd;
        let policy = SocketPolicy::interface_bound(Ipv4Addr::LOCALHOST, "lo").unwrap();
        let udp = match policy.bind_ephemeral_udp() {
            Ok(socket) => socket,
            Err(error) => {
                // Some kernels require CAP_NET_RAW for SO_BINDTODEVICE. This is
                // only a denial-path pass; the privileged integration binary
                // separately requires actual success and reports no skip.
                assert_eq!(error.kind(), io::ErrorKind::PermissionDenied);
                assert_eq!(error.to_string(), "SOCKET_DEVICE_BIND_DENIED");
                assert!(policy.bind_tcp("127.0.0.1:0".parse().unwrap()).is_err());
                return;
            }
        };
        assert_eq!(udp.local_addr().unwrap().ip(), Ipv4Addr::LOCALHOST);
        let mut bytes = [0u8; 16];
        let mut length = bytes.len() as libc::socklen_t;
        assert_eq!(unsafe { libc::getsockopt(udp.as_raw_fd(), libc::SOL_SOCKET, libc::SO_BINDTODEVICE,
            bytes.as_mut_ptr().cast(), &mut length) }, 0);
        assert_eq!(&bytes[..length as usize], b"lo\0");
        let mut index: libc::c_int = 0;
        let mut index_length = std::mem::size_of::<libc::c_int>() as libc::socklen_t;
        assert_eq!(unsafe { libc::getsockopt(udp.as_raw_fd(), libc::SOL_SOCKET, libc::SO_BINDTOIFINDEX,
            std::ptr::from_mut(&mut index).cast(), &mut index_length) }, 0);
        assert_eq!(index_length, std::mem::size_of::<libc::c_int>() as libc::socklen_t);
        assert_eq!(u32::try_from(index).unwrap(), unsafe { libc::if_nametoindex(c"lo".as_ptr()) });
        let peer = UdpSocket::bind("127.0.0.1:0").unwrap();
        udp.set_read_timeout(Some(Duration::from_secs(2))).unwrap();
        peer.send_to(b"device-confined", udp.local_addr().unwrap()).unwrap();
        let (count, source) = udp.recv_from(&mut bytes).unwrap();
        assert_eq!(&bytes[..count], b"device-confined");
        assert_eq!(source, peer.local_addr().unwrap());
        let tcp = policy.bind_tcp("127.0.0.1:0".parse().unwrap()).unwrap();
        let mut client = std::net::TcpStream::connect(tcp.local_addr().unwrap()).unwrap();
        client.write_all(b"tcp").unwrap();
        let (mut received, _) = tcp.accept().unwrap();
        received.set_read_timeout(Some(Duration::from_secs(2))).unwrap();
        received.read_exact(&mut bytes[..3]).unwrap();
        assert_eq!(&bytes[..3], b"tcp");
    }
}
