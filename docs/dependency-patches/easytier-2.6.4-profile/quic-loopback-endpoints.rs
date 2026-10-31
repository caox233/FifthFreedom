// Bounded production outer-transport loopback observation. No core, TUN, RPC,
// PeerConn/Noise, STUN start, DNS name, public destination or game is used.
use easytier::{common::{config::{TomlConfigLoader, ConfigLoader}, global_ctx::GlobalCtx, stun::StunInfoCollector},
    tunnel::{quic::{QuicTunnelConnector, QuicTunnelListener}, TunnelConnector, TunnelListener,
        packet_def::ZCPacket, build_url_from_socket_addr}};
use futures::{SinkExt, StreamExt};
use std::{sync::Arc, time::Duration};
fn context() -> Arc<GlobalCtx> {
    let config = TomlConfigLoader::default();
    config.set_stun_servers(Some(Vec::new()));
    config.set_stun_servers_v6(Some(Vec::new()));
    let ctx = Arc::new(GlobalCtx::new(config));
    ctx.replace_stun_info_collector(Box::new(StunInfoCollector::new(Vec::new(), Vec::new(), Vec::new())));
    ctx
}
#[tokio::main(flavor="current_thread")]
async fn main() {
    let mut listener = QuicTunnelListener::new("quic://127.0.0.1:0".parse().unwrap(), context());
    listener.listen().await.unwrap();
    let target = listener.local_url();
    assert_eq!(target.host_str(), Some("127.0.0.1"));
    let mut accepted = tokio::spawn(async move {
        let tunnel = listener.accept().await.unwrap();
        let info = tunnel.info().unwrap();
        let (mut input, mut output) = tunnel.split();
        let item = input.next().await.unwrap().unwrap();
        assert_eq!(item.payload(), b"ff-loopback-endpoint".as_slice());
        output.send(item).await.unwrap();
        // Wait for client closing its stream before dropping this owned listener.
        let _ = input.next().await;
        info
    });
    let result = tokio::time::timeout(Duration::from_secs(5), async {
        let mut connector = QuicTunnelConnector::new(target.clone(), context());
        let tunnel = connector.connect().await.unwrap();
        let client_info = tunnel.info().unwrap();
        let (mut input, mut output) = tunnel.split();
        output.send(ZCPacket::new_with_payload(b"ff-loopback-endpoint")).await.unwrap();
        let echo = input.next().await.unwrap().unwrap();
        assert_eq!(echo.payload(), b"ff-loopback-endpoint".as_slice());
        output.close().await.unwrap();
        drop((input, output));
        let server_info = (&mut accepted).await.unwrap();
        let profile_address = build_url_from_socket_addr(&"[::ffff:10.77.0.2]:22002".to_owned(), "quic");
        serde_json::json!({"schema":"ff-quic-loopback-endpoints-v1", "echo_verified":true,
            "client":client_info, "server":server_info,
            "synthetic_profile_mapped_url":profile_address.as_str(),
            "noise_or_overlay_verified":false})
    }).await;
    if result.is_err() { accepted.abort(); let _ = accepted.await; }
    println!("{}", serde_json::to_string_pretty(&result.unwrap()).unwrap());
}
