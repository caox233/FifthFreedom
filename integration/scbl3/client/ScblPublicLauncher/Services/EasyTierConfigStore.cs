using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SplinterCellCNLauncher.Models;

namespace SplinterCellCNLauncher.Services;

internal interface IEasyTierDnsResolver
{
    IReadOnlyList<IPAddress> Resolve(string host);
}

internal sealed class SystemEasyTierDnsResolver : IEasyTierDnsResolver
{
    public IReadOnlyList<IPAddress> Resolve(string host) => Dns.GetHostAddresses(host);
}

internal interface IEasyTierUnderlayAvailability
{
    bool HasUsableIpv6();
}

internal sealed record EasyTierServerPeerPlan(
    IReadOnlyList<string> PreferredPeerUris,
    IReadOnlyList<string> Ipv4FallbackPeerUris,
    bool PrefersIpv6)
{
    public bool HasIpv4Fallback => Ipv4FallbackPeerUris.Count > 0;
}

internal sealed class SystemEasyTierUnderlayAvailability : IEasyTierUnderlayAvailability
{
    public bool HasUsableIpv6()
    {
        try
        {
            foreach (NetworkInterface networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.OperationalStatus != OperationalStatus.Up
                    || networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback
                        or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                IPInterfaceProperties properties = networkInterface.GetIPProperties();
                bool hasGlobalAddress = properties.UnicastAddresses.Any(item =>
                    item.Address.AddressFamily == AddressFamily.InterNetworkV6
                    && !IPAddress.IsLoopback(item.Address)
                    && !item.Address.IsIPv6LinkLocal
                    && !item.Address.IsIPv6Multicast
                    && !item.Address.IsIPv6SiteLocal);
                bool hasIpv6Gateway = properties.GatewayAddresses.Any(item =>
                    item.Address.AddressFamily == AddressFamily.InterNetworkV6
                    && !item.Address.Equals(IPAddress.IPv6Any));
                if (hasGlobalAddress && hasIpv6Gateway)
                    return true;
            }
        }
        catch (Exception ex)
        {
            LogService.Info("IPv6 underlay availability probe skipped: " + ex.GetType().Name);
        }
        return false;
    }
}

internal sealed class EasyTierConfigWriter
{
    private readonly IEasyTierDnsResolver _dnsResolver;
    private readonly IEasyTierUnderlayAvailability _underlayAvailability;

    public EasyTierConfigWriter(
        IEasyTierDnsResolver? dnsResolver = null,
        IEasyTierUnderlayAvailability? underlayAvailability = null)
    {
        _dnsResolver = dnsResolver ?? new SystemEasyTierDnsResolver();
        _underlayAvailability = underlayAvailability
            ?? new SystemEasyTierUnderlayAvailability();
    }

