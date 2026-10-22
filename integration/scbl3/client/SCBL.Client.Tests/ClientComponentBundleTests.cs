using SplinterCellCNLauncher.ApplicationLayer;
using SplinterCellCNLauncher.Models;
using SplinterCellCNLauncher.Services;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace SCBL.Client.Tests;

public sealed class ClientComponentBundleTests
{
    private static readonly Uri ManifestUri = new(
        "https://updates.example.test:18443/scbl-server.json");
    private const string ManifestSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void Bundle_endpoint_is_fixed_and_same_origin()
    {
        Uri endpoint = ClientComponentUpdateService.BuildBundleUri(ManifestUri);

        Assert.Equal("https://updates.example.test:18443/components/bundles/v1", endpoint.AbsoluteUri);
        Assert.Equal(ManifestUri.Scheme, endpoint.Scheme);
        Assert.Equal(ManifestUri.Host, endpoint.Host);
        Assert.Equal(ManifestUri.Port, endpoint.Port);
    }

    [Fact]
    public void Valid_bundle_installs_every_declared_component_into_existing_cache_shape()
    {
        using var fixture = new BundleFixture();
        string archive = fixture.CreateArchive();

        IReadOnlyList<string> installed = ClientComponentUpdateService.InstallBundleArchiveForTest(
            archive,
            fixture.Manifest,
            "stable",
            ManifestSha,
            fixture.CacheRoot);

        Assert.Equal(["hooks", "updater"], installed);
        fixture.AssertInstalled("hooks", "uplay_r1_loader.dll");
        fixture.AssertInstalled("updater", "SCBL.Updater.exe");
    }

