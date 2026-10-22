using SplinterCellCNLauncher.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace SplinterCellCNLauncher.Services;

/// <summary>
/// Projects optional ticker and startup notices from the already validated
/// server descriptor. Persistence and offline fallback belong to
/// <see cref="ServerDescriptorClient"/>, so announcement presentation does not
/// maintain a second network client or cache.
/// </summary>
public sealed class AnnouncementService
{
    private readonly Func<ServerDescriptorSnapshot?> _getDescriptor;

    public AnnouncementService(Func<ServerDescriptorSnapshot?> getDescriptor)
        => _getDescriptor = getDescriptor ?? throw new ArgumentNullException(nameof(getDescriptor));

    public Task<LauncherAnnouncement?> GetActiveAnnouncementAsync(
        CancellationToken cancellationToken = default)
        => GetAnnouncementAsync(
            snapshot => snapshot.Descriptor.Announcements.Active,
            cancellationToken);

    public Task<LauncherAnnouncement?> GetStartupAnnouncementAsync(
        CancellationToken cancellationToken = default)
        => GetAnnouncementAsync(
            snapshot => snapshot.Descriptor.Announcements.Startup,
            cancellationToken);

    private Task<LauncherAnnouncement?> GetAnnouncementAsync(
        Func<ServerDescriptorSnapshot, ServerAnnouncementDescriptor?> select,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ServerDescriptorSnapshot? snapshot = _getDescriptor();
        ServerAnnouncementDescriptor? announcement = snapshot is null ? null : select(snapshot);
        return Task.FromResult(announcement is null ? null : Map(announcement));
    }

    internal static LauncherAnnouncement Map(ServerAnnouncementDescriptor announcement)
    {
        ArgumentNullException.ThrowIfNull(announcement);
        return new LauncherAnnouncement
        {
            Id = announcement.Id,
            Title = announcement.Title,
            TitleEn = string.IsNullOrWhiteSpace(announcement.TitleEn)
                ? announcement.Title
                : announcement.TitleEn,
            Body = announcement.Body,
            BodyEn = string.IsNullOrWhiteSpace(announcement.BodyEn)
                ? announcement.Body
                : announcement.BodyEn,
            Level = announcement.Level is "warning" or "error" or "success"
                ? announcement.Level
                : "info",
            ShowOnce = announcement.ShowOnce
        };
    }
}

public sealed class LauncherAnnouncement
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string TitleEn { get; init; } = "";
    public string Body { get; init; } = "";
    public string BodyEn { get; init; } = "";
    public string Level { get; init; } = "info";
    public bool ShowOnce { get; init; } = true;
}
