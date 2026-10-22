using System.Text;

namespace Scbl.Server;

public static class RuntimeTemplates
{
    public static string EasyTier(ServerConfiguration config, ServerSecrets secrets)
    {
        // EasyTier treats a 0.0.0.0 listener as dual-stack when enable_ipv6 is true.
        // Adding a second [::] listener for the same protocol and port fails with
        // WSAEINVAL on Windows because both sockets cover the IPv6 endpoint.
        string list = string.Join(", ", new[]
        {
            $"wss://0.0.0.0:{config.GamePort}",
            $"quic://0.0.0.0:{config.GamePort}",
        }.Select(value => $"\"{value}\""));
        return $"""
            instance_name = "{config.EasyTierInstanceName}"
            instance_id = "{config.EasyTierInstanceId}"
            hostname = "{config.ServerId}"
            ipv4 = "{config.VirtualIp}/{config.VirtualPrefixLength}"
            dhcp = false
            listeners = [{list}]

            [network_identity]
            network_name = "{config.EasyTierNetworkName}"
            network_secret = "{secrets.NetworkSecret}"

            [flags]
            default_protocol = "quic"
            dev_name = "scbl0"
            enable_encryption = true
            enable_ipv6 = {config.EnableIpv6.ToString().ToLowerInvariant()}
            mtu = {config.Mtu}
            latency_first = false
            disable_p2p = true
            p2p_only = false
            lazy_p2p = false
            need_p2p = false
            relay_all_peer_rpc = true
            disable_relay_data = false
            disable_udp_hole_punching = true
            disable_tcp_hole_punching = true
            disable_sym_hole_punching = true
            disable_upnp = false
            enable_udp_broadcast_relay = true
            enable_kcp_proxy = false
            enable_quic_proxy = false
            relay_network_whitelist = "{config.EasyTierNetworkName}"
            """;
    }

    public static string Dedicated(ServerConfiguration config, byte[] ticketKey)
    {
        if (ticketKey.Length != 32) throw new ArgumentException("Ticket key must be 32 bytes.", nameof(ticketKey));
        string values = string.Join(",\n    ", ticketKey);
        return $"""
            services = ["sc_bl_auth", "onlineconfig", "content", "sc_bl_secure"]
            api_server = "{config.VirtualIp}:50051"

            [service.content]
            type = "content"
            listen = "{config.VirtualIp}:8000"
            [service.content.files]
            "/mp_balancing.ini" = "./data/mp_balancing.ini"

            [service.onlineconfig]
            type = "config"
            listen = "{config.VirtualIp}:80"
            [[service.onlineconfig.content]]
            Name = "SandboxUrl"
            Values = ["prudp:/address={config.VirtualIp};port=21126"]
            [[service.onlineconfig.content]]
            Name = "SandboxUrlWS"
            Values = ["{config.VirtualIp}:21126"]

            [service.sc_bl_auth]
            type = "authentication"
            access_key = "yl4NG7qZ"
            crypto_key = "CD&ML"
            listen = "{config.VirtualIp}:21126"
            vport = 1
            secure_server_addr = "{config.VirtualIp}:21127"
            ticket_key = [
                {values},
            ]
            [service.sc_bl_auth.settings]

            [service.sc_bl_secure]
            type = "secure"
            access_key = "yl4NG7qZ"
            crypto_key = "CD&ML"
            listen = "{config.VirtualIp}:21127"
            vport = 1
            ticket_key = [
                {values},
            ]
            [service.sc_bl_secure.settings]
            storage_host = "{config.VirtualIp}:8000"
            storage_path = "/mp_balancing.ini"

            [debug]
            mark_all_as_online = false
            force_joins = false
            """;
    }
}
