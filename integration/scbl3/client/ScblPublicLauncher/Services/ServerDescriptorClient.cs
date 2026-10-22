using SplinterCellCNLauncher.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SplinterCellCNLauncher.Services;

public sealed class ServerDescriptorUnavailableException : InvalidOperationException
{
    public ServerDescriptorUnavailableException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

public sealed class ServerDescriptorClient
{
    private const int MaximumDescriptorBytes = 1024 * 1024;
    private static readonly Regex VersionPattern = new(
        "^[0-9]+\\.[0-9]+\\.[0-9]+$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Sha256Pattern = new(
        "^[0-9a-f]{64}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false
    };
    private static readonly HttpClient DefaultHttp = CreateHttpClient();

    private readonly ServerConnectionProfile _profile;
    private readonly HttpClient _http;
    private readonly string _cachePath;

    public ServerDescriptorClient(
        ServerConnectionProfile profile,
        HttpClient? httpClient = null,
        string? cachePath = null)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _http = httpClient ?? DefaultHttp;
        _cachePath = string.IsNullOrWhiteSpace(cachePath)
            ? BuildDefaultCachePath(profile)
            : Path.GetFullPath(cachePath);
    }

    internal static string BuildDefaultCachePath(ServerConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        string endpointFingerprint = Convert.ToHexString(
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                profile.DescriptorUri.AbsoluteUri.ToLowerInvariant())))[..16].ToLowerInvariant();
        return Path.Combine(
            LogService.ConfigDirectory,
            $"scbl-server-{profile.ChannelName}-{endpointFingerprint}.json");
    }

    public async Task<ServerDescriptorSnapshot> GetAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            byte[] content = await DownloadAsync(cancellationToken).ConfigureAwait(false);
            ServerDescriptor descriptor = ParseAndValidate(content, _profile);
            WriteCache(content);
            return CreateSnapshot(descriptor, content, ServerDescriptorSource.Live);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception liveError)
        {
            try
            {
                byte[] cached = File.ReadAllBytes(_cachePath);
                ServerDescriptor descriptor = ParseAndValidate(cached, _profile);
                LogService.Warning(
                    $"Server descriptor live refresh failed; using validated cache: channel={_profile.ChannelName}, error={liveError.GetType().Name}");
                return CreateSnapshot(descriptor, cached, ServerDescriptorSource.Cache);
            }
            catch (Exception cacheError)
            {
                throw new ServerDescriptorUnavailableException(
                    $"{_profile.ChannelName} 服务器描述不可用，实时读取与本地缓存均未通过验证。",
                    new AggregateException(liveError, cacheError));
            }
        }
    }

    internal static ServerDescriptor ParseAndValidate(
        ReadOnlySpan<byte> content,
        ServerConnectionProfile profile)
    {
        if (content.IsEmpty || content.Length > MaximumDescriptorBytes)
            throw new InvalidDataException("服务器描述大小无效。");
        ServerDescriptor descriptor = JsonSerializer.Deserialize<ServerDescriptor>(content, JsonOptions)
            ?? throw new InvalidDataException("服务器描述为空。");
        Validate(descriptor, profile);
        return descriptor;
    }

    internal static void Validate(
        ServerDescriptor descriptor,
        ServerConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(profile);
        if (descriptor.SchemaVersion != 1)
            throw new InvalidDataException("服务器描述 schemaVersion 不受支持。");
        if (descriptor.Server is null
            || descriptor.Client is null
            || descriptor.Network is null
            || descriptor.ControlPlane is null
            || descriptor.Dedicated is null
            || descriptor.Announcements is null
            || descriptor.Client.Components is null
            || descriptor.Client.Files is null
            || descriptor.Network.Transports is null)
        {
            throw new InvalidDataException("服务器描述缺少必需对象。");
        }
        if (string.IsNullOrWhiteSpace(descriptor.Server.Id))
            throw new InvalidDataException("服务器描述缺少 server.id。");
        if (!descriptor.Server.Mode.Equals(profile.ChannelName, StringComparison.Ordinal))
            throw new InvalidDataException("服务器身份与所选客户端通道不一致。");
        if (string.IsNullOrWhiteSpace(descriptor.Client.Version)
            || !VersionPattern.IsMatch(descriptor.Client.Version))
            throw new InvalidDataException("服务器描述的客户端版本无效。");
        if (!Uri.TryCreate(descriptor.Client.UpdateBaseUrl, UriKind.Absolute, out Uri? updateBase)
            || (updateBase.Scheme != Uri.UriSchemeHttp && updateBase.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(updateBase.UserInfo)
            || !string.IsNullOrEmpty(updateBase.Query)
            || !string.IsNullOrEmpty(updateBase.Fragment)
            || !updateBase.Scheme.Equals(profile.DescriptorUri.Scheme, StringComparison.OrdinalIgnoreCase)
            || !updateBase.Host.Equals(profile.DescriptorUri.Host, StringComparison.OrdinalIgnoreCase)
            || updateBase.Port != profile.DescriptorUri.Port)
        {
            throw new InvalidDataException("服务器描述的更新地址无效。");
        }
        if (descriptor.Network.PublicPort is <= 0 or > 65535
            || descriptor.Network.Mtu is < 576 or > 9000
            || (!descriptor.Network.UseOriginHost && string.IsNullOrWhiteSpace(descriptor.Network.PublicHost))
            || string.IsNullOrWhiteSpace(descriptor.Network.NetworkName)
            || string.IsNullOrWhiteSpace(descriptor.Network.Secret)
            || descriptor.Network.Transports.Count != 2
            || descriptor.Network.Transports.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 2
            || !descriptor.Network.Transports.Contains("quic", StringComparer.OrdinalIgnoreCase)
            || !descriptor.Network.Transports.Contains("wss", StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("服务器描述的网络配置无效。");
        }
        ValidateAddressPort(descriptor.ControlPlane.Address, descriptor.ControlPlane.Port, "controlPlane");
        ValidateAddressPort(descriptor.Dedicated.Address, descriptor.Dedicated.ConfigPort, "dedicated.config");
        if (descriptor.Dedicated.AuthPort is <= 0 or > 65535
            || descriptor.Dedicated.GamePort is <= 0 or > 65535
            || string.IsNullOrWhiteSpace(descriptor.Dedicated.Protocol))
        {
            throw new InvalidDataException("服务器描述的 Dedicated 配置无效。");
        }
        if (descriptor.Client.Components.Keys.Intersect(
                descriptor.Client.Files.Keys,
                StringComparer.OrdinalIgnoreCase).Any())
        {
            throw new InvalidDataException("服务器描述的 components 与 files 不得包含同名记录。");
        }
        foreach ((string name, ClientComponentDefinition component) in descriptor.Client.Components
                     .Concat(descriptor.Client.Files))
        {
            ValidateComponent(name, component);
        }
        int componentCount = descriptor.Client.Components.Count + descriptor.Client.Files.Count;
        if (componentCount == 0
            || string.IsNullOrWhiteSpace(descriptor.Client.ComponentGeneration)
            || !Sha256Pattern.IsMatch(descriptor.Client.ComponentGeneration))
            throw new InvalidDataException("服务器描述的组件代际无效。");
        if (descriptor.Client.FullPackage is { } fullPackage)
        {
            if (!VersionPattern.IsMatch(fullPackage.Version)
                || !Sha256Pattern.IsMatch(fullPackage.Sha256)
                || fullPackage.Size <= 0
                || !IsSafeRelativeUrl(fullPackage.Url))
            {
                throw new InvalidDataException("服务器描述的完整包记录无效。");
            }
        }
        ValidateAnnouncement(descriptor.Announcements.Active, "active");
        ValidateAnnouncement(descriptor.Announcements.Startup, "startup");
        ValidateAnnouncement(descriptor.Announcements.Update, "update");
    }

    private async Task<byte[]> DownloadAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _profile.DescriptorUri);
        using HttpResponseMessage response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length
            && (length <= 0 || length > MaximumDescriptorBytes))
        {
            throw new InvalidDataException("服务器描述声明大小无效。");
        }
        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var destination = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        while (true)
        {
            int read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            if (destination.Length + read > MaximumDescriptorBytes)
                throw new InvalidDataException("服务器描述超过大小限制。");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return destination.ToArray();
    }

    private ServerDescriptorSnapshot CreateSnapshot(
        ServerDescriptor descriptor,
        byte[] content,
        ServerDescriptorSource source)
        => new(
            descriptor,
            source,
            Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
            _profile.DescriptorUri);

    private void WriteCache(byte[] content)
    {
        string directory = Path.GetDirectoryName(_cachePath) ?? ".";
        Directory.CreateDirectory(directory);
        string temporary = _cachePath + ".tmp";
        File.WriteAllBytes(temporary, content);
        using (var stream = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            stream.Flush(flushToDisk: true);
        File.Move(temporary, _cachePath, overwrite: true);
    }

    private static void ValidateComponent(string name, ClientComponentDefinition component)
    {
        if (string.IsNullOrWhiteSpace(name)
            || component is null
            || !VersionPattern.IsMatch(component.Version)
            || !Sha256Pattern.IsMatch(component.Sha256)
            || component.Size <= 0
            || component.Required is not true
            || !IsSafeRelativeUrl(component.Url))
        {
            throw new InvalidDataException($"服务器描述的组件记录无效：{name}。");
        }
    }

    private static bool IsSafeRelativeUrl(string value)
        => !string.IsNullOrWhiteSpace(value)
            && value.StartsWith("/", StringComparison.Ordinal)
            && !value.StartsWith("//", StringComparison.Ordinal)
            && !value.Contains("..", StringComparison.Ordinal)
            && !value.Contains('%')
            && !value.Contains('\\')
            && !value.Contains('?')
            && !value.Contains('#')
            && !value.Any(char.IsControl);

    private static void ValidateAddressPort(string address, int port, string name)
    {
        if (string.IsNullOrWhiteSpace(address) || port is <= 0 or > 65535)
            throw new InvalidDataException($"服务器描述的 {name} 地址无效。");
    }

    private static void ValidateAnnouncement(ServerAnnouncementDescriptor? announcement, string name)
    {
        if (announcement is null)
            return;
        if (string.IsNullOrWhiteSpace(announcement.Id)
            || string.IsNullOrWhiteSpace(announcement.Title)
            || string.IsNullOrWhiteSpace(announcement.Body))
        {
            throw new InvalidDataException($"服务器描述的 {name} 公告无效。");
        }
    }

    private static HttpClient CreateHttpClient()
        => DualStackHttpClient.Create(
            "SCBL-Server-Descriptor/3",
            TimeSpan.FromSeconds(8));
}
