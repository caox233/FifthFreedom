//! API-only source confinement. The production overlay capability provider is
//! deliberately closed until Windows owned-session/secure-peer proof exists.
//! No config value, historical socket, return heartbeat or adapter name is a lease.
use std::io;
use std::net::SocketAddr;
use std::pin::Pin;
use std::sync::{Arc, Mutex, Weak};
use std::task::{Context, Poll, Waker};
use tokio::io::{AsyncRead, AsyncWrite, ReadBuf};
use tokio::net::{TcpSocket, TcpStream};
use tokio::sync::watch;
use server_api::source_binding_contract::SocketBinding;

// Public owner loss is terminal for this loaded DLL. A future provider may not
// replace an epoch in-process while an older owner can still revoke it.
static PUBLIC_REVOKED: std::sync::atomic::AtomicBool = std::sync::atomic::AtomicBool::new(false);

fn unavailable() -> io::Error { io::Error::new(io::ErrorKind::PermissionDenied, "PUBLIC_TRANSPORT_UNAVAILABLE") }
fn revoked() -> io::Error { io::Error::new(io::ErrorKind::ConnectionAborted, "PUBLIC_TRANSPORT_REVOKED") }
fn lock<T>(value: &Mutex<T>) -> std::sync::MutexGuard<'_, T> {
    value.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
}

#[derive(Clone)]
pub(super) enum Permit { Lan, Public(LiveOverlayLease) }
impl Permit {
    pub(super) fn capture(public_requested: bool) -> io::Result<Self> {
        if public_requested { LiveOverlayLease::production().map(Self::Public) } else { Ok(Self::Lan) }
    }
    pub(super) fn with_live<T>(&self, publish: impl FnOnce() -> T) -> io::Result<T> {
        match self { Self::Lan => Ok(publish()), Self::Public(lease) => lease.with_live(publish) }
    }
    pub(super) async fn connect(&self, endpoint: tonic::transport::Endpoint) -> io::Result<tonic::transport::Channel> {
        match self {
            Self::Lan => endpoint.connect().await.map_err(io::Error::other),
            Self::Public(lease) => {
                // Reject DNS, another destination, TLS authority substitution,
                // and userinfo. This connector never falls back to a default route.
                let expected = format!("http://{}", lease.0.binding.destination());
                let actual = endpoint.uri().to_string();
                if actual != expected && actual != format!("{expected}/") { return Err(unavailable()); }
                let owned = lease.clone();
                let connector = tower::service_fn(move |_| {
                    let lease = owned.clone();
                    async move { lease.connect().await.map(hyper_util::rt::TokioIo::new) }
                });
                let channel = endpoint.connect_with_connector(connector).await.map_err(|_| unavailable())?;
                lease.with_live(|| channel)
            }
        }
    }
}

