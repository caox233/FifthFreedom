using SplinterCellCNLauncher.Services;

namespace SCBL.Client.Tests;

public sealed class ComponentCacheCleanupTests
{
    [Fact]
    public void SuccessfulActivationDeletesExactPayloadAndEmptyCacheRoot()
    {
        using var fixture = new CacheFixture();
        string payload = fixture.CreatePayload("launcher", "2.1.6", "SCBL.exe");

        Assert.True(StagedComponentBootstrapService.TryDeleteAppliedPayloadForTest(fixture.Root, payload));

        Assert.False(File.Exists(payload));
        Assert.False(Directory.Exists(fixture.Root));
    }

    [Fact]
    public void UnknownSiblingIsNotDeletedWithAppliedPayload()
    {
        using var fixture = new CacheFixture();
        string payload = fixture.CreatePayload("launcher", "2.1.6", "SCBL.exe");
        string unrelated = Path.Combine(Path.GetDirectoryName(payload)!, "keep-user-file.txt");
        File.WriteAllText(unrelated, "keep");

        Assert.True(StagedComponentBootstrapService.TryDeleteAppliedPayloadForTest(fixture.Root, payload));

        Assert.False(File.Exists(payload));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public void OutsideAndNonVersionedPathsAreRejectedWithoutDeletion()
    {
        using var fixture = new CacheFixture();
        string outside = Path.Combine(fixture.Parent, "outside.bin");
        string loose = Path.Combine(fixture.Root, "loose.bin");
        File.WriteAllText(outside, "outside");
        Directory.CreateDirectory(fixture.Root);
        File.WriteAllText(loose, "loose");

        Assert.False(StagedComponentBootstrapService.TryDeleteAppliedPayloadForTest(fixture.Root, outside));
        Assert.False(StagedComponentBootstrapService.TryDeleteAppliedPayloadForTest(fixture.Root, loose));

        Assert.True(File.Exists(outside));
        Assert.True(File.Exists(loose));
    }

    [Fact]
    public void ReparsePointInOwnedPathIsRejectedWithoutDeletion()
    {
        using var fixture = new CacheFixture();
        string payload = fixture.CreatePayload("hooks", "2.0.96", "uplay_r1_loader.dll");
        string versionDirectory = Path.GetDirectoryName(payload)!;

        FileAttributes Attributes(string path)
        {
            FileAttributes actual = File.GetAttributes(path);
            return Path.GetFullPath(path).Equals(Path.GetFullPath(versionDirectory), StringComparison.OrdinalIgnoreCase)
                ? actual | FileAttributes.ReparsePoint
                : actual;
        }

        Assert.False(StagedComponentBootstrapService.TryDeleteAppliedPayloadForTest(
            fixture.Root,
            payload,
            Attributes));
        Assert.True(File.Exists(payload));
    }

    [Fact]
    public void PendingRecoveryStateAndOtherPayloadRemainIntact()
    {
        using var fixture = new CacheFixture();
        string applied = fixture.CreatePayload("hooks", "2.0.96", "uplay_r1_loader.dll");
        string pending = fixture.CreatePayload("easytier", "2.6.4-scbl.1", "easytier.zip");
        string state = Path.Combine(fixture.Root, "component_state.json");
        File.WriteAllText(state, "{\"Channel\":\"test\"}");

        Assert.True(StagedComponentBootstrapService.TryDeleteAppliedPayloadForTest(fixture.Root, applied));

        Assert.False(File.Exists(applied));
        Assert.True(File.Exists(pending));
        Assert.True(File.Exists(state));
    }

    private sealed class CacheFixture : IDisposable
    {
        public CacheFixture()
        {
            Parent = Path.Combine(Path.GetTempPath(), "scbl-component-cleanup-" + Guid.NewGuid().ToString("N"));
            Root = Path.Combine(Parent, "components");
            Directory.CreateDirectory(Parent);
        }

        public string Parent { get; }
        public string Root { get; }

        public string CreatePayload(string component, string version, string fileName)
        {
            string path = Path.Combine(Root, component, version, fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Parent))
                Directory.Delete(Parent, recursive: true);
        }
    }
}
