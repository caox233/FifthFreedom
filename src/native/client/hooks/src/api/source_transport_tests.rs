//! Real Windows loopback sockets only. These are not TUN or secure-peer tests.
use super::*;
use std::future::Future;
use std::net::Ipv4Addr;
use std::time::Duration;
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::TcpListener;

fn runtime() -> tokio::runtime::Runtime { tokio::runtime::Runtime::new().unwrap() }
async fn bounded<T>(future: impl std::future::Future<Output = T>) -> T {
    tokio::time::timeout(Duration::from_secs(5), future).await.expect("bounded synthetic socket operation")
}
fn binding(destination: SocketAddr) -> SocketBinding {
    use windows::Win32::NetworkManagement::IpHelper::{GetBestInterface, GetIfEntry2, MIB_IF_ROW2};
    let mut index = 0;
    let mut row = MIB_IF_ROW2::default();
    unsafe {
        assert_eq!(GetBestInterface(u32::from_ne_bytes(Ipv4Addr::LOCALHOST.octets()), &mut index), 0);
        row.InterfaceIndex = index;
        assert!(GetIfEntry2(&mut row).is_ok());
        SocketBinding::new(Ipv4Addr::LOCALHOST, index, row.InterfaceLuid.Value,
            match destination { SocketAddr::V4(value) => value, _ => panic!("IPv4 fixture") }).unwrap()
    }
}
async fn listener() -> TcpListener { TcpListener::bind((Ipv4Addr::LOCALHOST, 0)).await.unwrap() }

#[test]
fn production_factory_is_closed_and_lan_needs_no_lease() {
    assert!(LiveOverlayLease::production().is_err());
    assert!(Permit::capture(true).is_err());
    assert!(matches!(Permit::capture(false).unwrap(), Permit::Lan));
}

#[test]
fn real_options_source_and_established_connection_are_revocable() {
    runtime().block_on(async {
        let server = listener().await;
        let lease = LiveOverlayLease::synthetic(binding(server.local_addr().unwrap()));
        let (mut client, (mut peer, actual_source)) = bounded(async {
            let (client, peer) = tokio::join!(lease.connect(), server.accept());
            (client.unwrap(), peer.unwrap())
        }).await;
        assert_eq!(actual_source.ip(), Ipv4Addr::LOCALHOST);
        client.write_all(b"owned").await.unwrap();
        let mut bytes = [0; 5]; peer.read_exact(&mut bytes).await.unwrap(); assert_eq!(&bytes, b"owned");
        lease.revoke();
        assert_eq!(bounded(peer.read(&mut bytes)).await.unwrap(), 0);
        assert!(client.write_all(b"late").await.is_err());
        assert!(lease.connect().await.is_err());
    });
}

#[test]
fn pending_read_is_woken_even_when_io_handle_is_retained() {
    runtime().block_on(async {
        let server = listener().await;
        let lease = LiveOverlayLease::synthetic(binding(server.local_addr().unwrap()));
        let mut client = lease.connect().await.unwrap();
        let (_peer, _) = server.accept().await.unwrap();
        let (polled, ready) = tokio::sync::oneshot::channel();
        let reader = tokio::spawn(async move {
            let mut bytes = [0];
            let mut operation = Box::pin(client.read(&mut bytes));
            std::future::poll_fn(|cx| {
                assert!(operation.as_mut().poll(cx).is_pending());
                Poll::Ready(())
            }).await;
            polled.send(()).unwrap();
            operation.await
        });
        ready.await.unwrap(); lease.revoke();
        assert!(bounded(reader).await.unwrap().is_err());
    });
}

#[test]
fn late_connect_completion_is_cancelled_before_registration() {
    runtime().block_on(async {
        let server = listener().await;
        let lease = LiveOverlayLease::synthetic(binding(server.local_addr().unwrap()));
        let (arrived, at_barrier) = tokio::sync::oneshot::channel();
        let (release, released) = tokio::sync::oneshot::channel::<()>();
        let connecting = lease.clone();
        let task = tokio::spawn(async move { connecting.connect_after(async {
            arrived.send(()).unwrap(); let _ = released.await;
        }).await });
        let (mut peer, _) = bounded(server.accept()).await.unwrap();
        at_barrier.await.unwrap(); lease.revoke();
        assert!(bounded(task).await.unwrap().is_err());
        drop(release);
        assert!(lock(&lease.0.authority).streams.is_empty());
        assert_eq!(bounded(peer.read(&mut [0])).await.unwrap(), 0);
    });
}