struct Authority { revoked: bool, streams: Vec<Weak<Mutex<StreamCell>>> }
struct LeaseState { binding: SocketBinding, authority: Mutex<Authority>, cancelled: watch::Sender<bool> }
/// Clones share one non-reusable epoch. There is no serializable constructor.
#[derive(Clone)]
pub(super) struct LiveOverlayLease(Arc<LeaseState>);
impl LiveOverlayLease {
    fn production() -> io::Result<Self> {
        if PUBLIC_REVOKED.load(std::sync::atomic::Ordering::Acquire) { return Err(revoked()); }
        Err(unavailable())
    }
    #[cfg(test)]
    fn synthetic(binding: SocketBinding) -> Self {
        let (cancelled, _) = watch::channel(false);
        Self(Arc::new(LeaseState { binding, authority: Mutex::new(Authority { revoked: false, streams: Vec::new() }), cancelled }))
    }
    fn with_live<T>(&self, publish: impl FnOnce() -> T) -> io::Result<T> {
        let state = lock(&self.0.authority);
        if state.revoked { return Err(revoked()); }
        // Only bounded synchronous state publication: no await, IPC or I/O.
        Ok(publish())
    }
    /// Owner loss revokes first, closes all registered streams and wakes pending
    /// I/O, then invalidates global credentials. The owner must call this on any
    /// liveness loss; the currently closed provider cannot issue such an owner.
    #[allow(dead_code)]
    pub(super) fn revoke(&self) {
        let streams = {
            let mut state = lock(&self.0.authority);
            if state.revoked { return; }
            state.revoked = true;
            std::mem::take(&mut state.streams)
        };
        PUBLIC_REVOKED.store(true, std::sync::atomic::Ordering::Release);
        self.0.cancelled.send_replace(true);
        for stream in streams.into_iter().filter_map(|stream| stream.upgrade()) {
            let (socket, reader, writer) = {
                let mut cell = lock(&stream);
                (cell.socket.take(), cell.reader.take(), cell.writer.take())
            };
            drop(socket);
            if let Some(waker) = reader { waker.wake(); }
            if let Some(waker) = writer { waker.wake(); }
        }
        super::invalidate_public_transport();
    }
    fn register(&self, socket: TcpStream) -> io::Result<RevocableIo> {
        let mut state = lock(&self.0.authority);
        if state.revoked { return Err(revoked()); }
        state.streams.retain(|entry| entry.strong_count() != 0);
        // HTTP reconnect pressure cannot create an unbounded retained registry.
        if state.streams.len() >= 16 { return Err(unavailable()); }
        let cell = Arc::new(Mutex::new(StreamCell { socket: Some(socket), reader: None, writer: None }));
        state.streams.push(Arc::downgrade(&cell));
        Ok(RevocableIo(cell))
    }
    async fn connect(&self) -> io::Result<RevocableIo> { self.connect_after(std::future::ready(())).await }
    async fn connect_after(&self, after_connect: impl std::future::Future<Output = ()>) -> io::Result<RevocableIo> {
        let mut cancellation = self.0.cancelled.subscribe();
        self.with_live(|| ())?;
        let binding = self.0.binding;
        platform::verify(binding)?;
        let socket = TcpSocket::new_v4().map_err(|_| unavailable())?;
        platform::constrain(&socket, binding)?;
        socket.bind(SocketAddr::V4(std::net::SocketAddrV4::new(binding.source(), 0))).map_err(|_| unavailable())?;
        // The cancellation branch drops the connecting socket future, including
        // its owned OS socket. The post-connect scheduling seam is test-only data,
        // never an alternative readiness or ownership provider.
        let connecting = async {
            let stream = socket.connect(SocketAddr::V4(binding.destination())).await.map_err(|_| unavailable())?;
            after_connect.await;
            Ok::<_, io::Error>(stream)
        };
        let stream = tokio::select! {
            biased;
            _ = cancellation.changed() => return Err(revoked()),
            result = tokio::time::timeout(std::time::Duration::from_secs(1), connecting) => result.map_err(|_| unavailable())??,
        };
        self.with_live(|| ())?;
        platform::verify(binding)?;
        platform::readback(&stream, binding)?;
        if stream.local_addr().map_err(|_| unavailable())?.ip() != binding.source()
            || stream.peer_addr().map_err(|_| unavailable())? != SocketAddr::V4(binding.destination()) { return Err(unavailable()); }
        stream.set_nodelay(true).map_err(|_| unavailable())?;
        self.register(stream)
    }
}

