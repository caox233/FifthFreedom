#[path = "../../../../docs/dependency-patches/easytier-2.6.4-windows-no-packet/source/windows_interfaces.rs"]
mod production;

#[cfg(test)]
mod tests {
    use super::production::collect;
    use network_interface::{Addr, Error, NetworkInterface, V4IfAddr, V6IfAddr};
    use std::net::{IpAddr, Ipv4Addr};

    fn iface(ip: &str, mac: Option<&str>) -> NetworkInterface {
        let mut value = NetworkInterface::new_afinet("synthetic", ip.parse().unwrap(), None, None, 71, false);
        value.mac_addr = mac.map(str::to_owned);
        value
    }

    #[test]
    fn real_native_records_preserve_metadata_and_subnet() {
        let mut native = iface("192.0.2.5", Some("02:00:00:00:00:01"));
        native.addr = vec![Addr::V4(V4IfAddr { ip: "192.0.2.5".parse().unwrap(),
            netmask: Some("255.255.255.0".parse().unwrap()), broadcast: Some("192.0.2.255".parse().unwrap()) })];
        let result = collect(Ok(vec![native]));
        assert_eq!((&result[0].name, result[0].index), (&"synthetic".to_string(), 71));
        assert_eq!(result[0].mac.unwrap().to_string(), "02:00:00:00:00:01");
        assert_eq!(result[0].ips[0].ip(), IpAddr::V4("192.0.2.5".parse().unwrap()));
        assert_eq!(result[0].ips[0].prefix(), 24);
        assert!(result[0].allows_discovery());
    }

    #[test]
    fn missing_masks_keep_exact_host_prefixes_for_both_families() {
        let mut native = iface("192.0.2.5", Some("02:00:00:00:00:01"));
        native.addr.push(Addr::V6(V6IfAddr { ip: "2001:db8::5".parse().unwrap(), netmask: None, broadcast: None }));
        let result = collect(Ok(vec![native]));
        assert_eq!(result[0].ips.iter().map(|ip| ip.prefix()).collect::<Vec<_>>(), [32, 128]);
        assert_eq!(result[0].ips[1].ip(), "2001:db8::5".parse::<IpAddr>().unwrap());
    }

    #[test]
    fn invalid_netmask_drops_only_that_address() {
        let mut native = iface("192.0.2.5", Some("02:00:00:00:00:01"));
        native.addr.push(Addr::V4(V4IfAddr { ip: "192.0.2.6".parse().unwrap(),
            netmask: Some(Ipv4Addr::new(255, 0, 255, 0)), broadcast: None }));
        let result = collect(Ok(vec![native]));
        assert_eq!(result[0].ips.len(), 1);
        assert!(result[0].allows_discovery());
    }

    #[test]
    fn missing_zero_or_malformed_mac_never_becomes_discoverable() {
        for mac in [None, Some("00:00:00:00:00:00"), Some("invalid")] {
            assert!(!collect(Ok(vec![iface("192.0.2.5", mac)]))[0].allows_discovery());
        }
    }

    #[test]
    fn loopback_unspecified_and_multicast_are_not_discovery_addresses() {
        for ip in ["127.0.0.1", "0.0.0.0", "224.0.0.1"] {
            assert!(!collect(Ok(vec![iface(ip, Some("02:00:00:00:00:01"))]))[0].allows_discovery());
        }
        for ip in ["::1", "::", "ff02::1"] {
            let mut native = iface("0.0.0.0", Some("02:00:00:00:00:01"));
            native.addr = vec![Addr::V6(V6IfAddr { ip: ip.parse().unwrap(), netmask: None, broadcast: None })];
            assert!(!collect(Ok(vec![native]))[0].allows_discovery());
        }
    }

    #[test]
    fn original_flags_zero_behavior_does_not_invent_link_health() {
        let mut native = iface("192.0.2.5", Some("02:00:00:00:00:01"));
        native.internal = true; // Previous Windows conversion ignored this, with flags=0.
        native.addr.push(Addr::V6(V6IfAddr { ip: "fe80::1".parse().unwrap(), netmask: None, broadcast: None }));
        let result = collect(Ok(vec![native]));
        assert!(result[0].allows_discovery());
        assert_eq!(result[0].ips.len(), 2); // Later IPCollector still filters IPv6 link-local.
    }

    #[test]
    fn enumeration_failure_or_empty_input_returns_no_synthetic_interface() {
        assert!(collect(Err(Error::GetIfAddrsError("synthetic failure".into(), 5))).is_empty());
        assert!(collect(Ok(Vec::new())).is_empty());
        let mut native = iface("192.0.2.5", Some("02:00:00:00:00:01"));
        native.addr.clear();
        assert!(!collect(Ok(vec![native]))[0].allows_discovery());
    }

    #[test]
    fn retained_pnet_packet_codec_does_not_need_capture_features() {
        use pnet::packet::{ipv4::MutableIpv4Packet, Packet};
        let mut bytes = [0u8; 20];
        let mut packet = MutableIpv4Packet::new(&mut bytes).unwrap();
        packet.set_version(4);
        packet.set_header_length(5);
        packet.set_source("192.0.2.1".parse().unwrap());
        assert_eq!(packet.get_source(), "192.0.2.1".parse::<Ipv4Addr>().unwrap());
        assert_eq!(packet.packet()[0], 0x45);
    }
}
