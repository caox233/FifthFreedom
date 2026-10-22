using SplinterCellCNLauncher;
using System.Xml.Linq;

namespace SCBL.Client.Tests;

public sealed class ComponentUpdatePresentationTests
{
    [Fact]
    public void DialogScrollViewerUsesDarkAccessibleLauncherScrollBar()
    {
        string repositoryRoot = FindRepositoryRoot();
        string xamlPath = Path.Combine(
            repositoryRoot,
            "client",
            "ScblPublicLauncher",
            "MainWindow.xaml");
        XDocument document = XDocument.Load(xamlPath, LoadOptions.PreserveWhitespace);
        XNamespace presentation = document.Root!.Name.Namespace;
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        XElement message = document
            .Descendants(presentation + "TextBlock")
            .Single(element =>
                string.Equals(
                    (string?)element.Attribute(x + "Name"),
                    "txtDialogMessage",
                    StringComparison.Ordinal));
        XElement scrollViewer = message
            .Ancestors(presentation + "ScrollViewer")
            .Single();

        Assert.Equal(
            "{StaticResource LauncherDialogScrollViewer}",
            (string?)scrollViewer.Attribute("Style"));
        Assert.Equal("330", (string?)scrollViewer.Attribute("MaxHeight"));
        Assert.Equal(
            "{Binding ElementName=txtDialogTitle}",
            (string?)scrollViewer.Attribute("AutomationProperties.LabeledBy"));

        Dictionary<string, XElement> styles = document
            .Descendants(presentation + "Style")
            .Where(style => style.Attribute(x + "Key") is not null)
            .ToDictionary(
                style => (string)style.Attribute(x + "Key")!,
                StringComparer.Ordinal);

        XElement viewerStyle = styles["LauncherDialogScrollViewer"];
        Dictionary<string, string?> viewerSetters = viewerStyle
            .Elements(presentation + "Setter")
            .Where(setter => setter.Attribute("Value") is not null)
            .ToDictionary(
                setter => (string)setter.Attribute("Property")!,
                setter => (string?)setter.Attribute("Value"),
                StringComparer.Ordinal);

        Assert.Equal("Auto", viewerSetters["VerticalScrollBarVisibility"]);
        Assert.Equal("Disabled", viewerSetters["HorizontalScrollBarVisibility"]);
        Assert.Equal("VerticalOnly", viewerSetters["PanningMode"]);
        Assert.Equal("True", viewerSetters["Focusable"]);
        Assert.Equal("Continue", viewerSetters["KeyboardNavigation.TabNavigation"]);

        XElement scrollBarStyle = styles["LauncherDialogScrollBar"];
        string?[] commands = scrollBarStyle
            .Descendants(presentation + "RepeatButton")
            .Select(button => (string?)button.Attribute("Command"))
            .Where(command => !string.IsNullOrWhiteSpace(command))
            .ToArray();

        Assert.Contains("{x:Static ScrollBar.PageUpCommand}", commands);
        Assert.Contains("{x:Static ScrollBar.PageDownCommand}", commands);
        Assert.DoesNotContain(
            commands,
            command =>
                command!.Contains("LineUpCommand", StringComparison.Ordinal) ||
                command.Contains("LineDownCommand", StringComparison.Ordinal));
        Assert.Empty(scrollBarStyle.Descendants(presentation + "Path"));
        XElement track = Assert.Single(scrollBarStyle.Descendants(presentation + "Track"));
        Assert.Equal("Vertical", (string?)track.Attribute("Orientation"));
        Assert.Single(scrollBarStyle.Descendants(presentation + "Thumb"));

        XElement thumbStyle = styles["LauncherDialogScrollBarThumb"];
        string?[] triggerProperties = thumbStyle
            .Descendants(presentation + "Trigger")
            .Select(trigger => (string?)trigger.Attribute("Property"))
            .ToArray();

        Assert.Contains("IsMouseOver", triggerProperties);
        Assert.Contains("IsDragging", triggerProperties);
        Assert.Contains(
            scrollBarStyle.Descendants(presentation + "DataTrigger"),
            trigger =>
                ((string?)trigger.Attribute("Binding"))?.Contains(
                    "IsKeyboardFocusWithin",
                    StringComparison.Ordinal) == true);
    }

