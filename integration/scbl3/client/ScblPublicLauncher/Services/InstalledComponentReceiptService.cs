using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace SplinterCellCNLauncher.Services;

/// <summary>
/// Stores only component identities and exact installed-file hashes. Portable
/// client files use paths relative to the client root; Route Guard is the sole
/// component allowed to record files below its protected ProgramData root.
/// </summary>
internal static class InstalledComponentReceiptService
{
    private sealed class ReceiptDocument
    {
        public int SchemaVersion { get; set; } = 4;
        public Dictionary<string, Dictionary<string, ReceiptEntry>> Channels { get; set; }
            = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, ReceiptEntry> InstalledFacts { get; set; }
            = new(StringComparer.OrdinalIgnoreCase);

    }

    private sealed class ReceiptEntry
    {
        public string Version { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public long ArtifactSize { get; set; }
        public Dictionary<string, string> Files { get; set; }
            = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly object Sync = new();
    private static readonly AsyncLocal<string?> ReceiptPathOverride = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static string ReceiptPath
        => ReceiptPathOverride.Value
            ?? Path.Combine(LogService.RuntimeDirectory, "installed_components.json");

    internal static IDisposable UseReceiptPathForTest(string path)
    {
        string? previous = ReceiptPathOverride.Value;
        ReceiptPathOverride.Value = Path.GetFullPath(path);
        return new ReceiptPathScope(previous);
    }

    private sealed class ReceiptPathScope(string? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            ReceiptPathOverride.Value = previous;
            _disposed = true;
        }
    }

    internal static IReadOnlyDictionary<string, (string Version, string Sha256)> Read(
        string channel,
        string? clientRootOverride = null)
    {
        lock (Sync)
        {
            ReceiptDocument document = Load();
            var result = new Dictionary<string, (string Version, string Sha256)>(StringComparer.OrdinalIgnoreCase);
            if (!document.Channels.TryGetValue(channel, out Dictionary<string, ReceiptEntry>? components))
                return result;
            foreach ((string name, ReceiptEntry entry) in components)
            {
                if (string.IsNullOrWhiteSpace(entry.Version) || string.IsNullOrWhiteSpace(entry.Sha256))
                    continue;
                if (!InstalledFilesMatch(name, entry, clientRootOverride))
                    continue;
                result[name] = (entry.Version, entry.Sha256);
            }
            return result;
        }
    }

    internal static void MarkApplied(
        string channel,
        string name,
        string version,
        string sha256,
        long artifactSize,
        IReadOnlyDictionary<string, string> installedFiles,
        string? clientRootOverride = null)
    {
        lock (Sync)
        {
            ReceiptDocument document = Load();
            Dictionary<string, ReceiptEntry> components = GetOrCreateChannel(document, channel);
            var entry = new ReceiptEntry
            {
                Version = version,
                Sha256 = sha256.ToLowerInvariant(),
                ArtifactSize = artifactSize,
                Files = NormalizeInstalledFiles(name, installedFiles, clientRootOverride)
            };
            components[name] = entry;
            document.InstalledFacts[name] = CloneEntry(entry);
            Save(document);
        }
    }

    /// <summary>
    /// Establishes an independent receipt for <paramref name="channel"/> only
    /// from that channel's verified manifest identity and freshly verified local
    /// files. No receipt from another channel is read or copied.
    /// </summary>
    internal static bool TryAdoptInstalledFact(
        string channel,
        string name,
        string version,
        string sha256,
        long artifactSize)
    {
        lock (Sync)
        {
            if (string.IsNullOrWhiteSpace(channel)
                || string.IsNullOrWhiteSpace(name)
                || string.IsNullOrWhiteSpace(version)
                || !IsSha256(sha256)
                || artifactSize <= 0)
            {
                return false;
            }

            ReceiptDocument document = Load();
            Dictionary<string, ReceiptEntry> channelEntries = GetOrCreateChannel(document, channel);
            if (channelEntries.TryGetValue(name, out ReceiptEntry? existing)
                && existing.Version.Equals(version, StringComparison.OrdinalIgnoreCase)
                && existing.Sha256.Equals(sha256, StringComparison.OrdinalIgnoreCase)
                && existing.ArtifactSize == artifactSize
                && InstalledFilesMatch(name, existing))
            {
                return true;
            }

            if (!document.InstalledFacts.TryGetValue(name, out ReceiptEntry? fact)
                || !fact.Version.Equals(version, StringComparison.OrdinalIgnoreCase)
                || !fact.Sha256.Equals(sha256, StringComparison.OrdinalIgnoreCase)
                || (fact.ArtifactSize > 0 && fact.ArtifactSize != artifactSize)
                || !InstalledFilesMatch(name, fact))
            {
                return false;
            }

            ReceiptEntry adopted = CloneEntry(fact);
            adopted.Version = version;
            adopted.Sha256 = sha256.ToLowerInvariant();
            adopted.ArtifactSize = artifactSize;
            channelEntries[name] = adopted;
            document.InstalledFacts[name] = CloneEntry(adopted);
            Save(document);
            LogService.Info($"Installed component content adopted for independently verified channel receipt: channel={channel}, component={name}, version={version}");
            return true;
        }
    }

