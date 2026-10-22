using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using SplinterCellCNLauncher.Models;

namespace SplinterCellCNLauncher.Services;

/// <summary>
/// Applies components that were downloaded and verified during the previous run.
/// It only consumes component_state.json for the currently selected update channel.
/// Runtime bundles are applied before EasyTier/Route Guard are started. Hooks is
/// promoted into its component directory after manifest reconciliation, then copied from there into
/// the game directory immediately before game start.
/// </summary>
public static class StagedComponentBootstrapService
{
    private sealed class ComponentStateDocument
    {
        public string Channel { get; set; } = "";
        public Dictionary<string, ComponentStateEntry> Components { get; set; }
            = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ComponentStateEntry
    {
        public string Version { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public string FilePath { get; set; } = "";
    }

    private sealed class ClientFilesManifest
    {
        public int SchemaVersion { get; set; }
        public List<ClientFileRecord> Files { get; set; } = new();
        public List<string> Delete { get; set; } = new();
    }

    private sealed class ClientFileRecord
    {
        public string Path { get; set; } = "";
        public long Size { get; set; }
        public string Sha256 { get; set; } = "";
    }

    private static readonly HashSet<string> ProtectedClientFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "SCBL.exe",
        ClientInstallLayout.UpdaterRelativePath,
        ClientInstallLayout.EasyTierCoreRelativePath,
        ClientInstallLayout.EasyTierCliRelativePath,
        ClientInstallLayout.PacketDllRelativePath,
        ClientInstallLayout.WintunDllRelativePath,
        ClientInstallLayout.RouteGuardExecutableRelativePath,
        ClientInstallLayout.HooksRelativePath,
        ClientInstallLayout.WinDivertDllRelativePath,
        ClientInstallLayout.WinDivertPayloadRelativePath
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static void ApplyUpdaterAndEasyTier(string channel)
    {
        ComponentStateDocument? state = LoadSelectedChannelState(channel);
        if (state == null)
            return;

        if (state.Components.TryGetValue("updater", out ComponentStateEntry? updater))
        {
            string baseDir = GetBaseDirectory();
            string target = ClientInstallLayout.Resolve(
                baseDir,
                ClientInstallLayout.UpdaterRelativePath);
            IReadOnlyDictionary<string, string> installed = ApplySingleFile("updater", updater, target);
            CompleteApplication(state, "updater", updater, installed);
        }

        if (state.Components.TryGetValue("easytier", out ComponentStateEntry? easytier))
        {
            if (IsAnyProcessRunning("easytier-core", "easytier-cli"))
            {
                LogService.Warning("EasyTier component update remains staged because an EasyTier process is still running.");
                return;
            }

            IReadOnlyDictionary<string, string> installed = ApplyZipBundle(
                "easytier",
                easytier,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["easytier-core.exe"] = ClientInstallLayout.EasyTierCoreRelativePath,
                    ["easytier-cli.exe"] = ClientInstallLayout.EasyTierCliRelativePath,
                    ["Packet.dll"] = ClientInstallLayout.PacketDllRelativePath,
                    ["wintun.dll"] = ClientInstallLayout.WintunDllRelativePath
                });
            CompleteApplication(state, "easytier", easytier, installed);
        }
    }

    public static void ApplyClientFiles(VerifiedClientComponent bundle)
    {
        var entry = new ComponentStateEntry
        {
            Version = bundle.Version,
            Sha256 = bundle.Sha256,
            FilePath = bundle.FilePath
        };
        IReadOnlyDictionary<string, string> previouslyOwned =
            InstalledComponentReceiptService.ReadOwnedClientFiles(bundle.Channel, bundle.Name);
        IReadOnlyDictionary<string, string> installed = ApplyManagedClientFilesBundle(
            bundle.Name,
            entry,
            previouslyOwnedFiles: previouslyOwned);
        InstalledComponentReceiptService.MarkApplied(
            bundle.Channel,
            bundle.Name,
            bundle.Version,
            bundle.Sha256,
            new FileInfo(bundle.FilePath).Length,
            installed);
        TryDeleteAppliedPayload(
            LogService.ComponentsDirectory,
            bundle.FilePath,
            message => LogService.Warning(message));
        LogService.Info($"Client file bundle applied immediately: bundle={bundle.Name}, version={bundle.Version}");
    }

