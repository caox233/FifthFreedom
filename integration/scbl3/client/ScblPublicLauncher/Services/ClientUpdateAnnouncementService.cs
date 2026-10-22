using SplinterCellCNLauncher.Models;
using System;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SplinterCellCNLauncher.Services;

public interface IUpdateAnnouncementReader
{
    Task<UpdateAnnouncementSnapshot> GetRequiredAsync(
        CancellationToken cancellationToken = default);
}

public sealed record UpdateAnnouncementSnapshot(
    string Id,
    string Version,
    string TitleZh,
    string BodyZh,
    string TitleEn,
    string BodyEn,
    string ContentSha256);

public sealed class UpdateAnnouncementUnavailableException : InvalidOperationException
{
    public UpdateAnnouncementUnavailableException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Projects the required update notice from the same validated descriptor as
/// component reconciliation. This removes a second HTTP request and prevents
/// the prompt from describing a different server generation.
/// </summary>
public sealed class ClientUpdateAnnouncementService : IUpdateAnnouncementReader
{
    private readonly Func<ServerDescriptorSnapshot?> _getDescriptor;

    public ClientUpdateAnnouncementService(Func<ServerDescriptorSnapshot?> getDescriptor)
        => _getDescriptor = getDescriptor ?? throw new ArgumentNullException(nameof(getDescriptor));

    public Task<UpdateAnnouncementSnapshot> GetRequiredAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ServerDescriptorSnapshot? descriptor = _getDescriptor();
        ServerAnnouncementDescriptor? announcement = descriptor?.Descriptor.Announcements.Update;
        if (descriptor is null
            || announcement is null
            || !string.Equals(
                announcement.Version,
                descriptor.Descriptor.Client.Version,
                StringComparison.Ordinal))
        {
            throw new UpdateAnnouncementUnavailableException(
                "服务器描述没有提供与当前组件版本匹配的更新公告。");
        }

        string titleEn = string.IsNullOrWhiteSpace(announcement.TitleEn)
            ? announcement.Title
            : announcement.TitleEn;
        string bodyEn = string.IsNullOrWhiteSpace(announcement.BodyEn)
            ? announcement.Body
            : announcement.BodyEn;
        byte[] canonical = JsonSerializer.SerializeToUtf8Bytes(announcement);
        return Task.FromResult(new UpdateAnnouncementSnapshot(
            announcement.Id,
            announcement.Version,
            announcement.Title,
            announcement.Body,
            titleEn,
            bodyEn,
            Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant()));
    }
}