#[test]
fn changed_luid_index_or_address_is_rejected_before_connect() {
    runtime().block_on(async {
        let server = listener().await;
        let expected = binding(server.local_addr().unwrap());
        for bad in [
            SocketBinding::new(expected.source(), u32::MAX, expected.luid(), expected.destination()).unwrap(),
            SocketBinding::new(expected.source(), expected.index(), expected.luid() ^ 1, expected.destination()).unwrap(),
            SocketBinding::new(Ipv4Addr::new(192, 0, 2, 123), expected.index(), expected.luid(), expected.destination()).unwrap(),
        ] {
            assert!(LiveOverlayLease::synthetic(bad).connect().await.is_err());
        }
        assert!(tokio::time::timeout(Duration::from_millis(30), server.accept()).await.is_err());
    });
}

#[test]
fn revoked_epoch_cannot_register_or_publish_and_new_epoch_is_distinct() {
    runtime().block_on(async {
        let server = listener().await;
        let expected = binding(server.local_addr().unwrap());
        let lease = LiveOverlayLease::synthetic(expected);
        let socket = TcpStream::connect(server.local_addr().unwrap()).await.unwrap();
        let (mut peer, _) = server.accept().await.unwrap();
        let retained = lease.clone(); lease.revoke();
        assert!(retained.register(socket).is_err());
        let mut late_publication = false;
        assert!(retained.with_live(|| late_publication = true).is_err());
        assert!(!late_publication);
        assert_eq!(bounded(peer.read(&mut [0])).await.unwrap(), 0);
        let replacement = LiveOverlayLease::synthetic(expected);
        assert!(replacement.with_live(|| ()).is_ok());
        assert!(retained.with_live(|| ()).is_err());
    });
}

#[test]
fn tonic_clone_does_not_keep_connection_alive_after_revoke() {
    runtime().block_on(async {
        let server = listener().await;
        let lease = LiveOverlayLease::synthetic(binding(server.local_addr().unwrap()));
        let (handshake, established) = tokio::sync::oneshot::channel();
        let peer = tokio::spawn(async move {
            let (mut socket, _) = server.accept().await.unwrap();
            let mut preface = [0; 24]; socket.read_exact(&mut preface).await.unwrap();
            assert_eq!(&preface, b"PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n");
            socket.write_all(&[0,0,0,4,0,0,0,0,0]).await.unwrap();
            handshake.send(()).unwrap();
            let mut bytes = [0; 4096];
            loop {
                match socket.read(&mut bytes).await {
                    Ok(0) => break,
                    Ok(_) => {},
                    // Closing while HTTP/2 SETTINGS are in flight may reset
                    // rather than FIN. Both prove the retained clone lost I/O.
                    Err(error) if matches!(error.kind(), io::ErrorKind::ConnectionReset | io::ErrorKind::ConnectionAborted) => break,
                    Err(_) => panic!("unexpected synthetic peer read failure"),
                }
            }
        });
        let endpoint = tonic::transport::Endpoint::from_shared(format!("http://{}", lease.0.binding.destination())).unwrap();
        let channel = bounded(Permit::Public(lease.clone()).connect(endpoint)).await.unwrap();
        bounded(established).await.unwrap();
        let retained = channel.clone(); drop(channel);
        assert!(!peer.is_finished()); lease.revoke();
        bounded(peer).await.unwrap();
        drop(retained);
    });
}

#[test]
fn authority_and_destination_cannot_be_substituted() {
    runtime().block_on(async {
        let server = listener().await;
        let expected = binding(server.local_addr().unwrap());
        let port = expected.destination().port();
        let other_port = port.checked_add(1).unwrap_or(1);
        let permit = Permit::Public(LiveOverlayLease::synthetic(expected));
        for uri in [format!("http://localhost:{port}"), format!("http://127.0.0.1:{other_port}"),
            format!("https://127.0.0.1:{port}"), format!("http://127.0.0.1:{port}/path"), format!("http://127.0.0.1:{port}///")] {
            // Binding and fixed destination are real and usable: removing the
            // authority guard would actually connect to this owned listener.
            assert!(bounded(permit.connect(tonic::transport::Endpoint::from_shared(uri).unwrap())).await.is_err());
            assert!(tokio::time::timeout(Duration::from_millis(30), server.accept()).await.is_err());
        }
    });
}
