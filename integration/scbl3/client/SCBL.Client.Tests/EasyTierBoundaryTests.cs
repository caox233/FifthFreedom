using System.Diagnostics;
using System.Net;
using System.Text.Json;
using SplinterCellCNLauncher.Models;
using SplinterCellCNLauncher.Services;

namespace SCBL.Client.Tests;

public sealed class EasyTierBoundaryTests : IDisposable
{
    private const string Ipv4ConnectionId = "5519ba22-a8d8-429c-a86a-4b445de7b6ab";
    private const string Ipv6ConnectionId = "9589ab18-215c-4a9e-ab17-241aa3261618";
    private readonly string _root;

    public EasyTierBoundaryTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "scbl-easytier-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void PeerParsersExtractSupportedValuesAndFailClosed()
    {
        IReadOnlyList<string> ips = EasyTierCliParsers.ParseVirtualPeerIps(
            "[{\"ipv4\":\"10.66.0.9/24\"},{\"ipv4\":\"192.168.1.2\"}]");

        Assert.Equal(new[] { "10.66.0.9" }, ips);
        Assert.Empty(EasyTierCliParsers.ParseVirtualPeerIps("not-json"));

        EasyTierPeerPath? path = EasyTierCliParsers.ParsePeerPath(
            "[{\"ipv4\":\"10.66.0.9/24\",\"lat_ms\":\"12.4\",\"cost\":\"1\","
            + "\"tunnel_proto\":\"udp://1.2.3.4:5\",\"remote_addr\":\"1.2.3.4:5\"}]",
            "10.66.0.9");

        Assert.Null(path);
    }

    [Fact]
    public void VerbosePeerParserHonorsObjectDefaultConnectionBeforeLowerLatencyIpv4()
    {
        EasyTierPeerPath? path = EasyTierCliParsers.ParsePeerPath(
            BuildVerbosePeerJson(
                UuidParts(0x9589ab18u, 0x215c4a9eu, 0xab17241au, 0xa3261618u),
                ipv4LatencyMicroseconds: 800,
                ipv6LatencyMicroseconds: 5_000),
            PublicTunnelConfig.ServerVirtualIp);

        Assert.NotNull(path);
        Assert.Equal("WSS", path.TransportMode);
        Assert.Equal("IPv6", path.UnderlayAddressFamily);
        Assert.Equal(5, path.LatencyMs);
    }

    [Fact]
    public void VerbosePeerParserHonorsObjectDefaultConnectionBeforeLowerLatencyIpv6()
    {
        EasyTierPeerPath? path = EasyTierCliParsers.ParsePeerPath(
            BuildVerbosePeerJson(
                UuidParts(0x5519ba22u, 0xa8d8429cu, 0xa86a4b44u, 0x5de7b6abu),
                ipv4LatencyMicroseconds: 5_000,
                ipv6LatencyMicroseconds: 800),
            PublicTunnelConfig.ServerVirtualIp);

        Assert.NotNull(path);
        Assert.Equal("WSS", path.TransportMode);
        Assert.Equal("IPv4", path.UnderlayAddressFamily);
        Assert.Equal(5, path.LatencyMs);
    }

    [Fact]
    public void VerbosePeerParserKeepsStringDefaultConnectionCompatibility()
    {
        EasyTierPeerPath? path = EasyTierCliParsers.ParsePeerPath(
            BuildVerbosePeerJson(
                Ipv6ConnectionId,
                ipv4LatencyMicroseconds: 800,
                ipv6LatencyMicroseconds: 5_000),
            PublicTunnelConfig.ServerVirtualIp);

        Assert.NotNull(path);
        Assert.Equal("IPv6", path.UnderlayAddressFamily);
    }

    [Fact]
    public void VerbosePeerParserFailsClosedForInvalidDefaultObject()
    {
        EasyTierPeerPath? path = EasyTierCliParsers.ParsePeerPath(
            BuildVerbosePeerJson(
                new Dictionary<string, uint>
                {
                    ["part1"] = 0x9589ab18u,
                    ["part2"] = 0x215c4a9eu,
                    ["part3"] = 0xab17241au,
                },
                ipv4LatencyMicroseconds: 800,
                ipv6LatencyMicroseconds: 5_000),
            PublicTunnelConfig.ServerVirtualIp);

        Assert.Null(path);
    }

    [Fact]
    public void VerbosePeerParserFailsClosedWhenParsedDefaultConnectionIsMissing()
    {
        EasyTierPeerPath? path = EasyTierCliParsers.ParsePeerPath(
            BuildVerbosePeerJson(
                "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
                ipv4LatencyMicroseconds: 800,
                ipv6LatencyMicroseconds: 5_000),
            PublicTunnelConfig.ServerVirtualIp);

        Assert.Null(path);
    }

    [Fact]
    public void VerbosePeerParserReadsNestedIpv4EndpointWithoutTreatingJsonAsIpv6()
    {
        EasyTierPeerPath? path = EasyTierCliParsers.ParsePeerPath(
            BuildVerbosePeerJson(
                Ipv4ConnectionId,
                ipv4LatencyMicroseconds: 800,
                ipv6LatencyMicroseconds: 5_000,
                ipv4ResolvedRemoteAddress: new
                {
                    Ip = new { addr = "198.51.100.10:11010" },
                }),
            PublicTunnelConfig.ServerVirtualIp);

        Assert.NotNull(path);
        Assert.Equal("IPv4", path.UnderlayAddressFamily);
    }

    [Fact]
    public void VerbosePeerParserTreatsIpv4MappedSocketAddressAsIpv4()
    {
        EasyTierPeerPath? path = EasyTierCliParsers.ParsePeerPath(
            BuildVerbosePeerJson(
                UuidParts(0x5519ba22u, 0xa8d8429cu, 0xa86a4b44u, 0x5de7b6abu),
                ipv4LatencyMicroseconds: 5_000,
                ipv6LatencyMicroseconds: 800,
                ipv4ResolvedRemoteAddress: new
                {
                    url = "quic://[::ffff:198.51.100.10]:11012/",
                }),
            PublicTunnelConfig.ServerVirtualIp);

        Assert.NotNull(path);
        Assert.Equal("WSS", path.TransportMode);
        Assert.Equal("IPv4", path.UnderlayAddressFamily);
    }

    [Fact]
    public void VerbosePeerParserStillReportsNativeIpv6SocketAddressAsIpv6()
    {
        EasyTierPeerPath? path = EasyTierCliParsers.ParsePeerPath(
            BuildVerbosePeerJson(
                Ipv4ConnectionId,
                ipv4LatencyMicroseconds: 800,
                ipv6LatencyMicroseconds: 5_000,
                ipv4ResolvedRemoteAddress: new
                {
                    url = "quic://[2001:db8::10]:11012/",
                }),
            PublicTunnelConfig.ServerVirtualIp);

        Assert.NotNull(path);
        Assert.Equal("WSS", path.TransportMode);
        Assert.Equal("IPv6", path.UnderlayAddressFamily);
    }

