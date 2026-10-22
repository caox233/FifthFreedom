using System.IO.Compression;

namespace Scbl.Server;

public sealed class PackageDeployment
{
    private readonly ServerPaths _paths;
    private readonly ServerLog _log;

    public PackageDeployment(ServerPaths paths, ServerLog log)
    {
        _paths = paths;
        _log = log;
    }

    public StagedPackage Stage(string packagePath, bool requireFull = false)
    {
        if (!File.Exists(packagePath)) throw new FileNotFoundException("服务端包不存在。", packagePath);
        if (new FileInfo(packagePath).Length is <= 0 or > PackageManifest.MaximumPackageBytes) throw new InvalidDataException("服务端包大小无效。");
        string staging = Path.Combine(_paths.Staging, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            using ZipArchive archive = ZipFile.OpenRead(packagePath);
            PackageManifest manifest = PackageManifest.ReadFromZip(archive);
            manifest.Validate(requireFull);
            if (manifest.PackageType == "windows-update")
            {
                ReleasePointer? current = AtomicJson.Read<ReleasePointer>(_paths.CurrentReleasePointer);
                if (current is not null && Version.Parse(manifest.ReleaseVersion) <= Version.Parse(current.Version))
                {
                    throw new InvalidDataException($"更新包版本 {manifest.ReleaseVersion} 必须高于当前版本 {current.Version}；降级请使用明确回滚。");
                }
            }
            foreach (PackageComponent component in manifest.Components)
            {
                string componentPath = component.Path.Replace('\\', '/');
                ZipArchiveEntry[] matches = archive.Entries.Where(entry => string.Equals(entry.FullName, componentPath, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length != 1) throw new InvalidDataException($"包内必须恰好包含一个组件条目：{component.Path}");
                ZipArchiveEntry entry = matches[0];
                if (entry.Length != component.Size) throw new InvalidDataException($"{component.Id} 大小不一致。");
                string target = PathSafety.ResolveRelative(staging, component.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using (Stream input = entry.Open())
                using (FileStream output = new(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    input.CopyTo(output);
                    output.Flush(flushToDisk: true);
                }
                if (!Hashing.FixedEqualsHex(Hashing.Sha256(target), component.Sha256)) throw new InvalidDataException($"{component.Id} SHA256 不一致。");
            }
            _log.Write("deployment", $"staged {manifest.PackageType} {manifest.ReleaseVersion}");
            return new StagedPackage(staging, manifest);
        }
        catch
        {
            Directory.Delete(staging, recursive: true);
            throw;
        }
    }

    public ReleasePointer ActivateRuntime(StagedPackage package)
    {
        List<PackageComponent> runtime = package.Manifest.Components.Where(component => component.Target == "runtime").ToList();
        if (runtime.Count == 0) throw new InvalidDataException("更新包没有 Runtime 组件。");
        ReleasePointer? reusable = TryReuseCurrentFullRuntime(package, runtime);
        if (reusable is not null)
        {
            _log.Write("deployment", $"reused unchanged runtime {reusable.Directory}");
            return reusable;
        }
        string releaseId = $"{package.Manifest.ReleaseVersion}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
        string temporary = Path.Combine(_paths.Releases, "." + releaseId + ".staging");
        string final = Path.Combine(_paths.Releases, releaseId);
        if (Directory.Exists(final) || Directory.Exists(temporary)) throw new IOException("Runtime 目标目录已存在。");
        Directory.CreateDirectory(temporary);
        try
        {
            ReleasePointer? current = AtomicJson.Read<ReleasePointer>(_paths.CurrentReleasePointer);
            PackageManifest? currentManifest = null;
            if (package.Manifest.PackageType == "windows-update")
            {
                if (current is null) throw new InvalidOperationException("增量更新要求已经安装完整 Runtime。");
                string currentDirectory = PathSafety.ResolveRelative(_paths.Releases, current.Directory);
                if (!Directory.Exists(currentDirectory)) throw new DirectoryNotFoundException("当前 Runtime 目录不存在。");
                CopyDirectory(currentDirectory, temporary);
                currentManifest = AtomicJson.Read<PackageManifest>(Path.Combine(currentDirectory, "release.json"))
                    ?? throw new InvalidDataException("当前 Runtime 缺少 release.json。");
                currentManifest.Validate(fullPackage: false);
            }
            PackageManifest effectiveManifest = MergeRuntimeManifest(currentManifest, package.Manifest, runtime);
            effectiveManifest.Validate(fullPackage: false);
            foreach (PackageComponent component in runtime)
            {
                string source = PathSafety.ResolveRelative(package.Directory, component.Path);
                string relative = component.EffectiveDestination();
                PackageComponent? previousComponent = currentManifest?.Components.FirstOrDefault(value => string.Equals(value.Id, component.Id, StringComparison.OrdinalIgnoreCase));
                if (previousComponent is not null && !string.Equals(previousComponent.EffectiveDestination(), relative, StringComparison.OrdinalIgnoreCase))
                {
                    RemoveExistingTarget(temporary, previousComponent.EffectiveDestination());
                }
                RemoveExistingTarget(temporary, relative);
                if (component.Extract)
                {
                    string extractionRoot = PathSafety.ResolveRelative(temporary, relative);
                    Directory.CreateDirectory(extractionRoot);
                    ExtractComponentArchive(source, extractionRoot);
                }
                else
                {
                    string target = PathSafety.ResolveRelative(temporary, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(source, target, overwrite: true);
                }
            }
            AtomicJson.Write(Path.Combine(temporary, "release.json"), effectiveManifest);
            Directory.Move(temporary, final);
            ReleasePointer pointer = new(package.Manifest.ReleaseVersion, releaseId, DateTimeOffset.UtcNow);
            if (current is not null) AtomicJson.Write(_paths.PreviousReleasePointer, current);
            AtomicJson.Write(_paths.CurrentReleasePointer, pointer);
            _log.Write("deployment", $"activated runtime {releaseId}");
            HashSet<string?> protectedReleases = new(new[] { pointer.Directory, current?.Directory }, StringComparer.OrdinalIgnoreCase);
            foreach (string directory in new BackupManager(_paths, _log).ProtectedReleaseDirectories()) protectedReleases.Add(directory);
            Retention.PruneDirectories(_paths.Releases, 3, protectedReleases);
            return pointer;
        }
        catch
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
            throw;
        }
    }

    private ReleasePointer? TryReuseCurrentFullRuntime(StagedPackage package, IReadOnlyList<PackageComponent> incoming)
    {
        if (package.Manifest.PackageType != "windows-full") return null;
        ReleasePointer? current = AtomicJson.Read<ReleasePointer>(_paths.CurrentReleasePointer);
        if (current is null || !string.Equals(current.Version, package.Manifest.ReleaseVersion, StringComparison.Ordinal)) return null;
        string currentDirectory;
        try { currentDirectory = _paths.RequireRelease(current); }
        catch (Exception) { return null; }
        PackageManifest? installed = AtomicJson.Read<PackageManifest>(Path.Combine(currentDirectory, "release.json"));
        if (installed is null) return null;
        List<PackageComponent> existing = installed.Components.Where(component => component.Target == "runtime").ToList();
        if (existing.Count != incoming.Count) return null;
        foreach (PackageComponent component in incoming)
        {
            PackageComponent? match = existing.SingleOrDefault(value => string.Equals(value.Id, component.Id, StringComparison.OrdinalIgnoreCase));
            if (match is null ||
                !string.Equals(match.Version, component.Version, StringComparison.Ordinal) ||
                match.Size != component.Size ||
                !string.Equals(match.Sha256, component.Sha256, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(match.EffectiveDestination(), component.EffectiveDestination(), StringComparison.OrdinalIgnoreCase) ||
                match.Extract != component.Extract) return null;
            string target = PathSafety.ResolveRelative(currentDirectory, match.EffectiveDestination());
            if (match.Extract)
            {
                if (!Directory.Exists(target)) return null;
            }
            else if (!File.Exists(target) || new FileInfo(target).Length != match.Size || !Hashing.FixedEqualsHex(Hashing.Sha256(target), match.Sha256)) return null;
        }
        return current;
    }

    private static PackageManifest MergeRuntimeManifest(PackageManifest? current, PackageManifest incoming, IReadOnlyList<PackageComponent> changed)
    {
        Dictionary<string, PackageComponent> components = new(StringComparer.OrdinalIgnoreCase);
        if (current is not null)
        {
            foreach (PackageComponent component in current.Components.Where(value => value.Target == "runtime")) components[component.Id] = component;
        }
        foreach (PackageComponent component in changed) components[component.Id] = component;
        return new PackageManifest
        {
            SchemaVersion = incoming.SchemaVersion,
            PackageType = "windows-update",
            ReleaseVersion = incoming.ReleaseVersion,
            Components = components.Values.OrderBy(component => component.Id, StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }

    private static void ExtractComponentArchive(string source, string extractionRoot)
    {
        using ZipArchive archive = ZipFile.OpenRead(source);
        if (archive.Entries.Count > 4096) throw new InvalidDataException("组件压缩包文件数量过多。");
        HashSet<string> targets = new(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            expanded = checked(expanded + entry.Length);
            if (expanded > PackageManifest.MaximumExpandedComponentBytes) throw new InvalidDataException("组件解压后大小超过限制。");
            string target = PathSafety.ResolveRelative(extractionRoot, entry.FullName);
            if (!targets.Add(target)) throw new InvalidDataException($"组件压缩包包含重复目标：{entry.FullName}");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: false);
        }
    }

    private static void RemoveExistingTarget(string root, string relative)
    {
        string target = PathSafety.ResolveRelative(root, relative);
        if (File.Exists(target)) File.Delete(target);
        else if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(PathSafety.ResolveRelative(destination, Path.GetRelativePath(source, directory)));
        }
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = PathSafety.ResolveRelative(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    public void InstallBootstrap(StagedPackage package)
    {
        List<PackageComponent> bootstrap = package.Manifest.Components.Where(component => component.Target == "bootstrap").ToList();
        if (bootstrap.Count == 0) throw new InvalidDataException("完整包没有 Bootstrap 组件。");
        string staging = _paths.Bootstrap + ".staging." + Guid.NewGuid().ToString("N");
        string previous = _paths.Bootstrap + ".previous." + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        try
        {
            if (package.Manifest.PackageType == "windows-update" && Directory.Exists(_paths.Bootstrap)) CopyDirectory(_paths.Bootstrap, staging);
            foreach (PackageComponent component in bootstrap)
            {
                string source = PathSafety.ResolveRelative(package.Directory, component.Path);
                string name = string.IsNullOrWhiteSpace(component.Destination) ? Path.GetFileName(component.Path) : component.Destination;
                string target = PathSafety.ResolveRelative(staging, name);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, overwrite: true);
                if (!Hashing.FixedEqualsHex(Hashing.Sha256(target), component.Sha256)) throw new InvalidDataException($"安装后 {component.Id} 校验失败。");
            }
            bool hadExisting = Directory.Exists(_paths.Bootstrap);
            if (hadExisting) Directory.Move(_paths.Bootstrap, previous);
            try
            {
                Directory.Move(staging, _paths.Bootstrap);
            }
            catch
            {
                if (hadExisting && Directory.Exists(previous) && !Directory.Exists(_paths.Bootstrap)) Directory.Move(previous, _paths.Bootstrap);
                throw;
            }
            if (Directory.Exists(previous))
            {
                try { Directory.Delete(previous, recursive: true); }
                catch (IOException error) { _log.Write("deployment", "previous bootstrap cleanup deferred: " + error.Message); }
            }
            _log.Write("deployment", $"installed bootstrap {package.Manifest.ReleaseVersion}");
        }
        catch
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            throw;
        }
    }

    public string? SnapshotBootstrap()
    {
        if (!Directory.Exists(_paths.Bootstrap)) return null;
        string snapshot = Path.Combine(_paths.Staging, "bootstrap-snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(snapshot);
        CopyDirectory(_paths.Bootstrap, snapshot);
        return snapshot;
    }

    public void RestoreBootstrap(string? snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot) || !Directory.Exists(snapshot)) return;
        if (Directory.Exists(_paths.Bootstrap)) DeleteDirectoryWithRetry(_paths.Bootstrap);
        Directory.Move(snapshot, _paths.Bootstrap);
        _log.Write("deployment", "restored previous bootstrap");
    }

    private static void DeleteDirectoryWithRetry(string path)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                last = error;
                Thread.Sleep(250);
            }
        }
        throw new IOException($"等待 Windows 释放安装文件后仍无法恢复 {path}。", last);
    }

