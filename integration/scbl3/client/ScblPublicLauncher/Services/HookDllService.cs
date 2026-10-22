using SplinterCellCNLauncher.Models;
using SplinterCellCNLauncher.ApplicationLayer;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SplinterCellCNLauncher.Services;

public sealed class HookDllService
{
    private readonly LauncherRuntimeContext _runtimeContext;

    public HookDllService(LauncherRuntimeContext runtimeContext)
    {
        _runtimeContext = runtimeContext ?? throw new ArgumentNullException(nameof(runtimeContext));
    }

    internal const string RequiredHooksConfigProtocol = "SCBL_HOOKS_CONFIG=scbl.toml.v1";
    internal const string HooksComponentVersionPrefix = "SCBL_HOOKS_VERSION=";

    internal static readonly string[] LocalTestPluginFileNames =
    [
        "scbl_gameplay.dll"
    ];

    internal static readonly string[] LocalTestOptionalPluginFileNames = [];

    internal static readonly string[] CanonicalModPluginFileNames =
    [
        "scbl_gameplay.dll"
    ];

    internal static readonly string[] OfficialForbiddenPluginFileNames =
    [
        "scbl_audio.dll",
        "scbl_gameplay.dll",
        "scbl_mod_runtime.dll",
        "scbl_diagnostics.dll",
        "scbl_diagnostics_runtime.dll"
    ];

    private static readonly string[] RequiredEmbeddedFiles =
    [
        "00000001.meta",
        "00000001.sav",
        "00000002.meta",
        "00000002.sav"
    ];

    public void ValidateEmbeddedFiles()
    {
        var missing = RequiredEmbeddedFiles
            .Where(x => !EmbeddedResourceService.EmbeddedFileExists(x))
            .ToList();

        if (missing.Count > 0)
        {
            throw new Exception(
                "缺少内置存档资源：" + string.Join(", ", missing) + Environment.NewLine +
                "请重新下载完整客户端。");
        }
    }

    public Task DeployHookDllSafelyAsync(
        string gameDir,
        string? gameplayMode = null,
        CancellationToken cancellationToken = default)
        => RunIoBoundAsync(
            () => DeployHookDllSafely(gameDir, gameplayMode),
            cancellationToken);

