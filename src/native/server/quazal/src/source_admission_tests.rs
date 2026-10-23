//! Synthetic packet/peer fixtures; no packets are sent to private/WAN addresses.
use super::*;
use crate::source_policy::SourcePolicy;
use std::sync::{Arc, atomic::{AtomicUsize, Ordering}};
type TestServer<'a> = Server<'a, fn(ClientInfo<()>), fn(ClientInfo<()>)>;

struct CountLogs(Arc<AtomicUsize>);
impl slog::Drain for CountLogs {
    type Ok = ();
    type Err = std::convert::Infallible;
    fn log(&self, _: &slog::Record<'_>, _: &slog::OwnedKVList) -> Result<(), Self::Err> {
        self.0.fetch_add(1, Ordering::Relaxed);
        Ok(())
    }
}

#[test]
fn outside_public_peer_drops_before_parse_allocation_ack_and_callbacks() {
    let calls = Arc::new(AtomicUsize::new(0));
    let logger = Logger::root(CountLogs(calls.clone()), o!());
    let ctx = Context::splinter_cell_blacklist();
    let registry = StreamHandlerRegistry::new(logger.clone());
    let policy = SourcePolicy::public_overlay("10.77.0.1".parse().unwrap(),24).unwrap();
    let mut server: TestServer<'_> = Server::new_with_source_policy(logger.clone(),&ctx,registry,policy);
    // No socket exists: any accidental ACK/application dispatch would panic.
    for address in ["10.78.0.2:4000","10.77.0.0:4000","10.77.0.255:4000","127.0.0.1:4000","[::ffff:10.77.0.2]:4000"] {
        let address = address.parse().unwrap();
        server.handle_datagram(b"invalid packet must not reach parser",address);
        for packet_type in [PacketType::Syn,PacketType::Connect,PacketType::User,PacketType::Data,PacketType::Ping,PacketType::Disconnect] {
            server.handle_packet(&logger,QPacket { packet_type, ..Default::default() },address);
        }
        assert_eq!(server.maintenance_counts(),(0,0,0,0));
    }
    assert_eq!(calls.load(Ordering::Relaxed),0);
    assert_eq!(server.next_conn_id.load(Ordering::Relaxed),0x3AAA_AAAA);
}

static USER_CALLS: AtomicUsize = AtomicUsize::new(0);
fn user_packet(_: &Logger, _: QPacket, _: SocketAddr, _: &UdpSocket) { USER_CALLS.fetch_add(1,Ordering::Relaxed); }

#[test]
fn admitted_public_datagram_reaches_real_parser_and_dispatch_without_network_send() {
    let logger=Logger::root(slog::Discard,o!());
    let ctx=Context::splinter_cell_blacklist();
    let registry=StreamHandlerRegistry::new(logger.clone());
    let policy=SourcePolicy::public_overlay("10.77.0.1".parse().unwrap(),24).unwrap();
    let mut server:TestServer<'_>=Server::new_with_source_policy(logger,&ctx,registry,policy);
    server.socket=Some(UdpSocket::bind("127.0.0.1:0").unwrap());
    server.user_handler=Some(user_packet);
    let packet=QPacket { packet_type:PacketType::User,flags:PacketFlag::HasSize.into(),..Default::default() };
    let bytes=packet.to_bytes(&ctx);
    USER_CALLS.store(0,Ordering::Relaxed);
    server.handle_datagram(&bytes,"10.77.0.2:4000".parse().unwrap());
    assert_eq!(USER_CALLS.load(Ordering::Relaxed),1);
    server.handle_datagram(&bytes,"10.78.0.2:4000".parse().unwrap());
    assert_eq!(USER_CALLS.load(Ordering::Relaxed),1);
    assert_eq!(server.maintenance_counts(),(0,0,0,0));
}

#[test]
fn lan_real_loopback_syn_bytes_and_pending_identity_remain_admitted() {
    let logger=Logger::root(slog::Discard,o!());
    let ctx=Context::splinter_cell_blacklist();
    let registry=StreamHandlerRegistry::new(logger.clone());
    let mut server:TestServer<'_>=Server::new(logger,&ctx,registry);
    server.socket=Some(UdpSocket::bind("127.0.0.1:0").unwrap());
    let client=UdpSocket::bind("127.0.0.1:0").unwrap();
    client.set_read_timeout(Some(Duration::from_secs(2))).unwrap();
    let packet=QPacket { packet_type:PacketType::Syn,conn_signature:Some(0),flags:PacketFlag::HasSize.into(),..Default::default() };
    server.handle_datagram(&packet.to_bytes(&ctx),client.local_addr().unwrap());
    let mut bytes=[0;1024];let(count,peer)=client.recv_from(&mut bytes).unwrap();
    assert_eq!(peer,server.socket.as_ref().unwrap().local_addr().unwrap());
    let(ack,_)=QPacket::from_bytes(&ctx,&bytes[..count]).unwrap();ack.validate(&ctx,&bytes[..count]).unwrap();
    assert!(ack.flags.contains(PacketFlag::Ack));
    assert_eq!(server.maintenance_counts(),(0,0,1,0));
    assert_eq!(*server.new_clients.values().next().unwrap().address(),client.local_addr().unwrap());
}
