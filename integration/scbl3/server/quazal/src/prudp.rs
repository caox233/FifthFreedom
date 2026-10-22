/// This module handles the PRUDP protocol, which is a custom reliable UDP protocol.
pub mod packet;

use std::cell::RefCell;
use std::collections::HashMap;
use std::io;
use std::net::SocketAddr;
use std::net::UdpSocket;
use std::net::{self};
use std::sync::atomic::AtomicU32;
use std::time::Duration;
use std::time::Instant;

use slog::debug;
use slog::error;
use slog::o;
use slog::Logger;

use self::packet::crypt_key;
use self::packet::PacketFlag;
use self::packet::PacketType;
use self::packet::QPacket;
use self::packet::StreamHandler;
use self::packet::StreamHandlerRegistry;
use self::packet::VPort;
use crate::kerberos::KerberosTicketInternal;
use crate::rmc::basic::ReadStream;
use crate::rmc::basic::ToStream;
use crate::ClientInfo;
use crate::ConnectionID;
use crate::Context;
use crate::Signature;

const MAX_PAYLOAD_SIZE: usize = 1000;
const MAX_INBOUND_MESSAGE_BYTES: usize = 256 * 1024;
const MAX_CACHED_RESPONSES: usize = 64;
const MAX_CACHED_RESPONSE_BYTES: usize = 512 * 1024;
const FRAGMENT_ASSEMBLY_TIMEOUT: Duration = Duration::from_secs(10);
const SESSION_TIMEOUT: Duration = Duration::from_secs(60);

fn reset_fragment_state<T>(ci: &mut ClientInfo<T>) {
    ci.packet_fragments.clear();
    ci.packet_fragment_bytes = 0;
    ci.packet_fragment_start_sequence = None;
    ci.packet_fragment_started_at = None;
}

fn expire_fragment_state<T>(ci: &mut ClientInfo<T>) {
    if ci.packet_fragment_started_at.is_some_and(|started| started.elapsed() > FRAGMENT_ASSEMBLY_TIMEOUT) {
        reset_fragment_state(ci);
    }
}

fn cache_fragment<T>(ci: &mut ClientInfo<T>, fragment_id: u8, sequence: u16, payload: Vec<u8>) -> Result<bool, &'static str> {
    expire_fragment_state(ci);
    if fragment_id == 0 {
        return Err("fragment id outside protocol limit");
    }

    // The Blacklist client numbers payload chunks from one upward while the
    // reliable packet sequence also increases. Fragment N therefore belongs
    // to the group whose first sequence is sequence - (N - 1). Fragment zero
    // follows the highest numbered chunk.
    let start_sequence = sequence.wrapping_sub(u16::from(fragment_id - 1));
    if ci.packet_fragment_start_sequence != Some(start_sequence) {
        reset_fragment_state(ci);
        ci.packet_fragment_start_sequence = Some(start_sequence);
        ci.packet_fragment_started_at = Some(Instant::now());
    }

    if let Some(existing) = ci.packet_fragments.get(&fragment_id) {
        return if *existing == payload { Ok(false) } else { Err("conflicting duplicate fragment") };
    }

    let total = ci.packet_fragment_bytes.checked_add(payload.len()).ok_or("fragment byte count overflow")?;
    if total > MAX_INBOUND_MESSAGE_BYTES {
        return Err("fragmented message exceeds byte limit");
    }

    ci.packet_fragments.insert(fragment_id, payload);
    ci.packet_fragment_bytes = total;
    Ok(true)
}