    public void WriteClientConfig(
        string path,
        string publicEndpoint,
        string tunnelSecret,
        EasyTierClientOptions options,
        IReadOnlyList<string>? serverPeerUris = null)
    {
        string toml = BuildClientConfig(
            publicEndpoint,
            tunnelSecret,
            options,
            serverPeerUris);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, toml, new UTF8Encoding(false));
    }

    internal string BuildClientConfig(
        string publicEndpoint,
        string tunnelSecret,
        EasyTierClientOptions options,
        IReadOnlyList<string>? serverPeerUris = null)
    {
        string instanceId = Guid.TryParse(options.InstanceId, out Guid parsed)
            ? parsed.ToString("D")
            : Guid.NewGuid().ToString("D");
        string networkName = string.IsNullOrWhiteSpace(options.NetworkName)
            ? PublicTunnelConfig.EasyTierNetworkName
            : options.NetworkName.Trim();
        string endpoint = PublicTunnelConfig.NormalizePublicEndpoint(publicEndpoint);
        string secret = PublicTunnelConfig.NormalizeTunnelSecret(tunnelSecret);
        if (string.IsNullOrWhiteSpace(secret))
            throw new InvalidOperationException(
                "The current server descriptor did not provide an EasyTier network secret.");
        string hostname = "scbl-" + Environment.MachineName.Trim().Replace(' ', '-');
        bool disableUdpHolePunching = !options.EnableP2P;
        IReadOnlyList<string> peerUris = serverPeerUris
            ?? BuildServerPeerPlan(endpoint, options).PreferredPeerUris;
        if (peerUris.Count == 0)
            throw new InvalidDataException("EasyTier server peer plan is empty.");
        bool enableIpv6Underlay = peerUris.Any(ContainsIpv6LiteralHost);
        string listeners = enableIpv6Underlay
            ? "[\"udp://0.0.0.0:0\", \"udp://[::]:0\"]"
            : "[\"udp://0.0.0.0:0\"]";
        string defaultProtocol = options.NetworkMode == ClientNetworkMode.Compatible
            ? "wss"
            : "quic";

        string peers = string.Join(
            "\n\n",
            peerUris.Select(uri =>
                $"[[peer]]\nuri = {QuoteTomlBasicString(uri)}"));
        string toml = $"""
instance_name = {QuoteTomlBasicString(PublicTunnelConfig.EasyTierInstanceName)}
instance_id = {QuoteTomlBasicString(instanceId)}
hostname = {QuoteTomlBasicString(hostname)}
dhcp = true
listeners = {listeners}

[network_identity]
network_name = {QuoteTomlBasicString(networkName)}
network_secret = {QuoteTomlBasicString(secret)}

{peers}

[flags]
default_protocol = "{defaultProtocol}"
dev_name = {QuoteTomlBasicString(PublicTunnelConfig.TunnelName)}
bind_device = true
enable_encryption = true
# No virtual IPv6 address is assigned to the game overlay. Underlay IPv6 is
# enabled only when this runtime actually has an explicit IPv6 server peer.
enable_ipv6 = {enableIpv6Underlay.ToString().ToLowerInvariant()}
mtu = {PublicTunnelConfig.Mtu}
latency_first = {options.LatencyFirst.ToString().ToLowerInvariant()}
disable_p2p = {(!options.EnableP2P).ToString().ToLowerInvariant()}
p2p_only = false
# Reuse EasyTier's native relay-first topology: ordinary player peers start
# through the server relay and are upgraded to direct P2P only after real
# traffic. The server advertises disable_p2p=true and remains on its fixed
# QUIC/WSS listener instead of becoming a hole-punch target.
lazy_p2p = true
need_p2p = false
relay_all_peer_rpc = true
# Ordinary clients keep control-plane participation but never forward another player's data.
disable_relay_data = true
disable_udp_hole_punching = {disableUdpHolePunching.ToString().ToLowerInvariant()}
disable_tcp_hole_punching = true
disable_sym_hole_punching = {disableUdpHolePunching.ToString().ToLowerInvariant()}
disable_upnp = {disableUdpHolePunching.ToString().ToLowerInvariant()}
enable_udp_broadcast_relay = {options.EnableUdpBroadcastRelay.ToString().ToLowerInvariant()}
enable_kcp_proxy = false
enable_quic_proxy = false
relay_network_whitelist = {QuoteTomlBasicString(networkName)}
""";
        LogService.Info("EasyTier peer entries written: " + string.Join(", ", peerUris));
        return toml.Replace("\r\n", "\n");
    }

    internal static string QuoteTomlBasicString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var escaped = new StringBuilder(value.Length + 2);
        escaped.Append('"');
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            switch (character)
            {
                case '\\':
                    escaped.Append("\\\\");
                    break;
                case '"':
                    escaped.Append("\\\"");
                    break;
                case '\b':
                    escaped.Append("\\b");
                    break;
                case '\t':
                    escaped.Append("\\t");
                    break;
                case '\n':
                    escaped.Append("\\n");
                    break;
                case '\f':
                    escaped.Append("\\f");
                    break;
                case '\r':
                    escaped.Append("\\r");
                    break;
                default:
                    if (character <= '\u001f' || character == '\u007f')
                    {
                        escaped.Append("\\u");
                        escaped.Append(((int)character).ToString("X4"));
                    }
                    else if (char.IsHighSurrogate(character))
                    {
                        if (index + 1 >= value.Length
                            || !char.IsLowSurrogate(value[index + 1]))
                        {
                            throw new InvalidDataException(
                                "TOML basic string contains an unpaired high surrogate.");
                        }
                        escaped.Append(character);
                        escaped.Append(value[++index]);
                    }
                    else if (char.IsLowSurrogate(character))
                    {
                        throw new InvalidDataException(
                            "TOML basic string contains an unpaired low surrogate.");
                    }
                    else
                    {
                        escaped.Append(character);
                    }
                    break;
            }
        }
        escaped.Append('"');
        return escaped.ToString();
    }

    internal EasyTierServerPeerPlan BuildServerPeerPlan(
        string publicEndpoint,
        EasyTierClientOptions options)
    {
        string host = PublicTunnelConfig.GetEndpointHost(publicEndpoint);
        int wssPort = options.WssPort is > 0 and <= 65535
            ? options.WssPort
            : PublicTunnelConfig.DefaultWssPort;
        int quicPort = options.QuicPort is > 0 and <= 65535
            ? options.QuicPort
            : PublicTunnelConfig.DefaultQuicPort;
        var preferredHosts = new List<string>();
        var fallbackHosts = new List<string>();
        if (IPAddress.TryParse(host, out IPAddress? literal))
        {
            preferredHosts.Add(literal.ToString());
            if (literal.AddressFamily == AddressFamily.InterNetwork)
                fallbackHosts.Add(literal.ToString());
        }
        else
        {
            try
            {
                IPAddress[] resolved = _dnsResolver.Resolve(host)
                    .Where(address => !IPAddress.IsLoopback(address)
                        && !address.Equals(IPAddress.Any)
                        && !address.Equals(IPAddress.IPv6Any))
                    .Distinct()
                    .ToArray();
                IPAddress[] ipv6Addresses = resolved
                    .Where(address => address.AddressFamily == AddressFamily.InterNetworkV6)
                    .ToArray();
                IPAddress[] ipv4Addresses = resolved
                    .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
                    .ToArray();
                bool preferIpv6 = _underlayAvailability.HasUsableIpv6()
                    && ipv6Addresses.Length > 0;
                if (preferIpv6)
                {
                    // Every URI is a manual EasyTier connection. Multiple addresses for
                    // the same server node create competing route-sync sessions, so one
                    // runtime generation receives exactly one server address.
                    preferredHosts.Add(ipv6Addresses[0].ToString());
                    if (ipv4Addresses.Length > 0)
                        fallbackHosts.Add(ipv4Addresses[0].ToString());
                    LogService.Info(
                        "EasyTier server peer selection uses one IPv6 server connection with a staged IPv4 fallback.");
                }
                else
                {
                    if (ipv4Addresses.Length > 0)
                    {
                        preferredHosts.Add(ipv4Addresses[0].ToString());
                        fallbackHosts.Add(ipv4Addresses[0].ToString());
                    }
                    // Do not add the hostname beside an already resolved IPv4
                    // literal. EasyTier treats the two URLs as separate manual
                    // connections even when they reach the same server node,
                    // which repeatedly resets that node's route-sync session.
                    if (preferredHosts.Count == 0)
                        preferredHosts.Add(host);
                }
            }
            catch (Exception ex)
            {
                LogService.Warning("EasyTier dual-stack DNS expansion skipped: " + ex.Message);
                preferredHosts.Add(host);
            }
        }

        return new EasyTierServerPeerPlan(
            BuildPeerUris(preferredHosts, options, wssPort, quicPort),
            BuildPeerUris(fallbackHosts, options, wssPort, quicPort),
            preferredHosts.Any(hostValue =>
                IPAddress.TryParse(hostValue, out IPAddress? address)
                && address.AddressFamily == AddressFamily.InterNetworkV6));
    }

    private static IReadOnlyList<string> BuildPeerUris(
        IEnumerable<string> hosts,
        EasyTierClientOptions options,
        int wssPort,
        int quicPort)
    {
        var uris = new List<string>();
        foreach (string candidateHost in hosts.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string endpoint = options.NetworkMode == ClientNetworkMode.Compatible
                ? PublicTunnelConfig.BuildEndpoint(candidateHost, wssPort)
                : PublicTunnelConfig.BuildEndpoint(candidateHost, quicPort);
            string protocol = options.NetworkMode == ClientNetworkMode.Compatible ? "wss" : "quic";
            uris.Add(protocol + "://" + endpoint);
        }
        return uris.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static bool ContainsIpv6LiteralHost(string peerUri)
    {
        if (!Uri.TryCreate(peerUri, UriKind.Absolute, out Uri? uri))
            return false;
        string host = uri.Host.Trim('[', ']');
        return IPAddress.TryParse(host, out IPAddress? address)
            && address.AddressFamily == AddressFamily.InterNetworkV6;
    }

    internal static string BuildCanonicalServerPeerUri(
        string publicEndpoint,
        EasyTierClientOptions options)
        => options.NetworkMode == ClientNetworkMode.Compatible
            ? PublicTunnelConfig.ToWssPeerUrl(publicEndpoint, options.WssPort)
            : PublicTunnelConfig.ToQuicPeerUrl(publicEndpoint, options.QuicPort);
}

