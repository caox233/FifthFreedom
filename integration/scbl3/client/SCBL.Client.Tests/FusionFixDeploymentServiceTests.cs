using SplinterCellCNLauncher.ApplicationLayer;
using SplinterCellCNLauncher.Models;
using SplinterCellCNLauncher.Services;
using System.IO.Compression;
using System.Security.Cryptography;

namespace SCBL.Client.Tests;

public sealed class FusionFixDeploymentServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "scbl-fusionfix-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Pristine_game_installs_frozen_files_but_never_the_player_ini()
    {
        Directory.CreateDirectory(_root);
        byte[] asset = CreateArchive(
            ("src/SYSTEM/version.dll", [(byte)'M', (byte)'Z', 1]),
            ("src/SYSTEM/scripts/SplinterCellBlacklist.FusionFix.asi", [(byte)'M', (byte)'Z', 2]),
            ("src/SYSTEM/scripts/SplinterCellBlacklist.FusionFix.ini", "forbidden"u8.ToArray()),
            ("src/SYSTEM/update/difficultyconfiguration.ini", "safe"u8.ToArray()));
        FusionFixDeploymentService service = CreateService(asset);

        FusionFixDeploymentResult installed = await service.EnsureInstalledAsync(_root);
        FusionFixDeploymentResult repeated = await service.EnsureInstalledAsync(_root);

        Assert.Equal(FusionFixDeploymentOutcome.Installed, installed.Outcome);
        Assert.Equal(3, installed.InstalledFileCount);
        Assert.False(File.Exists(Path.Combine(
            _root,
            "SYSTEM",
            "scripts",
            "SplinterCellBlacklist.FusionFix.ini")));
        Assert.Equal(FusionFixDeploymentOutcome.AlreadyPresent, repeated.Outcome);
        Assert.Equal(0, repeated.InstalledFileCount);
    }

    [Fact]
    public async Task Unrelated_script_does_not_block_missing_fusionfix_files()
    {
        string scripts = Path.Combine(_root, "SYSTEM", "scripts");
        Directory.CreateDirectory(scripts);
        string playerMod = Path.Combine(scripts, "player-owned.asi");
        File.WriteAllText(playerMod, "keep-me");
        byte[] asset = CreateArchive(
            ("src/SYSTEM/version.dll", [(byte)'M', (byte)'Z', 1]),
            ("src/SYSTEM/scripts/SplinterCellBlacklist.FusionFix.asi", [(byte)'M', (byte)'Z', 2]));

        FusionFixDeploymentResult result = await CreateService(asset)
            .EnsureInstalledAsync(_root);

        Assert.Equal(FusionFixDeploymentOutcome.Installed, result.Outcome);
        Assert.Equal(2, result.InstalledFileCount);
        Assert.Equal("keep-me", File.ReadAllText(playerMod));
        Assert.True(File.Exists(Path.Combine(_root, "SYSTEM", "version.dll")));
        Assert.True(File.Exists(Path.Combine(
            scripts,
            "SplinterCellBlacklist.FusionFix.asi")));
    }

    [Fact]
    public async Task Editable_mod_configuration_is_matched_by_name_and_never_overwritten()
    {
        string system = Path.Combine(_root, "SYSTEM");
        string scripts = Path.Combine(system, "scripts");
        string update = Path.Combine(system, "update");
        Directory.CreateDirectory(scripts);
        Directory.CreateDirectory(update);
        byte[] version = [(byte)'M', (byte)'Z', 1];
        byte[] asi = [(byte)'M', (byte)'Z', 2];
        byte[] asset = CreateArchive(
            ("src/SYSTEM/version.dll", version),
            ("src/SYSTEM/scripts/SplinterCellBlacklist.FusionFix.asi", asi),
            ("src/SYSTEM/scripts/SplinterCellBlacklist.FusionFix.ini", "frozen-secret"u8.ToArray()),
            ("src/SYSTEM/update/difficultyconfiguration.ini", "frozen-default"u8.ToArray()),
            ("src/SYSTEM/update/Data/DefaultWaveConfig.xml", "frozen-xml"u8.ToArray()));
        File.WriteAllBytes(Path.Combine(system, "version.dll"), version);
        File.WriteAllBytes(Path.Combine(scripts, "SplinterCellBlacklist.FusionFix.asi"), asi);
        string protectedIni = Path.Combine(scripts, "SplinterCellBlacklist.FusionFix.ini");
        string editableIni = Path.Combine(update, "difficultyconfiguration.ini");
        string editableXml = Path.Combine(update, "Data", "DefaultWaveConfig.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(editableXml)!);
        File.WriteAllText(protectedIni, "player-private");
        File.WriteAllText(editableIni, "player-tuned");
        File.WriteAllText(editableXml, "player-xml");

        using var protectedLock = new FileStream(
            protectedIni,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        using var iniLock = new FileStream(
            editableIni,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        using var xmlLock = new FileStream(
            editableXml,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        FusionFixDeploymentResult result = await CreateService(asset)
            .EnsureInstalledAsync(_root);

        Assert.Equal(FusionFixDeploymentOutcome.AlreadyPresent, result.Outcome);
        Assert.Equal("complete-usable-payload", result.ReasonCode);
        Assert.Equal(4, result.PreservedFileCount);
        Assert.Equal("player-private", ReadLockedText(protectedLock));
        Assert.Equal("player-tuned", ReadLockedText(iniLock));
        Assert.Equal("player-xml", ReadLockedText(xmlLock));
    }

    [Fact]
    public async Task Missing_editable_configuration_is_created_without_replacing_existing_configuration()
    {
        string system = Path.Combine(_root, "SYSTEM");
        string update = Path.Combine(system, "update");
        Directory.CreateDirectory(update);
        byte[] version = [(byte)'M', (byte)'Z', 1];
        byte[] asset = CreateArchive(
            ("src/SYSTEM/version.dll", version),
            ("src/SYSTEM/update/existing.ini", "frozen-existing"u8.ToArray()),
            ("src/SYSTEM/update/missing.xml", "frozen-missing"u8.ToArray()));
        File.WriteAllBytes(Path.Combine(system, "version.dll"), version);
        string existing = Path.Combine(update, "existing.ini");
        File.WriteAllText(existing, "player-value");

        FusionFixDeploymentResult result = await CreateService(asset)
            .EnsureInstalledAsync(_root);

        Assert.Equal(FusionFixDeploymentOutcome.Installed, result.Outcome);
        Assert.Equal(1, result.InstalledFileCount);
        Assert.Equal(2, result.PreservedFileCount);
        Assert.Equal("player-value", File.ReadAllText(existing));
        Assert.Equal("frozen-missing", File.ReadAllText(Path.Combine(update, "missing.xml")));
    }

    [Fact]
    public async Task Invalid_existing_binary_is_preserved_and_reported_as_conflict()
    {
        string system = Path.Combine(_root, "SYSTEM");
        Directory.CreateDirectory(system);
        File.WriteAllBytes(Path.Combine(system, "version.dll"), [0, 0, 9]);
        byte[] asset = CreateArchive(
            ("src/SYSTEM/version.dll", [(byte)'M', (byte)'Z', 1]),
            ("src/SYSTEM/update/missing.xml", "safe"u8.ToArray()));

        FusionFixDeploymentResult result = await CreateService(asset)
            .EnsureInstalledAsync(_root);

        Assert.Equal(FusionFixDeploymentOutcome.ExistingModPreserved, result.Outcome);
        Assert.Equal("existing-binary-invalid", result.ReasonCode);
        Assert.Equal("SYSTEM/version.dll", result.ConflictRelativePath);
        Assert.False(File.Exists(Path.Combine(system, "update", "missing.xml")));
        Assert.Equal(9, File.ReadAllBytes(Path.Combine(system, "version.dll"))[2]);
    }

    [Fact]
    public async Task Other_loader_file_does_not_block_non_overwriting_install()
    {
        string system = Path.Combine(_root, "SYSTEM");
        Directory.CreateDirectory(system);
        File.WriteAllBytes(Path.Combine(system, "dinput8.dll"), [(byte)'M', (byte)'Z']);
        byte[] asset = CreateArchive(
            ("src/SYSTEM/version.dll", [(byte)'M', (byte)'Z', 1]));
        FusionFixDeploymentResult result = await CreateService(asset)
            .EnsureInstalledAsync(_root);

        Assert.Equal(FusionFixDeploymentOutcome.Installed, result.Outcome);
        Assert.True(File.Exists(Path.Combine(_root, "SYSTEM", "version.dll")));
        Assert.Equal(
            [(byte)'M', (byte)'Z'],
            File.ReadAllBytes(Path.Combine(system, "dinput8.dll")));
    }

    [Fact]
    public async Task Different_but_usable_existing_binaries_are_reused_by_name()
    {
        string system = Path.Combine(_root, "SYSTEM");
        string scripts = Path.Combine(system, "scripts");
        Directory.CreateDirectory(scripts);
        File.WriteAllBytes(Path.Combine(system, "version.dll"), [(byte)'M', (byte)'Z', 9]);
        File.WriteAllBytes(
            Path.Combine(scripts, "SplinterCellBlacklist.FusionFix.asi"),
            [(byte)'M', (byte)'Z', 8]);
        byte[] asset = CreateArchive(
            ("src/SYSTEM/version.dll", [(byte)'M', (byte)'Z', 1]),
            ("src/SYSTEM/scripts/SplinterCellBlacklist.FusionFix.asi", [(byte)'M', (byte)'Z', 2]));

        FusionFixDeploymentResult result = await CreateService(asset)
            .EnsureInstalledAsync(_root);

        Assert.Equal(FusionFixDeploymentOutcome.AlreadyPresent, result.Outcome);
        Assert.Equal("complete-usable-payload", result.ReasonCode);
        Assert.Equal(9, File.ReadAllBytes(Path.Combine(system, "version.dll"))[2]);
        Assert.Equal(8, File.ReadAllBytes(Path.Combine(
            scripts,
            "SplinterCellBlacklist.FusionFix.asi"))[2]);
    }

    [Fact]
    public async Task Selected_system_directory_reuses_valid_payload_without_nested_system_path()
    {
        string system = Path.Combine(_root, "SYSTEM");
        string scripts = Path.Combine(system, "scripts");
        Directory.CreateDirectory(scripts);
        byte[] version = [(byte)'M', (byte)'Z', 1];
        byte[] asi = [(byte)'M', (byte)'Z', 2];
        File.WriteAllBytes(Path.Combine(system, "version.dll"), version);
        File.WriteAllBytes(Path.Combine(scripts, "SplinterCellBlacklist.FusionFix.asi"), asi);
        byte[] asset = CreateArchive(
            ("src/SYSTEM/version.dll", version),
            ("src/SYSTEM/scripts/SplinterCellBlacklist.FusionFix.asi", asi));

        FusionFixDeploymentResult result = await CreateService(asset)
            .EnsureInstalledAsync(system);

        Assert.Equal(FusionFixDeploymentOutcome.AlreadyPresent, result.Outcome);
        Assert.Equal(2, result.PreservedFileCount);
        Assert.False(Directory.Exists(Path.Combine(system, "SYSTEM")));
    }

    [Fact]
    public async Task Selected_system_directory_installs_missing_payload_into_actual_load_directory()
    {
        string system = Path.Combine(_root, "SYSTEM");
        Directory.CreateDirectory(system);
        byte[] asset = CreateArchive(
            ("src/SYSTEM/version.dll", [(byte)'M', (byte)'Z', 1]),
            ("src/SYSTEM/scripts/SplinterCellBlacklist.FusionFix.asi", [(byte)'M', (byte)'Z', 2]));

        FusionFixDeploymentResult result = await CreateService(asset)
            .EnsureInstalledAsync(system);

        Assert.Equal(FusionFixDeploymentOutcome.Installed, result.Outcome);
        Assert.True(File.Exists(Path.Combine(system, "version.dll")));
        Assert.True(File.Exists(Path.Combine(
            system,
            "scripts",
            "SplinterCellBlacklist.FusionFix.asi")));
        Assert.False(Directory.Exists(Path.Combine(system, "SYSTEM")));
    }

    [Fact]
    public async Task Loader_name_outside_actual_system_directory_does_not_block_installation()
    {
        string system = Path.Combine(_root, "SYSTEM");
        Directory.CreateDirectory(system);
        File.WriteAllBytes(Path.Combine(_root, "version.dll"), [(byte)'M', (byte)'Z', 9]);
        byte[] asset = CreateArchive(
            ("src/SYSTEM/version.dll", [(byte)'M', (byte)'Z', 1]));

        FusionFixDeploymentResult result = await CreateService(asset)
            .EnsureInstalledAsync(system);

        Assert.Equal(FusionFixDeploymentOutcome.Installed, result.Outcome);
        Assert.Equal(9, File.ReadAllBytes(Path.Combine(_root, "version.dll"))[2]);
        Assert.Equal(1, File.ReadAllBytes(Path.Combine(system, "version.dll"))[2]);
    }

    [Fact]
    public async Task Non_canonical_archive_entry_is_rejected_before_any_game_write()
    {
        Directory.CreateDirectory(_root);
        byte[] asset = CreateArchive(("src/../escape.dll", [(byte)'M', (byte)'Z']));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => CreateService(asset).EnsureInstalledAsync(_root));

        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_root)!, "escape.dll")));
    }

    [Fact]
    public async Task Selection_entry_requires_platform_availability_and_is_idempotent()
    {
        Directory.CreateDirectory(_root);
        byte[] asset = CreateArchive(
            ("src/SYSTEM/version.dll", [(byte)'M', (byte)'Z', 1]));
        var service = new ModSelectionPreparationService(CreateService(asset));
        LauncherConfigurationAvailability denied = Availability(isModSelectable: false);
        LauncherConfigurationAvailability allowed = Availability(isModSelectable: true);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.PrepareAsync(_root, denied));
        ModSelectionPreparationResult first = await service.PrepareAsync(_root, allowed);
        ModSelectionPreparationResult second = await service.PrepareAsync(_root, allowed);

        Assert.Equal(FusionFixDeploymentOutcome.Installed, first.FusionFixOutcome);
        Assert.Equal(FusionFixDeploymentOutcome.AlreadyPresent, second.FusionFixOutcome);
    }

    [Fact]
    public void Embedded_asset_matches_the_frozen_hash_and_only_lists_src_entries()
    {
        using Stream source = EmbeddedResourceService.OpenEmbeddedFileStrict(
            FusionFixDeploymentService.AssetFileName);
        using var buffer = new MemoryStream();
        source.CopyTo(buffer);
        byte[] bytes = buffer.ToArray();
        Assert.Equal(
            FusionFixDeploymentService.AssetSha256,
            Convert.ToHexString(SHA256.HashData(bytes)));

        using var archive = new ZipArchive(
            new MemoryStream(bytes, writable: false),
            ZipArchiveMode.Read);
        Assert.All(
            archive.Entries,
            entry => Assert.StartsWith("src/", entry.FullName, StringComparison.Ordinal));
        Assert.Contains(
            archive.Entries,
            entry => entry.FullName.Equals(
                "src/" + FusionFixDeploymentService.ProtectedIniRelativePath,
                StringComparison.OrdinalIgnoreCase));
    }

    private static FusionFixDeploymentService CreateService(
        byte[] asset,
        Action<FusionFixDeploymentResult>? reportResult = null)
        => new(
            () => new MemoryStream(asset, writable: false),
            Convert.ToHexString(SHA256.HashData(asset)),
            reportResult);

    private static byte[] CreateArchive(
        params (string Name, byte[] Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[] content) in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
                using Stream target = entry.Open();
                target.Write(content);
            }
        }
        return buffer.ToArray();
    }

    private static string ReadLockedText(FileStream stream)
    {
        stream.Position = 0;
        using var reader = new StreamReader(stream, leaveOpen: true);
        return reader.ReadToEnd();
    }

    private static LauncherConfigurationAvailability Availability(bool isModSelectable)
        => new(
            IsRendererConfigurationEnabled: true,
            IsGameplayConfigurationEnabled: true,
            IsModAvailable: isModSelectable,
            IsModSelectable: isModSelectable,
            IsModRuntimeLoaded: false,
            EffectiveGameplayMode: isModSelectable ? GameplayMode.Mod : GameplayMode.Official,
            ServerObservationId: isModSelectable ? "server" : "",
            RuntimeObservationId: isModSelectable ? "runtime" : "");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }
}