fn finish_fragmented_payload<T>(ci: &mut ClientInfo<T>, sequence: u16, final_payload: Vec<u8>) -> Result<Vec<u8>, &'static str> {
    expire_fragment_state(ci);
    if final_payload.len() > MAX_INBOUND_MESSAGE_BYTES {
        return Err("final fragment exceeds byte limit");
    }
    if ci.packet_fragments.is_empty() {
        return Ok(final_payload);
    }
    let highest = *ci.packet_fragments.keys().max().ok_or("empty fragment group")?;
    if ci
        .packet_fragment_start_sequence
        .is_none_or(|start_sequence| start_sequence.wrapping_add(u16::from(highest)) != sequence)
    {
        return Err("fragment group sequence mismatch");
    }
    if ci.packet_fragments.len() != usize::from(highest) {
        return Err("fragment group has a missing id");
    }
    let total = ci.packet_fragment_bytes.checked_add(final_payload.len()).ok_or("reassembled byte count overflow")?;
    if total > MAX_INBOUND_MESSAGE_BYTES {
        return Err("reassembled message exceeds byte limit");
    }

    let fragments = std::mem::take(&mut ci.packet_fragments);
    ci.packet_fragment_bytes = 0;
    ci.packet_fragment_start_sequence = None;
    ci.packet_fragment_started_at = None;
    let mut output = Vec::new();
    output.try_reserve_exact(total).map_err(|_| "reassembled message allocation failed")?;
    // The Blacklist client sends fragment one first and increases the id for
    // every subsequent payload chunk, then terminates the group with zero.
    for fragment_id in 1..=highest {
        output.extend_from_slice(fragments.get(&fragment_id).ok_or("fragment group has a missing id")?);
    }
    output.extend_from_slice(&final_payload);
    Ok(output)
}

fn cache_response<T>(ci: &mut ClientInfo<T>, request_sequence: u16, responses: Vec<QPacket>) {
    if responses.is_empty() {
        return;
    }

    let Some(response_bytes) = responses.iter().try_fold(0usize, |total, response| total.checked_add(response.payload.len())) else {
        return;
    };
    if response_bytes > MAX_CACHED_RESPONSE_BYTES {
        return;
    }

    if let Some(previous) = ci.response_cache.insert(request_sequence, responses) {
        let previous_bytes = previous.iter().map(|response| response.payload.len()).sum::<usize>();
        ci.response_cache_bytes = ci.response_cache_bytes.saturating_sub(previous_bytes);
        ci.response_cache_order.retain(|sequence| *sequence != request_sequence);
    }
    ci.response_cache_bytes += response_bytes;
    ci.response_cache_order.push_back(request_sequence);

    while ci.response_cache_order.len() > MAX_CACHED_RESPONSES || ci.response_cache_bytes > MAX_CACHED_RESPONSE_BYTES {
        if let Some(sequence) = ci.response_cache_order.pop_front() {
            if let Some(response) = ci.response_cache.remove(&sequence) {
                let response_bytes = response.iter().map(|packet| packet.payload.len()).sum::<usize>();
                ci.response_cache_bytes = ci.response_cache_bytes.saturating_sub(response_bytes);
            }
        }
    }
}

fn cached_response<T>(ci: &ClientInfo<T>, request_sequence: u16) -> Option<Vec<QPacket>> {
    ci.response_cache.get(&request_sequence).cloned()
}

fn prepare_response<T>(mut response: QPacket, ci: &mut ClientInfo<T>) -> QPacket {
    response.sequence = ci.server_sequence_id;
    ci.server_sequence_id = ci.server_sequence_id.wrapping_add(1);
    response.flags.insert(PacketFlag::HasSize);
    response.flags.insert(PacketFlag::NeedAck);
    response.flags.insert(PacketFlag::Reliable);
    response.signature = ci.client_signature.unwrap_or_default();
    response.session_id = ci.server_session;
    response
}

/// A registry for clients.
#[derive(Default)]
pub struct ClientRegistry<T> {
    clients: HashMap<u32, RefCell<ClientInfo<T>>>,
    connection_id_session_ids: HashMap<ConnectionID, Signature>,
}

impl<T> ClientRegistry<T> {
    /// Returns a client by its connection ID.
    #[must_use]
    pub fn client_by_connection_id(&self, conn_id: ConnectionID) -> Option<&RefCell<ClientInfo<T>>> {
        self.connection_id_session_ids.get(&conn_id).and_then(|sig| self.clients.get(&sig.0))
    }
}

/// A PRUDP server.
pub struct Server<'a, ECH, DH, T = ()>
where
    ECH: FnMut(ClientInfo<T>),
    DH: FnMut(ClientInfo<T>),
{
    logger: slog::Logger,
    registry: StreamHandlerRegistry<T>,
    socket: Option<net::UdpSocket>,
    ctx: &'a Context,
    new_clients: HashMap<u32, ClientInfo<T>>,
    client_registry: ClientRegistry<T>,
    /// A handler for user-defined packets.
    pub user_handler: Option<fn(logger: &Logger, packet: QPacket, client: SocketAddr, sock: &net::UdpSocket)>,
    /// A handler for expired clients.
    pub expired_client_handler: Option<ECH>,
    /// A handler for disconnected clients.
    pub disconnect_handler: Option<DH>,
    next_conn_id: AtomicU32,
}