    [Fact]
    public void VerbosePeerParserFailsClosedForUnknownStructuredEndpoint()
    {
        EasyTierPeerPath? path = EasyTierCliParsers.ParsePeerPath(
            BuildVerbosePeerJson(
                Ipv4ConnectionId,
                ipv4LatencyMicroseconds: 800,
                ipv6LatencyMicroseconds: 5_000,
                ipv4ResolvedRemoteAddress: new
                {
                    endpoint = new { host = "198.51.100.10", port = 11010 },
                }),
            PublicTunnelConfig.ServerVirtualIp);

        Assert.NotNull(path);
        Assert.Empty(path.UnderlayAddressFamily);
    }

    [Fact]
    public async Task PeerDiscoveryOmitsUnreachableRouteWithoutHidingReachablePeer()
    {
        using var service = new PeerProbeService((ip, _, _) => Task.FromResult(
            ip == "10.66.0.5"
                ? new PeerInfo
                {
                    Username = "reachable-player",
                    VirtualIp = ip,
                    Version = "test",
                    LatencyMs = 7,
                    IsReachable = true,
                }
                : null));

        IReadOnlyList<PeerInfo> peers = await service.DiscoverAsync(
            "10.66.0.2",
            "self",
            "test",
            new[] { "10.66.0.5", "10.66.0.8" },
            scanFallback: false);

        Assert.Equal(new[] { "10.66.0.2", "10.66.0.5" }, peers.Select(peer => peer.VirtualIp));
        Assert.DoesNotContain(peers, peer => peer.VirtualIp == "10.66.0.8");
    }

    [Fact]
    public void TomlBasicStringEscapesControlsAndRejectsInvalidUtf16()
    {
        string quoted = EasyTierConfigWriter.QuoteTomlBasicString(
            "a\\\"\b\t\n\f\r\u0001\u007f😀");

        Assert.Equal("\"a\\\\\\\"\\b\\t\\n\\f\\r\\u0001\\u007F😀\"", quoted);
        Assert.Throws<InvalidDataException>(
            () => EasyTierConfigWriter.QuoteTomlBasicString("\uD800"));
        Assert.Throws<InvalidDataException>(
            () => EasyTierConfigWriter.QuoteTomlBasicString("\uDC00"));
    }

