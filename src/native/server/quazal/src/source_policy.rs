//! Additional kernel-peer admission, never account identity or overlay proof.
//! LAN retains its original API address-family check and unfiltered transport.
use std::net::{IpAddr, Ipv4Addr};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct SourcePolicy { network: Option<PublicNetwork> }

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
struct PublicNetwork { network: u32, broadcast: u32 }

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct InvalidPublicNetwork;
impl std::fmt::Display for InvalidPublicNetwork {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter.write_str("PUBLIC_SOURCE_NETWORK_INVALID")
    }
}
impl std::error::Error for InvalidPublicNetwork {}

impl SourcePolicy {
    #[must_use]
    pub const fn lan() -> Self { Self { network: None } }

    /// Reuses the Public deployment contract: host address in a whole RFC1918
    /// or CGNAT network, /8..30. It does not inspect an interface or enable I/O.
    pub fn public_overlay(address: Ipv4Addr, prefix: u8) -> Result<Self, InvalidPublicNetwork> {
        if !(8..=30).contains(&prefix) { return Err(InvalidPublicNetwork); }
        let value = u32::from(address);
        let mask = u32::MAX << (32 - prefix);
        let network = value & mask;
        let broadcast = network | !mask;
        if value == network || value == broadcast
            || ![(0x0a00_0000, 8), (0xac10_0000, 12), (0xc0a8_0000, 16), (0x6440_0000, 10)]
                .iter().any(|&(base, parent)| prefix >= parent && network & (u32::MAX << (32 - parent)) == base) {
            return Err(InvalidPublicNetwork);
        }
        Ok(Self { network: Some(PublicNetwork { network, broadcast }) })
    }

    #[must_use]
    pub const fn is_public(self) -> bool { self.network.is_some() }

    /// Apply to recv_from/accepted TCP peer only, never forwarded headers, a
    /// supplied StationURL, claimed source IP, username, or token metadata.
    #[must_use]
    pub fn admits_transport(self, address: IpAddr) -> bool {
        let Some(network) = self.network else { return true; };
        let IpAddr::V4(address) = address else { return false; };
        let value = u32::from(address);
        value > network.network && value < network.broadcast
    }

    #[must_use]
    pub fn contains_client(self, address: IpAddr) -> bool {
        if self.is_public() { return self.admits_transport(address); }
        match address {
            IpAddr::V4(ip) => ip.octets()[0] != 0 && ip.octets()[0] < 224 && !ip.is_broadcast(),
            IpAddr::V6(_) => false,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn public_network_boundaries_and_unicast_membership() {
        for (host, prefix, first, last) in [
            ("10.77.0.1",24,"10.77.0.1","10.77.0.254"),
            ("172.16.0.1",12,"172.16.0.1","172.31.255.254"),
            ("192.168.0.1",16,"192.168.0.1","192.168.255.254"),
            ("100.64.0.1",10,"100.64.0.1","100.127.255.254"),
        ] {
            let policy = SourcePolicy::public_overlay(host.parse().unwrap(), prefix).unwrap();
            for allowed in [first,last] { assert!(policy.contains_client(allowed.parse().unwrap())); }
            let range = policy.network.unwrap();
            for address in [IpAddr::V4(range.network.into()),IpAddr::V4(range.broadcast.into()),
                "127.0.0.1".parse().unwrap(),"8.8.8.8".parse().unwrap(),"::ffff:10.77.0.2".parse().unwrap()] {
                assert!(!policy.admits_transport(address));
            }
        }
    }
    #[test]
    fn invalid_or_overbroad_public_network_never_falls_back_to_lan() {
        for (address,prefix) in [("10.0.0.1",7),("172.16.0.1",11),("192.168.1.1",15),
            ("100.64.1.1",9),("127.0.0.1",24),("169.254.1.1",24),("8.8.8.8",24),
            ("10.77.0.0",24),("10.77.0.255",24),("10.77.0.1",31),("10.77.0.1",32),
            ("10.77.0.1",255)] {
            assert_eq!(SourcePolicy::public_overlay(address.parse().unwrap(),prefix),Err(InvalidPublicNetwork));
        }
    }
    #[test]
    fn lan_api_and_transport_contracts_remain_distinct() {
        let policy = SourcePolicy::lan();
        for address in ["26.1.2.3","100.64.2.5","127.0.0.1","10.77.0.0","10.77.0.255"] {
            assert!(policy.contains_client(address.parse().unwrap()));
        }
        for address in ["0.0.0.0","224.0.0.1","::1"] {
            let address = address.parse().unwrap();
            assert!(!policy.contains_client(address));
            assert!(policy.admits_transport(address));
        }
    }
}
