using System.Xml.Linq;

namespace SCBL.Client.Tests;

public sealed class LauncherWindowChromeAccessibilityTests
{
    [Fact]
    public void WindowChromeRemainsInteractiveAboveModalOverlays()
    {
        XDocument document = XDocument.Load(FindRepositoryFile(
            "client",
            "ScblPublicLauncher",
            "MainWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        XElement titleBar = FindNamedElement(document, x, "titleBarDragSurface");
        XElement titleButtons = FindNamedElement(document, x, "spTitleButtons");
        int titleBarZIndex = ReadZIndex(titleBar);
        int titleButtonsZIndex = ReadZIndex(titleButtons);

        Assert.Equal("TitleBar_MouseLeftButtonDown", titleBar.Attribute("MouseLeftButtonDown")?.Value);

        foreach (string overlayName in new[]
                 {
                     "playersOverlay",
                     "dialogOverlay",
                     "passwordRecoveryOverlay",
                     "serverSettingsOverlay",
                     "guideOverlay"
                 })
        {
            int overlayZIndex = ReadZIndex(FindNamedElement(document, x, overlayName));
            Assert.True(titleBarZIndex > overlayZIndex,
                $"The title bar must remain draggable above {overlayName}.");
            Assert.True(titleButtonsZIndex > overlayZIndex,
                $"Window controls must remain available above {overlayName}.");
        }
    }

    [Fact]
    public void TestChannelDialogsKeepThePrimaryActionFocusedWithoutAnInfoAdapter()
    {
        string source = File.ReadAllText(FindRepositoryFile(
            "client",
            "ScblPublicLauncher",
            "MainWindow.TestChannel.cs"));

        Assert.DoesNotContain("ShowTestChannelInfoDialogAsync", source, StringComparison.Ordinal);
        Assert.Equal(
            2,
            source.Split(
                "FocusTestChannelDialogPrimaryAction();",
                StringSplitOptions.None).Length - 1);
        Assert.Contains("btnDialogYes.Focus();", source, StringComparison.Ordinal);
        Assert.Contains("Keyboard.Focus(btnDialogYes);", source, StringComparison.Ordinal);
    }

    private static XElement FindNamedElement(XDocument document, XNamespace x, string name)
        => document.Descendants().Single(element => element.Attribute(x + "Name")?.Value == name);

    private static int ReadZIndex(XElement element)
        => int.Parse(element.Attribute("Panel.ZIndex")?.Value
            ?? throw new InvalidOperationException($"{element.Name.LocalName} has no Panel.ZIndex."));

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