impl<ECH, DH, T> Server<'_, ECH, DH, T>
where
    T: Default,
    ECH: FnMut(ClientInfo<T>),
    DH: FnMut(ClientInfo<T>),
{
    /// Creates a new PRUDP server.
    #[must_use]
    pub fn new(logger: slog::Logger, ctx: &Context, registry: StreamHandlerRegistry<T>) -> Server<'_, ECH, DH, T> {
        let client_registry = ClientRegistry {
            clients: HashMap::default(),
            connection_id_session_ids: HashMap::default(),
        };
        Server {
            logger,
            registry,
            socket: None,
            ctx,
            new_clients: HashMap::default(),
            client_registry,
            user_handler: None,
            expired_client_handler: None,
            disconnect_handler: None,
            next_conn_id: AtomicU32::new(0x3AAA_AAAA),
        }
    }

    /// Registers a stream handler for a virtual port.
    pub fn register(&mut self, vport: VPort, protocol: Box<dyn StreamHandler<T>>) {
        self.registry.register(vport, protocol);
    }

    /// Binds the server to a socket address.
    pub fn bind<A: net::ToSocketAddrs>(&mut self, addrs: A) -> io::Result<()> {
        self.socket = Some(net::UdpSocket::bind(addrs)?);
        info!(self.logger, "Listening on {}", self.socket.as_ref().unwrap().local_addr().unwrap());
        Ok(())
    }

    /// Starts the server's main loop.
    pub fn serve(mut self) {
        let socket = self.socket.as_ref().expect("UDP socket required").try_clone().expect("Couldn't clone socket");
        socket.set_read_timeout(Some(Duration::from_secs(1))).expect("error setting read timeout");
        let mut buf = vec![0u8; 1024];
        'outer: loop {
            let (nread, client) = match socket.recv_from(&mut buf) {
                Ok(x) => x,
                Err(e) => {
                    if e.kind() == std::io::ErrorKind::TimedOut || e.kind() == std::io::ErrorKind::WouldBlock {
                        self.clear_clients();
                    } else {
                        error!(self.logger, "recv_from failed: {}", e);
                    }
                    continue;
                }
            };
            let logger = self.logger.new(o!("client" => client));
            let mut data = &buf[..nread];

            while !data.is_empty() {
                let (packet, nparsed) = match QPacket::from_bytes(self.ctx, data) {
                    Ok(p) => p,
                    Err(e) => {
                        error!(logger, "Invalid packet received"; "error" =>  %e);
                        continue 'outer;
                    }
                };
                #[allow(clippy::cast_possible_truncation)]
                let (packet_data, next_data) = data.split_at(nparsed as usize);
                data = next_data;
                trace!(logger, "-> {:02x?}", packet_data);

                if let Err(e) = packet.validate(self.ctx, packet_data) {
                    error!(logger, "Invalid packet received: {:?}", packet; "error" =>  %e);
                    continue;
                }

                self.handle_packet(&logger.new(o!("seq" => packet.sequence, "session" => packet.session_id)), packet, client);
            }
        }
    }

    /// Handles a received packet.
    fn handle_packet(&mut self, logger: &Logger, packet: QPacket, client: SocketAddr) {
        debug!(logger, "packet: {:?}", packet);
        if packet.flags.contains(PacketFlag::Ack) {
            debug!(logger, "Received ACK");
            return;
        }
        match packet.packet_type {
            PacketType::Syn => self.handle_syn(logger, packet, client),
            PacketType::Connect => self.handle_connect(logger, packet, client),
            PacketType::Data => self.handle_data(logger, packet, client),
            PacketType::Disconnect => {
                let Some(ci) = self.client_registry.clients.remove(&packet.signature) else {
                    return;
                };
                info!(logger, "Client disconnected"; "signature" => packet.signature, "session" => packet.session_id);
                if self.send_ack(logger, &client, &packet, &ci.borrow(), false).is_err() {
                    // ignore
                }
                if let Some(handler) = self.disconnect_handler.as_mut() {
                    (handler)(ci.into_inner());
                }
            }
            PacketType::Ping => {
                let Some(ci) = self.client_registry.clients.get(&packet.signature) else {
                    return;
                };
                ci.borrow_mut().seen();
                if self.send_ack(logger, &client, &packet, &ci.borrow(), false).is_err() {
                    // ignore
                }
            }
            PacketType::User => {
                if self.user_handler.is_none() {
                    error!(logger, "unsupported user packet");
                } else {
                    (self.user_handler.as_ref().unwrap())(logger, packet, client, self.socket.as_ref().unwrap());
                }
            }
            PacketType::Route | PacketType::Raw => {
                warn!(
                    logger,
                    "Dropping unsupported PRUDP packet type";
                    "packet_type" => format!("{:?}", packet.packet_type),
                    "client" => client.to_string(),
                );
            }
        }
    }

    /// Handles a data packet.
    fn handle_data(&mut self, logger: &Logger, packet: QPacket, client: SocketAddr) {
        #![allow(clippy::cast_possible_truncation)]

        debug!(logger, "Handling data packet");
        let Some(ci) = self.client_registry.clients.get(&packet.signature) else {
            warn!(logger, "client is unknown!");
            return;
        };
        let logger = logger.new(o!("pid" => ci.borrow().user_id));
        let ci = &mut ci.borrow_mut();
        ci.seen();
        if let Err(e) = self.send_ack(&logger, &client, &packet, &*ci, false) {
            error!(logger, "Error sending ack"; "error" => %e);
        } else {
            debug!(logger, "Send ack");
        }
        if packet.fragment_id.is_none_or(|fragment_id| fragment_id == 0) && self.replay_cached_response(&logger, &client, packet.sequence, ci) {
            return;
        }
        let payload = if let Some(fid) = packet.fragment_id {
            if fid != 0 {
                match cache_fragment(ci, fid, packet.sequence, packet.payload) {
                    Ok(true) => info!(logger, "Caching fragment {}", fid),
                    Ok(false) => debug!(logger, "Ignoring duplicate fragment {}", fid),
                    Err(reason) => {
                        warn!(logger, "Dropping fragmented PRUDP message"; "reason" => reason, "fragment" => fid);
                        reset_fragment_state(ci);
                    }
                }
                return;
            }

            match finish_fragmented_payload(ci, packet.sequence, packet.payload) {
                Ok(payload) => payload,
                Err(reason) => {
                    warn!(logger, "Dropping fragmented PRUDP message"; "reason" => reason, "fragment" => fid);
                    reset_fragment_state(ci);
                    return;
                }
            }
        } else {
            packet.payload
        };
        let diagnostic_request = match crate::rmc::Packet::from_bytes(&payload) {
            Ok(crate::rmc::Packet::Request(request)) if matches!((request.protocol_id, request.method_id), (11, 1 | 4) | (5003, 1)) => {
                Some((request.protocol_id, request.method_id, request.call_id))
            }
            _ => None,
        };
        let resp = self
            .registry
            .handle_packet(&logger, self.ctx, ci, &packet.destination, &payload, &self.client_registry, self.socket.as_ref().unwrap());
        match resp {
            Some(Ok(payload)) => {
                let chunks = payload.chunks(MAX_PAYLOAD_SIZE);
                let mut responses = Vec::with_capacity(chunks.len());
                for (fid, chunk) in (0..chunks.len()).rev().zip(chunks) {
                    let response = prepare_response(
                        QPacket {
                            source: packet.destination,
                            destination: packet.source,
                            packet_type: PacketType::Data,
                            payload: chunk.to_vec(),
                            fragment_id: Some(fid as u8),
                            ..Default::default()
                        },
                        ci,
                    );
                    responses.push(response);
                }
                cache_response(ci, packet.sequence, responses.clone());
                let fragment_count = responses.len();
                for response in responses {
                    let response_sequence = response.sequence;
                    let fragment_index = response.fragment_id.unwrap_or_default();
                    if let Err(e) = self.send_packet(&logger, &client, response) {
                        error!(logger, "Error sending response"; "error" => %e);
                    } else {
                        trace!(logger, "Send response");
                        if let Some((protocol_id, method_id, call_id)) = diagnostic_request {
                            info!(logger, "RMC diagnostic response sent";
                                "protocol_id" => protocol_id,
                                "method_id" => method_id,
                                "call" => call_id,
                                "request_sequence" => packet.sequence,
                                "response_sequence" => response_sequence,
                                "fragment_index" => fragment_index,
                                "fragment_count" => fragment_count);
                        }
                    }
                }
            }
            None => {
                error!(logger, "No handler found");
            }
            Some(Err(_)) => {
                error!(logger, "Handler failed");
            }
        }
    }

    fn replay_cached_response(&self, logger: &Logger, client: &SocketAddr, request_sequence: u16, ci: &mut ClientInfo<T>) -> bool {
        let Some(responses) = cached_response(ci, request_sequence) else {
            return false;
        };
        reset_fragment_state(ci);
        info!(
            logger,
            "Replaying cached response for duplicate request";
            "request_sequence" => request_sequence,
            "response_packets" => responses.len(),
        );
        for response in responses {
            if let Err(e) = self.send_packet(logger, client, response) {
                error!(logger, "Error replaying cached response"; "error" => %e);
            }
        }
        true
    }

    /// Handles a SYN packet.
    fn handle_syn(&mut self, logger: &Logger, mut packet: QPacket, client: SocketAddr) {
        debug!(logger, "Handling syn packet");
        let ci: ClientInfo<T> = ClientInfo::new(client);
        let sig = ci.server_signature;
        self.new_clients.insert(sig, ci);

        packet.conn_signature = Some(sig);

        let ci = self.new_clients.get(&sig).unwrap();

        if let Err(e) = self.send_ack(logger, &client, &packet, ci, false) {
            error!(logger, "Error sending syn ack packet"; "error" => %e);
        }
    }

    /// Handles a CONNECT packet.
    fn handle_connect(&mut self, logger: &Logger, mut packet: QPacket, client: SocketAddr) {
        debug!(logger, "Handling connect packet");
        let Some(signature) = packet.conn_signature else {
            error!(logger, "Client {:x} did not provide a connection signature. This should not happen", packet.signature);
            return;
        };

        let Some(mut ci) = self.new_clients.remove(&packet.signature) else {
            warn!(logger, "Unknown client {:x} tried to connect. Ignoring the attempt", packet.signature);
            return;
        };
        ci.client_signature = Some(signature);
        ci.server_session = rand::random();
        ci.client_session = packet.session_id;

        let ci = {
            self.client_registry.clients.insert(packet.signature, RefCell::new(ci));
            let ci = self.client_registry.clients.get(&packet.signature).unwrap();
            ci
        };

        if !packet.payload.is_empty() {
            let data = std::mem::take(&mut packet.payload);
            let mut s = ReadStream::from_bytes(&data);
            let ticket_key = &self.ctx.ticket_key;
            let cids = &mut self.client_registry.connection_id_session_ids;
            let next_conn_id = &mut self.next_conn_id;
            let res = move || -> Result<_, crate::rmc::basic::FromStreamError> {
                let ticket: Vec<u8> = s.read()?;
                let request_data: Vec<u8> = s.read()?;

                let ti = KerberosTicketInternal::open(&ticket, ticket_key)?;

                if ti.valid_until
                    < std::time::SystemTime::now()
                        .duration_since(std::time::SystemTime::UNIX_EPOCH)
                        .as_ref()
                        .map(std::time::Duration::as_secs)
                        .unwrap_or_default()
                {
                    return Ok(vec![]);
                }
                let id = next_conn_id.fetch_add(1, std::sync::atomic::Ordering::AcqRel);
                ci.borrow_mut().user_id.replace(ti.principle_id);
                ci.borrow_mut().connection_id.replace(ConnectionID(id));
                cids.insert(ci.borrow().connection_id.unwrap(), Signature(packet.signature));
                let data = crypt_key(ti.session_key.as_ref(), &request_data);

                #[allow(clippy::items_after_statements)]
                #[derive(FromStream, Debug)]
                struct ConnectData {
                    _user_pid: u32,
                    _connection_id: u32,
                    challenge: u32,
                }

                let cd: ConnectData = ReadStream::from_bytes(&data).read()?;

                let resp = cd.challenge + 1;
                let resp = resp.to_bytes();

                Ok(resp.to_bytes())
            }();

            match res {
                Ok(resp) => packet.payload = resp,
                Err(e) => error!(logger, "Error parsing ticket"; "error" => %e),
            }
        }

        packet.conn_signature = Some(0);

        if let Err(e) = self.send_ack(logger, &client, &packet, &ci.borrow(), !packet.payload.is_empty()) {
            error!(logger, "Error sending syn ack packet"; "error" => %e);
        }
        info!(logger, "New client connected"; "signature" => packet.signature, "session" => packet.session_id);
    }

    /// Sends a packet to a client.
    fn send_packet(&self, logger: &Logger, src: &SocketAddr, resp: QPacket) -> Result<usize, Box<dyn std::error::Error>> {
        send_packet(logger, self.ctx, src, self.socket.as_ref().unwrap(), resp)
    }

    /// Sends an ACK packet to a client.
    fn send_ack(&self, logger: &Logger, src: &SocketAddr, packet: &QPacket, ci: &ClientInfo<T>, keep_payload: bool) -> Result<usize, Box<dyn std::error::Error>> {
        let mut resp = packet.clone();
        resp.source = packet.destination;
        resp.destination = packet.source;
        resp.flags = PacketFlag::Ack | PacketFlag::HasSize;
        resp.signature = ci.client_signature.unwrap_or_default();
        resp.session_id = ci.server_session;
        if !keep_payload {
            resp.payload.clear();
        }
        resp.sequence = packet.sequence;
        self.send_packet(logger, src, resp)
    }

    /// Clears expired clients from the client registry.
    fn clear_clients(&mut self) {
        let now = Instant::now();
        for (_, ci) in self
            .client_registry
            .clients
            .extract_if(|_k, v| v.try_borrow().map(|ci| (now - ci.last_seen) > SESSION_TIMEOUT).unwrap_or(false))
        {
            if let Some(handler) = self.expired_client_handler.as_mut() {
                let ci = ci.into_inner();
                if let Some(conn_id) = ci.connection_id {
                    self.client_registry.connection_id_session_ids.remove(&conn_id);
                }
                (handler)(ci);
            }
        }
    }
}

