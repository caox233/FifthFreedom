using SplinterCellCNLauncher.Models;
using SplinterCellCNLauncher.Services;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace SCBL.Client.Tests;

public sealed class ServerDescriptorClientTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "scbl-server-descriptor-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Profiles_select_independent_stable_and_test_servers()
    {
        var settings = new LauncherSettings
        {
            PublicEndpoint = "stable.example:11010",
            PublicUpdatePort = 18080,
            TestPublicEndpoint = "test.example:11011",
            TestPublicUpdatePort = 18081
        };

        ServerConnectionProfile stable = ServerConnectionProfiles.Resolve(
            settings,
            ClientUpdateChannel.Stable);
        ServerConnectionProfile test = ServerConnectionProfiles.Resolve(
            settings,
            ClientUpdateChannel.Test);

        Assert.Equal("http://stable.example:18080/scbl-server.json", stable.DescriptorUri.AbsoluteUri);
        Assert.Equal("http://test.example:18081/scbl-server.json", test.DescriptorUri.AbsoluteUri);
    }

    [Fact]
    public async Task Live_descriptor_is_validated_and_cached_atomically()
    {
        Directory.CreateDirectory(_directory);
        string cache = Path.Combine(_directory, "stable.json");
        byte[] payload = Descriptor("stable");
        using var http = new HttpClient(new SequenceHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            }));
        var profile = new ServerConnectionProfile(
            ClientUpdateChannel.Stable,
            "stable.example:11010",
            18080);
        var client = new ServerDescriptorClient(profile, http, cache);

        ServerDescriptorSnapshot snapshot = await client.GetAsync();

        Assert.Equal(ServerDescriptorSource.Live, snapshot.Source);
        Assert.Equal("stable", snapshot.Descriptor.Server.Mode);
        Assert.Equal("3.0.0", snapshot.Descriptor.Client.Version);
        Assert.Equal(payload, File.ReadAllBytes(cache));
        Assert.False(File.Exists(cache + ".tmp"));
    }

    [Fact]
    public async Task Network_failure_uses_only_a_valid_same_server_mode_cache()
    {
        Directory.CreateDirectory(_directory);
        string cache = Path.Combine(_directory, "test.json");
        await File.WriteAllBytesAsync(cache, Descriptor("test"));
        using var http = new HttpClient(new SequenceHandler(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var profile = new ServerConnectionProfile(
            ClientUpdateChannel.Test,
            "test.example:11011",
            18081);

        ServerDescriptorSnapshot snapshot = await new ServerDescriptorClient(profile, http, cache)
            .GetAsync();

        Assert.Equal(ServerDescriptorSource.Cache, snapshot.Source);
        Assert.Equal("test", snapshot.Descriptor.Server.Mode);
    }

    [Fact]
    public async Task Mismatched_server_mode_fails_closed_without_overwriting_cache()
    {
        Directory.CreateDirectory(_directory);
        string cache = Path.Combine(_directory, "stable.json");
        byte[] before = Descriptor("stable");
        await File.WriteAllBytesAsync(cache, before);
        using var http = new HttpClient(new SequenceHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Descriptor("test"))
            }));
        var profile = new ServerConnectionProfile(
            ClientUpdateChannel.Stable,
            "stable.example:11010",
            18080);

        ServerDescriptorSnapshot snapshot = await new ServerDescriptorClient(profile, http, cache)
            .GetAsync();

        Assert.Equal(ServerDescriptorSource.Cache, snapshot.Source);
        Assert.Equal(before, File.ReadAllBytes(cache));
    }

    [Fact]
    public void Unsafe_component_url_is_rejected()
    {
        string json = Encoding.UTF8.GetString(Descriptor("stable"))
            .Replace(
                "/components/artifacts/launcher.zip",
                "/components/artifacts/../escape.zip",
                StringComparison.Ordinal);
        var profile = new ServerConnectionProfile(
            ClientUpdateChannel.Stable,
            "stable.example:11010",
            18080);

        Assert.Throws<InvalidDataException>(() =>
            ServerDescriptorClient.ParseAndValidate(Encoding.UTF8.GetBytes(json), profile));
    }

    [Theory]
    [InlineData("/components/%2e%2e/escape.zip")]
    [InlineData("//other.example/launcher.zip")]
    [InlineData("/components/launcher.zip?version=3")]
    public void Encoded_or_ambiguous_component_url_is_rejected(string unsafeUrl)
    {
        string json = Encoding.UTF8.GetString(Descriptor("stable"))
            .Replace(
                "/components/artifacts/launcher.zip",
                unsafeUrl,
                StringComparison.Ordinal);
        var profile = new ServerConnectionProfile(
            ClientUpdateChannel.Stable,
            "stable.example:11010",
            18080);

        Assert.Throws<InvalidDataException>(() =>
            ServerDescriptorClient.ParseAndValidate(Encoding.UTF8.GetBytes(json), profile));
    }

    [Fact]
    public void Cross_server_update_origin_is_rejected()
    {
        string json = Encoding.UTF8.GetString(Descriptor("stable"))
            .Replace(
                "http://stable.example:18080/",
                "http://other.example:18080/",
                StringComparison.Ordinal);
        var profile = new ServerConnectionProfile(
            ClientUpdateChannel.Stable,
            "stable.example:11010",
            18080);

        Assert.Throws<InvalidDataException>(() =>
            ServerDescriptorClient.ParseAndValidate(Encoding.UTF8.GetBytes(json), profile));
    }

    [Theory]
    [InlineData("components")]
    [InlineData("transports")]
    public void Missing_or_ambiguous_required_descriptor_state_fails_closed(string target)
    {
        JsonNode root = JsonNode.Parse(Descriptor("stable"))!;
        if (target == "components")
            root["client"]!["components"] = null;
        else
            root["network"]!["transports"] = new JsonArray("quic", "quic");
        var profile = new ServerConnectionProfile(
            ClientUpdateChannel.Stable,
            "stable.example:11010",
            18080);

        Assert.Throws<InvalidDataException>(() =>
            ServerDescriptorClient.ParseAndValidate(
                Encoding.UTF8.GetBytes(root.ToJsonString()),
                profile));
    }

    [Fact]
    public void Default_test_profile_uses_the_independent_test_ports()
    {
        ServerConnectionProfile profile = ServerConnectionProfiles.Resolve(
            new LauncherSettings(),
            ClientUpdateChannel.Test);

        Assert.Equal(PublicTunnelConfig.DefaultTestPublicEndpoint, profile.PublicEndpoint);
        Assert.Equal(PublicTunnelConfig.DefaultTestPublicUpdatePort, profile.UpdatePort);
    }

    [Fact]
    public void Descriptor_cache_is_scoped_to_the_exact_server_endpoint()
    {
        var first = new ServerConnectionProfile(
            ClientUpdateChannel.Stable,
            "first.example:11010",
            18080);
        var second = new ServerConnectionProfile(
            ClientUpdateChannel.Stable,
            "second.example:11010",
            18080);

        string firstPath = ServerDescriptorClient.BuildDefaultCachePath(first);
        string secondPath = ServerDescriptorClient.BuildDefaultCachePath(second);

        Assert.NotEqual(firstPath, secondPath);
        Assert.Contains("scbl-server-stable-", Path.GetFileName(firstPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Descriptor_connection_prefers_ipv6_then_falls_back_to_ipv4()
    {
        IPAddress ipv6 = IPAddress.Parse("2001:db8::10");
        IPAddress ipv4 = IPAddress.Parse("192.0.2.10");
        var attempts = new List<IPAddress>();

        await using Stream connected = await DualStackHttpClient.ConnectPreferredAddressAsync(
            new DnsEndPoint("server.example", 18080),
            CancellationToken.None,
            (_, _) => Task.FromResult(new[] { ipv4, ipv6 }),
            (address, _, _) =>
            {
                attempts.Add(address);
                if (address.AddressFamily == AddressFamily.InterNetworkV6)
                    throw new SocketException((int)SocketError.HostUnreachable);
                return ValueTask.FromResult<Stream>(new MemoryStream());
            });

        Assert.Equal(new[] { ipv6, ipv4 }, attempts);
    }

    private static byte[] Descriptor(string mode)
        => Encoding.UTF8.GetBytes(
            $$"""
            {
              "schemaVersion": 1,
              "generatedAt": "2026-08-22T00:00:00Z",
              "server": { "id": "scbl-{{mode}}-01", "mode": "{{mode}}" },
              "client": {
                "version": "3.0.0",
                "updateBaseUrl": "http://{{mode}}.example:{{(mode == "test" ? 18081 : 18080)}}/",
                "componentGeneration": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "components": {
                  "launcher": {
                    "version": "3.0.0",
                    "sha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                    "size": 123,
                    "url": "/components/artifacts/launcher.zip",
                    "updateMode": "restart-launcher",
                    "required": true
                  }
                },
                "files": {},
                "fullPackage": null
              },
              "network": {
                "publicHost": "server.example",
                "useOriginHost": false,
                "publicPort": 11010,
                "transports": ["quic", "wss"],
                "networkName": "scbl-public",
                "secret": "test-secret",
                "mtu": 1380,
                "enableIpv6": true
              },
              "controlPlane": { "address": "10.66.0.1", "port": 19080 },
              "dedicated": {
                "address": "10.66.0.1",
                "configPort": 80,
                "authPort": 50051,
                "gamePort": 21126,
                "protocol": "prudp"
              },
              "announcements": {
                "active": null,
                "startup": null,
                "update": null
              }
            }
            """);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch
        {
        }
    }

    private sealed class SequenceHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal("scbl-server.json", Path.GetFileName(request.RequestUri!.AbsolutePath));
            return Task.FromResult(_responses.Dequeue());
        }
    }
}
