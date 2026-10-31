// Copy into the ignored exact upstream easytier/examples/ directory to run.
// Pure config parsing only: no GlobalCtx, RPC, core, TUN, sockets or secrets output.
use easytier::common::config::{ConfigLoader, TomlConfigLoader};
use std::path::Path;

fn check(directory: &Path) -> Result<usize, &'static str> {
    let mut count = 0;
    for transport in ["tcp", "quic", "wss"] {
        for role in ["initiator", "responder"] {
            let stem = format!("{transport}-{role}");
            let input = std::fs::read_to_string(directory.join(format!("{stem}.toml")))
                .map_err(|_| "input unavailable")?;
            let expected: serde_json::Value = serde_json::from_str(
                &std::fs::read_to_string(directory.join(format!("{stem}.json")))
                    .map_err(|_| "expectation unavailable")?,
            ).map_err(|_| "expectation invalid")?;
            let config = TomlConfigLoader::new_from_str(&input).map_err(|_| "config rejected")?;
            let flags = serde_json::to_value(config.get_flags()).map_err(|_| "flags invalid")?;
            for (key, value) in expected["flags"].as_object().ok_or("flags absent")? {
                if flags.get(key) != Some(value) { return Err("flag mismatch"); }
            }
            let secure = config.get_secure_mode().ok_or("secure mode absent")?;
            if !secure.enabled || secure.local_public_key.as_deref() != expected["public_key"].as_str()
                || secure.local_private_key.is_none()
                || config.get_ipv4().map(|x| x.to_string()).as_deref() != expected["overlay_cidr"].as_str()
                || config.get_id().to_string() != expected["instance_id"].as_str().ok_or("id absent")?
                || config.get_network_identity().network_name != expected["network_name"].as_str().ok_or("name absent")?
                || config.get_network_identity().network_secret.as_deref() != expected["psk"].as_str()
                || config.get_dhcp() || config.get_ipv6().is_some()
                || config.get_listeners().ok_or("listeners absent")?.iter().map(|x| x.as_str()).collect::<Vec<_>>()
                    != vec![expected["listener"].as_str().ok_or("listener expectation absent")?]
                || !config.get_mapped_listeners().is_empty() || !config.get_exit_nodes().is_empty()
                || !config.get_routes().ok_or("routes absent")?.is_empty()
                || !config.get_proxy_cidrs().is_empty() || !config.get_port_forwards().is_empty()
                || config.get_socks5_portal().is_some() || config.get_vpn_portal_config().is_some()
                || config.get_credential_file().is_some()
                || config.get_stun_servers() != Some(vec![]) || config.get_stun_servers_v6() != Some(vec![]) {
                return Err("profile mismatch");
            }
            let peers = config.get_peers();
            if role == "initiator" {
                if peers.len() != 1 || peers[0].uri.as_str() != expected["peer"].as_str().ok_or("peer absent")?
                    || peers[0].peer_public_key.as_deref() != expected["remote_public_key"].as_str() {
                    return Err("initiator mismatch");
                }
            } else if !peers.is_empty() { return Err("responder mismatch"); }
            count += 1;
        }
    }
    Ok(count)
}

fn main() {
    let directory = std::env::args_os().nth(1).expect("input directory required");
    match check(Path::new(&directory)) {
        Ok(count) => println!("upstream config parser: {count}/6 profiles passed; no runtime started"),
        Err(code) => { eprintln!("profile check failed: {code}"); std::process::exit(1); }
    }
}
