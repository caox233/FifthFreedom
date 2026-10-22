using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace SplinterCellCNLauncher.Models;

public sealed class ClientComponentManifest
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("channel")]
    public string Channel { get; set; } = "";

    [JsonPropertyName("generatedAt")]
    public string GeneratedAt { get; set; } = "";

    [JsonPropertyName("components")]
    public Dictionary<string, ClientComponentDefinition> Components { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    // Portable client files are deliberately outside "components" so older
    // launchers ignore them while still being able to update SCBL.exe first.
    [JsonPropertyName("files")]
    public Dictionary<string, ClientComponentDefinition> Files { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ClientComponentDefinition
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("updateMode")]
    public string UpdateMode { get; set; } = "";

    [JsonPropertyName("required")]
    public bool Required { get; set; }

    [JsonPropertyName("releaseNotes")]
    public ClientComponentReleaseNotes? ReleaseNotes { get; set; }
}

public sealed class ClientComponentReleaseNotes
{
    [JsonPropertyName("zh")]
    public List<string> Zh { get; set; } = [];

    [JsonPropertyName("en")]
    public List<string> En { get; set; } = [];
}

/// <summary>
/// Read-only component metadata from the selected server descriptor. Release notes remain
/// available to publishing and diagnostic code, but the Launcher update dialog
/// uses the server's dedicated update-announcement endpoint instead.
/// </summary>
public sealed class PendingComponentUpdate
{
    public PendingComponentUpdate(
        string name,
        string version,
        IEnumerable<string>? releaseNotesZh,
        IEnumerable<string>? releaseNotesEn)
    {
        Name = name;
        Version = version;
        ReleaseNotesZh = Array.AsReadOnly((releaseNotesZh ?? []).ToArray());
        ReleaseNotesEn = Array.AsReadOnly((releaseNotesEn ?? []).ToArray());
    }

    public string Name { get; }

    public string Version { get; }

    public IReadOnlyList<string> ReleaseNotesZh { get; }

    public IReadOnlyList<string> ReleaseNotesEn { get; }
}

/// <summary>
/// Internal component preview derived from the validated server descriptor.
/// The application coordinator exposes only whether updates exist and the
/// descriptor generation later used for reconciliation.
/// </summary>
public sealed class PreparedClientComponentUpdate
{
    public PreparedClientComponentUpdate(
        string channel,
        string componentGeneration,
        IEnumerable<PendingComponentUpdate>? pendingUpdates)
    {
        Channel = channel;
        ComponentGeneration = componentGeneration;
        PendingUpdates = Array.AsReadOnly((pendingUpdates ?? []).ToArray());
    }

    public string Channel { get; }

    public string ComponentGeneration { get; }

    public IReadOnlyList<PendingComponentUpdate> PendingUpdates { get; }

}

public sealed class ClientComponentBundleRequest
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("channel")]
    public string Channel { get; set; } = "";

    [JsonPropertyName("manifestSha256")]
    public string ManifestSha256 { get; set; } = "";

    [JsonPropertyName("components")]
    public List<ClientComponentBundleRequestItem> Components { get; set; } = [];
}

public sealed class ClientComponentBundleRequestItem
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";
}

public sealed class ClientComponentBundleManifest
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("channel")]
    public string Channel { get; set; } = "";

    [JsonPropertyName("manifestSha256")]
    public string ManifestSha256 { get; set; } = "";

    [JsonPropertyName("components")]
    public List<ClientComponentBundleManifestItem> Components { get; set; } = [];
}

public sealed class ClientComponentBundleManifestItem
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";
}

public sealed record VerifiedClientComponent(
    string Name,
    string Version,
    string Sha256,
    string FilePath,
    string Channel,
    Uri SourceUri);