    [Theory]
    [InlineData(BundleFault.CorruptPayload)]
    [InlineData(BundleFault.MissingEntry)]
    [InlineData(BundleFault.DuplicateEntry)]
    [InlineData(BundleFault.ExtraEntry)]
    [InlineData(BundleFault.WrongChannel)]
    public void Invalid_bundle_is_rejected_before_it_can_seed_component_cache(BundleFault fault)
    {
        using var fixture = new BundleFixture();
        string archive = fixture.CreateArchive(fault);

        Assert.ThrowsAny<Exception>(() => ClientComponentUpdateService.InstallBundleArchiveForTest(
            archive,
            fixture.Manifest,
            "stable",
            ManifestSha,
            fixture.CacheRoot));
        Assert.Empty(Directory.GetFiles(fixture.CacheRoot, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Unsupported_or_limited_bundle_api_falls_back_once(HttpStatusCode status)
    {
        using var fixture = new BundleFixture();
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(status)));
        using var http = new HttpClient(handler);
        var service = CreateService(http);

        bool bundled = await service.TryPrefetchBundleForTestAsync(
            fixture.Manifest,
            "stable",
            ManifestUri,
            ManifestSha,
            fixture.CacheRoot);

        Assert.False(bundled);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Bundle_timeout_falls_back_once()
    {
        using var fixture = new BundleFixture();
        var handler = new StubHandler((_, _) => throw new TaskCanceledException("simulated timeout"));
        using var http = new HttpClient(handler);
        var service = CreateService(http);

        bool bundled = await service.TryPrefetchBundleForTestAsync(
            fixture.Manifest,
            "stable",
            ManifestUri,
            ManifestSha,
            fixture.CacheRoot);

        Assert.False(bundled);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Corrupt_bundle_response_falls_back_once_without_partial_cache()
    {
        using var fixture = new BundleFixture();
        byte[] invalidZip = [1, 2, 3, 4];
        var handler = new StubHandler((request, _) =>
        {
            Assert.Equal(new Uri("https://updates.example.test:18443/components/bundles/v1"), request.RequestUri);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(invalidZip)
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var service = CreateService(http);

        bool bundled = await service.TryPrefetchBundleForTestAsync(
            fixture.Manifest,
            "stable",
            ManifestUri,
            ManifestSha,
            fixture.CacheRoot);

        Assert.False(bundled);
        Assert.Equal(1, handler.Calls);
        Assert.Empty(Directory.GetFiles(fixture.CacheRoot, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Valid_bundle_response_uses_bundle_without_fallback()
    {
        using var fixture = new BundleFixture();
        byte[] archive = File.ReadAllBytes(fixture.CreateArchive());
        var handler = new StubHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(archive)
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var service = CreateService(http);

        bool bundled = await service.TryPrefetchBundleForTestAsync(
            fixture.Manifest,
            "stable",
            ManifestUri,
            ManifestSha,
            fixture.CacheRoot);

        Assert.True(bundled);
        Assert.Equal(1, handler.Calls);
        fixture.AssertInstalled("hooks", "uplay_r1_loader.dll");
        fixture.AssertInstalled("updater", "SCBL.Updater.exe");
    }

    private static ClientComponentUpdateService CreateService(HttpClient http)
        => new(
            new LauncherRuntimeContext(ClientUpdateChannel.Stable),
            httpClient: http);

    public enum BundleFault
    {
        None,
        CorruptPayload,
        MissingEntry,
        DuplicateEntry,
        ExtraEntry,
        WrongChannel
    }

    private sealed class BundleFixture : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "scbl-component-bundle-" + Guid.NewGuid().ToString("N"));
        private readonly Dictionary<string, (string FileName, byte[] Payload)> _payloads = new()
        {
            ["hooks"] = ("uplay_r1_loader.dll", "hooks payload"u8.ToArray()),
            ["updater"] = ("SCBL.Updater.exe", "updater payload"u8.ToArray())
        };

        public BundleFixture()
        {
            Directory.CreateDirectory(_root);
            CacheRoot = Path.Combine(_root, "cache");
            Directory.CreateDirectory(CacheRoot);
            Manifest = new ClientComponentManifest
            {
                SchemaVersion = 2,
                Channel = "stable",
                Components = _payloads.ToDictionary(
                    pair => pair.Key,
                    pair => new ClientComponentDefinition
                    {
                        Version = "1.0.0",
                        Sha256 = Hash(pair.Value.Payload),
                        Size = pair.Value.Payload.Length,
                        Url = $"/components/artifacts/{pair.Key}/1.0.0/{pair.Value.FileName}",
                        UpdateMode = pair.Key == "hooks" ? "before-game-start" : "next-launch",
                        Required = true
                    },
                    StringComparer.OrdinalIgnoreCase),
                Files = new Dictionary<string, ClientComponentDefinition>(StringComparer.OrdinalIgnoreCase)
            };
        }

        public ClientComponentManifest Manifest { get; }
        public string CacheRoot { get; }

        public string CreateArchive(BundleFault fault = BundleFault.None)
        {
            string path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
            using ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create);
            var items = _payloads.Select(pair => new ClientComponentBundleManifestItem
            {
                Name = pair.Key,
                Sha256 = Manifest.Components[pair.Key].Sha256,
                Size = pair.Value.Payload.Length,
                Path = $"components/{pair.Key}/{pair.Value.FileName}"
            }).ToList();
            var bundleManifest = new ClientComponentBundleManifest
            {
                SchemaVersion = 1,
                Channel = fault == BundleFault.WrongChannel ? "test" : "stable",
                ManifestSha256 = ManifestSha,
                Components = items
            };
            WriteEntry(
                archive,
                "bundle-manifest.json",
                JsonSerializer.SerializeToUtf8Bytes(bundleManifest));
            foreach ((string name, (string fileName, byte[] payload)) in _payloads)
            {
                if (fault == BundleFault.MissingEntry && name == "hooks")
                    continue;
                byte[] content = fault == BundleFault.CorruptPayload && name == "hooks"
                    ? payload.Concat(new byte[] { 0xff }).ToArray()
                    : payload;
                string entryPath = $"components/{name}/{fileName}";
                WriteEntry(archive, entryPath, content);
                if (fault == BundleFault.DuplicateEntry && name == "hooks")
                    WriteEntry(archive, entryPath, payload);
            }
            if (fault == BundleFault.ExtraEntry)
                WriteEntry(archive, "components/extra/payload.bin", [1]);
            return path;
        }

        public void AssertInstalled(string name, string fileName)
        {
            string path = Path.Combine(CacheRoot, name, "1.0.0", fileName);
            Assert.True(File.Exists(path));
            Assert.Equal(Manifest.Components[name].Sha256, Hash(File.ReadAllBytes(path)));
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }

        private static void WriteEntry(ZipArchive archive, string name, byte[] payload)
        {
            ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Fastest);
            using Stream stream = entry.Open();
            stream.Write(payload);
        }

        private static string Hash(byte[] payload)
            => Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response)
        : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return response(request, cancellationToken);
        }
    }
}