    private static IReadOnlyDictionary<string, string> ApplyManagedClientFilesBundle(
        string component,
        ComponentStateEntry entry,
        string? clientRoot = null,
        IReadOnlyDictionary<string, string>? previouslyOwnedFiles = null)
    {
        VerifyCachedComponent(component, entry);
        using ZipArchive archive = ZipFile.OpenRead(entry.FilePath);
        ZipArchiveEntry manifestEntry = archive.GetEntry("client-files-manifest.json")
            ?? throw new InvalidDataException($"组件 {component} 缺少文件清单。");
        ClientFilesManifest manifest;
        using (Stream stream = manifestEntry.Open())
        {
            manifest = JsonSerializer.Deserialize<ClientFilesManifest>(stream, JsonOptions)
                ?? throw new InvalidDataException($"组件 {component} 文件清单无法解析。");
        }
        if (manifest.SchemaVersion != 1
            || (manifest.Files.Count == 0 && (manifest.Delete?.Count ?? 0) == 0))
            throw new InvalidDataException($"组件 {component} 文件清单版本无效或没有操作。");

        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (ZipArchiveEntry candidate in archive.Entries)
        {
            string normalized = candidate.FullName.Replace('\\', '/');
            if (entries.ContainsKey(normalized))
                throw new InvalidDataException($"组件 {component} 包含重复路径：{normalized}");
            entries[normalized] = candidate;
        }

        string workRoot = Path.Combine(
            LogService.UpdatesDirectory,
            "component-work",
            $"scbl-{component}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workRoot);
        try
        {
            var installs = new List<ComponentFileInstall>();
            var installedRelativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int index = 0;
            foreach (ClientFileRecord file in manifest.Files)
            {
                string relative = NormalizeManagedClientPath(file.Path);
                if (!installedRelativePaths.Add(relative))
                    throw new InvalidDataException($"组件 {component} 文件清单重复：{relative}");
                if (file.Size < 0
                    || string.IsNullOrWhiteSpace(file.Sha256)
                    || file.Sha256.Length != 64
                    || file.Sha256.Any(ch => !Uri.IsHexDigit(ch)))
                    throw new InvalidDataException($"组件 {component} 文件记录无效：{relative}");
                string archivePath = "files/" + relative;
                if (!entries.TryGetValue(archivePath, out ZipArchiveEntry? sourceEntry)
                    || sourceEntry.Length != file.Size)
                {
                    throw new InvalidDataException($"组件 {component} 缺少文件或大小不符：{relative}");
                }

                string extractedPath = Path.Combine(workRoot, $"{index++:D4}.payload");
                using (Stream input = sourceEntry.Open())
                using (FileStream output = new(extractedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    input.CopyTo(output);
                string actualHash = ComputeSha256(extractedPath);
                if (!actualHash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new CryptographicException($"组件 {component} 文件 SHA256 不符：{relative}");
                installs.Add(new ComponentFileInstall(
                    extractedPath,
                    ResolveManagedClientTarget(relative, clientRoot),
                    actualHash));
            }

            var deleteTargets = new List<(string Relative, ComponentFileDelete Delete)>();
            var uniqueDeletes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string deletePath in manifest.Delete ?? new List<string>())
            {
                string relative = NormalizeManagedClientPath(deletePath);
                if (!uniqueDeletes.Add(relative))
                    continue;
                if (installedRelativePaths.Contains(relative))
                    throw new InvalidDataException($"组件 {component} 同时安装和删除文件：{relative}");
                if (ProtectedClientFiles.Contains(relative))
                    throw new InvalidDataException($"组件 {component} 不允许删除运行组件：{relative}");
                string fullPath = ResolveManagedClientTarget(relative, clientRoot);
                string recoveryBackup = fullPath + ".component-delete-backup";
                if (Directory.Exists(fullPath))
                    throw new InvalidDataException($"Component {component} deletion list may contain files only: {relative}");
                if (!File.Exists(fullPath) && !File.Exists(recoveryBackup))
                    continue;
                if (previouslyOwnedFiles == null
                    || !previouslyOwnedFiles.TryGetValue(fullPath, out string? expectedHash))
                {
                    LogService.Warning($"Preserved obsolete client file because no trusted receipt owns it: {relative}");
                    continue;
                }
                EnsureLiteralManagedFileTarget(fullPath, clientRoot);
                if (File.Exists(recoveryBackup))
                    EnsureLiteralManagedFileTarget(recoveryBackup, clientRoot);
                string currentPath = File.Exists(fullPath) ? fullPath : recoveryBackup;
                string actualHash = ComputeSha256(currentPath);
                if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    LogService.Warning($"Preserved user-modified managed client file: {relative}");
                    continue;
                }
                deleteTargets.Add((relative, new ComponentFileDelete(fullPath, expectedHash)));
            }

            TransactionalComponentInstaller.Install(
                installs,
                deleteTargets.Select(item => item.Delete).ToArray());
            foreach ((string relative, ComponentFileDelete delete) in deleteTargets)
            {
                string fullPath = delete.TargetPath;
                if (Directory.Exists(fullPath))
                    throw new InvalidDataException($"组件 {component} 删除清单只能包含文件：{relative}");
                LogService.Info($"Removed obsolete managed client file: {relative}");
            }

            var installed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (ComponentFileInstall item in installs)
                installed[item.TargetPath] = item.ExpectedSha256;
            LogService.Info(
                $"Managed client file bundle applied: component={component}, version={entry.Version}, files={installs.Count}, deleted={deleteTargets.Count}");
            return installed;
        }
        finally
        {
            try { Directory.Delete(workRoot, recursive: true); } catch { }
        }
    }

    internal static IReadOnlyDictionary<string, string> ApplyManagedClientFilesBundleForTest(
        string bundlePath,
        string clientRoot,
        IReadOnlyDictionary<string, string>? previouslyOwnedFiles = null)
    {
        var entry = new ComponentStateEntry
        {
            Version = "test",
            Sha256 = ComputeSha256(bundlePath),
            FilePath = bundlePath
        };
        return ApplyManagedClientFilesBundle(
            "client-files",
            entry,
            clientRoot,
            previouslyOwnedFiles);
    }

    private static string NormalizeManagedClientPath(string value)
    {
        string normalized = (value ?? "").Trim().Replace('\\', '/');
        string[] parts = normalized.Split('/');
        if (string.IsNullOrWhiteSpace(normalized)
            || Path.IsPathRooted(normalized)
            || normalized.Contains(':')
            || parts.Any(part => string.IsNullOrWhiteSpace(part) || part is "." or ".."))
        {
            throw new InvalidDataException("客户端文件清单包含不安全路径：" + value);
        }
        return normalized;
    }

    private static string ResolveManagedClientTarget(string relative, string? clientRoot)
    {
        string root = Path.GetFullPath(string.IsNullOrWhiteSpace(clientRoot) ? GetBaseDirectory() : clientRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string target = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("客户端文件目标超出客户端目录：" + relative);
        return target;
    }

    private static void EnsureLiteralManagedFileTarget(string target, string? clientRoot)
    {
        string root = Path.GetFullPath(string.IsNullOrWhiteSpace(clientRoot) ? GetBaseDirectory() : clientRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string relative = Path.GetRelativePath(root, target);
        string current = root;
        foreach (string segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
                continue;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Managed client deletion target traverses a reparse point.");
        }
    }

    public static void FinalizeLauncherUpdate(string channel)
    {
        ComponentStateDocument? state = LoadSelectedChannelState(channel);
        if (state == null || !state.Components.TryGetValue(ClientComponentUpdateService.LauncherComponentName, out ComponentStateEntry? launcher))
            return;

        string? current = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(current) || !File.Exists(current))
            return;
        if (!ComputeSha256(current).Equals(launcher.Sha256, StringComparison.OrdinalIgnoreCase))
            return;

        LogService.Info($"Launcher component activation confirmed: version={launcher.Version}, target={current}");
        CompleteApplication(
            state,
            ClientComponentUpdateService.LauncherComponentName,
            launcher,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [current] = ComputeSha256(current)
            });
    }

    public static void ApplyHooks(string channel)
    {
        ComponentStateDocument? state = LoadSelectedChannelState(channel);
        if (state == null || !state.Components.TryGetValue("hooks", out ComponentStateEntry? hooks))
            return;

        string target = ClientInstallLayout.Resolve(
            GetBaseDirectory(),
            ClientInstallLayout.HooksRelativePath);
        IReadOnlyDictionary<string, string> installed = ApplySingleFile("hooks", hooks, target);

        string expected = hooks.Sha256.ToUpperInvariant();
        string sidecar = target + ".sha256";
        string temporary = sidecar + ".component-new";
        Directory.CreateDirectory(Path.GetDirectoryName(sidecar)!);
        File.WriteAllText(temporary, expected.ToLowerInvariant() + "  uplay_r1_loader.dll\n");
        File.Move(temporary, sidecar, overwrite: true);
        LogService.Info($"Hooks canonical component copy ready: version={hooks.Version}, target={target}, sha256={expected}");
        CompleteApplication(state, "hooks", hooks, installed);
    }

    public static void ApplyRouteGuard(string channel)
    {
        ComponentStateDocument? state = LoadSelectedChannelState(channel);
        if (state == null || !state.Components.TryGetValue("route-guard", out ComponentStateEntry? routeGuard))
            return;

        if (IsAnyProcessRunning("Blacklist_game", "Blacklist_DX11_game"))
        {
            LogService.Warning("Route Guard component update remains staged because the game is running.");
            return;
        }

        if (!ProcessRouterService.StopAllRouters("staged Route Guard component update"))
        {
            LogService.Warning("Route Guard component update remains staged because a live owned guard could not be stopped safely.");
            return;
        }
        RouteGuardRuntimePaths.EnsureControlledDirectory();
        IReadOnlyDictionary<string, string> installed = ApplyZipBundle(
            "route-guard",
            routeGuard,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["scbl-process-router.exe"] = "scbl-process-router.exe",
                ["WinDivert.dll"] = "WinDivert.dll",
                ["WinDivert64.sys"] = "WinDivert64.sys"
            },
            RouteGuardRuntimePaths.RootDirectory);
        CompleteApplication(state, "route-guard", routeGuard, installed);
    }

    private static void CompleteApplication(
        ComponentStateDocument state,
        string component,
        ComponentStateEntry entry,
        IReadOnlyDictionary<string, string> installedFiles)
    {
        InstalledComponentReceiptService.MarkApplied(
            state.Channel,
            component,
            entry.Version,
            entry.Sha256,
            new FileInfo(entry.FilePath).Length,
            installedFiles);
        state.Components.Remove(component);

        string componentsRoot = Path.GetFullPath(LogService.ComponentsDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string statePath = Path.Combine(componentsRoot, "component_state.json");
        string temporary = statePath + ".tmp";
        if (state.Components.Count == 0)
        {
            if (File.Exists(statePath))
                File.Delete(statePath);
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
        else
        {
            Directory.CreateDirectory(componentsRoot);
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(temporary, statePath, overwrite: true);
        }

        bool removed = TryDeleteAppliedPayload(
            componentsRoot,
            entry.FilePath,
            message => LogService.Warning(message));
        LogService.Info(
            $"Component activated; verified payload cleanup={(removed ? "completed" : "preserved")}: component={component}, version={entry.Version}");
    }

    internal static bool TryDeleteAppliedPayloadForTest(
        string componentsRoot,
        string payloadPath,
        Func<string, FileAttributes>? getAttributes = null)
        => TryDeleteAppliedPayload(componentsRoot, payloadPath, null, getAttributes);

    private static bool TryDeleteAppliedPayload(
        string componentsRoot,
        string payloadPath,
        Action<string>? report,
        Func<string, FileAttributes>? getAttributes = null)
    {
        try
        {
            string root = Path.GetFullPath(componentsRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string? volumeRoot = Path.GetPathRoot(root)?.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(root)
                || root.Equals(volumeRoot, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(payloadPath))
            {
                report?.Invoke("Applied component cache cleanup refused an unsafe root or empty payload path.");
                return false;
            }

            string payload = Path.GetFullPath(payloadPath);
            string rootPrefix = root + Path.DirectorySeparatorChar;
            if (!payload.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                report?.Invoke("Applied component cache cleanup refused a payload outside the components root.");
                return false;
            }

            string relative = Path.GetRelativePath(root, payload);
            string[] parts = relative.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3
                || parts.Any(part => part is "." or ".." || string.IsNullOrWhiteSpace(part)))
            {
                report?.Invoke("Applied component cache cleanup refused a payload outside the owned component/version/file layout.");
                return false;
            }

            if (!File.Exists(payload))
                return true;

            string versionDirectory = Path.GetDirectoryName(payload)!;
            string componentDirectory = Path.GetDirectoryName(versionDirectory)!;
            getAttributes ??= File.GetAttributes;
            foreach (string path in new[] { root, componentDirectory, versionDirectory, payload })
            {
                if ((getAttributes(path) & FileAttributes.ReparsePoint) != 0)
                {
                    report?.Invoke("Applied component cache cleanup refused a reparse point.");
                    return false;
                }
            }

            File.Delete(payload);
            DeleteDirectoryIfEmpty(versionDirectory);
            DeleteDirectoryIfEmpty(componentDirectory);
            DeleteDirectoryIfEmpty(root);
            return true;
        }
        catch (Exception ex)
        {
            report?.Invoke("Applied component cache cleanup preserved payload after a safe failure: " + ex.Message);
            return false;
        }
    }

    private static void DeleteDirectoryIfEmpty(string path)
    {
        if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            Directory.Delete(path);
    }

    private static ComponentStateDocument? LoadSelectedChannelState(string channel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        try
        {
            string path = Path.Combine(LogService.PersistentDataDirectory, "components", "component_state.json");
            if (!File.Exists(path))
                return null;

            ComponentStateDocument? state = JsonSerializer.Deserialize<ComponentStateDocument>(File.ReadAllText(path), JsonOptions);
            if (state == null || !state.Channel.Equals(channel, StringComparison.OrdinalIgnoreCase))
            {
                LogService.Info(
                    $"Staged component state ignored because its channel does not match this launch. state={state?.Channel ?? "missing"}, launch={channel}");
                return null;
            }
            return state;
        }
        catch (Exception ex)
        {
            LogService.Warning("Unable to read staged component state: " + ex.Message);
            return null;
        }
    }

    private static IReadOnlyDictionary<string, string> ApplySingleFile(
        string component,
        ComponentStateEntry entry,
        string targetPath)
    {
        VerifyCachedComponent(component, entry);
        string expected = entry.Sha256.ToUpperInvariant();
        if (File.Exists(targetPath) && ComputeSha256(targetPath).Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            LogService.Info($"Staged component already applied: component={component}, version={entry.Version}, target={targetPath}");
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [targetPath] = expected
            };
        }

        ReplaceFileAtomically(entry.FilePath, targetPath, expected);
        LogService.Info($"Staged component applied: component={component}, version={entry.Version}, target={targetPath}, sha256={expected}");
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [targetPath] = expected
        };
    }

    private static IReadOnlyDictionary<string, string> ApplyZipBundle(
        string component,
        ComponentStateEntry entry,
        IReadOnlyDictionary<string, string> fileMap,
        string? targetRoot = null)
    {
        VerifyCachedComponent(component, entry);
        using ZipArchive archive = ZipFile.OpenRead(entry.FilePath);
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (ZipArchiveEntry candidate in archive.Entries)
        {
            string normalized = candidate.FullName.Replace('\\', '/');
            if (normalized.StartsWith("/", StringComparison.Ordinal)
                || normalized.Split('/').Any(part => part == ".."))
            {
                throw new InvalidDataException($"组件 {component} 压缩包包含不安全路径：{candidate.FullName}");
            }
            if (fileMap.ContainsKey(normalized))
                entries[normalized] = candidate;
        }

        string workRoot = Path.Combine(
            LogService.UpdatesDirectory,
            "component-work",
            $"scbl-{component}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workRoot);
        try
        {
            var installs = new List<ComponentFileInstall>();
            foreach ((string sourceName, string targetRelativePath) in fileMap)
            {
                if (!entries.TryGetValue(sourceName, out ZipArchiveEntry? sourceEntry))
                    throw new InvalidDataException($"组件 {component} 缺少文件：{sourceName}");

                string extractedPath = Path.Combine(workRoot, Path.GetFileName(sourceName));
                using (Stream input = sourceEntry.Open())
                using (FileStream output = new(extractedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    input.CopyTo(output);

                string root = string.IsNullOrWhiteSpace(targetRoot) ? GetBaseDirectory() : Path.GetFullPath(targetRoot);
                string targetPath = Path.GetFullPath(Path.Combine(root, targetRelativePath.Replace('/', Path.DirectorySeparatorChar)));
                if (!targetPath.StartsWith(
                        root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Component {component} target escaped its install root: {targetRelativePath}");
                installs.Add(new ComponentFileInstall(
                    extractedPath,
                    targetPath,
                    ComputeSha256(extractedPath)));
            }

            TransactionalComponentInstaller.Install(installs);
            var installed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (ComponentFileInstall item in installs)
                installed[item.TargetPath] = item.ExpectedSha256;
            LogService.Info($"Staged bundle applied: component={component}, version={entry.Version}, files={fileMap.Count}");
            return installed;
        }
        finally
        {
            try { Directory.Delete(workRoot, recursive: true); } catch { }
        }
    }

    private static void VerifyCachedComponent(string component, ComponentStateEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.FilePath) || !File.Exists(entry.FilePath))
            throw new FileNotFoundException($"缓存组件 {component} 不存在。", entry.FilePath);
        string actual = ComputeSha256(entry.FilePath);
        if (!actual.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new CryptographicException($"缓存组件 {component} SHA256 不一致。expected={entry.Sha256}, actual={actual}");
    }

    private static void ReplaceFileAtomically(string sourcePath, string targetPath, string expectedHash)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        string temporary = targetPath + ".component-new";
        string backup = targetPath + ".component-backup";
        TryDelete(temporary);
        TryDelete(backup);

        try
        {
            File.Copy(sourcePath, temporary, overwrite: true);
            if (!ComputeSha256(temporary).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException("staged component temporary file hash mismatch");
            if (File.Exists(targetPath))
                File.Move(targetPath, backup, overwrite: true);
            File.Move(temporary, targetPath, overwrite: true);
            if (!ComputeSha256(targetPath).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException("staged component installed file hash mismatch");
            TryDelete(backup);
        }
        catch
        {
            TryDelete(temporary);
            if (File.Exists(backup))
                File.Move(backup, targetPath, overwrite: true);
            throw;
        }
    }

    private static bool IsAnyProcessRunning(params string[] names)
    {
        foreach (string name in names)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(name);
            }
            catch
            {
                return true;
            }

            try
            {
                if (processes.Length > 0)
                    return true;
            }
            finally
            {
                foreach (Process process in processes)
                    process.Dispose();
            }
        }
        return false;
    }

    private static string GetBaseDirectory()
        => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
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
            // The following install operation reports the actual error.
        }
    }
}