    [Fact]
    public void ConfigWriterUsesOneIpv6QuicConnectionWithStagedIpv4Fallback()
    {
        var writer = new EasyTierConfigWriter(
            new FakeDnsResolver(new[]
            {
                IPAddress.Parse("203.0.113.10"),
                IPAddress.Parse("2001:db8::10")
            }),
            new FakeUnderlayAvailability(hasIpv6: true));

        var options = new EasyTierClientOptions(
            Guid.Empty.ToString(),
            "scbl-test\nname",
            WssPort: 11011,
            QuicPort: 11012,
            NetworkMode: ClientNetworkMode.Fast);
        EasyTierServerPeerPlan plan = writer.BuildServerPeerPlan("example.test:11010", options);
        string config = writer.BuildClientConfig(
            "example.test:11010",
            "fixed-test-secret",
            options,
            plan.PreferredPeerUris);

        Assert.Equal(
            new[] { "quic://[2001:db8::10]:11012" },
            plan.PreferredPeerUris);
        Assert.True(plan.PrefersIpv6);
        Assert.Equal(
            new[] { "quic://203.0.113.10:11012" },
            plan.Ipv4FallbackPeerUris);
        Assert.DoesNotContain("quic://example.test:11012", plan.PreferredPeerUris);
        Assert.DoesNotContain(
            plan.PreferredPeerUris.Concat(plan.Ipv4FallbackPeerUris),
            value => value.StartsWith("wss://", StringComparison.Ordinal));
        Assert.DoesNotContain(
            plan.PreferredPeerUris.Concat(plan.Ipv4FallbackPeerUris),
            value => value.StartsWith("udp://", StringComparison.Ordinal));
        Assert.Contains("uri = \"quic://[2001:db8::10]:11012\"", config, StringComparison.Ordinal);
        Assert.DoesNotContain("uri = \"quic://203.0.113.10:11012\"", config, StringComparison.Ordinal);
        Assert.DoesNotContain("example.test", config, StringComparison.Ordinal);
        Assert.Contains("dhcp = true", config, StringComparison.Ordinal);
        Assert.DoesNotContain("\nipv4 =", config, StringComparison.Ordinal);
        Assert.Contains("listeners = [\"udp://0.0.0.0:0\", \"udp://[::]:0\"]", config, StringComparison.Ordinal);
        Assert.DoesNotContain("tcp://0.0.0.0:0", config, StringComparison.Ordinal);
        Assert.Contains("default_protocol = \"quic\"", config, StringComparison.Ordinal);
        Assert.Contains("lazy_p2p = true", config, StringComparison.Ordinal);
        Assert.Contains("p2p_only = false", config, StringComparison.Ordinal);
        Assert.Contains("need_p2p = false", config, StringComparison.Ordinal);
        Assert.Contains("disable_relay_data = true", config, StringComparison.Ordinal);
        Assert.Contains("disable_udp_hole_punching = false", config, StringComparison.Ordinal);
        Assert.Contains("disable_tcp_hole_punching = true", config, StringComparison.Ordinal);
        Assert.Contains("network_name = \"scbl-test\\nname\"", config, StringComparison.Ordinal);
        Assert.Contains("enable_udp_broadcast_relay = true", config, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigWriterRejectsMissingServerDescriptorSecret()
    {
        var writer = new EasyTierConfigWriter(
            new FakeDnsResolver([IPAddress.Parse("203.0.113.10")]),
            new FakeUnderlayAvailability(hasIpv6: false));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            writer.BuildClientConfig(
                "example.test:11010",
                "",
                new EasyTierClientOptions(Guid.Empty.ToString(), "scbl-test")));

        Assert.Contains("server descriptor", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConfigWriterUsesOneIpv6WssConnectionWithStagedIpv4Fallback()
    {
        var writer = new EasyTierConfigWriter(
            new FakeDnsResolver(new[]
            {
                IPAddress.Parse("203.0.113.10"),
                IPAddress.Parse("2001:db8::10")
            }),
            new FakeUnderlayAvailability(hasIpv6: true));
        var options = new EasyTierClientOptions(
            Guid.Empty.ToString(),
            "scbl-test",
            WssPort: 11010,
            QuicPort: 11012,
            NetworkMode: ClientNetworkMode.Compatible);

        EasyTierServerPeerPlan plan = writer.BuildServerPeerPlan("example.test:11010", options);
        string config = writer.BuildClientConfig(
            "example.test:11010",
            "fixed-test-secret",
            options,
            plan.PreferredPeerUris);

        Assert.Equal(
            new[] { "wss://[2001:db8::10]:11010" },
            plan.PreferredPeerUris);
        Assert.True(plan.PrefersIpv6);
        Assert.Equal(
            new[] { "wss://203.0.113.10:11010" },
            plan.Ipv4FallbackPeerUris);
        Assert.DoesNotContain("wss://example.test:11010", plan.PreferredPeerUris);
        Assert.DoesNotContain(
            plan.PreferredPeerUris.Concat(plan.Ipv4FallbackPeerUris),
            value => value.StartsWith("quic://", StringComparison.Ordinal));
        Assert.DoesNotContain(
            plan.PreferredPeerUris.Concat(plan.Ipv4FallbackPeerUris),
            value => value.StartsWith("udp://", StringComparison.Ordinal));
        Assert.Contains("uri = \"wss://[2001:db8::10]:11010\"", config, StringComparison.Ordinal);
        Assert.DoesNotContain("uri = \"wss://203.0.113.10:11010\"", config, StringComparison.Ordinal);
        Assert.DoesNotContain("example.test", config, StringComparison.Ordinal);
        Assert.Contains("default_protocol = \"wss\"", config, StringComparison.Ordinal);
        Assert.Contains("disable_tcp_hole_punching = true", config, StringComparison.Ordinal);
    }

    [Fact]
    public void ServerPeerSelectionUsesOneExplicitIpv4PeerWithoutUsableIpv6()
    {
        var writer = new EasyTierConfigWriter(
            new FakeDnsResolver(new[]
            {
                IPAddress.Parse("2001:db8::10"),
                IPAddress.Parse("203.0.113.10")
            }),
            new FakeUnderlayAvailability(hasIpv6: false));
        var options = new EasyTierClientOptions(
            Guid.Empty.ToString(),
            "scbl-test",
            WssPort: 11010,
            QuicPort: 11012,
            NetworkMode: ClientNetworkMode.Fast);

        EasyTierServerPeerPlan plan = writer.BuildServerPeerPlan("example.test:11010", options);

        Assert.Equal(
            new[] { "quic://203.0.113.10:11012" },
            plan.PreferredPeerUris);
        Assert.Equal(
            new[] { "quic://203.0.113.10:11012" },
            plan.Ipv4FallbackPeerUris);
        Assert.False(plan.PrefersIpv6);

        string config = writer.BuildClientConfig(
            "example.test:11010",
            "fixed-test-secret",
            options,
            plan.PreferredPeerUris);
        Assert.DoesNotContain("example.test", config, StringComparison.Ordinal);
        Assert.Contains("listeners = [\"udp://0.0.0.0:0\"]", config, StringComparison.Ordinal);
        Assert.Contains("enable_ipv6 = false", config, StringComparison.Ordinal);
        Assert.DoesNotContain("udp://[::]:0", config, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartupFallsBackToIpv4WithoutChangingSelectedTransport()
    {
        string tools = Path.Combine(_root, "components", "network");
        Directory.CreateDirectory(tools);
        File.WriteAllBytes(Path.Combine(tools, "easytier-core.exe"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(tools, "easytier-cli.exe"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(tools, "Packet.dll"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(tools, "wintun.dll"), new byte[] { 1 });

        var factory = new FakeCoreProcessFactory(_ => new FakeCoreProcess());
        var snapshots = new FakeNetworkSnapshots(() => factory.StartCalls >= 2
            ? new[]
            {
                new EasyTierAdapterSnapshot(
                    PublicTunnelConfig.TunnelName,
                    new[] { "10.66.0.5" })
            }
            : Array.Empty<EasyTierAdapterSnapshot>());
        var service = new PublicTunnelService(
            new EasyTierConfigWriter(
                new FakeDnsResolver(new[]
                {
                    IPAddress.Parse("203.0.113.10"),
                    IPAddress.Parse("2001:db8::10")
                }),
                new FakeUnderlayAvailability(hasIpv6: true)),
            new EasyTierProfileStore(() => Path.Combine(_root, "profile.json")),
            new EasyTierCliClient(new FakeCommandRunner(
                new EasyTierCommandResult(0, string.Empty, string.Empty, false))),
            new EasyTierProcessSupervisor(factory),
            snapshots,
            ownedProcessProbe: () => false,
            ipv6PreferredWindow: _ => TimeSpan.FromMilliseconds(800),
            rpcPortalRelease: _ => Task.CompletedTask);
        var options = new EasyTierClientOptions(
            Guid.Empty.ToString(),
            "scbl-test",
            WssPort: 11010,
            QuicPort: 11012,
            NetworkMode: ClientNetworkMode.Compatible);

        try
        {
            string assignedIp = await service.StartAsync(
                _root,
                TimeSpan.FromSeconds(3),
                "example.test:11010",
                "fixed-test-secret",
                options);

            Assert.Equal("10.66.0.5", assignedIp);
            Assert.Equal(2, factory.StartCalls);
            Assert.Equal(2, factory.ConfigSnapshots.Count);
            Assert.True(service.Ipv6DisabledForLauncherSession);
            Assert.True(service.RuntimeUsesIpv4Fallback);
            Assert.Contains(
                "uri = \"wss://[2001:db8::10]:11010\"",
                factory.ConfigSnapshots[0],
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "uri = \"wss://203.0.113.10:11010\"",
                factory.ConfigSnapshots[0],
                StringComparison.Ordinal);
            Assert.DoesNotContain("example.test", factory.ConfigSnapshots[0], StringComparison.Ordinal);
            Assert.Contains(
                "uri = \"wss://203.0.113.10:11010\"",
                factory.ConfigSnapshots[1],
                StringComparison.Ordinal);
            Assert.DoesNotContain("example.test", factory.ConfigSnapshots[1], StringComparison.Ordinal);
            Assert.DoesNotContain("quic://", factory.ConfigSnapshots[1], StringComparison.Ordinal);
        }
        finally
        {
            service.Stop("test cleanup");
        }
    }

    [Fact]
    public async Task StartupKeepsIpv6OnlyWhenActualIpv6PathIsAlreadyConnected()
    {
        string tools = Path.Combine(_root, "components", "network");
        Directory.CreateDirectory(tools);
        File.WriteAllBytes(Path.Combine(tools, "easytier-core.exe"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(tools, "easytier-cli.exe"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(tools, "Packet.dll"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(tools, "wintun.dll"), new byte[] { 1 });

        var factory = new FakeCoreProcessFactory(_ => new FakeCoreProcess());
        var service = new PublicTunnelService(
            new EasyTierConfigWriter(
                new FakeDnsResolver(new[]
                {
                    IPAddress.Parse("203.0.113.10"),
                    IPAddress.Parse("2001:db8::10")
                }),
                new FakeUnderlayAvailability(hasIpv6: true)),
            new EasyTierProfileStore(() => Path.Combine(_root, "profile-ipv6.json")),
            new EasyTierCliClient(new FakeCommandRunner(
                new EasyTierCommandResult(
                    0,
                    BuildVerbosePeerJson(
                        Ipv6ConnectionId,
                        ipv4LatencyMicroseconds: 800,
                        ipv6LatencyMicroseconds: 5_000),
                    string.Empty,
                    false))),
            new EasyTierProcessSupervisor(factory),
            new FakeNetworkSnapshots(new[]
            {
                new EasyTierAdapterSnapshot(
                    PublicTunnelConfig.TunnelName,
                    new[] { "10.66.0.5" })
            }),
            ownedProcessProbe: () => false,
            ipv6PreferredWindow: _ => TimeSpan.FromMilliseconds(20),
            rpcPortalRelease: _ => Task.CompletedTask);
        var options = new EasyTierClientOptions(
            Guid.Empty.ToString(),
            "scbl-test",
            WssPort: 11010,
            QuicPort: 11012,
            NetworkMode: ClientNetworkMode.Compatible);

        try
        {
            string assignedIp = await service.StartAsync(
                _root,
                TimeSpan.FromSeconds(3),
                "example.test:11010",
                "fixed-test-secret",
                options);

            Assert.Equal("10.66.0.5", assignedIp);
            Assert.Equal(1, factory.StartCalls);
            Assert.Single(factory.ConfigSnapshots);
            Assert.Contains(
                "uri = \"wss://[2001:db8::10]:11010\"",
                factory.ConfigSnapshots[0],
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "uri = \"wss://203.0.113.10:11010\"",
                factory.ConfigSnapshots[0],
                StringComparison.Ordinal);
            Assert.DoesNotContain("example.test", factory.ConfigSnapshots[0], StringComparison.Ordinal);
        }
        finally
        {
            service.Stop("test cleanup");
        }
    }

    [Fact]
    public async Task ConfirmedIpv6RuntimeCanRestartSameTransportWithIpv4Fallback()
    {
        string tools = Path.Combine(_root, "components", "network");
        Directory.CreateDirectory(tools);
        File.WriteAllBytes(Path.Combine(tools, "easytier-core.exe"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(tools, "easytier-cli.exe"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(tools, "Packet.dll"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(tools, "wintun.dll"), new byte[] { 1 });

        var factory = new FakeCoreProcessFactory(_ => new FakeCoreProcess());
        var service = new PublicTunnelService(
            new EasyTierConfigWriter(
                new FakeDnsResolver(new[]
                {
                    IPAddress.Parse("203.0.113.10"),
                    IPAddress.Parse("2001:db8::10")
                }),
                new FakeUnderlayAvailability(hasIpv6: true)),
            new EasyTierProfileStore(() => Path.Combine(_root, "profile-runtime-fallback.json")),
            new EasyTierCliClient(new FakeCommandRunner(
                new EasyTierCommandResult(
                    0,
                    BuildVerbosePeerJson(
                        Ipv6ConnectionId,
                        ipv4LatencyMicroseconds: 800,
                        ipv6LatencyMicroseconds: 5_000),
                    string.Empty,
                    false))),
            new EasyTierProcessSupervisor(factory),
            new FakeNetworkSnapshots(new[]
            {
                new EasyTierAdapterSnapshot(
                    PublicTunnelConfig.TunnelName,
                    new[] { "10.66.0.5" })
            }),
            ownedProcessProbe: () => false,
            ipv6PreferredWindow: _ => TimeSpan.FromMilliseconds(20),
            rpcPortalRelease: _ => Task.CompletedTask);
        var options = new EasyTierClientOptions(
            Guid.Empty.ToString(),
            "scbl-test",
            WssPort: 11010,
            QuicPort: 11012,
            NetworkMode: ClientNetworkMode.Compatible);

        try
        {
            string initialIp = await service.StartAsync(
                _root,
                TimeSpan.FromSeconds(3),
                "example.test:11010",
                "fixed-test-secret",
                options);
            string? fallbackIp = await service.RestartWithIpv4FallbackAsync(
                _root,
                TimeSpan.FromSeconds(3),
                "example.test:11010",
                "fixed-test-secret",
                options);

            Assert.Equal("10.66.0.5", initialIp);
            Assert.Equal("10.66.0.5", fallbackIp);
            Assert.True(service.RuntimeUsesIpv4Fallback);
            Assert.Equal(2, factory.StartCalls);
            Assert.Equal(2, factory.ConfigSnapshots.Count);
            Assert.Contains(
                "uri = \"wss://[2001:db8::10]:11010\"",
                factory.ConfigSnapshots[0],
                StringComparison.Ordinal);
            Assert.Contains(
                "uri = \"wss://203.0.113.10:11010\"",
                factory.ConfigSnapshots[1],
                StringComparison.Ordinal);
            Assert.DoesNotContain("example.test", factory.ConfigSnapshots[1], StringComparison.Ordinal);
            Assert.Null(await service.RestartWithIpv4FallbackAsync(
                _root,
                TimeSpan.FromSeconds(3),
                "example.test:11010",
                "fixed-test-secret",
                options));
            Assert.Equal(2, factory.StartCalls);

            service.Stop("simulate later same-session rebuild");
            string sameSessionIp = await service.StartAsync(
                _root,
                TimeSpan.FromSeconds(3),
                "example.test:11010",
                "fixed-test-secret",
                options);

            Assert.Equal("10.66.0.5", sameSessionIp);
            Assert.True(service.Ipv6DisabledForLauncherSession);
            Assert.Equal(3, factory.StartCalls);
            Assert.Contains(
                "uri = \"wss://203.0.113.10:11010\"",
                factory.ConfigSnapshots[2],
                StringComparison.Ordinal);
            Assert.DoesNotContain("2001:db8::10", factory.ConfigSnapshots[2], StringComparison.Ordinal);
        }
        finally
        {
            service.Stop("test cleanup");
        }
    }

    [Fact]
    public async Task FastRuntimeFallbackKeepsQuicAndUsesOnlyResolvedIpv4()
    {
        string tools = Path.Combine(_root, "components", "network");
        Directory.CreateDirectory(tools);
        File.WriteAllBytes(Path.Combine(tools, "easytier-core.exe"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(tools, "easytier-cli.exe"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(tools, "Packet.dll"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(tools, "wintun.dll"), new byte[] { 1 });

        var factory = new FakeCoreProcessFactory(_ => new FakeCoreProcess());
        var service = new PublicTunnelService(
            new EasyTierConfigWriter(
                new FakeDnsResolver(new[]
                {
                    IPAddress.Parse("203.0.113.10"),
                    IPAddress.Parse("2001:db8::10")
                }),
                new FakeUnderlayAvailability(hasIpv6: true)),
            new EasyTierProfileStore(() => Path.Combine(_root, "profile-fast-runtime-fallback.json")),
            new EasyTierCliClient(new FakeCommandRunner(
                new EasyTierCommandResult(0, string.Empty, string.Empty, false))),
            new EasyTierProcessSupervisor(factory),
            new FakeNetworkSnapshots(new[]
            {
                new EasyTierAdapterSnapshot(
                    PublicTunnelConfig.TunnelName,
                    new[] { "10.66.0.5" })
            }),
            ownedProcessProbe: () => false,
            rpcPortalRelease: _ => Task.CompletedTask);
        var options = new EasyTierClientOptions(
            Guid.Empty.ToString(),
            "scbl-test",
            WssPort: 11010,
            QuicPort: 11012,
            NetworkMode: ClientNetworkMode.Fast);

        try
        {
            string? fallbackIp = await service.RestartWithIpv4FallbackAsync(
                _root,
                TimeSpan.FromSeconds(3),
                "example.test:11010",
                "fixed-test-secret",
                options);

            Assert.Equal("10.66.0.5", fallbackIp);
            Assert.True(service.RuntimeUsesIpv4Fallback);
            Assert.True(service.Ipv6DisabledForLauncherSession);
            Assert.Single(factory.ConfigSnapshots);
            Assert.Contains(
                "uri = \"quic://203.0.113.10:11012\"",
                factory.ConfigSnapshots[0],
                StringComparison.Ordinal);
            Assert.DoesNotContain("example.test", factory.ConfigSnapshots[0], StringComparison.Ordinal);
            Assert.DoesNotContain("wss://", factory.ConfigSnapshots[0], StringComparison.Ordinal);
            Assert.Contains("default_protocol = \"quic\"", factory.ConfigSnapshots[0], StringComparison.Ordinal);
        }
        finally
        {
            service.Stop("test cleanup");
        }
    }

    [Fact]
    public async Task FailedIpv6PathWithoutExplicitIpv4DisablesIpv6AndFailsClosedForTheSession()
    {
        string tools = Path.Combine(_root, "components", "network");
        Directory.CreateDirectory(tools);
        File.WriteAllBytes(Path.Combine(tools, "easytier-core.exe"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(tools, "easytier-cli.exe"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(tools, "Packet.dll"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(tools, "wintun.dll"), new byte[] { 1 });

        var factory = new FakeCoreProcessFactory(_ => new FakeCoreProcess());
        var service = new PublicTunnelService(
            new EasyTierConfigWriter(
                new FakeDnsResolver(new[] { IPAddress.Parse("2001:db8::10") }),
                new FakeUnderlayAvailability(hasIpv6: true)),
            new EasyTierProfileStore(() => Path.Combine(_root, "profile-no-ipv4.json")),
            new EasyTierCliClient(new FakeCommandRunner(
                new EasyTierCommandResult(0, string.Empty, string.Empty, false))),
            new EasyTierProcessSupervisor(factory),
            networkSnapshots: null,
            ownedProcessProbe: () => false,
            rpcPortalRelease: _ => Task.CompletedTask);
        var options = new EasyTierClientOptions(
            Guid.Empty.ToString(),
            "scbl-test",
            NetworkMode: ClientNetworkMode.Fast);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RestartWithIpv4FallbackAsync(
                _root,
                TimeSpan.FromSeconds(3),
                "example.test:11010",
                "fixed-test-secret",
                options));

        Assert.Contains("no explicit IPv4 fallback", error.Message, StringComparison.Ordinal);
        Assert.True(service.Ipv6DisabledForLauncherSession);
        Assert.Equal(0, factory.StartCalls);
    }

    [Fact]
    public void ReusedRuntimePresenceDebouncesTransientProbeButLifecycleProbeStaysStrict()
    {
        DateTimeOffset now = new(2026, 8, 8, 22, 2, 0, TimeSpan.Zero);
        bool ownedProcessPresent = true;
        var presence = new EasyTierRuntimePresenceTracker(
            () => now,
            TimeSpan.FromSeconds(5));
        var service = new PublicTunnelService(
            new EasyTierConfigWriter(new FakeDnsResolver(Array.Empty<IPAddress>())),
            new EasyTierProfileStore(() => Path.Combine(_root, "profile.json")),
            new EasyTierCliClient(new FakeCommandRunner(
                new EasyTierCommandResult(0, string.Empty, string.Empty, false))),
            new EasyTierProcessSupervisor(
                new FakeCoreProcessFactory(new FakeCoreProcess())),
            networkSnapshots: null,
            ownedProcessProbe: () => ownedProcessPresent,
            runtimePresenceTracker: presence);

        Assert.True(service.HasRunningTunnelClientProcess());
        Assert.True(service.HasContinuousRuntimePresence());

        ownedProcessPresent = false;
        Assert.False(service.HasDefinitiveRunningTunnelClientProcess());
        Assert.False(service.HasRunningTunnelClientProcess());
        Assert.True(service.HasContinuousRuntimePresence());

        now = now.AddSeconds(6);
        Assert.False(service.HasContinuousRuntimePresence());

        ownedProcessPresent = true;
        Assert.True(service.HasRunningTunnelClientProcess());
        Assert.True(service.HasContinuousRuntimePresence());
        service.Stop("test reset");
        ownedProcessPresent = false;
        Assert.False(service.HasRunningTunnelClientProcess());
        Assert.False(service.HasContinuousRuntimePresence());
    }

    [Fact]
    public void ProfileSignatureChangesWithSecret()
    {
        string launcherRoot = Path.Combine(_root, "launcher");
        Directory.CreateDirectory(Path.Combine(launcherRoot, "components", "network"));
        File.WriteAllText(
            Path.Combine(launcherRoot, "components", "network", "easytier-core.exe"),
            "core");
        var options = new EasyTierClientOptions(Guid.Empty.ToString(), "scbl-test");
        var store = new EasyTierProfileStore(() => Path.Combine(_root, "profile.json"));

        store.Write(
            launcherRoot,
            "203.0.113.10:11010",
            "secret-a",
            options,
            "10.66.0.4");

        Assert.True(store.Matches(
            launcherRoot,
            "203.0.113.10:11010",
            "secret-a",
            options));
        Assert.False(store.Matches(
            launcherRoot,
            "203.0.113.10:11010",
            "secret-b",
            options));

        var compatible = options with { NetworkMode = ClientNetworkMode.Compatible };
        Assert.False(store.Matches(
            launcherRoot,
            "203.0.113.10:11010",
            "secret-a",
            compatible));

        string profile = File.ReadAllText(Path.Combine(_root, "profile.json"));
        Assert.Contains("\"networkMode\": \"Fast\"", profile, StringComparison.Ordinal);
        Assert.Contains("\"serverIngressPolicy\": \"ipv4-only\"", profile, StringComparison.Ordinal);
        Assert.Contains("\"serverPeer\": \"quic://203.0.113.10:11010\"", profile, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeProfileRejectsReuseWhenResolvedPeerSetChangesAddressFamily()
    {
        string launcherRoot = Path.Combine(_root, "launcher-peer-set");
        Directory.CreateDirectory(Path.Combine(launcherRoot, "components", "network"));
        File.WriteAllText(
            Path.Combine(launcherRoot, "components", "network", "easytier-core.exe"),
            "core");
        var options = new EasyTierClientOptions(Guid.Empty.ToString(), "scbl-test");
        var store = new EasyTierProfileStore(() => Path.Combine(_root, "peer-set-profile.json"));
        string[] ipv6Preferred =
        [
            "quic://[2001:db8::10]:11012",
        ];
        string[] ipv4Only = ["quic://203.0.113.10:11012"];

        store.Write(
            launcherRoot,
            "example.test:11010",
            "secret-a",
            options,
            "10.66.0.4",
            activePeerUris: ipv6Preferred);

        Assert.True(store.Matches(
            launcherRoot,
            "example.test:11010",
            "secret-a",
            options,
            expectedPeerUris: ipv6Preferred));
        Assert.False(store.Matches(
            launcherRoot,
            "example.test:11010",
            "secret-a",
            options,
            expectedPeerUris: ipv4Only));
    }

    [Fact]
    public void Ipv4FallbackProfileCannotBeReusedAsIpv6PreferredRuntime()
    {
        string launcherRoot = Path.Combine(_root, "launcher-fallback");
        Directory.CreateDirectory(Path.Combine(launcherRoot, "components", "network"));
        File.WriteAllText(
            Path.Combine(launcherRoot, "components", "network", "easytier-core.exe"),
            "core");
        var options = new EasyTierClientOptions(Guid.Empty.ToString(), "scbl-test");
        var store = new EasyTierProfileStore(() => Path.Combine(_root, "fallback-profile.json"));

        store.Write(
            launcherRoot,
            "203.0.113.10:11010",
            "secret-a",
            options,
            "10.66.0.4",
            usesIpv4Fallback: true);

        Assert.True(store.Matches(
            launcherRoot,
            "203.0.113.10:11010",
            "secret-a",
            options,
            requireIpv4Fallback: true));
        Assert.False(store.Matches(
            launcherRoot,
            "203.0.113.10:11010",
            "secret-a",
            options,
            requireIpv4Fallback: false));
    }

    [Fact]
    public async Task CliClientUsesInjectedRunnerAndExpectedArguments()
    {
        var runner = new FakeCommandRunner(
            new EasyTierCommandResult(0, "{\"ipv4\":\"10.66.0.7/24\"}", "", false));
        var client = new EasyTierCliClient(runner);

        string result = await client.QueryNodeJsonAsync(
            Path.Combine(_root, "easytier-cli.exe"),
            TimeSpan.FromSeconds(1));
        string[] arguments = runner.StartInfo!.ArgumentList.ToArray();

        Assert.Contains("10.66.0.7", result, StringComparison.Ordinal);
        Assert.Contains("-o", arguments);
        Assert.Contains("json", arguments);
        Assert.Equal("node", arguments[^1]);
    }

    [Fact]
    public async Task CliClientCanCaptureVerboseRouteStateAtFailureTime()
    {
        var runner = new FakeCommandRunner(
            new EasyTierCommandResult(0, "{\"peer_routes\":[]}", "", false));
        var client = new EasyTierCliClient(runner);

        string result = await client.QueryRoutesJsonAsync(
            Path.Combine(_root, "easytier-cli.exe"),
            TimeSpan.FromSeconds(1));
        string[] arguments = runner.StartInfo!.ArgumentList.ToArray();

        Assert.Contains("peer_routes", result, StringComparison.Ordinal);
        Assert.Contains("-v", arguments);
        Assert.Contains("-o", arguments);
        Assert.Equal("route", arguments[^1]);
    }

    [Fact]
    public async Task CliTimeoutKillsWaitsAndDisposesProcess()
    {
        var process = new FakeCliProcess();
        var runner = new SystemEasyTierCommandRunner(new FakeCliProcessFactory(process));

        EasyTierCommandResult result = await runner.RunAsync(
            new ProcessStartInfo("easytier-cli.exe"),
            TimeSpan.FromMilliseconds(20));

        Assert.True(result.TimedOut);
        Assert.True(process.Killed);
        Assert.Equal(1, process.WaitCalls);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task CliExternalCancellationCleansUpThenPropagates()
    {
        var process = new FakeCliProcess();
        var runner = new SystemEasyTierCommandRunner(new FakeCliProcessFactory(process));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(
            new ProcessStartInfo("easytier-cli.exe"),
            TimeSpan.FromSeconds(1),
            cancellation.Token));

        Assert.True(process.Killed);
        Assert.Equal(1, process.WaitCalls);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task OverlayResolverUsesMatchingAdapterFallback()
    {
        var resolver = new OverlayAddressResolver(
            new EasyTierCliClient(new FakeCommandRunner(
                new EasyTierCommandResult(0, "", "", false))),
            new FakeNetworkSnapshots(new[]
            {
                new EasyTierAdapterSnapshot("unrelated", new[] { "10.66.0.8" }),
                new EasyTierAdapterSnapshot(
                    PublicTunnelConfig.TunnelName,
                    new[] { "10.66.0.6" })
            }));

        string address = await resolver.ResolveAsync(
            Path.Combine(_root, "missing-cli-root"),
            TimeSpan.FromMilliseconds(10));

        Assert.Equal("10.66.0.6", address);
        Assert.Equal(
            "10.66.0.5",
            OverlayAddressResolver.ExtractScblIpFromJson(
                "{\"nested\":\"address=10.66.0.5/24\"}"));
    }

    [Fact]
    public void CoreBeginReadFailureTerminatesAndUntracksProcess()
    {
        var process = new FakeCoreProcess
        {
            BeginReadFailure = new InvalidOperationException("injected")
        };
        var supervisor = new EasyTierProcessSupervisor(new FakeCoreProcessFactory(process));

        Assert.Throws<InvalidOperationException>(() => supervisor.Start(
            Path.Combine(_root, "easytier-core.exe"),
            Path.Combine(_root, "config.toml"),
            (_, _) => { }));

        Assert.True(process.Killed);
        Assert.Equal(1, process.WaitCalls);
        Assert.True(process.Disposed);
        Assert.False(supervisor.IsRunning);
    }

    [Fact]
    public void DuplicateCoreStartFailsBeforeLaunchingAnotherProcess()
    {
        var process = new FakeCoreProcess();
        var factory = new FakeCoreProcessFactory(process);
        var supervisor = new EasyTierProcessSupervisor(factory);
        supervisor.Start(
            Path.Combine(_root, "easytier-core.exe"),
            Path.Combine(_root, "config.toml"),
            (_, _) => { });

        Assert.Throws<InvalidOperationException>(() => supervisor.Start(
            Path.Combine(_root, "easytier-core.exe"),
            Path.Combine(_root, "config.toml"),
            (_, _) => { }));
        Assert.Equal(1, factory.StartCalls);
        Assert.True(supervisor.IsRunning);

        supervisor.Stop("test");
    }

    [Fact]
    public void CoreStopRejectsLateOutput()
    {
        var process = new FakeCoreProcess();
        var supervisor = new EasyTierProcessSupervisor(new FakeCoreProcessFactory(process));
        int calls = 0;
        supervisor.Start(
            Path.Combine(_root, "easytier-core.exe"),
            Path.Combine(_root, "config.toml"),
            (_, _) => calls++);

        process.Emit("started", false);
        supervisor.Stop("test");
        process.Emit("late", true);

        Assert.Equal(1, calls);
        Assert.True(process.Killed);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task CoreStopWaitsForInFlightObserverAndRejectsLateGeneration()
    {
        var process = new FakeCoreProcess();
        var supervisor = new EasyTierProcessSupervisor(new FakeCoreProcessFactory(process));
        using var observerEntered = new ManualResetEventSlim();
        using var releaseObserver = new ManualResetEventSlim();
        int calls = 0;
        supervisor.Start(
            Path.Combine(_root, "easytier-core.exe"),
            Path.Combine(_root, "config.toml"),
            (_, _) =>
            {
                Interlocked.Increment(ref calls);
                observerEntered.Set();
                releaseObserver.Wait(TimeSpan.FromSeconds(2));
            });

        Task callbackTask = Task.Run(() => process.Emit("blocking", false));
        Assert.True(observerEntered.Wait(TimeSpan.FromSeconds(1)));
        Task stopTask = Task.Run(() => supervisor.Stop("test-race"));
        try
        {
            await Task.Delay(50);
            Assert.False(stopTask.IsCompleted);
        }
        finally
        {
            releaseObserver.Set();
        }
        await Task.WhenAll(callbackTask, stopTask).WaitAsync(TimeSpan.FromSeconds(2));
        process.Emit("late", true);

        Assert.Equal(1, calls);
        Assert.False(supervisor.IsRunning);
    }

    [Fact]
    public void NetstatTimeoutKillsProcessAndFailsClosed()
    {
        var process = new FakeNetstatProcess();
        var runner = new SystemEasyTierNetstatCommandRunner(
            new FakeNetstatProcessFactory(process));

        EasyTierNetstatCommandResult result = runner.Run(TimeSpan.FromMilliseconds(10));

        Assert.False(result.Completed);
        Assert.True(process.Killed);
        Assert.Equal(2, process.WaitCalls);
        Assert.True(process.Disposed);
        Assert.Empty(EasyTierProcessSupervisor.GetTcpListenerProcessIds(
            15966,
            new FakeNetstatRunner(new EasyTierNetstatCommandResult(false, "ignored"))));
    }

    [Fact]
    public void NetstatParserAndOwnershipPolicySelectOnlyScblTargets()
    {
        const string output = " TCP    127.0.0.1:15966    0.0.0.0:0    LISTENING    1234\n"
            + " TCP    127.0.0.1:5000    0.0.0.0:0    LISTENING    9999";
        string packaged = Path.Combine(
            _root,
            "SCBL",
            "components",
            "network",
            "easytier-core.exe");

        Assert.Equal(
            new[] { 1234 },
            EasyTierNetstatParser.ParseTcpListenerProcessIds(output, 15966));
        Assert.True(EasyTierOwnedProcessPolicy.IsScblPackagedPath(
            packaged,
            Path.Combine(_root, "SCBL")));
        Assert.False(EasyTierOwnedProcessPolicy.IsScblPackagedPath(
            Path.Combine(_root, "unrelated", "easytier-core.exe"),
            Path.Combine(_root, "SCBL")));
    }

    [Fact]
    public void ManualServerReconnectFailureRequestsImmediateValidation()
    {
        const string line = "easytier::connector::manual: reconn_tasks done, "
            + "reconn result: Some(Err(AnyhowError(handshake timeout)))";

        Assert.True(PublicTunnelService.IsServerPathFailureSignal(line));
    }

    [Theory]
    [InlineData("easytier::peers: PeerNoConnectionError(peer=server)")]
    [InlineData("easytier::route: RouteError(None)")]
    [InlineData("sync_route_info failed: Timeout waiting for peer route")]
    [InlineData("pingpong loss too much, closing the connection")]
    public void AuthoritativeEasyTierPathLossRequestsImmediateValidation(string line)
    {
        Assert.True(PublicTunnelService.IsServerPathFailureSignal(line));
    }

    [Theory]
    [InlineData("RouteError(no route to peer)")]
    [InlineData("sync_route_info failed")]
    [InlineData("OSPF route timeout")]
    [InlineData("peer conn notified closed")]
    [InlineData("udp send syn retry")]
    [InlineData("checksum mismatch")]
    [InlineData("hole punching continues")]
    [InlineData("peer route healthy")]
    public void OrdinaryEasyTierNoiseDoesNotRequestRecovery(string line)
    {
        Assert.False(PublicTunnelService.IsServerPathFailureSignal(line));
    }

    [Theory]
    [InlineData("easytier::connector::manual: reconn_tasks done, reconn result: Some(Ok(peer=server))", true)]
    [InlineData("reconn_tasks done, reconn result: Some(Ok(peer=server))", false)]
    [InlineData("easytier::connector::manual: reconn_tasks done, reconn result: Some(Err(connect timeout))", false)]
    [InlineData("peer route healthy", false)]
    public void EasyTierReconnectCompletionRequestsImmediateValidation(
        string line,
        bool expected)
    {
        Assert.Equal(expected, PublicTunnelService.IsServerPathRecoverySignal(line));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static Dictionary<string, uint> UuidParts(
        uint part1,
        uint part2,
        uint part3,
        uint part4)
        => new()
        {
            ["part1"] = part1,
            ["part2"] = part2,
            ["part3"] = part3,
            ["part4"] = part4,
        };

    private static string BuildVerbosePeerJson(
        object defaultConnectionId,
        long ipv4LatencyMicroseconds,
        long ipv6LatencyMicroseconds,
        object? ipv4ResolvedRemoteAddress = null)
        => JsonSerializer.Serialize(new[]
        {
            new
            {
                route = new
                {
                    peer_id = 17,
                    ipv4_addr = new
                    {
                        address = new { addr = 172_097_537u },
                        network_length = 24,
                    },
                    next_hop_peer_id = 17,
                    cost = 1,
                    path_latency = 2,
                },
                peer = new
                {
                    default_conn_id = defaultConnectionId,
                    conns = new[]
                    {
                        new
                        {
                            conn_id = Ipv4ConnectionId,
                            stats = new { latency_us = ipv4LatencyMicroseconds },
                            tunnel = new
                            {
                                tunnel_type = "wss",
                                resolved_remote_addr = ipv4ResolvedRemoteAddress
                                    ?? new { url = "wss://198.51.100.10:11010/" },
                            },
                        },
                        new
                        {
                            conn_id = Ipv6ConnectionId,
                            stats = new { latency_us = ipv6LatencyMicroseconds },
                            tunnel = new
                            {
                                tunnel_type = "wss",
                                resolved_remote_addr = (object)new
                                {
                                    url = "wss://[2001:db8::10]:11010/",
                                },
                            },
                        },
                    },
                },
            },
        });

    private sealed class FakeCommandRunner : IEasyTierCommandRunner
    {
        private readonly EasyTierCommandResult _result;

        public FakeCommandRunner(EasyTierCommandResult result) => _result = result;
        public ProcessStartInfo? StartInfo { get; private set; }

        public Task<EasyTierCommandResult> RunAsync(
            ProcessStartInfo startInfo,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            StartInfo = startInfo;
            return Task.FromResult(_result);
        }
    }

    private sealed class FakeNetworkSnapshots : IEasyTierNetworkSnapshotProvider
    {
        private readonly Func<IReadOnlyList<EasyTierAdapterSnapshot>> _capture;

        public FakeNetworkSnapshots(IReadOnlyList<EasyTierAdapterSnapshot> snapshots)
            : this(() => snapshots)
        {
        }

        public FakeNetworkSnapshots(Func<IReadOnlyList<EasyTierAdapterSnapshot>> capture)
            => _capture = capture;

        public IReadOnlyList<EasyTierAdapterSnapshot> Capture() => _capture();
    }

    private sealed class FakeDnsResolver : IEasyTierDnsResolver
    {
        private readonly IReadOnlyList<IPAddress> _addresses;

        public FakeDnsResolver(IReadOnlyList<IPAddress> addresses) => _addresses = addresses;
        public IReadOnlyList<IPAddress> Resolve(string host) => _addresses;
    }

    private sealed class FakeUnderlayAvailability : IEasyTierUnderlayAvailability
    {
        private readonly bool _hasIpv6;

        public FakeUnderlayAvailability(bool hasIpv6) => _hasIpv6 = hasIpv6;

        public bool HasUsableIpv6() => _hasIpv6;
    }

    private sealed class FakeCoreProcessFactory : IEasyTierCoreProcessFactory
    {
        private readonly Func<int, IEasyTierCoreProcess> _createProcess;

        public FakeCoreProcessFactory(IEasyTierCoreProcess process)
            : this(_ => process)
        {
        }

        public FakeCoreProcessFactory(Func<int, IEasyTierCoreProcess> createProcess)
            => _createProcess = createProcess;

        public int StartCalls { get; private set; }
        public List<string> ConfigSnapshots { get; } = new();

        public IEasyTierCoreProcess Start(ProcessStartInfo startInfo)
        {
            StartCalls++;
            string configPath = startInfo.ArgumentList.Count >= 2
                ? startInfo.ArgumentList[1]
                : string.Empty;
            ConfigSnapshots.Add(File.Exists(configPath)
                ? File.ReadAllText(configPath)
                : string.Empty);
            return _createProcess(StartCalls);
        }
    }

    private sealed class FakeCoreProcess : IEasyTierCoreProcess
    {
        private Action<string?, bool>? _observer;

        public int Id => 42;
        public bool HasExited { get; private set; }
        public int ExitCode => 0;
        public bool Killed { get; private set; }
        public bool Disposed { get; private set; }
        public int WaitCalls { get; private set; }
        public Exception? BeginReadFailure { get; init; }

        public void BeginRead(Action<string?, bool> observer)
        {
            if (BeginReadFailure != null)
                throw BeginReadFailure;
            _observer = observer;
        }

        public void Emit(string line, bool isError) => _observer?.Invoke(line, isError);

        public void KillTree()
        {
            Killed = true;
            HasExited = true;
        }

        public bool WaitForExit(int milliseconds)
        {
            WaitCalls++;
            return true;
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeCliProcessFactory : IEasyTierCliProcessFactory
    {
        private readonly IEasyTierCliProcess _process;

        public FakeCliProcessFactory(IEasyTierCliProcess process) => _process = process;
        public IEasyTierCliProcess Start(ProcessStartInfo startInfo) => _process;
    }

    private sealed class FakeCliProcess : IEasyTierCliProcess
    {
        public int ExitCode => 0;
        public bool Killed { get; private set; }
        public int WaitCalls { get; private set; }
        public bool Disposed { get; private set; }

        public Task<string> ReadOutputAsync() => Task.FromResult(string.Empty);
        public Task<string> ReadErrorAsync() => Task.FromResult(string.Empty);
        public Task WaitForExitAsync(CancellationToken cancellationToken)
            => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        public void KillTree() => Killed = true;

        public bool WaitForExit(int milliseconds)
        {
            WaitCalls++;
            return true;
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeNetstatProcessFactory : IEasyTierNetstatProcessFactory
    {
        private readonly IEasyTierNetstatProcess _process;

        public FakeNetstatProcessFactory(IEasyTierNetstatProcess process) => _process = process;
        public IEasyTierNetstatProcess Start() => _process;
    }

    private sealed class FakeNetstatProcess : IEasyTierNetstatProcess
    {
        public bool Killed { get; private set; }
        public int WaitCalls { get; private set; }
        public bool Disposed { get; private set; }

        public Task<string> ReadOutputAsync() => Task.FromResult(string.Empty);
        public bool WaitForExit(int milliseconds) => ++WaitCalls > 1;
        public void KillTree() => Killed = true;
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeNetstatRunner : IEasyTierNetstatCommandRunner
    {
        private readonly EasyTierNetstatCommandResult _result;

        public FakeNetstatRunner(EasyTierNetstatCommandResult result) => _result = result;
        public EasyTierNetstatCommandResult Run(TimeSpan timeout) => _result;
    }
}
