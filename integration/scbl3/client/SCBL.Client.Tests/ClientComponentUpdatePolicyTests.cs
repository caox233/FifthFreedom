using SplinterCellCNLauncher.ApplicationLayer;
using SplinterCellCNLauncher.Models;
using SplinterCellCNLauncher.Services;
using System.Security.Cryptography;

namespace SCBL.Client.Tests;

public sealed class ClientComponentUpdatePolicyTests
{
    private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void Component_source_requires_the_selected_server_descriptor()
    {
        var service = new ClientComponentUpdateService(
            new LauncherRuntimeContext(ClientUpdateChannel.Stable));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            service.PrepareSelectedChannelUpdateAsync().GetAwaiter().GetResult());

        Assert.Contains("服务器描述", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Matching_hash_with_different_version_requires_download()
        => Assert.False(ClientComponentUpdateService.InstalledComponentSatisfiesCandidate(
            "hooks", "2.0.0.11", HashA, "2.0.0.10", HashA));

    [Theory]
    [InlineData("2.0.0.11", HashB, "2.0.0.11", HashA)]
    [InlineData("9.0.0", HashA, "1.0.0", HashB)]
    [InlineData("2.0.0", "", "2.0.0", HashA)]
    public void Missing_or_different_hash_requires_download(
        string currentVersion,
        string currentHash,
        string candidateVersion,
        string candidateHash)
        => Assert.False(ClientComponentUpdateService.InstalledComponentSatisfiesCandidate(
            "hooks", currentVersion, currentHash, candidateVersion, candidateHash));

    [Fact]
    public void Pending_updates_preserve_order_and_copy_bilingual_notes()
    {
        ClientComponentManifest manifest = ManifestWith(
            ("hooks", Definition("uplay_r1_loader.dll", "3.0.1", ["  修复邀请  "], ["  Fix invites  "])),
            ("launcher", Definition("SCBL.exe", "3.0.2", ["改进更新"], ["Improve updates"])));

        IReadOnlyList<PendingComponentUpdate> pending =
            ClientComponentUpdateService.CollectPendingComponentUpdates(
                manifest,
                new Dictionary<string, (string Version, string Sha256)>(),
                new Uri("http://server.example:18080/scbl-server.json"));

        Assert.Equal(new[] { "hooks", "launcher" }, pending.Select(item => item.Name));
        Assert.Equal(new[] { "修复邀请" }, pending[0].ReleaseNotesZh);
        Assert.Equal(new[] { "Fix invites" }, pending[0].ReleaseNotesEn);
    }

    [Fact]
    public void Release_notes_are_bounded()
    {
        string exactBoundary = string.Concat(Enumerable.Repeat("界", 240));
        Assert.Equal(
            6,
            ClientComponentUpdateService.NormalizeReleaseNotes(
                "hooks",
                new ClientComponentReleaseNotes
                {
                    Zh = Enumerable.Repeat(exactBoundary, 6).ToList(),
                    En = Enumerable.Repeat("note", 6).ToList()
                }).Zh.Count);

        Assert.Throws<InvalidDataException>(() =>
            ClientComponentUpdateService.NormalizeReleaseNotes(
                "hooks",
                new ClientComponentReleaseNotes { Zh = [exactBoundary + "界"], En = ["note"] }));
    }

    [Fact]
    public async Task Prepared_update_binds_the_descriptor_component_generation()
    {
        string receiptPath = TemporaryPath("installed-components.json");
        try
        {
            ServerDescriptorSnapshot snapshot = Snapshot(HashA, "3.0.1", HashB);
            var service = new ClientComponentUpdateService(
                new LauncherRuntimeContext(ClientUpdateChannel.Stable),
                getServerDescriptorAsync: _ => Task.FromResult(snapshot));
            using (InstalledComponentReceiptService.UseReceiptPathForTest(receiptPath))
            {
                PreparedClientComponentUpdate prepared = await service.PrepareSelectedChannelUpdateAsync();

                Assert.Equal(HashA, prepared.ComponentGeneration);
                PendingComponentUpdate pending = Assert.Single(prepared.PendingUpdates);
                Assert.Equal("hooks", pending.Name);
                Assert.Equal("3.0.1", pending.Version);
            }
        }
        finally
        {
            TryDeleteDirectory(Path.GetDirectoryName(receiptPath)!);
        }
    }

    [Fact]
    public async Task Reconcile_rejects_descriptor_generation_drift_before_downloading()
    {
        string receiptPath = TemporaryPath("installed-components.json");
        int calls = 0;
        try
        {
            var service = new ClientComponentUpdateService(
                new LauncherRuntimeContext(ClientUpdateChannel.Stable),
                getServerDescriptorAsync: _ => Task.FromResult(
                    ++calls == 1
                        ? Snapshot(HashA, "3.0.1", HashB)
                        : Snapshot(HashB, "3.0.2", HashA)));
            using (InstalledComponentReceiptService.UseReceiptPathForTest(receiptPath))
            {
                PreparedClientComponentUpdate prepared = await service.PrepareSelectedChannelUpdateAsync();

                await Assert.ThrowsAsync<ClientComponentManifestChangedException>(() =>
                    service.ReconcileSelectedChannelAsync(prepared.ComponentGeneration));
            }
        }
        finally
        {
            TryDeleteDirectory(Path.GetDirectoryName(receiptPath)!);
        }
    }

    [Fact]
    public void Full_package_receipt_is_used_only_while_files_match()
    {
        string root = TemporaryDirectory();
        try
        {
            string installed = CreatePackagedHooks(root, "3.0.4", out string sha);
            IReadOnlyDictionary<string, (string Version, string Sha256)> verified =
                ClientComponentUpdateService.ReadPackagedComponentVersions(root);
            Assert.Equal(("3.0.4", sha), verified["hooks"]);

            File.WriteAllText(installed, "tampered");
            Assert.DoesNotContain(
                "hooks",
                ClientComponentUpdateService.ReadPackagedComponentVersions(root).Keys);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Valid_descriptor_can_adopt_matching_fresh_package_hooks()
    {
        string root = TemporaryDirectory();
        string receiptPath = Path.Combine(root, "runtime", "installed_components.json");
        try
        {
            string installed = CreatePackagedHooks(root, "3.0.4", out string sha);
            long size = new FileInfo(installed).Length;
            var service = new ClientComponentUpdateService(
                new LauncherRuntimeContext(ClientUpdateChannel.Stable),
                getServerDescriptorAsync: _ => Task.FromResult(Snapshot(HashA, "3.0.4", sha, size)),
                getPackageRoot: () => root);
            using (InstalledComponentReceiptService.UseReceiptPathForTest(receiptPath))
            {
                PreparedClientComponentUpdate prepared = await service.PrepareSelectedChannelUpdateAsync();

                Assert.Empty(prepared.PendingUpdates);
                Assert.Equal(
                    ("3.0.4", sha),
                    InstalledComponentReceiptService.Read("stable", root)["hooks"]);
            }
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(10737418240, 30)]
    public void Artifact_download_timeout_is_bounded(long size, int expectedMinutes)
        => Assert.Equal(
            TimeSpan.FromMinutes(expectedMinutes),
            ClientComponentUpdateService.CalculateArtifactDownloadTimeout(size));

    private static ClientComponentManifest ManifestWith(
        params (string Name, ClientComponentDefinition Definition)[] entries)
        => new()
        {
            SchemaVersion = 2,
            Channel = "stable",
            Components = entries.ToDictionary(item => item.Name, item => item.Definition)
        };

    private static ClientComponentDefinition Definition(
        string fileName,
        string version,
        IEnumerable<string> zh,
        IEnumerable<string> en)
        => new()
        {
            Version = version,
            Sha256 = HashA,
            Size = 1,
            Url = $"/components/artifacts/{version}/{fileName}",
            UpdateMode = fileName == "SCBL.exe" ? "updater-restart" : "before-game-start",
            Required = true,
            ReleaseNotes = new ClientComponentReleaseNotes { Zh = zh.ToList(), En = en.ToList() }
        };

    private static ServerDescriptorSnapshot Snapshot(
        string generation,
        string version,
        string sha,
        long size = 1)
        => new(
            new ServerDescriptor
            {
                SchemaVersion = 1,
                GeneratedAt = "2026-08-22T00:00:00Z",
                Server = new ServerIdentityDescriptor { Id = "scbl-stable", Mode = "stable" },
                Client = new ServerClientDescriptor
                {
                    Version = "3.0.0",
                    UpdateBaseUrl = "http://server.example:18080/",
                    ComponentGeneration = generation,
                    Components = new Dictionary<string, ClientComponentDefinition>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["hooks"] = new ClientComponentDefinition
                        {
                            Version = version,
                            Sha256 = sha,
                            Size = size,
                            Url = $"/components/artifacts/hooks/{version}/uplay_r1_loader.dll",
                            UpdateMode = "before-game-start",
                            Required = true,
                            ReleaseNotes = new ClientComponentReleaseNotes
                            {
                                Zh = ["更新 Hooks"],
                                En = ["Update Hooks"]
                            }
                        }
                    }
                }
            },
            ServerDescriptorSource.Live,
            generation,
            new Uri("http://server.example:18080/scbl-server.json"));

    private static string CreatePackagedHooks(string root, string version, out string sha)
    {
        string hooksDirectory = Path.Combine(root, "components", "hooks");
        Directory.CreateDirectory(hooksDirectory);
        string hooks = Path.Combine(hooksDirectory, "uplay_r1_loader.dll");
        File.WriteAllText(hooks, "verified hooks payload");
        sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(hooks))).ToLowerInvariant();
        File.WriteAllText(hooks + ".sha256", sha + "  uplay_r1_loader.dll");
        File.WriteAllText(
            Path.Combine(root, "client_package_manifest.json"),
            System.Text.Json.JsonSerializer.Serialize(new
            {
                componentReceipts = new Dictionary<string, object>
                {
                    ["hooks"] = new
                    {
                        version,
                        sha256 = sha,
                        files = new Dictionary<string, string>
                        {
                            ["components/hooks/uplay_r1_loader.dll"] = sha
                        }
                    }
                }
            }));
        return hooks;
    }

    private static string TemporaryDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "scbl-component-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string TemporaryPath(string name)
    {
        string root = TemporaryDirectory();
        return Path.Combine(root, name);
    }

    private static void TryDeleteDirectory(string root)
    {
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch
        {
        }
    }
}