    [Fact]
    public void DialogKeepsLongMessagesBoundedForLauncherScaling()
    {
        string repositoryRoot = FindRepositoryRoot();
        string xamlPath = Path.Combine(
            repositoryRoot,
            "client",
            "ScblPublicLauncher",
            "MainWindow.xaml");
        XDocument document = XDocument.Load(xamlPath, LoadOptions.PreserveWhitespace);
        XNamespace presentation = document.Root!.Name.Namespace;
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        XElement message = document
            .Descendants(presentation + "TextBlock")
            .Single(element =>
                string.Equals(
                    (string?)element.Attribute(x + "Name"),
                    "txtDialogMessage",
                    StringComparison.Ordinal));
        XElement scrollViewer = message
            .Ancestors(presentation + "ScrollViewer")
            .Single();
        XElement dialogBorder = scrollViewer
            .Ancestors(presentation + "Border")
            .First(border =>
                string.Equals(
                    (string?)border.Attribute("Width"),
                    "520",
                    StringComparison.Ordinal));

        Assert.Equal("330", (string?)scrollViewer.Attribute("MaxHeight"));
        Assert.Equal("520", (string?)dialogBorder.Attribute("Width"));
        Assert.Equal("520", (string?)dialogBorder.Attribute("MaxHeight"));

        XElement rootViewbox = document
            .Descendants(presentation + "Viewbox")
            .First();
        Assert.True(
            rootViewbox.Attribute("Stretch") is null ||
            (string?)rootViewbox.Attribute("Stretch") == "Uniform");
    }

    [Fact]
    public void DialogTitlesKeepDefaultLimitAndUseDedicatedUpdateNoticeLimit()
    {
        Assert.Equal(80, MainWindow.DefaultDialogTitleCharacters);
        Assert.Equal(160, MainWindow.UpdateNoticeDialogTitleCharacters);

        string repositoryRoot = FindRepositoryRoot();
        string dialogsSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "client",
            "ScblPublicLauncher",
            "MainWindow.Dialogs.cs"));
        string updatesSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "client",
            "ScblPublicLauncher",
            "MainWindow.ClientUpdates.cs"));

        Assert.Contains(
            "int titleMaxChars = DefaultDialogTitleCharacters",
            dialogsSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "titleMaxChars: UpdateNoticeDialogTitleCharacters",
            dialogsSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "CompactDialogText(title, titleMaxChars)",
            dialogsSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "await ShowUpdateNoticeDialogAsync(",
            updatesSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DialogTruncationLogsLengthsWithoutLoggingOriginalText()
    {
        string repositoryRoot = FindRepositoryRoot();
        string dialogsSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "client",
            "ScblPublicLauncher",
            "MainWindow.Dialogs.cs"));

        Assert.Contains("originalLength={normalized.Length}", dialogsSource, StringComparison.Ordinal);
        Assert.Contains("maxChars={maxChars}", dialogsSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Full text", dialogsSource, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("+ normalized", dialogsSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Update_flow_and_channel_presentation_share_the_atomic_operation_gate()
    {
        string repositoryRoot = FindRepositoryRoot();
        string updatesSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "client",
            "ScblPublicLauncher",
            "MainWindow.ClientUpdates.cs"));
        string projectionSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "client",
            "ScblPublicLauncher",
            "MainWindow.Presentation.cs"));

        Assert.Contains(
            "_clientUpdateCoordinator.TryBeginUpdateOperation()",
            updatesSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "Component update skipped because an update-channel transition owns the operation gate.",
            updatesSource,
            StringComparison.Ordinal);
        Assert.Contains("if (busy && settingsMenu != null)", projectionSource, StringComparison.Ordinal);
        Assert.Contains("if (updating && settingsMenu != null)", projectionSource, StringComparison.Ordinal);
        Assert.True(
            projectionSource.Split("UpdateSettingsMenuText();", StringSplitOptions.None).Length >= 3,
            "Busy and update state transitions must immediately refresh the open settings menu.");
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "COMPONENT_VERSIONS.json"))
                && Directory.Exists(Path.Combine(current.FullName, "client", "ScblPublicLauncher")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Unable to locate the SCBL repository root.");
    }
}
