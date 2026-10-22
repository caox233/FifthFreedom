using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SCBL.Client.Tests;

public sealed class LauncherUiResourceAuditTests
{
    [Fact]
    public void EveryEmbeddedLauncherBackgroundIsReferencedByXaml()
    {
        string xamlPath = FindRepositoryFile("client", "ScblPublicLauncher", "MainWindow.xaml");
        string projectPath = FindRepositoryFile("client", "ScblPublicLauncher", "SplinterCellCNLauncher.csproj");
        string xaml = File.ReadAllText(xamlPath);
        XDocument project = XDocument.Load(projectPath);

        string[] embeddedBackgrounds = project
            .Descendants("Resource")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(include => include?.StartsWith("Assets\\", StringComparison.OrdinalIgnoreCase) == true)
            .Select(include => include!.Replace('\\', '/'))
            .ToArray();

        Assert.NotEmpty(embeddedBackgrounds);
        foreach (string resource in embeddedBackgrounds)
        {
            Assert.Contains($"Source=\"{resource}\"", xaml, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryExplicitWindowResourceIsReferenced()
    {
        string xamlPath = FindRepositoryFile("client", "ScblPublicLauncher", "MainWindow.xaml");
        string launcherDirectory = Path.GetDirectoryName(xamlPath)
            ?? throw new InvalidOperationException("Launcher directory is unavailable.");
        string xaml = File.ReadAllText(xamlPath);
        XDocument document = XDocument.Parse(xaml);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement resources = document.Root?.Element(presentation + "Window.Resources")
            ?? throw new InvalidDataException("MainWindow.xaml has no Window.Resources element.");
        string source = string.Join(
            "\n",
            Directory.GetFiles(launcherDirectory, "MainWindow*.cs")
                .Select(File.ReadAllText));

        foreach (string key in resources.Elements()
                     .Select(element => element.Attribute(x + "Key")?.Value)
                     .Where(key => !string.IsNullOrWhiteSpace(key))
                     .Cast<string>())
        {
            bool referencedByXaml = Regex.IsMatch(
                xaml,
                @"\{(?:Static|Dynamic)Resource\s+" + Regex.Escape(key) + @"(?:\}|,)");
            bool referencedByCode = Regex.IsMatch(
                source,
                "(?:Find|TryFind)Resource\\(\\s*\"" + Regex.Escape(key) + "\"\\s*\\)");

            Assert.True(referencedByXaml || referencedByCode, $"Window resource '{key}' is not referenced.");
        }
    }

    [Fact]
    public void GuideCopyMatchesCurrentModeAndPlayerPresentation()
    {
        string guide = File.ReadAllText(FindRepositoryFile(
            "client",
            "ScblPublicLauncher",
            "MainWindow.Guide.cs"));

        Assert.Contains("玩家名称、在线状态和延迟", guide, StringComparison.Ordinal);
        Assert.Contains("player names, online status, and latency", guide, StringComparison.Ordinal);
        Assert.Contains("官方/模组模式", guide, StringComparison.Ordinal);
        Assert.Contains("Official/MOD mode", guide, StringComparison.Ordinal);
        Assert.DoesNotContain("虚拟 IP", guide, StringComparison.Ordinal);
        Assert.DoesNotContain("优化/修复模式", guide, StringComparison.Ordinal);
        Assert.DoesNotContain("Optimized/Fixed mode", guide, StringComparison.Ordinal);
        Assert.DoesNotContain("欢乐/严肃模式", guide, StringComparison.Ordinal);
        Assert.DoesNotContain("Fun/Serious mode", guide, StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(params string[] relativeParts)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string candidate = Path.Combine(new[] { directory.FullName }.Concat(relativeParts).ToArray());
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException("Unable to locate repository file.", Path.Combine(relativeParts));
    }
}
