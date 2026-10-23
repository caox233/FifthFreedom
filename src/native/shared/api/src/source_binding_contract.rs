//! Socket constraints are not an overlay ownership or encryption capability.
use std::net::{Ipv4Addr, SocketAddrV4};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct SocketBinding {
    source: Ipv4Addr,
    index: u32,
    luid: u64,
    destination: SocketAddrV4,
}

impl SocketBinding {
    pub fn new(source: Ipv4Addr, index: u32, luid: u64, destination: SocketAddrV4) -> Option<Self> {
        let unicast = |ip: Ipv4Addr| !ip.is_unspecified() && !ip.is_multicast() && !ip.is_broadcast();
        (unicast(source) && unicast(*destination.ip()) && destination.port() != 0 && index != 0 && luid != 0)
            .then_some(Self { source, index, luid, destination })
    }
    pub fn source(self) -> Ipv4Addr { self.source }
    pub fn index(self) -> u32 { self.index }
    pub fn luid(self) -> u64 { self.luid }
    pub fn destination(self) -> SocketAddrV4 { self.destination }
    pub fn matches(self, index: u32, luid: u64, source: Ipv4Addr, up: bool, preferred: bool) -> bool {
        index == self.index && luid == self.luid && source == self.source && up && preferred
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn invalid_or_wildcard_inputs_cannot_form_a_binding() {
        let source = Ipv4Addr::LOCALHOST;
        let destination = SocketAddrV4::new(source, 1);
        assert!(SocketBinding::new(source, 0, 1, destination).is_none());
        assert!(SocketBinding::new(source, 1, 0, destination).is_none());
        assert!(SocketBinding::new(source, 1, 1, SocketAddrV4::new(source, 0)).is_none());
        for ip in [Ipv4Addr::UNSPECIFIED, Ipv4Addr::BROADCAST, Ipv4Addr::new(224, 1, 1, 1)] {
            assert!(SocketBinding::new(ip, 1, 1, destination).is_none());
            assert!(SocketBinding::new(source, 1, 1, SocketAddrV4::new(ip, 1)).is_none());
        }
    }
    #[test]
    fn index_luid_address_and_status_must_all_remain_exact() {
        let ip = Ipv4Addr::new(10, 66, 0, 10);
        let binding = SocketBinding::new(ip, 4, 8, SocketAddrV4::new(ip, 1234)).unwrap();
        assert!(binding.matches(4, 8, ip, true, true));
        for (index, luid, address, up, preferred) in [
            (5, 8, ip, true, true), (4, 9, ip, true, true),
            (4, 8, Ipv4Addr::LOCALHOST, true, true), (4, 8, ip, false, true), (4, 8, ip, true, false),
        ] { assert!(!binding.matches(index, luid, address, up, preferred)); }
    }
}
