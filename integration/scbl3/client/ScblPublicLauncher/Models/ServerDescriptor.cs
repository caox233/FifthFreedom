using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SplinterCellCNLauncher.Models;

public sealed class ServerDescriptor
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("generatedAt")]
    public string GeneratedAt { get; init; } = "";

    [JsonPropertyName("server")]
    public ServerIdentityDescriptor Server { get; init; } = new();

    [JsonPropertyName("client")]
    public ServerClientDescriptor Client { get; init; } = new();

    [JsonPropertyName("network")]
    public ServerNetworkDescriptor Network { get; init; } = new();

    [JsonPropertyName("controlPlane")]
    public ServerControlPlaneDescriptor ControlPlane { get; init; } = new();

    [JsonPropertyName("dedicated")]
    public ServerDedicatedDescriptor Dedicated { get; init; } = new();

    [JsonPropertyName("announcements")]
    public ServerAnnouncementsDescriptor Announcements { get; init; } = new();
}

public sealed class ServerIdentityDescriptor
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("mode")]
    public string Mode { get; init; } = "";
}

public sealed class ServerClientDescriptor
{
    [JsonPropertyName("version")]
    public string Version { get; init; } = "";

    [JsonPropertyName("updateBaseUrl")]
    public string UpdateBaseUrl { get; init; } = "";

    [JsonPropertyName("componentGeneration")]
    public string ComponentGeneration { get; init; } = "";

    [JsonPropertyName("components")]
    public Dictionary<string, ClientComponentDefinition> Components { get; init; }
        = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("files")]
    public Dictionary<string, ClientComponentDefinition> Files { get; init; }
        = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("fullPackage")]
    public ServerFullPackageDescriptor? FullPackage { get; init; }
}

public sealed class ServerFullPackageDescriptor
{
    [JsonPropertyName("version")]
    public string Version { get; init; } = "";

    [JsonPropertyName("url")]
    public string Url { get; init; } = "";

    [JsonPropertyName("sha256")]
    public string Sha256 { get; init; } = "";

    [JsonPropertyName("size")]
    public long Size { get; init; }
}

public sealed class ServerNetworkDescriptor
{
    [JsonPropertyName("publicHost")]
    public string PublicHost { get; init; } = "";

    [JsonPropertyName("useOriginHost")]
    public bool UseOriginHost { get; init; }

    [JsonPropertyName("publicPort")]
    public int PublicPort { get; init; }

    [JsonPropertyName("transports")]
    public List<string> Transports { get; init; } = [];

    [JsonPropertyName("networkName")]
    public string NetworkName { get; init; } = "";

    [JsonPropertyName("secret")]
    public string Secret { get; init; } = "";

    [JsonPropertyName("mtu")]
    public int Mtu { get; init; }

    [JsonPropertyName("enableIpv6")]
    public bool EnableIpv6 { get; init; }
}

public sealed class ServerControlPlaneDescriptor
{
    [JsonPropertyName("address")]
    public string Address { get; init; } = "";

    [JsonPropertyName("port")]
    public int Port { get; init; }
}

public sealed class ServerDedicatedDescriptor
{
    [JsonPropertyName("address")]
    public string Address { get; init; } = "";

    [JsonPropertyName("configPort")]
    public int ConfigPort { get; init; }

    [JsonPropertyName("authPort")]
    public int AuthPort { get; init; }

    [JsonPropertyName("gamePort")]
    public int GamePort { get; init; }

    [JsonPropertyName("protocol")]
    public string Protocol { get; init; } = "";
}

public sealed class ServerAnnouncementsDescriptor
{
    [JsonPropertyName("active")]
    public ServerAnnouncementDescriptor? Active { get; init; }

    [JsonPropertyName("startup")]
    public ServerAnnouncementDescriptor? Startup { get; init; }

    [JsonPropertyName("update")]
    public ServerAnnouncementDescriptor? Update { get; init; }
}

public sealed class ServerAnnouncementDescriptor
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("version")]
    public string Version { get; init; } = "";

    [JsonPropertyName("title")]
    public string Title { get; init; } = "";

    [JsonPropertyName("body")]
    public string Body { get; init; } = "";

    [JsonPropertyName("title_en")]
    public string TitleEn { get; init; } = "";

    [JsonPropertyName("body_en")]
    public string BodyEn { get; init; } = "";

    [JsonPropertyName("level")]
    public string Level { get; init; } = "info";

    [JsonPropertyName("showOnce")]
    public bool ShowOnce { get; init; } = true;
}

public enum ServerDescriptorSource
{
    Live,
    Cache
}

public sealed record ServerDescriptorSnapshot(
    ServerDescriptor Descriptor,
    ServerDescriptorSource Source,
    string ContentSha256,
    Uri DescriptorUri);
