using SplinterCellCNLauncher.Services;
using System.Text.Json;

namespace SCBL.Client.Tests;

public sealed class ClientStorageMaintenanceServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "scbl-storage-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Rotation_keeps_bounded_game_log_archives()
    {
        string log = Write("logs/game/bl-tracing.log", "0123456789");
        Write("logs/game/bl-tracing.log.1", "previous");

        ClientStorageMaintenanceService.RotateIfOversized(log, maxBytes: 5, archiveCount: 2);

        Assert.False(File.Exists(log));
        Assert.Equal("0123456789", File.ReadAllText(log + ".1"));
        Assert.Equal("previous", File.ReadAllText(log + ".2"));
    }

    [Fact]
    public void Startup_cleanup_prunes_artifacts_but_preserves_active_component()
    {
        DateTime now = new(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);
        for (int index = 0; index < 12; index++)
            Stamp(Write($"diagnostics/SCBL_Diagnostics_{index:D2}.zip", index.ToString()), now.AddMinutes(index));
        for (int index = 0; index < 4; index++)
            Stamp(Write($"updates/downloads/client-{index}.zip", index.ToString()), now.AddMinutes(index));
        Write("updates/component-packages/launcher-2.0.9.zip", "applied");

        string oldWork = Write("updates/work/old/item.tmp", "old");
        Stamp(oldWork, now.AddDays(-3));
        Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(oldWork)!, now.AddDays(-3));
        string staleRunner = Write("updates/runner/finished/SCBL.Updater.exe", "runner");
        Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(staleRunner)!, now);

        var componentDirectories = new List<string>();
        for (int index = 0; index < 4; index++)
        {
            string file = Write($"components/hooks/{index}.0.0/uplay_r1_loader.dll", index.ToString());
            string directory = Path.GetDirectoryName(file)!;
            Directory.SetLastWriteTimeUtc(directory, now.AddMinutes(index));
            componentDirectories.Add(directory);
        }
        string state = JsonSerializer.Serialize(new
        {
            Components = new Dictionary<string, object>
            {
                ["hooks"] = new { FilePath = Path.Combine(componentDirectories[0], "uplay_r1_loader.dll") }
            }
        });
        Write("components/component_state.json", state);

        ClientStorageMaintenanceService.Run(_root, now);

        Assert.Equal(10, Directory.GetFiles(Path.Combine(_root, "diagnostics"), "*.zip").Length);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "updates", "downloads"), "*.zip"));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "updates", "component-packages"), "*.zip"));
        Assert.False(Directory.Exists(Path.Combine(_root, "updates", "work", "old")));
        Assert.False(Directory.Exists(Path.GetDirectoryName(staleRunner)));
        Assert.True(Directory.Exists(componentDirectories[0]));
        Assert.False(Directory.Exists(componentDirectories[1]));
        Assert.False(Directory.Exists(componentDirectories[2]));
        Assert.True(Directory.Exists(componentDirectories[3]));
    }

    [Fact]
    public void Startup_cleanup_restores_unique_backup_after_interrupted_bundle_transaction()
    {
        DateTime now = new(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);
        string transactionId = Guid.NewGuid().ToString("N");
        string target = Path.Combine(_root, "components", "hooks", "2.0.0", "uplay_r1_loader.dll");
        string backup = target
            + ClientStorageMaintenanceService.BundleBackupMarker
            + transactionId
            + ClientStorageMaintenanceService.BundleBackupSuffix;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(backup, "verified previous payload");
        Stamp(backup, now.AddDays(-2));
        string staging = Path.Combine(
            _root,
            "components",
            ClientStorageMaintenanceService.BundleStagingDirectoryPrefix + transactionId);
        Directory.CreateDirectory(staging);
        File.WriteAllText(
            Path.Combine(staging, ClientStorageMaintenanceService.BundleTransactionLockFileName),
            "");
        Directory.SetLastWriteTimeUtc(staging, now.AddDays(-2));

        ClientStorageMaintenanceService.Run(_root, now);

        Assert.Equal("verified previous payload", File.ReadAllText(target));
        Assert.False(File.Exists(backup));
        Assert.False(Directory.Exists(staging));
    }

    [Fact]
    public void Startup_cleanup_ignores_files_and_directories_outside_strict_bundle_names()
    {
        DateTime now = new(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);
        string ordinary = Write("components/hooks/2.0.0/readme.txt", "keep");
        string malformedBackup = Write(
            "components/hooks/2.0.0/readme.txt.bundle-not-a-transaction.bak",
            "keep");
        string malformedStage = Path.Combine(_root, "components", ".bundle-stage-not-a-transaction");
        Directory.CreateDirectory(malformedStage);
        Directory.SetLastWriteTimeUtc(malformedStage, now.AddDays(-2));

        ClientStorageMaintenanceService.Run(_root, now);

        Assert.True(File.Exists(ordinary));
        Assert.True(File.Exists(malformedBackup));
        Assert.True(Directory.Exists(malformedStage));
    }

    [Fact]
    public void Startup_cleanup_preserves_active_and_recent_bundle_transactions()
    {
        DateTime now = new(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);
        string activeId = Guid.NewGuid().ToString("N");
        string activeTarget = Write("components/hooks/2.0.0/uplay_r1_loader.dll", "current");
        string activeVersion = Path.GetDirectoryName(activeTarget)!;
        string newerVersionFile = Write("components/hooks/3.0.0/uplay_r1_loader.dll", "newer");
        Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(newerVersionFile)!, now);
        string activeBackup = activeTarget
            + ClientStorageMaintenanceService.BundleBackupMarker
            + activeId
            + ClientStorageMaintenanceService.BundleBackupSuffix;
        File.Move(activeTarget, activeBackup);
        Stamp(activeBackup, now.AddDays(-2));
        Directory.SetLastWriteTimeUtc(activeVersion, now.AddDays(-2));
        string activeStage = Path.Combine(
            _root,
            "components",
            ClientStorageMaintenanceService.BundleStagingDirectoryPrefix + activeId);
        Directory.CreateDirectory(activeStage);
        string activeLock = Path.Combine(
            activeStage,
            ClientStorageMaintenanceService.BundleTransactionLockFileName);
        using var lease = new FileStream(
            activeLock,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None);
        Directory.SetLastWriteTimeUtc(activeStage, now.AddDays(-2));

        string recentId = Guid.NewGuid().ToString("N");
        string recentTarget = Write("components/updater/2.0.0/SCBL.Updater.exe", "current");
        string recentBackup = recentTarget
            + ClientStorageMaintenanceService.BundleBackupMarker
            + recentId
            + ClientStorageMaintenanceService.BundleBackupSuffix;
        File.WriteAllText(recentBackup, "previous");
        Stamp(recentBackup, now.AddMinutes(-5));
        string recentStage = Path.Combine(
            _root,
            "components",
            ClientStorageMaintenanceService.BundleStagingDirectoryPrefix + recentId);
        Directory.CreateDirectory(recentStage);
        Directory.SetLastWriteTimeUtc(recentStage, now.AddMinutes(-5));

        ClientStorageMaintenanceService.Run(_root, now);

        Assert.False(File.Exists(activeTarget));
        Assert.True(File.Exists(activeBackup));
        Assert.True(Directory.Exists(activeVersion));
        Assert.True(Directory.Exists(activeStage));
        Assert.True(File.Exists(recentBackup));
        Assert.True(Directory.Exists(recentStage));
    }

    private string Write(string relative, string text)
    {
        string path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private static void Stamp(string path, DateTime utc)
        => File.SetLastWriteTimeUtc(path, utc);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
