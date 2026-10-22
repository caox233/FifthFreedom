using SplinterCellCNLauncher.ApplicationLayer;
using SplinterCellCNLauncher.Models;

namespace SCBL.Client.Tests;

public sealed class ClientUpdateChannelTests
{
    [Theory]
    [InlineData("--test")]
    [InlineData("--update-channel", "test")]
    [InlineData("--update-channel=test")]
    public void Test_aliases_select_test(params string[] args)
    {
        ClientUpdateChannelSelection selection = ClientUpdateChannelParser.Parse(args);

        Assert.Equal(ClientUpdateChannel.Test, selection.Channel);
        Assert.True(selection.WasExplicitlySelected);
        Assert.Empty(selection.Warning);
    }

    [Fact]
    public void Invalid_or_conflicting_explicit_values_fail_closed_to_stable()
    {
        ClientUpdateChannelSelection invalid = ClientUpdateChannelParser.Parse(
            ["--update-channel", "preview"]);
        ClientUpdateChannelSelection conflict = ClientUpdateChannelParser.Parse(
            ["--test", "--update-channel", "stable"]);

        Assert.Equal(ClientUpdateChannel.Stable, invalid.Channel);
        Assert.NotEmpty(invalid.Warning);
        Assert.Equal(ClientUpdateChannel.Stable, conflict.Channel);
        Assert.NotEmpty(conflict.Warning);
    }

    [Fact]
    public void Startup_uses_persisted_preference_only_without_an_explicit_argument()
    {
        ClientUpdateChannelSelection persisted = ClientUpdateChannelStartupPolicy.Resolve(
            Array.Empty<string>(),
            "test");
        ClientUpdateChannelSelection explicitStable = ClientUpdateChannelStartupPolicy.Resolve(
            ["--update-channel", "stable"],
            "test");
        ClientUpdateChannelSelection invalidPersisted = ClientUpdateChannelStartupPolicy.Resolve(
            Array.Empty<string>(),
            "preview");

        Assert.Equal(ClientUpdateChannel.Test, persisted.Channel);
        Assert.False(persisted.WasExplicitlySelected);
        Assert.Equal(ClientUpdateChannel.Stable, explicitStable.Channel);
        Assert.True(explicitStable.WasExplicitlySelected);
        Assert.Equal(ClientUpdateChannel.Stable, invalidPersisted.Channel);
        Assert.NotEmpty(invalidPersisted.Warning);
    }

    [Fact]
    public void Restart_arguments_replace_aliases_and_preserve_unrelated_arguments()
    {
        string[] normalized = ClientUpdateChannelArguments.Replace(
            ["--test", "--diagnostic", "--update-channel=stable"],
            ClientUpdateChannel.Test);

        Assert.Equal(
            ["--diagnostic", "--update-channel", "test"],
            normalized);
    }

    [Fact]
    public void Only_a_verified_explicit_selection_is_persisted()
    {
        ClientUpdateChannelSelection commandLine = ClientUpdateChannelParser.Parse(["--test"]);
        ClientUpdateChannelSelection accepted = ClientUpdateChannelStartupPolicy.Resolve(
            ["--test"], "stable");
        ClientUpdateChannelSelection rejected = accepted with { Warning = "invalid" };

        Assert.True(ClientUpdateChannelStartupPolicy.ShouldPersistExplicitSelection(
            accepted,
            commandLine));
        Assert.False(ClientUpdateChannelStartupPolicy.ShouldPersistExplicitSelection(
            rejected,
            commandLine));
    }

}