/// Sends a request to a client.
pub fn send_request<T>(
    logger: &Logger,
    ctx: &Context,
    src: &SocketAddr,
    socket: &UdpSocket,
    mut req: QPacket,
    ci: &mut ClientInfo<T>,
) -> Result<usize, Box<dyn std::error::Error>> {
    req.sequence = ci.client_sequence_id;
    ci.client_sequence_id += 1;
    req.flags.insert(PacketFlag::HasSize);
    req.flags.insert(PacketFlag::NeedAck);
    req.flags.insert(PacketFlag::Reliable);
    req.signature = ci.server_signature;
    req.session_id = ci.client_session;
    send_packet(logger, ctx, src, socket, req)
}

/// Sends a packet to a client.
pub(crate) fn send_packet(logger: &Logger, ctx: &Context, src: &SocketAddr, socket: &UdpSocket, mut resp: QPacket) -> Result<usize, Box<dyn std::error::Error>> {
    if matches!(resp.packet_type, PacketType::Data) {
        resp.use_compression = true;
        if resp.fragment_id.is_none() {
            resp.fragment_id = Some(0);
        }
    }
    resp.flags.insert(PacketFlag::HasSize);
    trace!(logger, "<- {:?}", resp);
    let data = &resp.to_bytes(ctx);
    trace!(logger, "<- {:02x?}", data);
    let sz = socket.send_to(data, src)?;
    assert_eq!(sz, data.len());
    Ok(sz)
}

