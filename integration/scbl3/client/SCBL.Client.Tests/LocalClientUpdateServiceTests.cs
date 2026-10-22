using SplinterCellCNLauncher.Services;
using SplinterCellCNLauncher.Models;
using System.Diagnostics;
using System.IO.Compression;

namespace SCBL.Client.Tests;

public sealed class LocalClientUpdateServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "scbl-updater-runner-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Test_channel_is_preserved_across_component_activation_restart()
    {
        Assert.Equal(
            new[] { "--update-channel", "test" },
            LocalClientUpdateService.GetLauncherRestartArguments(ClientUpdateChannel.Test));
        Assert.Equal(
            new[] { "--update-channel", "stable" },
            LocalClientUpdateService.GetLauncherRestartArguments(ClientUpdateChannel.Stable));
    }

    [Fact]
    public void Restart_handoff_requires_acceptance_not_only_helper_creation()
    {
        using var accepted = new ManualResetEvent(false);
        using var helperExited = new ManualResetEvent(true);

        Assert.False(LocalClientUpdateService.WaitForRestartHandoffSignal(
            accepted,
            helperExited,
            timeoutMs: 20));

        helperExited.Reset();
        accepted.Set();
        Assert.True(LocalClientUpdateService.WaitForRestartHandoffSignal(
            accepted,
            helperExited,
            timeoutMs: 20));
    }

    [Fact]
    public void Restart_helper_command_carries_handoff_and_test_channel()
    {
        ProcessStartInfo start = LocalClientUpdateService.CreateRestartHelperStartInfo(
            "SCBL.Updater.exe",
            @"C:\SCBL",
            @"C:\SCBL\SCBL.exe",
            1234,
            @"Local\SCBL_Updater_Restart_0123456789abcdef0123456789abcdef",
            ClientUpdateChannel.Test);

        Assert.False(start.UseShellExecute);
        Assert.True(start.CreateNoWindow);
        Assert.Equal(
            [
                "--restart-helper",
                "--wait-pid",
                "1234",
                "--target",
                @"C:\SCBL",
                "--restart",
                @"C:\SCBL\SCBL.exe",
                "--handoff-event",
                @"Local\SCBL_Updater_Restart_0123456789abcdef0123456789abcdef",
                "--launcher-argument",
                "--update-channel",
                "--launcher-argument",
                "test",
            ],
            start.ArgumentList);
    }

    [Fact]
    public void PrepareUpdaterRunner_copies_the_tools_updater_outside_the_install_tree()
    {
        string clientRoot = Path.Combine(_root, "client");
        string updates = Path.Combine(clientRoot, "data", "TEST-PC", "TestUser", "updates");
        string canonical = LocalClientUpdateService.GetCanonicalUpdaterPath(clientRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(canonical)!);
        byte[] content = Enumerable.Range(0, 4096).Select(index => (byte)(index % 251)).ToArray();
        File.WriteAllBytes(canonical, content);

        string runner = LocalClientUpdateService.PrepareUpdaterRunner(clientRoot, updates);

        Assert.True(File.Exists(runner));
        Assert.Equal(content, File.ReadAllBytes(runner));
        Assert.StartsWith(Path.Combine(updates, "runner") + Path.DirectorySeparatorChar, runner, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(Path.GetFullPath(canonical), Path.GetFullPath(runner));
    }

    [Fact]
    public void PrepareUpdaterRunner_requires_the_single_root_copy()
    {
        string clientRoot = Path.Combine(_root, "missing-client");
        string updates = Path.Combine(clientRoot, "data", "TEST-PC", "TestUser", "updates");

        FileNotFoundException error = Assert.Throws<FileNotFoundException>(
            () => LocalClientUpdateService.PrepareUpdaterRunner(clientRoot, updates));

        Assert.Equal(Path.Combine(clientRoot, "SCBL.Updater.exe"), error.FileName);
    }

    [Fact]
    public void Launcher_component_package_contains_only_the_launcher()
    {
        Directory.CreateDirectory(_root);
        string source = Path.Combine(_root, "SCBL.exe");
        File.WriteAllBytes(source, Enumerable.Repeat((byte)0x5a, 2048).ToArray());
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(source)));
        var component = new VerifiedClientComponent(
            "launcher", "2.0.7", hash, source, "stable", new Uri("http://127.0.0.1/SCBL.exe"));

        LocalClientUpdateService.UpdatePackageInfo package =
            LocalClientUpdateService.CreateLauncherComponentPackage(component);

        using ZipArchive archive = ZipFile.OpenRead(package.PackagePath);
        ZipArchiveEntry entry = Assert.Single(archive.Entries);
        Assert.Equal("SCBL.exe", entry.FullName);
        Assert.Equal("2.0.7", package.Version);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