    public static void DiscardBootstrapSnapshot(string? snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot) && Directory.Exists(snapshot)) Directory.Delete(snapshot, recursive: true);
    }

    public void RollbackRuntime()
    {
        ReleasePointer previous = AtomicJson.Read<ReleasePointer>(_paths.PreviousReleasePointer)
            ?? throw new InvalidOperationException("没有可用的直接回滚版本。");
        _ = _paths.RequireRelease(previous);
        ReleasePointer? current = AtomicJson.Read<ReleasePointer>(_paths.CurrentReleasePointer);
        AtomicJson.Write(_paths.CurrentReleasePointer, previous with { ActivatedAtUtc = DateTimeOffset.UtcNow });
        if (current is not null) AtomicJson.Write(_paths.PreviousReleasePointer, current);
        _log.Write("deployment", $"rolled back runtime to {previous.Directory}");
    }
}

public sealed class StagedPackage : IDisposable
{
    public StagedPackage(string directory, PackageManifest manifest) { Directory = directory; Manifest = manifest; }
    public string Directory { get; }
    public PackageManifest Manifest { get; }
    public void Dispose()
    {
        if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true);
    }
}

public static class Retention
{
    public static void PruneDirectories(string root, int keep, IEnumerable<string?> protectedNames)
    {
        HashSet<string> protectedSet = protectedNames.Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        DirectoryInfo directory = new(root);
        if (!directory.Exists) return;
        DirectoryInfo[] candidates = directory.GetDirectories().Where(item => !item.Name.StartsWith('.')).OrderByDescending(item => item.CreationTimeUtc).ToArray();
        foreach (DirectoryInfo item in candidates.Skip(keep))
        {
            if (!protectedSet.Contains(item.Name)) item.Delete(recursive: true);
        }
    }
}
