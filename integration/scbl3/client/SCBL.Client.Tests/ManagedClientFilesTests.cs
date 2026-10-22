using SplinterCellCNLauncher.Services;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SCBL.Client.Shared;

namespace SCBL.Client.Tests;

public sealed class ManagedClientFilesTests
{
    [Fact]
    public void Canonical_install_layout_keeps_only_updater_in_client_root()
    {
        string root = NewTemporaryDirectory();
        try
        {
            Assert.Equal(
                Path.Combine(root, "SCBL.Updater.exe"),
                ClientInstallLayout.Resolve(root, ClientInstallLayout.UpdaterRelativePath));
            Assert.Equal(
                Path.Combine(root, "components", "hooks", "uplay_r1_loader.dll"),
                ClientInstallLayout.Resolve(root, ClientInstallLayout.HooksRelativePath));
            Assert.Equal(
                Path.Combine(root, "components", "network", "easytier-core.exe"),
                ClientInstallLayout.Resolve(root, ClientInstallLayout.EasyTierCoreRelativePath));
            Assert.Equal(
                Path.Combine(root, "components", "route-guard", "WinDivert64.payload.sys"),
                ClientInstallLayout.Resolve(root, ClientInstallLayout.WinDivertPayloadRelativePath));
            Assert.Throws<InvalidDataException>(() => ClientInstallLayout.Resolve(root, "../outside.bin"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Portable_data_layout_isolated_by_computer_and_windows_user()
    {
        string root = NewTemporaryDirectory();
        try
        {
            Assert.Equal(
                Path.Combine(root, "data", "POWERPC", "Alice"),
                ClientDataLayout.ResolveForIdentity(root, "POWERPC", "Alice"));
            Assert.Equal(
                Path.Combine(root, "data", "UNKNOWN-PC", "UNKNOWN-USER"),
                ClientDataLayout.ResolveForIdentity(root, "", ""));
            string filesystemRoot = Path.GetPathRoot(root)!;
            Assert.Equal(
                Path.Combine(filesystemRoot, "data", "ROOT-PC", "RootUser"),
                ClientDataLayout.ResolveForIdentity(filesystemRoot, "ROOT-PC", "RootUser"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Source_distribution_excludes_generated_mod_binaries()
    {
        string repository = FindRepositoryRoot();
        string mod = Path.Combine(repository, "client", "distribution", "components", "hooks", "mod");
        foreach (string fileName in new[] { "scbl_gameplay.dll" })
        {
            Assert.False(File.Exists(Path.Combine(mod, fileName)), $"Generated binary leaked into source: {fileName}");
            Assert.False(File.Exists(Path.Combine(mod, fileName + ".sha256")), $"Generated sidecar leaked into source: {fileName}");
        }

        string buildEntry = File.ReadAllText(Path.Combine(repository, "scripts", "coordination", "scbl_task.py"));
        Assert.Contains("scbl_gameplay.dll", buildEntry, StringComparison.Ordinal);
    }

    [Fact]
    public void Distribution_removes_uninstall_entry_and_declares_exact_retirement_paths()
    {
        string repository = FindRepositoryRoot();
        string distribution = Path.Combine(repository, "client", "distribution");
        Assert.False(File.Exists(Path.Combine(distribution, "卸载启动器.bat")));
        Assert.False(File.Exists(Path.Combine(distribution, "tools", "SCBL-Uninstall-And-Delete.ps1")));

        using JsonDocument rules = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(distribution, "client-files.rules.json")));
        string[] delete = rules.RootElement.GetProperty("delete")
            .EnumerateArray()
            .Select(item => item.GetString() ?? "")
            .ToArray();
        Assert.Contains("卸载启动器.bat", delete, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("tools/SCBL-Uninstall-And-Delete.ps1", delete, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Manifest_bundle_installs_and_deletes_declared_files()
    {
        string root = NewTemporaryDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "obsolete.txt"), "old");
            byte[] payload = Encoding.UTF8.GetBytes("new payload");
            string bundle = CreateBundle(
                root,
                "extras/new.txt",
                payload,
                ["obsolete.txt"]);
            string obsolete = Path.Combine(root, "obsolete.txt");
            var receipt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [obsolete] = TransactionalComponentInstaller.ComputeSha256(obsolete)
            };

            IReadOnlyDictionary<string, string> installed =
                StagedComponentBootstrapService.ApplyManagedClientFilesBundleForTest(bundle, root, receipt);

            Assert.Equal("new payload", File.ReadAllText(Path.Combine(root, "extras", "new.txt")));
            Assert.False(File.Exists(Path.Combine(root, "obsolete.txt")));
            Assert.Single(installed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Delete_only_bundle_is_valid_when_receipt_owns_target()
    {
        string root = NewTemporaryDirectory();
        try
        {
            string obsolete = Path.Combine(root, "obsolete.txt");
            File.WriteAllText(obsolete, "managed old file");
            var receipt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [obsolete] = TransactionalComponentInstaller.ComputeSha256(obsolete)
            };
            string bundle = Path.Combine(root, "delete-only.zip");
            using (ZipArchive archive = ZipFile.Open(bundle, ZipArchiveMode.Create))
            {
                ZipArchiveEntry manifestEntry = archive.CreateEntry("client-files-manifest.json");
                using var writer = new StreamWriter(manifestEntry.Open(), new UTF8Encoding(false));
                writer.Write(JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    files = Array.Empty<object>(),
                    delete = new[] { "obsolete.txt" }
                }));
            }

            IReadOnlyDictionary<string, string> installed =
                StagedComponentBootstrapService.ApplyManagedClientFilesBundleForTest(bundle, root, receipt);

            Assert.False(File.Exists(obsolete));
            Assert.Empty(installed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Manifest_bundle_preserves_delete_target_without_trusted_receipt()
    {
        string root = NewTemporaryDirectory();
        try
        {
            string obsolete = Path.Combine(root, "user-file.txt");
            File.WriteAllText(obsolete, "user data");
            string bundle = CreateBundle(root, "extras/new.txt", [1, 2, 3], ["user-file.txt"]);

            StagedComponentBootstrapService.ApplyManagedClientFilesBundleForTest(bundle, root);

            Assert.Equal("user data", File.ReadAllText(obsolete));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Manifest_bundle_preserves_receipt_file_modified_by_user()
    {
        string root = NewTemporaryDirectory();
        try
        {
            string obsolete = Path.Combine(root, "owned.txt");
            File.WriteAllText(obsolete, "managed original");
            var receipt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [obsolete] = TransactionalComponentInstaller.ComputeSha256(obsolete)
            };
            File.WriteAllText(obsolete, "user modified");
            string bundle = CreateBundle(root, "extras/new.txt", [1, 2, 3], ["owned.txt"]);

            StagedComponentBootstrapService.ApplyManagedClientFilesBundleForTest(bundle, root, receipt);

            Assert.Equal("user modified", File.ReadAllText(obsolete));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Install_and_owned_delete_roll_back_together_on_failure()
    {
        string root = NewTemporaryDirectory();
        try
        {
            string source = Path.Combine(root, "source.bin");
            string target = Path.Combine(root, "new.bin");
            string obsolete = Path.Combine(root, "obsolete.bin");
            File.WriteAllBytes(source, [1, 2, 3]);
            File.WriteAllBytes(obsolete, [4, 5, 6]);
            var installs = new[]
            {
                new ComponentFileInstall(source, target, TransactionalComponentInstaller.ComputeSha256(source))
            };
            var deletes = new[]
            {
                new ComponentFileDelete(obsolete, TransactionalComponentInstaller.ComputeSha256(obsolete))
            };

            Assert.Throws<InvalidOperationException>(() =>
                TransactionalComponentInstaller.Install(
                    installs,
                    deletes,
                    beforeCommitForTest: index =>
                    {
                        if (index == 1)
                            throw new InvalidOperationException("injected deletion failure");
                    }));

            Assert.False(File.Exists(target));
            Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(obsolete));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Route_guard_controlled_path_rejects_nested_and_root_names()
    {
        string root = NewTemporaryDirectory();
        try
        {
            Assert.EndsWith(
                Path.Combine("RouteGuard", "scbl-process-router.exe"),
                RouteGuardRuntimePaths.ResolveFile("scbl-process-router.exe", Path.Combine(root, "RouteGuard")),
                StringComparison.OrdinalIgnoreCase);
            Assert.Throws<InvalidDataException>(() =>
                RouteGuardRuntimePaths.ResolveFile("..\\outside.exe", Path.Combine(root, "RouteGuard")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Route_guard_controlled_directory_rejects_reparse_root()
    {
        string root = NewTemporaryDirectory();
        string actual = Path.Combine(root, "actual");
        string link = Path.Combine(root, "linked-route-guard");
        Directory.CreateDirectory(actual);
        try
        {
            try
            {
                Directory.CreateSymbolicLink(link, actual);
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
            catch (IOException)
            {
                return;
            }

            Assert.Throws<InvalidDataException>(() =>
                RouteGuardRuntimePaths.EnsureControlledDirectory(link, configureAcl: false));
        }
        finally
        {
            if (Directory.Exists(link))
                Directory.Delete(link);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Manifest_bundle_rejects_paths_outside_client_root()
    {
        string root = NewTemporaryDirectory();
        try
        {
            string bundle = CreateBundle(root, "../escape.txt", [1, 2, 3], []);
            Assert.Throws<InvalidDataException>(() =>
                StagedComponentBootstrapService.ApplyManagedClientFilesBundleForTest(bundle, root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateBundle(
        string root,
        string path,
        byte[] payload,
        string[] delete)
    {
        string bundle = Path.Combine(root, Guid.NewGuid().ToString("N") + ".zip");
        string hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var manifest = new
        {
            schemaVersion = 1,
            files = new[] { new { path, size = payload.LongLength, sha256 = hash } },
            delete
        };
        using ZipArchive archive = ZipFile.Open(bundle, ZipArchiveMode.Create);
        ZipArchiveEntry manifestEntry = archive.CreateEntry("client-files-manifest.json");
        using (var writer = new StreamWriter(manifestEntry.Open(), new UTF8Encoding(false)))
            writer.Write(JsonSerializer.Serialize(manifest));
        ZipArchiveEntry payloadEntry = archive.CreateEntry("files/" + path);
        using Stream output = payloadEntry.Open();
        output.Write(payload);
        return bundle;
    }

    private static string NewTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "scbl-client-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory != null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "COMPONENT_VERSIONS.json")))
                return directory.FullName;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