    private void DeployHookDllSafely(string gameDir, string? gameplayMode)
    {
        ValidateEmbeddedFiles();

        if (IsGameRunning())
            throw new Exception("检测到游戏正在运行，请先关闭游戏后再启动。");

        string dllPath = Path.Combine(gameDir, "uplay_r1_loader.dll");
        string backupPath = Path.Combine(gameDir, "uplay_r1_loader.orig.dll");

        if (!File.Exists(dllPath))
            throw new Exception("游戏目录错误：未找到 uplay_r1_loader.dll。");

        if (!File.Exists(backupPath))
        {
            File.Copy(dllPath, backupPath, overwrite: false);
            LogService.Info("已创建 uplay_r1_loader.orig.dll 备份。");
        }
        else
        {
            LogService.Info("检测到 uplay_r1_loader.orig.dll，保留现有备份。");
        }

        GameplayMode mode = GameplayModeNames.ParseOrOfficial(gameplayMode);
        BootstrapHook? localTestHook = ResolveLocalTestHook();
        string? modPluginSourceDirectory = null;
        IReadOnlyList<string> modPluginFileNames = Array.Empty<string>();
        if (mode == GameplayMode.Mod)
        {
            if (localTestHook != null)
            {
                ValidateLocalTestPlugins(localTestHook.FilePath);
                modPluginSourceDirectory = Path.GetDirectoryName(
                    Path.GetFullPath(localTestHook.FilePath));
                modPluginFileNames = GetLocalTestPluginFileNames(
                    modPluginSourceDirectory!);
            }
            else
            {
                modPluginSourceDirectory = ResolveCanonicalModPluginDirectory();
                modPluginFileNames = CanonicalModPluginFileNames;
            }
        }

        if (mode == GameplayMode.Official)
            QuarantineOfficialForbiddenPlugins(gameDir);

        BootstrapHook bootstrap = localTestHook == null ? ResolveBootstrapHook() : BootstrapHook.Empty;
        string expectedHash = localTestHook?.Sha256 ?? bootstrap.Sha256;
        string sourcePath = localTestHook?.FilePath ?? bootstrap.FilePath;
        string sourceDescription = localTestHook != null
            ? "local-test-override"
            : "tools-canonical";
        string sourceChannel = localTestHook != null ? "test" : _runtimeContext.UpdateChannelName;
        string sourceVersion = localTestHook?.Version ?? bootstrap.Version;

        if (string.IsNullOrWhiteSpace(expectedHash) || !File.Exists(sourcePath))
            throw new Exception("专用联机组件部署失败：没有可用且已校验的 Hooks 组件。请使用完整客户端修复安装。");

        ValidateHooksConfigProtocol(sourcePath);

        string beforeHash = ComputeFileSha256BestEffort(dllPath);
        if (beforeHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            LogService.Info($"当前 uplay_r1_loader.dll 已是目标版本，仍会刷新部署标记。Source={sourceDescription}, Sha256={expectedHash}");
        }
        else
        {
            LogService.Info($"准备覆盖 uplay_r1_loader.dll。Current={beforeHash}, Expected={expectedHash}, Source={sourceDescription}");
        }

        DeployAtomically(
            dllPath,
            expectedHash,
            temporaryPath => File.Copy(sourcePath, temporaryPath, overwrite: true));

        string afterHash = ComputeFileSha256BestEffort(dllPath);
        if (!afterHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new Exception("专用联机组件部署失败：写入后的 uplay_r1_loader.dll 校验不一致。请检查杀软或文件权限。");

        ValidateHooksConfigProtocol(dllPath);

        if (mode == GameplayMode.Mod && modPluginSourceDirectory != null)
            DeployModPlugins(modPluginSourceDirectory, modPluginFileNames, gameDir);

        WriteDeployMarker(gameDir, afterHash, sourceChannel, sourceVersion, sourceDescription);
    }

    public Task<ModRuntimeObservation> ObserveModRuntimeFilesAsync(
        CancellationToken cancellationToken = default)
        => RunIoBoundAsync(ObserveModRuntimeFiles, cancellationToken);

    private ModRuntimeObservation ObserveModRuntimeFiles()
    {
        try
        {
            BootstrapHook? localTestHook = ResolveLocalTestHook();
            if (localTestHook != null)
                return ObserveValidatedModSourceFiles(localTestHook.FilePath);

            BootstrapHook canonicalHook = ResolveBootstrapHook();
            string canonicalPluginDirectory = ResolveCanonicalModPluginDirectory();
            return CreateModRuntimeObservation(
                canonicalHook.FilePath,
                canonicalPluginDirectory,
                CanonicalModPluginFileNames);
        }
        catch (Exception ex)
        {
            LogService.Warning($"Local MOD runtime observation failed closed: {ex.Message}");
            return ModRuntimeObservation.Unavailable;
        }
    }

    internal static ModRuntimeObservation ObserveValidatedModSourceFiles(string localCorePath)
    {
        ValidateHooksConfigProtocol(localCorePath);
        ValidateLocalTestPlugins(localCorePath);
        string sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(localCorePath))
            ?? throw new InvalidDataException("本地测试 Hooks 路径没有有效目录。");
        return CreateModRuntimeObservation(
            localCorePath,
            sourceDirectory,
            GetLocalTestPluginFileNames(sourceDirectory));
    }

