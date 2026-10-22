using SplinterCellCNLauncher.Services;

namespace SCBL.Client.Tests;

public sealed class ClientComponentCatalogTests
{
    [Fact]
    public void EmbeddedCatalogMatchesTheCrossToolComponentContract()
    {
        IReadOnlyDictionary<string, (string FileName, string Section, string UpdateMode)> actual =
            ClientComponentUpdateService.ReadComponentCatalogContract();

        var expected = new Dictionary<string, (string FileName, string Section, string UpdateMode)>(StringComparer.OrdinalIgnoreCase)
        {
            ["launcher"] = ("SCBL.exe", "components", "updater-restart"),
            ["hooks"] = ("uplay_r1_loader.dll", "components", "before-game-start"),
            ["route-guard"] = ("route-guard.zip", "components", "next-launch"),
            ["easytier"] = ("easytier-windows-x86_64.zip", "components", "next-launch"),
            ["updater"] = ("SCBL.Updater.exe", "components", "next-launch"),
            ["client-files"] = ("client-files.zip", "files", "immediate")
        };

        Assert.Equal(expected.Count, actual.Count);
        foreach (KeyValuePair<string, (string FileName, string Section, string UpdateMode)> pair in expected)
        {
            Assert.True(actual.TryGetValue(pair.Key, out var entry), $"Missing component: {pair.Key}");
            Assert.Equal(pair.Value.FileName, entry.FileName);
            Assert.Equal(pair.Value.Section, entry.Section);
            Assert.Equal(pair.Value.UpdateMode, entry.UpdateMode);
        }
    }
}
