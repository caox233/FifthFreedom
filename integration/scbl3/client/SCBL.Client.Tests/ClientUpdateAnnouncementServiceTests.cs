using SplinterCellCNLauncher.Models;
using SplinterCellCNLauncher.Services;

namespace SCBL.Client.Tests;

public sealed class ClientUpdateAnnouncementServiceTests
{
    [Fact]
    public async Task ReadsUpdateAnnouncementFromCurrentDescriptor()
    {
        ServerDescriptorSnapshot descriptor = AnnouncementServiceTests.CreateSnapshot();
        var service = new ClientUpdateAnnouncementService(() => descriptor);

        UpdateAnnouncementSnapshot result = await service.GetRequiredAsync();

        Assert.Equal("update-1", result.Id);
        Assert.Equal("3.0.0", result.Version);
        Assert.Equal("更新", result.TitleEn);
        Assert.Matches("^[0-9a-f]{64}$", result.ContentSha256);
    }

    [Fact]
    public async Task MissingDescriptorOrUpdateNoticeFailsClosed()
    {
        await Assert.ThrowsAsync<UpdateAnnouncementUnavailableException>(
            () => new ClientUpdateAnnouncementService(() => null).GetRequiredAsync());

        ServerDescriptorSnapshot descriptor = AnnouncementServiceTests.CreateSnapshot(includeUpdate: false);
        await Assert.ThrowsAsync<UpdateAnnouncementUnavailableException>(
            () => new ClientUpdateAnnouncementService(() => descriptor).GetRequiredAsync());
    }

    [Fact]
    public async Task NoticeForAnotherClientGenerationFailsClosed()
    {
        ServerDescriptorSnapshot snapshot = AnnouncementServiceTests.CreateSnapshot(
            clientVersion: "3.0.1",
            updateVersion: "3.0.0");
        var service = new ClientUpdateAnnouncementService(() => snapshot);

        await Assert.ThrowsAsync<UpdateAnnouncementUnavailableException>(
            () => service.GetRequiredAsync());
    }
}