    /// <summary>
    /// Returns only literal client-root files owned by a prior component receipt.
    /// It deliberately does not return external Route Guard paths.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> ReadOwnedClientFiles(
        string channel,
        string component,
        string? clientRootOverride = null)
    {
        lock (Sync)
        {
            ReceiptDocument document = Load();
            if (!document.Channels.TryGetValue(channel, out Dictionary<string, ReceiptEntry>? components)
                || !components.TryGetValue(component, out ReceiptEntry? entry))
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            string clientRoot = GetClientRoot(clientRootOverride);
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach ((string storedPath, string hash) in entry.Files ?? new Dictionary<string, string>())
            {
                if (storedPath.StartsWith(RouteGuardRuntimePaths.ReceiptPrefix, StringComparison.OrdinalIgnoreCase)
                    || !IsSha256(hash)
                    || !TryResolveClientReceiptPath(clientRoot, storedPath, out string? fullPath))
                {
                    continue;
                }
                result[fullPath!] = hash.ToLowerInvariant();
            }
            return result;
        }
    }

    private static Dictionary<string, string> NormalizeInstalledFiles(
        string component,
        IReadOnlyDictionary<string, string> installedFiles,
        string? clientRootOverride = null)
    {
        string clientRoot = GetClientRoot(clientRootOverride);
        string clientPrefix = clientRoot + Path.DirectorySeparatorChar;
        string routeGuardRoot = Path.GetFullPath(RouteGuardRuntimePaths.RootDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string routeGuardPrefix = routeGuardRoot + Path.DirectorySeparatorChar;
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string path, string hash) in installedFiles)
        {
            string full = Path.GetFullPath(path);
            if (!IsSha256(hash))
                throw new InvalidDataException("Component receipt contains an invalid file digest.");
            if (full.StartsWith(clientPrefix, StringComparison.OrdinalIgnoreCase))
            {
                result[Path.GetRelativePath(clientRoot, full).Replace('\\', '/')] = hash.ToLowerInvariant();
                continue;
            }
            if (component.Equals("route-guard", StringComparison.OrdinalIgnoreCase)
                && full.StartsWith(routeGuardPrefix, StringComparison.OrdinalIgnoreCase))
            {
                result[RouteGuardRuntimePaths.ReceiptPrefix
                    + Path.GetRelativePath(routeGuardRoot, full).Replace('\\', '/')] = hash.ToLowerInvariant();
                continue;
            }
            throw new InvalidDataException("Component receipt contains a file outside an authorised component root.");
        }
        if (result.Count == 0)
            throw new InvalidDataException("Component receipt has no installed file hashes.");
        return result;
    }

    private static bool InstalledFilesMatch(
        string component,
        ReceiptEntry entry,
        string? clientRootOverride = null)
    {
        if (entry.Files == null || entry.Files.Count == 0)
        {
            LogService.Warning($"Installed component receipt has no file hashes and will be refreshed: component={component}, version={entry.Version}");
            return false;
        }

        string clientRoot = GetClientRoot(clientRootOverride);
        foreach ((string storedPath, string expected) in entry.Files)
        {
            try
            {
                if (!IsSha256(expected)
                    || !TryResolveReceiptPath(component, clientRoot, storedPath, out string? full)
                    || !File.Exists(full))
                {
                    LogService.Warning($"Installed component file is missing or unsafe; redownload required: component={component}, file={storedPath}");
                    return false;
                }
                using FileStream stream = File.OpenRead(full);
                string actual = Convert.ToHexString(SHA256.HashData(stream));
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                {
                    LogService.Warning($"Installed component file hash mismatch; redownload required: component={component}, file={storedPath}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                LogService.Warning($"Installed component file validation failed; redownload required: component={component}, file={storedPath}, error={ex.Message}");
                return false;
            }
        }
        return true;
    }

    private static bool TryResolveReceiptPath(
        string component,
        string clientRoot,
        string storedPath,
        out string? fullPath)
    {
        if (storedPath.StartsWith(RouteGuardRuntimePaths.ReceiptPrefix, StringComparison.OrdinalIgnoreCase))
        {
            fullPath = null;
            if (!component.Equals("route-guard", StringComparison.OrdinalIgnoreCase))
                return false;
            try
            {
                string relative = storedPath[RouteGuardRuntimePaths.ReceiptPrefix.Length..];
                fullPath = RouteGuardRuntimePaths.ResolveFile(relative);
                return true;
            }
            catch
            {
                return false;
            }
        }
        return TryResolveClientReceiptPath(clientRoot, storedPath, out fullPath);
    }

    private static bool TryResolveClientReceiptPath(string clientRoot, string storedPath, out string? fullPath)
    {
        fullPath = null;
        try
        {
            string normalized = (storedPath ?? "").Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(normalized)
                || Path.IsPathRooted(normalized)
                || normalized.Contains(':')
                || normalized.Split('/').Any(part => string.IsNullOrWhiteSpace(part) || part is "." or ".."))
                return false;
            string candidate = Path.GetFullPath(Path.Combine(
                clientRoot,
                normalized.Replace('/', Path.DirectorySeparatorChar)));
            if (!candidate.StartsWith(clientRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return false;
            fullPath = candidate;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string GetClientRoot(string? overrideRoot)
        => Path.GetFullPath(string.IsNullOrWhiteSpace(overrideRoot) ? AppContext.BaseDirectory : overrideRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsSha256(string value)
        => !string.IsNullOrWhiteSpace(value) && value.Length == 64 && value.All(Uri.IsHexDigit);

    private static Dictionary<string, ReceiptEntry> GetOrCreateChannel(
        ReceiptDocument document,
        string channel)
    {
        if (!document.Channels.TryGetValue(channel, out Dictionary<string, ReceiptEntry>? components))
        {
            components = new Dictionary<string, ReceiptEntry>(StringComparer.OrdinalIgnoreCase);
            document.Channels[channel] = components;
        }
        return components;
    }

    private static ReceiptEntry CloneEntry(ReceiptEntry entry)
        => new()
        {
            Version = entry.Version,
            Sha256 = entry.Sha256,
            ArtifactSize = entry.ArtifactSize,
            Files = new Dictionary<string, string>(
                entry.Files ?? new Dictionary<string, string>(),
                StringComparer.OrdinalIgnoreCase)
        };

    private static ReceiptDocument Load()
    {
        try
        {
            if (!File.Exists(ReceiptPath))
            {
                string temporary = ReceiptPath + ".tmp";
                if (!TryLoadCommittedSchema4(temporary, out ReceiptDocument? recovered))
                    return new ReceiptDocument();
                Directory.CreateDirectory(Path.GetDirectoryName(ReceiptPath)!);
                File.Move(temporary, ReceiptPath, overwrite: true);
                LogService.Info("Installed component receipt recovered from a complete pending atomic save.");
                return recovered!;
            }
            if (TryLoadCommittedSchema4(ReceiptPath, out ReceiptDocument? document))
                return document!;
            LogService.Warning("Installed component receipt schema is unsupported; it will be rebuilt.");
            return new ReceiptDocument();
        }
        catch (Exception ex)
        {
            LogService.Warning("Installed component receipt is unreadable; it will be rebuilt: " + ex.Message);
            return new ReceiptDocument();
        }
    }

    private static void Save(ReceiptDocument document)
    {
        document.SchemaVersion = 4;
        Directory.CreateDirectory(Path.GetDirectoryName(ReceiptPath)!);
        string temporary = ReceiptPath + ".tmp";
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        using (var stream = new FileStream(
                   temporary,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None,
                   bufferSize: 4096,
                   FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, ReceiptPath, overwrite: true);
    }

    private static bool TryLoadCommittedSchema4(string path, out ReceiptDocument? document)
    {
        document = null;
        try
        {
            if (!File.Exists(path))
                return false;
            byte[] bytes = File.ReadAllBytes(path);
            using JsonDocument json = JsonDocument.Parse(bytes);
            JsonElement root = json.RootElement;
            if (!root.TryGetProperty(nameof(ReceiptDocument.SchemaVersion), out JsonElement schema)
                || schema.ValueKind != JsonValueKind.Number
                || schema.GetInt32() != 4
                || !root.TryGetProperty(nameof(ReceiptDocument.Channels), out JsonElement channels)
                || channels.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty(nameof(ReceiptDocument.InstalledFacts), out JsonElement facts)
                || facts.ValueKind != JsonValueKind.Object)
            {
                return false;
            }
            document = JsonSerializer.Deserialize<ReceiptDocument>(bytes, JsonOptions);
            return document != null;
        }
        catch
        {
            return false;
        }
    }

}
