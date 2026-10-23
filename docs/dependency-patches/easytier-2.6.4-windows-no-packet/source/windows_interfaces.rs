//! Windows interface discovery without a packet-capture backend.
//!
//! The successful API path keeps the former flags=0 conversion/filter behavior.
//! These records are discovery inputs, never interface ownership or UP evidence.
use std::net::IpAddr;

use ipnetwork::IpNetwork;
use network_interface::{Addr, NetworkInterface as SystemInterface};
use pnet::util::MacAddr;

#[derive(Debug, Clone)]
pub struct Interface {
    pub name: String,
    pub index: u32,
    pub mac: Option<MacAddr>,
    pub ips: Vec<IpNetwork>,
}

impl Interface {
    pub fn allows_discovery(&self) -> bool {
        self.ips.iter().map(IpNetwork::ip)
            .any(|ip| !ip.is_loopback() && !ip.is_unspecified() && !ip.is_multicast())
            && self.mac.map(|mac| !mac.is_zero()).unwrap_or(false)
    }
}

fn address(addr: Addr) -> Option<IpNetwork> {
    let (ip, mask) = match addr {
        Addr::V4(addr) => (IpAddr::V4(addr.ip), IpAddr::V4(addr.netmask
            .unwrap_or(std::net::Ipv4Addr::new(255, 255, 255, 255)))),
        Addr::V6(addr) => (IpAddr::V6(addr.ip), IpAddr::V6(addr.netmask
            .unwrap_or(std::net::Ipv6Addr::from(u128::MAX)))),
    };
    IpNetwork::with_netmask(ip, mask).ok()
}

pub fn collect(result: network_interface::Result<Vec<SystemInterface>>) -> Vec<Interface> {
    match result {
        Ok(ifaces) => ifaces.into_iter().map(|iface| Interface {
            name: iface.name,
            index: iface.index,
            mac: iface.mac_addr.as_deref().and_then(|mac| mac.parse::<MacAddr>().ok()),
            ips: iface.addr.into_iter().filter_map(address).collect(),
        }).collect(),
        Err(_) => {
            tracing::warn!("Windows interface enumeration failed; no capture fallback available");
            Vec::new()
        }
    }
}