#[cfg(test)]
mod fragment_tests {
    use std::sync::atomic::AtomicUsize;
    use std::sync::atomic::Ordering;
    use std::sync::Arc;

    use super::*;

    type TestServer<'a> = Server<'a, fn(ClientInfo<()>), fn(ClientInfo<()>), ()>;

    struct CountingHandler {
        calls: Arc<AtomicUsize>,
    }

    impl StreamHandler<()> for CountingHandler {
        fn handle(
            &self,
            _logger: &Logger,
            _ctx: &Context,
            _ci: &mut ClientInfo<()>,
            data: &[u8],
            _client_registry: &ClientRegistry<()>,
            _socket: &UdpSocket,
        ) -> Result<Vec<u8>, packet::Error> {
            self.calls.fetch_add(1, Ordering::Relaxed);
            Ok(data.to_vec())
        }
    }

    fn client() -> ClientInfo<()> {
        ClientInfo::new("127.0.0.1:3074".parse().unwrap())
    }

    #[test]
    fn unsupported_route_and_raw_packets_are_dropped_without_panicking() {
        let logger = Logger::root(slog::Discard, o!());
        let ctx = Context::default();
        let registry = StreamHandlerRegistry::new(logger.clone());
        let mut server: TestServer<'_> = Server::new(logger.clone(), &ctx, registry);
        let client_address = "127.0.0.1:3074".parse().unwrap();

        for packet_type in [PacketType::Route, PacketType::Raw] {
            server.handle_packet(
                &logger,
                QPacket {
                    packet_type,
                    ..Default::default()
                },
                client_address,
            );
        }
    }

