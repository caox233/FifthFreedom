using SplinterCellCNLauncher.Models;
using SplinterCellCNLauncher.Services;

namespace SCBL.Client.Tests;

public sealed class AnnouncementServiceTests
{
    [Fact]
    public async Task ReadsTickerAndStartupFromOneValidatedDescriptor()
    {
        ServerDescriptorSnapshot snapshot = CreateSnapshot();
        var service = new AnnouncementService(() => snapshot);

        LauncherAnnouncement? active = await service.GetActiveAnnouncementAsync();
        LauncherAnnouncement? startup = await service.GetStartupAnnouncementAsync();

        Assert.Equal("active-1", active?.Id);
        Assert.Equal("启动", startup?.Title);
        Assert.Equal("启动", startup?.TitleEn);
    }

    [Fact]
    public async Task MissingSnapshotOrNoticeReturnsNoPresentation()
    {
        Assert.Null(await new AnnouncementService(() => null).GetActiveAnnouncementAsync());
        ServerDescriptorSnapshot snapshot = CreateSnapshot(includeActive: false);
        Assert.Null(await new AnnouncementService(() => snapshot).GetActiveAnnouncementAsync());
    }

    internal static ServerDescriptorSnapshot CreateSnapshot(
        bool includeActive = true,
        bool includeUpdate = true,
        string clientVersion = "3.0.0",
        string updateVersion = "3.0.0")
    {
        var descriptor = new ServerDescriptor
        {
            Client = new ServerClientDescriptor { Version = clientVersion },
            Announcements = new ServerAnnouncementsDescriptor
            {
                Active = includeActive
                    ? new ServerAnnouncementDescriptor
                    {
                        Id = "active-1",
                        Title = "公告",
                        Body = "内容",
                        Level = "success"
                    }
                    : null,
                Startup = new ServerAnnouncementDescriptor
                {
                    Id = "startup-1",
                    Title = "启动",
                    Body = "正文"
                },
                Update = includeUpdate
                    ? new ServerAnnouncementDescriptor
                    {
                        Id = "update-1",
                        Version = updateVersion,
                        Title = "更新",
                        Body = "更新正文"
                    }
                    : null
            }
        };
        return new ServerDescriptorSnapshot(
            descriptor,
            ServerDescriptorSource.Live,
            new string('a', 64),
            new Uri("http://server/scbl-server.json"));
    }
}
