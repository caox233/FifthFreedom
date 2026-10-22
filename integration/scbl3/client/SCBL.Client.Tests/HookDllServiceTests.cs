using SplinterCellCNLauncher.ApplicationLayer;
using SplinterCellCNLauncher.Models;
using SplinterCellCNLauncher.Services;
using System.Security.Cryptography;
using System.Text;

namespace SCBL.Client.Tests;

public sealed class HookDllServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "scbl-hook-protocol-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ValidateHooksConfigProtocol_AcceptsExpectedMarker()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "uplay_r1_loader.dll");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("MZ\0" + HookDllService.RequiredHooksConfigProtocol + "\0payload"));

        HookDllService.ValidateHooksConfigProtocol(path);
    }

    [Fact]
    public void ValidateHooksConfigProtocol_RejectsDllWithoutCurrentMarker()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "uplay_r1_loader.dll");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("MZ\0unsupported-config-protocol\0"));

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => HookDllService.ValidateHooksConfigProtocol(path));

        Assert.Contains("不支持当前配置协议", error.Message);
    }

    [Theory]
    [InlineData(ClientUpdateChannel.Stable, "99.0.0", "2.0.138", false)]
    [InlineData(ClientUpdateChannel.Test, "2.0.137", "2.0.138", false)]
    [InlineData(ClientUpdateChannel.Test, "2.0.138", "2.0.138", true)]
    [InlineData(ClientUpdateChannel.Test, "2.0.139", "2.0.138", true)]
    [InlineData(ClientUpdateChannel.Test, "", "2.0.138", false)]
    [InlineData(ClientUpdateChannel.Test, "development", "2.0.138", false)]
    public void Local_override_requires_test_channel_and_non_stale_version(
        ClientUpdateChannel channel,
        string localVersion,
        string baselineVersion,
        bool expected)
    {
        Assert.Equal(
            expected,
            HookDllService.IsLocalOverrideVersionAllowed(channel, localVersion, baselineVersion));
    }

    [Fact]
    public void ReadHooksComponentVersion_reads_only_valid_embedded_numeric_version()
    {
        Directory.CreateDirectory(_directory);
        string valid = Path.Combine(_directory, "valid.dll");
        string invalid = Path.Combine(_directory, "invalid.dll");
        File.WriteAllBytes(valid, Encoding.ASCII.GetBytes("MZ\0SCBL_HOOKS_VERSION=2.0.139\0payload"));
        File.WriteAllBytes(invalid, Encoding.ASCII.GetBytes("MZ\0SCBL_HOOKS_VERSION=development\0payload"));

        Assert.Equal("2.0.139", HookDllService.ReadHooksComponentVersion(valid));
        Assert.Empty(HookDllService.ReadHooksComponentVersion(invalid));
    }

    [Fact]
    public void Canonical_hook_requires_selected_channel_receipt_sidecar_and_file_to_match()
    {
        string stable = new('A', 64);
        string test = new('B', 64);

        Assert.True(HookDllService.IsCanonicalHookIdentityValid(stable, stable.ToLowerInvariant(), stable));
        Assert.False(HookDllService.IsCanonicalHookIdentityValid(stable, test, test));
        Assert.False(HookDllService.IsCanonicalHookIdentityValid(stable, stable, test));
        Assert.False(HookDllService.IsCanonicalHookIdentityValid("", stable, stable));
    }

    [Fact]
    public void LocalTestPluginSet_RequiresOnlyGameplay()
    {
        Assert.Equal(
            ["scbl_gameplay.dll"],
            HookDllService.LocalTestPluginFileNames);
        Assert.Empty(HookDllService.LocalTestOptionalPluginFileNames);
        Assert.DoesNotContain("scbl_audio.dll", HookDllService.LocalTestPluginFileNames);
    }

    [Fact]
    public void Canonical_mod_plugins_require_matching_sha256_sidecars()
    {
        Directory.CreateDirectory(_directory);
        foreach (string fileName in HookDllService.CanonicalModPluginFileNames)
        {
            string path = Path.Combine(_directory, fileName);
            byte[] content = [(byte)'M', (byte)'Z', 1, 2, 3];
            File.WriteAllBytes(path, content);
            File.WriteAllText(path + ".sha256", Convert.ToHexString(SHA256.HashData(content)));
        }

        HookDllService.ValidateCanonicalModPlugins(_directory);

        File.WriteAllText(
            Path.Combine(_directory, HookDllService.CanonicalModPluginFileNames[0]) + ".sha256",
            new string('0', 64));
        Assert.Throws<CryptographicException>(() =>
            HookDllService.ValidateCanonicalModPlugins(_directory));
    }

    [Fact]
    public void ValidateLocalPluginDll_AcceptsPortableExecutableHeader()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "scbl_gameplay.dll");
        File.WriteAllBytes(path, [(byte)'M', (byte)'Z', 0, 0]);

        HookDllService.ValidateLocalPluginDll(path);
    }

    [Fact]
    public void ValidateLocalPluginDll_RejectsNonPortableExecutable()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "scbl_diagnostics.dll");
        File.WriteAllText(path, "not-a-dll");

        Assert.Throws<InvalidDataException>(() => HookDllService.ValidateLocalPluginDll(path));
    }

    [Fact]
    public void DeployAtomically_CreatesPreviouslyMissingPlugin()
    {
        Directory.CreateDirectory(_directory);
        string source = Path.Combine(_directory, "source.dll");
        string target = Path.Combine(_directory, "runtime", "scbl_gameplay.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        byte[] content = [(byte)'M', (byte)'Z', 1, 2, 3];
        File.WriteAllBytes(source, content);
        string expectedHash = Convert.ToHexString(SHA256.HashData(content));

        HookDllService.DeployAtomically(
            target,
            expectedHash,
            temporaryPath => File.Copy(source, temporaryPath, overwrite: true));

        Assert.Equal(content, File.ReadAllBytes(target));
        Assert.False(File.Exists(target + ".scbl-new"));
        Assert.False(File.Exists(target + ".scbl-rollback"));
    }

    [Fact]
    public void Official_mode_quarantines_every_gameplay_and_diagnostics_load_name()
    {
        Directory.CreateDirectory(_directory);
        foreach (string fileName in HookDllService.OfficialForbiddenPluginFileNames)
            File.WriteAllBytes(Path.Combine(_directory, fileName), [(byte)'M', (byte)'Z', 1]);

        HookDllService.QuarantineOfficialForbiddenPlugins(_directory);

        foreach (string fileName in HookDllService.OfficialForbiddenPluginFileNames)
        {
            Assert.False(File.Exists(Path.Combine(_directory, fileName)));
            Assert.Single(Directory.GetFiles(_directory, fileName + ".scbl-disabled-*"));
        }
    }

    [Fact]
    public void Mod_runtime_observation_requires_complete_validated_local_source_set_before_launch()
    {
        Directory.CreateDirectory(_directory);
        string core = Path.Combine(_directory, "uplay_r1_loader.dll");
        File.WriteAllBytes(core, Encoding.ASCII.GetBytes("MZ\0" + HookDllService.RequiredHooksConfigProtocol + "\0payload"));
        foreach (string fileName in HookDllService.LocalTestPluginFileNames.SkipLast(1))
            File.WriteAllBytes(Path.Combine(_directory, fileName), [(byte)'M', (byte)'Z', 1]);

        Assert.Throws<FileNotFoundException>(() =>
            HookDllService.ObserveValidatedModSourceFiles(core));

        File.WriteAllBytes(Path.Combine(_directory, HookDllService.LocalTestPluginFileNames[^1]), [(byte)'M', (byte)'Z', 2]);
        ModRuntimeObservation ready = HookDllService.ObserveValidatedModSourceFiles(core);
        Assert.True(ready.FilesValidated);
        Assert.False(ready.RuntimeLoaded);
        Assert.NotEmpty(ready.ObservationId);
    }

    [Fact]
    public async Task Io_bound_launcher_work_yields_the_calling_thread()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        Task operation = HookDllService.RunIoBoundAsync(() =>
        {
            started.Set();
            release.Wait();
        });

        Assert.True(started.Wait(TimeSpan.FromSeconds(2)));
        Assert.False(operation.IsCompleted);
        release.Set();
        await operation.WaitAsync(TimeSpan.FromSeconds(2));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