    #[test]
    fn reassembles_real_sender_order_with_increasing_sequences() {
        let mut ci = client();
        assert_eq!(cache_fragment(&mut ci, 1, 100, b"a".to_vec()), Ok(true));
        assert_eq!(cache_fragment(&mut ci, 2, 101, b"b".to_vec()), Ok(true));
        let payload = finish_fragmented_payload(&mut ci, 102, b"c".to_vec()).unwrap();
        assert_eq!(payload, b"abc");
        assert!(ci.packet_fragments.is_empty());
    }

    #[test]
    fn eleven_fragments_follow_the_blacklist_login_sequence_pattern() {
        let mut ci = client();
        for fragment_id in 1u8..=11 {
            let sequence = 500u16 + u16::from(fragment_id - 1);
            let byte = b'a' + (fragment_id - 1);
            assert_eq!(cache_fragment(&mut ci, fragment_id, sequence, vec![byte]), Ok(true));
        }
        let payload = finish_fragmented_payload(&mut ci, 511, b"z".to_vec()).unwrap();
        assert_eq!(payload, b"abcdefghijkz");
    }

    #[test]
    fn rejects_missing_fragment_without_allocating_from_payload_data() {
        let mut ci = client();
        assert_eq!(cache_fragment(&mut ci, 2, 101, b"b".to_vec()), Ok(true));
        assert_eq!(finish_fragmented_payload(&mut ci, 102, b"c".to_vec()), Err("fragment group has a missing id"));
    }