struct StreamCell { socket: Option<TcpStream>, reader: Option<Waker>, writer: Option<Waker> }
pub(super) struct RevocableIo(Arc<Mutex<StreamCell>>);
impl AsyncRead for RevocableIo {
    fn poll_read(self: Pin<&mut Self>, cx: &mut Context<'_>, buf: &mut ReadBuf<'_>) -> Poll<io::Result<()>> {
        let mut cell = lock(&self.0);
        cell.reader = Some(cx.waker().clone());
        match cell.socket.as_mut() { Some(socket) => Pin::new(socket).poll_read(cx, buf), None => Poll::Ready(Err(revoked())) }
    }
}
impl AsyncWrite for RevocableIo {
    fn poll_write(self: Pin<&mut Self>, cx: &mut Context<'_>, bytes: &[u8]) -> Poll<io::Result<usize>> {
        let mut cell = lock(&self.0);
        cell.writer = Some(cx.waker().clone());
        match cell.socket.as_mut() { Some(socket) => Pin::new(socket).poll_write(cx, bytes), None => Poll::Ready(Err(revoked())) }
    }
    fn poll_flush(self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        let mut cell = lock(&self.0);
        cell.writer = Some(cx.waker().clone());
        match cell.socket.as_mut() { Some(socket) => Pin::new(socket).poll_flush(cx), None => Poll::Ready(Err(revoked())) }
    }
    fn poll_shutdown(self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        let mut cell = lock(&self.0);
        cell.writer = Some(cx.waker().clone());
        match cell.socket.as_mut() { Some(socket) => Pin::new(socket).poll_shutdown(cx), None => Poll::Ready(Err(revoked())) }
    }
}

mod platform {
    use super::*;
    use std::os::windows::io::AsRawSocket;
    use windows::core::PSTR;
    use windows::Win32::NetworkManagement::IpHelper::{GetIfEntry2, GetUnicastIpAddressEntry, MIB_IF_ROW2, MIB_UNICASTIPADDRESS_ROW};
    use windows::Win32::NetworkManagement::Ndis::IfOperStatusUp;
    use windows::Win32::Networking::WinSock::*;
    pub(super) fn verify(binding: SocketBinding) -> io::Result<()> {
        unsafe {
            let mut interface = MIB_IF_ROW2 { InterfaceIndex: binding.index(), ..Default::default() };
            if GetIfEntry2(&mut interface).is_err() { return Err(unavailable()); }
            let mut address = MIB_UNICASTIPADDRESS_ROW { InterfaceIndex: binding.index(), ..Default::default() };
            address.Address.Ipv4.sin_family = AF_INET;
            address.Address.Ipv4.sin_addr.S_un.S_addr = u32::from_ne_bytes(binding.source().octets());
            if GetUnicastIpAddressEntry(&mut address).is_err()
                || !binding.matches(interface.InterfaceIndex, interface.InterfaceLuid.Value,
                    std::net::Ipv4Addr::from(address.Address.Ipv4.sin_addr.S_un.S_addr.to_ne_bytes()),
                    interface.OperStatus == IfOperStatusUp,
                    address.DadState == IpDadStatePreferred && address.ValidLifetime != 0)
                || address.InterfaceIndex != binding.index() || address.InterfaceLuid.Value != binding.luid() {
                return Err(unavailable());
            }
        }
        Ok(())
    }
    pub(super) fn constrain(socket: &TcpSocket, binding: SocketBinding) -> io::Result<()> {
        let bytes = binding.index().to_be().to_ne_bytes();
        unsafe {
            if setsockopt(SOCKET(socket.as_raw_socket() as usize), IPPROTO_IP.0, IP_UNICAST_IF, Some(&bytes)) != 0 { return Err(unavailable()); }
        }
        check_option(socket.as_raw_socket() as usize, binding)
    }
    pub(super) fn readback(socket: &TcpStream, binding: SocketBinding) -> io::Result<()> {
        check_option(socket.as_raw_socket() as usize, binding)
    }
    fn check_option(raw: usize, binding: SocketBinding) -> io::Result<()> {
        let mut value = 0u32;
        let mut length = std::mem::size_of::<u32>() as i32;
        unsafe {
            // Winsock's API is asymmetric: setsockopt takes network byte
            // order, whereas getsockopt returns this interface index in host
            // byte order. Do not byte-swap the already-native readback.
            if getsockopt(SOCKET(raw), IPPROTO_IP.0, IP_UNICAST_IF, PSTR((&mut value as *mut u32).cast()), &mut length) != 0
                || length != 4 || value != binding.index() { return Err(unavailable()); }
        }
        Ok(())
    }
}

#[cfg(test)]
#[path = "source_transport_tests.rs"]
mod tests;