    private static ModRuntimeObservation CreateModRuntimeObservation(
        string corePath,
        string sourceDirectory,
        IReadOnlyList<string> pluginFileNames)
    {
        var hashes = new List<string>(pluginFileNames.Count + 1)
        {
            ComputeFileSha256BestEffort(corePath)
        };
        foreach (string fileName in pluginFileNames)
            hashes.Add(ComputeFileSha256BestEffort(Path.Combine(sourceDirectory, fileName)));
        if (hashes.Any(string.IsNullOrWhiteSpace))
            return ModRuntimeObservation.Unavailable;

        string observation = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(";", hashes))))
            .ToLowerInvariant()[..16];
        return new ModRuntimeObservation(
            FilesValidated: true,
            RuntimeLoaded: false,
            ObservationId: observation);
    }

    internal static void QuarantineOfficialForbiddenPlugins(string gameDir)
    {
        foreach (string fileName in OfficialForbiddenPluginFileNames)
        {
            string path = Path.Combine(gameDir, fileName);
            if (!File.Exists(path))
                continue;

            string hash = ComputeFileSha256BestEffort(path);
            if (string.IsNullOrWhiteSpace(hash))
                throw new IOException($"Official mode cannot verify forbidden plugin before quarantine: {fileName}");
            string quarantine = path + $".scbl-disabled-{hash[..12].ToLowerInvariant()}";
            if (File.Exists(quarantine))
            {
                string existingHash = ComputeFileSha256BestEffort(quarantine);
                if (!existingHash.Equals(hash, StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"Official mode quarantine collision for {fileName}.");
                File.Delete(path);
            }
            else
            {
                File.Move(path, quarantine);
            }
            if (File.Exists(path))
                throw new IOException($"Official mode failed to remove forbidden plugin from the load directory: {fileName}");
            LogService.Warning($"Official mode quarantined forbidden plugin: file={fileName}, sha256={hash}");
        }
    }

    private static void DeployModPlugins(
        string sourceDirectory,
        IReadOnlyList<string> pluginFileNames,
        string gameDir)
    {
        var plugins = pluginFileNames
            .Select(fileName => new
            {
                FileName = fileName,
                SourcePath = Path.Combine(sourceDirectory, fileName),
                TargetPath = Path.Combine(gameDir, fileName)
            })
            .ToArray();

        foreach (var plugin in plugins)
        {
            string expectedHash = ComputeFileSha256BestEffort(plugin.SourcePath);
            if (string.IsNullOrWhiteSpace(expectedHash))
                throw new IOException($"无法读取本地测试附加组件：{plugin.FileName}");

            DeployAtomically(
                plugin.TargetPath,
                expectedHash,
                temporaryPath => File.Copy(plugin.SourcePath, temporaryPath, overwrite: true));

            LogService.Warning(
                $"Test channel deployed local Hooks plugin. File={plugin.FileName}, Sha256={expectedHash}");
        }
    }

    internal static void ValidateLocalTestPlugins(string localCorePath)
    {
        string sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(localCorePath))
            ?? throw new InvalidDataException("本地测试 Hooks 路径没有有效目录。");

        // Validate the complete set before replacing the core runtime DLL. This prevents
        // a new core DLL from being paired with missing development plugins.
        foreach (string fileName in LocalTestPluginFileNames)
        {
            string sourcePath = Path.Combine(sourceDirectory, fileName);
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException(
                    $"本地测试 Hooks 缺少附加组件 {fileName}，已停止启动以避免混合版本。",
                    sourcePath);
            }

            ValidateLocalPluginDll(sourcePath);
        }
        foreach (string fileName in LocalTestOptionalPluginFileNames)
        {
            string sourcePath = Path.Combine(sourceDirectory, fileName);
            if (File.Exists(sourcePath))
                ValidateLocalPluginDll(sourcePath);
        }
    }

    private static string[] GetLocalTestPluginFileNames(string sourceDirectory)
        => LocalTestPluginFileNames
            .Concat(LocalTestOptionalPluginFileNames.Where(
                fileName => File.Exists(Path.Combine(sourceDirectory, fileName))))
            .ToArray();

    internal static void ValidateCanonicalModPlugins(string sourceDirectory)
    {
        foreach (string fileName in CanonicalModPluginFileNames)
        {
            string sourcePath = Path.Combine(sourceDirectory, fileName);
            string sidecarPath = sourcePath + ".sha256";
            if (!File.Exists(sourcePath) || !File.Exists(sidecarPath))
                throw new FileNotFoundException(
                    $"已冻结的 MOD 组件不完整：{fileName}", sourcePath);
            ValidateLocalPluginDll(sourcePath);
            Match sidecarHash = Regex.Match(
                File.ReadAllText(sidecarPath),
                "(?i)^\\s*([0-9a-f]{64})(?:\\s+\\*?[^\\r\\n]+)?\\s*$",
                RegexOptions.CultureInvariant);
            if (!sidecarHash.Success)
                throw new InvalidDataException($"MOD 组件 SHA256 文件格式无效：{fileName}");
            string actualHash = ComputeFileSha256BestEffort(sourcePath);
            if (!actualHash.Equals(sidecarHash.Groups[1].Value, StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException($"MOD 组件 SHA256 不一致：{fileName}");
        }
    }

    private static string ResolveCanonicalModPluginDirectory()
    {
        string directory = ClientInstallLayout.Resolve(
            AppContext.BaseDirectory,
            ClientInstallLayout.HooksModDirectoryRelativePath);
        ValidateCanonicalModPlugins(directory);
        return directory;
    }

    private BootstrapHook? ResolveLocalTestHook()
    {
        if (_runtimeContext.UpdateChannel != ClientUpdateChannel.Test)
            return null;

        string configured = (Environment.GetEnvironmentVariable("SCBL_LOCAL_HOOKS_DLL") ?? "").Trim();
        string path = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, "local-components", "hooks", "uplay_r1_loader.dll")
            : Path.GetFullPath(configured);
        if (!File.Exists(path))
            return null;

        if (!TryReadInstalledHooksBaseline(out string baselineVersion, out _))
        {
            LogService.Warning(
                $"Local Hooks override rejected: test channel baseline is unavailable or no longer matches the verified canonical DLL. Path={path}");
            return null;
        }

        string localVersion = ReadHooksComponentVersion(path);
        if (!IsLocalOverrideVersionAllowed(_runtimeContext.UpdateChannel, localVersion, baselineVersion))
        {
            string reason = string.IsNullOrWhiteSpace(localVersion)
                ? "missing-or-invalid-version"
                : "version-below-test-baseline";
            LogService.Warning(
                $"Local Hooks override rejected: reason={reason}, localVersion={localVersion}, testBaseline={baselineVersion}, path={path}");
            return null;
        }

        string hash = ComputeFileSha256BestEffort(path);
        if (string.IsNullOrWhiteSpace(hash))
            throw new IOException("本地测试 Hooks 无法读取：" + path);

        LogService.Warning(
            $"Test channel accepted local Hooks override: localVersion={localVersion}, testBaseline={baselineVersion}, path={path}, sha256={hash}");
        return new BootstrapHook(path, hash, localVersion);
    }

    private bool TryReadInstalledHooksBaseline(out string version, out string sha256)
    {
        version = "";
        sha256 = "";
        IReadOnlyDictionary<string, (string Version, string Sha256)> installed =
            InstalledComponentReceiptService.Read(_runtimeContext.UpdateChannelName);
        if (!installed.TryGetValue("hooks", out (string Version, string Sha256) hooks)
            || !TryParseComparableVersion(hooks.Version, out _)
            || !IsSha256(hooks.Sha256))
        {
            return false;
        }
        version = hooks.Version.Trim();
        sha256 = hooks.Sha256.Trim();
        return true;
    }

    internal static bool IsLocalOverrideVersionAllowed(
        ClientUpdateChannel channel,
        string? localVersion,
        string? channelBaselineVersion)
    {
        if (channel != ClientUpdateChannel.Test
            || !TryParseComparableVersion(localVersion, out Version? local)
            || !TryParseComparableVersion(channelBaselineVersion, out Version? baseline))
        {
            return false;
        }
        return local.CompareTo(baseline) >= 0;
    }

    internal static string ReadHooksComponentVersion(string path)
    {
        byte[] content;
        try
        {
            content = File.ReadAllBytes(path);
        }
        catch
        {
            return "";
        }

        ReadOnlySpan<byte> prefix = Encoding.ASCII.GetBytes(HooksComponentVersionPrefix);
        int marker = content.AsSpan().IndexOf(prefix);
        if (marker < 0)
            return "";
        int start = marker + prefix.Length;
        int end = start;
        while (end < content.Length
               && end - start < 32
               && content[end] != 0
               && !char.IsWhiteSpace((char)content[end]))
        {
            end++;
        }
        string value = Encoding.ASCII.GetString(content, start, end - start).Trim();
        return TryParseComparableVersion(value, out _) ? value : "";
    }

    private static bool TryParseComparableVersion(string? value, out Version version)
    {
        string normalized = (value ?? "").Trim();
        if (normalized.StartsWith('v'))
            normalized = normalized[1..];
        if (Version.TryParse(normalized, out Version? parsed))
        {
            version = parsed;
            return true;
        }
        version = new Version(0, 0);
        return false;
    }

    private BootstrapHook ResolveBootstrapHook()
    {
        string root = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string dll = ClientInstallLayout.Resolve(root, ClientInstallLayout.HooksRelativePath);
        string sidecar = dll + ".sha256";

        if (!File.Exists(dll) || !File.Exists(sidecar))
            throw new FileNotFoundException("完整客户端缺少 bootstrap Hooks 或 SHA256 校验文件。", dll);

        string text = File.ReadAllText(sidecar);
        Match match = Regex.Match(text, "(?i)\\b[0-9a-f]{64}\\b", RegexOptions.CultureInvariant);
        if (!match.Success)
            throw new InvalidDataException("bootstrap Hooks SHA256 文件格式无效。");

        string expected = match.Value.ToUpperInvariant();
        string actual = ComputeFileSha256BestEffort(dll);
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new CryptographicException($"bootstrap Hooks SHA256 不一致。expected={expected}, actual={actual}");

        if (!TryReadInstalledHooksBaseline(out string version, out string receiptSha256)
            || !IsCanonicalHookIdentityValid(receiptSha256, expected, actual))
        {
            throw new CryptographicException(
                $"bootstrap Hooks 不属于当前 {_runtimeContext.UpdateChannelName} 通道的已验证组件，请重新检查组件更新。");
        }

        return new BootstrapHook(dll, actual, version);
    }

    internal static bool IsCanonicalHookIdentityValid(
        string? channelReceiptSha256,
        string? sidecarSha256,
        string? actualSha256)
        => IsSha256(channelReceiptSha256)
           && IsSha256(sidecarSha256)
           && IsSha256(actualSha256)
           && channelReceiptSha256!.Equals(sidecarSha256, StringComparison.OrdinalIgnoreCase)
           && channelReceiptSha256.Equals(actualSha256, StringComparison.OrdinalIgnoreCase);

    private static bool IsSha256(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && value.Length == 64
           && value.All(Uri.IsHexDigit);

    internal static void DeployAtomically(string targetPath, string expectedHash, Action<string> writeTemporary)
    {
        string temporaryPath = targetPath + ".scbl-new";
        string rollbackPath = targetPath + ".scbl-rollback";
        bool targetExisted = File.Exists(targetPath);
        TryDelete(temporaryPath);
        TryDelete(rollbackPath);

        try
        {
            writeTemporary(temporaryPath);
            if (!File.Exists(temporaryPath))
                throw new IOException("Hooks 临时文件未成功写入。");

            string temporaryHash = ComputeFileSha256BestEffort(temporaryPath);
            if (!temporaryHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException($"Hooks 临时文件校验不一致。expected={expectedHash}, actual={temporaryHash}");

            if (targetExisted)
                File.Copy(targetPath, rollbackPath, overwrite: true);
            File.Move(temporaryPath, targetPath, overwrite: true);

            string finalHash = ComputeFileSha256BestEffort(targetPath);
            if (!finalHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException($"Hooks 原子替换后校验不一致。expected={expectedHash}, actual={finalHash}");

            TryDelete(rollbackPath);
        }
        catch
        {
            TryDelete(temporaryPath);
            if (File.Exists(rollbackPath))
                File.Move(rollbackPath, targetPath, overwrite: true);
            else if (!targetExisted)
                TryDelete(targetPath);
            throw;
        }
    }

    public void RestoreOriginalDllBestEffort(string gameDir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir))
                return;
            if (IsGameRunning())
                return;

            string dllPath = Path.Combine(gameDir, "uplay_r1_loader.dll");
            string backupPath = Path.Combine(gameDir, "uplay_r1_loader.orig.dll");
            if (!File.Exists(backupPath))
                return;

            string currentHash = File.Exists(dllPath) ? ComputeFileSha256BestEffort(dllPath) : "";
            string markerHash = ReadDeployMarkerHash(gameDir);
            string bootstrapHash = TryGetBootstrapHash();
            bool isScblHook = !string.IsNullOrWhiteSpace(currentHash)
                && ((!string.IsNullOrWhiteSpace(markerHash)
                     && currentHash.Equals(markerHash, StringComparison.OrdinalIgnoreCase))
                    || (!string.IsNullOrWhiteSpace(bootstrapHash)
                        && currentHash.Equals(bootstrapHash, StringComparison.OrdinalIgnoreCase)));

            if (isScblHook)
            {
                File.Copy(backupPath, dllPath, overwrite: true);
                LogService.Info("Original uplay_r1_loader.dll restored for direct game launch.");
            }
            else
            {
                LogService.Info("Original DLL restore skipped: current uplay_r1_loader.dll is not the last verified SCBL Hook or cannot be verified.");
            }
        }
        catch (Exception ex)
        {
            LogService.Error("Restore original uplay_r1_loader.dll failed: " + ex.Message);
        }
    }

    private string TryGetBootstrapHash()
    {
        try
        {
            return ResolveBootstrapHook().Sha256;
        }
        catch
        {
            return "";
        }
    }

    private static bool IsGameRunning()
    {
        return Process.GetProcessesByName("Blacklist_game").Any()
            || Process.GetProcessesByName("Blacklist_DX11_game").Any();
    }

    private static string ComputeFileSha256BestEffort(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch
        {
            return "";
        }
    }

    internal static Task RunIoBoundAsync(
        Action operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return Task.Run(operation, cancellationToken);
    }

    internal static Task<T> RunIoBoundAsync<T>(
        Func<T> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return Task.Run(operation, cancellationToken);
    }

    internal static void ValidateHooksConfigProtocol(string path)
    {
        byte[] marker = Encoding.ASCII.GetBytes(RequiredHooksConfigProtocol);
        byte[] content;
        try
        {
            content = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            throw new IOException("无法读取 Hooks 组件以验证配置协议。", ex);
        }

        if (content.AsSpan().IndexOf(marker) < 0)
        {
            throw new InvalidDataException(
                "Hooks 组件不支持当前配置协议。" +
                Environment.NewLine + "请使用完整的最新客户端修复安装。");
        }
    }

    internal static void ValidateLocalPluginDll(string path)
    {
        byte[] header = new byte[2];
        try
        {
            using FileStream stream = File.OpenRead(path);
            if (stream.Length < header.Length || stream.Read(header, 0, header.Length) != header.Length)
                throw new InvalidDataException("本地测试附加组件不是有效的 DLL 文件。");
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new IOException("无法读取本地测试附加组件。", ex);
        }

        if (header[0] != (byte)'M' || header[1] != (byte)'Z')
            throw new InvalidDataException("本地测试附加组件不是有效的 Windows DLL 文件。");
    }

    private static string ReadDeployMarkerHash(string gameDir)
    {
        try
        {
            string path = Path.Combine(gameDir, "5th_cn_component.json");
            if (!File.Exists(path))
                return "";
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("Sha256", out JsonElement value)
                ? value.GetString()?.Trim() ?? ""
                : "";
        }
        catch
        {
            return "";
        }
    }

    private static void WriteDeployMarker(
        string gameDir,
        string dllSha256,
        string channel,
        string version,
        string sourceDescription)
    {
        var marker = new
        {
            Component = "SCBL",
            DeployedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Dll = "uplay_r1_loader.dll",
            Sha256 = dllSha256,
            Channel = channel,
            Version = version,
            Source = sourceDescription
        };

        string path = Path.Combine(gameDir, "5th_cn_component.json");
        File.WriteAllText(path, JsonSerializer.Serialize(marker, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Replacement or rollback reports the actual error.
        }
    }

    private sealed record BootstrapHook(string FilePath, string Sha256, string Version)
    {
        public static BootstrapHook Empty { get; } = new("", "", "");
    }
}