    #[test]
    fn reassembles_nonzero_fragments_that_arrive_out_of_order() {
        let mut ci = client();
        assert_eq!(cache_fragment(&mut ci, 2, 101, b"b".to_vec()), Ok(true));
        assert_eq!(cache_fragment(&mut ci, 1, 100, b"a".to_vec()), Ok(true));
        let payload = finish_fragmented_payload(&mut ci, 102, b"c".to_vec()).unwrap();
        assert_eq!(payload, b"abc");
    }

    #[test]
    fn a_new_final_sequence_discards_stale_fragments() {
        let mut ci = client();
        assert_eq!(cache_fragment(&mut ci, 1, 100, b"old".to_vec()), Ok(true));
        assert_eq!(cache_fragment(&mut ci, 1, 200, b"new".to_vec()), Ok(true));
        let payload = finish_fragmented_payload(&mut ci, 201, b"end".to_vec()).unwrap();
        assert_eq!(payload, b"newend");
    }

    #[test]
    fn duplicate_fragment_does_not_inflate_retained_bytes() {
        let mut ci = client();
        assert_eq!(cache_fragment(&mut ci, 1, 100, b"same".to_vec()), Ok(true));
        let retained = ci.packet_fragment_bytes;
        assert_eq!(cache_fragment(&mut ci, 1, 100, b"same".to_vec()), Ok(false));
        assert_eq!(ci.packet_fragment_bytes, retained);
    }

