// Source-linked serialization only: no socket, runtime, core, RPC or TUN.
use easytier::{
    proto::{api::instance::{PeerConnInfo, PeerInfo, Route, list_peer_route_pair},
        common::{TunnelInfo, Ipv4Inet}, peer_rpc::SecureAuthLevel},
    tunnel::build_url_from_socket_addr,
};
fn public_vector(value: &str) -> Vec<u8> {
    (0..value.len()).step_by(2).map(|i| u8::from_str_radix(&value[i..i+2], 16).unwrap()).collect()
}
fn main() {
    // RFC 7748 public vectors only. No secret or private-key material.
    let local_key = public_vector("8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a");
    let remote_key = public_vector("de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f");
    let id: uuid::Uuid = "ba32ace5-d0ac-4b60-928e-31be9a34ba5e".parse().unwrap();
    let items: Vec<_> = ["0.0.0.0:35000", "[::]:35000", "10.77.0.1:22001"]
        .iter().map(|local| {
            let tunnel = TunnelInfo {
                tunnel_type: "quic".to_owned(),
                local_addr: Some(build_url_from_socket_addr(&local.to_string(), "quic").into()),
                remote_addr: Some(build_url_from_socket_addr(&"10.77.0.2:22002".to_owned(), "quic").into()),
                resolved_remote_addr: Some(build_url_from_socket_addr(&"10.77.0.2:22002".to_owned(), "quic").into()),
            };
            let conn = PeerConnInfo { conn_id: id.to_string(), my_peer_id: 101, peer_id: 102,
                is_client: true, network_name: format!("ffprobe-{}", "1".repeat(32)),
                noise_local_static_pubkey: local_key.clone(), noise_remote_static_pubkey: remote_key.clone(),
                secure_auth_level: SecureAuthLevel::NetworkSecretConfirmed as i32,
                tunnel: Some(tunnel), ..Default::default() };
            let peer = PeerInfo { peer_id: 102, conns: vec![conn], default_conn_id: Some(id.into()),
                directly_connected_conns: vec![id.into()] };
            let route = Route { peer_id: 102, next_hop_peer_id: 102, cost: 1,
                ipv4_addr: Some(Ipv4Inet { address: Some(std::net::Ipv4Addr::new(10,78,0,2).into()), network_length: 30 }),
                ..Default::default() };
            list_peer_route_pair(vec![peer], vec![route])
        }).collect();
    println!("{}", serde_json::to_string_pretty(&items).unwrap());
}