internal sealed class EasyTierProfileStore
{
    internal const int RuntimeProfileRevision = 16;
    private readonly Func<string> _profilePath;

    public EasyTierProfileStore(Func<string>? profilePath = null)
        => _profilePath = profilePath ?? (() => Path.Combine(
            LogService.PersistentDataDirectory,
            "network",
            "runtime-profile.json"));

    public bool Matches(
        string launcherBaseDir,
        string publicEndpoint,
        string tunnelSecret,
        EasyTierClientOptions options,
        bool requireIpv4Fallback = false,
        IReadOnlyList<string>? expectedPeerUris = null)
    {
        string path = _profilePath();
        if (!File.Exists(path))
            return false;
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
        string recorded = document.RootElement.TryGetProperty("signature", out JsonElement signature)
            ? signature.GetString() ?? string.Empty
            : string.Empty;
        return recorded.Equals(
            ComputeSignature(
                launcherBaseDir,
                publicEndpoint,
                tunnelSecret,
                options,
                requireIpv4Fallback,
                expectedPeerUris),
            StringComparison.OrdinalIgnoreCase);
    }

    public void Write(
        string launcherBaseDir,
        string publicEndpoint,
        string tunnelSecret,
        EasyTierClientOptions options,
        string assignedIp,
        bool usesIpv4Fallback = false,
        IReadOnlyList<string>? activePeerUris = null)
    {
        string path = _profilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string json = JsonSerializer.Serialize(new
        {
            revision = RuntimeProfileRevision,
            signature = ComputeSignature(
                launcherBaseDir,
                publicEndpoint,
                tunnelSecret,
                options,
                usesIpv4Fallback,
                activePeerUris),
            assignedIp,
            addressMode = "dhcp",
            networkMode = ClientNetworkModeNames.ToName(options.NetworkMode),
            serverIngressPolicy = usesIpv4Fallback
                ? "ipv4-fallback"
                : activePeerUris?.Any(EasyTierConfigWriter.ContainsIpv6LiteralHost) == true
                    ? "ipv6-preferred"
                    : "ipv4-only",
            serverPeer = EasyTierConfigWriter.BuildCanonicalServerPeerUri(publicEndpoint, options),
            writtenAt = DateTimeOffset.Now
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json, new UTF8Encoding(false));
    }

    internal static string ComputeSignature(
        string launcherBaseDir,
        string publicEndpoint,
        string tunnelSecret,
        EasyTierClientOptions options,
        bool usesIpv4Fallback = false,
        IReadOnlyList<string>? activePeerUris = null)
    {
        string corePath = EasyTierProcessSupervisor.GetCorePath(launcherBaseDir);
        string coreHash = File.Exists(corePath)
            ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(corePath)))
            : "missing";
        string material = string.Join("\n", new[]
        {
            RuntimeProfileRevision.ToString(),
            coreHash,
            PublicTunnelConfig.NormalizePublicEndpoint(publicEndpoint),
            PublicTunnelConfig.NormalizeTunnelSecret(tunnelSecret),
            options.InstanceId?.Trim() ?? string.Empty,
            options.NetworkName?.Trim() ?? string.Empty,
            options.StableRelayMode.ToString(),
            options.EnableP2P.ToString(),
            options.LatencyFirst.ToString(),
            options.EnableUdpBroadcastRelay.ToString(),
            options.ForceGameVirtualAdapter.ToString(),
            options.WssPort.ToString(),
            options.QuicPort.ToString(),
            ClientNetworkModeNames.ToName(options.NetworkMode),
            EasyTierConfigWriter.BuildCanonicalServerPeerUri(publicEndpoint, options),
            usesIpv4Fallback ? "ipv4-fallback" : "ipv6-preferred",
            string.Join(",", (activePeerUris ?? Array.Empty<string>())
                .Select(value => value.Trim())
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)),
            "dual-stack-underlay",
            "server-path-isolated-lazy-player-p2p-ipv6-first-ipv4-fallback",
            PublicTunnelConfig.Mtu.ToString()
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    public bool ValidateDynamicDhcpConfig(
        string configPath,
        out bool? broadcastConfigured,
        out string message)
    {
        broadcastConfigured = null;
        message = string.Empty;
        if (!File.Exists(configPath))
        {
            message = "EasyTier runtime configuration file is missing.";
            return false;
        }

        string config = File.ReadAllText(configPath, Encoding.UTF8);
        bool dhcpEnabled = Regex.IsMatch(
            config,
            @"(?m)^\s*dhcp\s*=\s*true\s*$",
            RegexOptions.IgnoreCase);
        bool staticIpv4Present = Regex.IsMatch(
            config,
            @"(?m)^\s*ipv4\s*=",
            RegexOptions.IgnoreCase);
        if (!dhcpEnabled || staticIpv4Present)
        {
            message = "EasyTier is not running in pure DHCP mode.";
            return false;
        }

        broadcastConfigured = Regex.IsMatch(
            config,
            @"(?m)^\s*enable_udp_broadcast_relay\s*=\s*true\s*$",
            RegexOptions.IgnoreCase);
        if (broadcastConfigured != true)
        {
            message = "UDP broadcast relay is not enabled in the generated EasyTier configuration.";
            return false;
        }

        message = "EasyTier dynamic DHCP configuration is valid.";
        return true;
    }
}