    #[test]
    fn mismatched_final_sequence_is_rejected() {
        let mut ci = client();
        assert_eq!(cache_fragment(&mut ci, 1, 100, b"a".to_vec()), Ok(true));
        assert_eq!(finish_fragmented_payload(&mut ci, 102, b"b".to_vec()), Err("fragment group sequence mismatch"));
    }

    #[test]
    fn oversized_fragment_group_is_rejected() {
        let mut ci = client();
        let oversized = vec![0u8; MAX_INBOUND_MESSAGE_BYTES + 1];
        assert_eq!(cache_fragment(&mut ci, 1, 100, oversized), Err("fragmented message exceeds byte limit"));
    }

    #[test]
    fn duplicate_request_reuses_original_response_sequence() {
        let mut ci = client();
        ci.client_signature = Some(0x1234_5678);
        ci.server_session = 7;

        let response = prepare_response(QPacket::default(), &mut ci);
        assert_eq!(response.sequence, 1);
        assert_eq!(ci.server_sequence_id, 2);
        cache_response(&mut ci, 41, vec![response.clone()]);

        let replay = cached_response(&ci, 41).unwrap();
        assert_eq!(replay, vec![response]);
        assert_eq!(ci.server_sequence_id, 2);

        let next_response = prepare_response(QPacket::default(), &mut ci);
        assert_eq!(next_response.sequence, 2);
    }

    #[test]
    fn response_cache_evicts_oldest_request() {
        let mut ci = client();
        let max_cached_sequence = u16::try_from(MAX_CACHED_RESPONSES).expect("response cache limit fits u16");
        for sequence in 0..=max_cached_sequence {
            cache_response(&mut ci, sequence, vec![QPacket::default()]);
        }

        assert!(cached_response(&ci, 0).is_none());
        assert!(cached_response(&ci, 1).is_some());
        assert!(cached_response(&ci, max_cached_sequence).is_some());
        assert_eq!(ci.response_cache.len(), MAX_CACHED_RESPONSES);
    }

    #[test]
    fn response_cache_is_bounded_by_payload_bytes() {
        let mut ci = client();
        let half_limit = vec![0u8; MAX_CACHED_RESPONSE_BYTES / 2];
        for sequence in 1..=3 {
            cache_response(
                &mut ci,
                sequence,
                vec![QPacket {
                    payload: half_limit.clone(),
                    ..Default::default()
                }],
            );
        }

        assert!(cached_response(&ci, 1).is_none());
        assert!(cached_response(&ci, 2).is_some());
        assert!(cached_response(&ci, 3).is_some());
        assert_eq!(ci.response_cache_bytes, MAX_CACHED_RESPONSE_BYTES);
    }

    #[test]
    fn duplicate_data_packet_replays_without_calling_handler_again() {
        sodiumoxide::init().unwrap();
        let logger = Logger::root(slog::Discard, o!());
        let ctx = Context::default();
        let calls = Arc::new(AtomicUsize::new(0));
        let vport = VPort {
            stream_type: packet::StreamType::RVSec,
            port: 1,
        };
        let mut registry = StreamHandlerRegistry::new(logger.clone());
        registry.register(vport, Box::new(CountingHandler { calls: Arc::clone(&calls) }));
        let mut server: TestServer<'_> = Server::new(logger.clone(), &ctx, registry);
        server.bind("127.0.0.1:0").unwrap();

        let client_socket = UdpSocket::bind("127.0.0.1:0").unwrap();
        let client_address = client_socket.local_addr().unwrap();
        let signature = 0x1020_3040;
        let mut ci = ClientInfo::new(client_address);
        ci.client_signature = Some(0x5060_7080);
        ci.server_session = 7;
        server.client_registry.clients.insert(signature, RefCell::new(ci));

        let request = QPacket {
            source: vport,
            destination: vport,
            packet_type: PacketType::Data,
            signature,
            sequence: 41,
            payload: b"login".to_vec(),
            ..Default::default()
        };
        server.handle_data(&logger, request.clone(), client_address);
        server.handle_data(&logger, request, client_address);

        assert_eq!(calls.load(Ordering::Relaxed), 1);
        let ci = server.client_registry.clients.get(&signature).unwrap().borrow();
        assert_eq!(ci.server_sequence_id, 2);
        assert_eq!(cached_response(&ci, 41).unwrap()[0].sequence, 1);
    }
}
