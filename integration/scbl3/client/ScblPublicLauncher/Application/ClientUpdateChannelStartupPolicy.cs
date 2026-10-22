using SplinterCellCNLauncher.Models;

namespace SplinterCellCNLauncher.ApplicationLayer;

public static class ClientUpdateChannelStartupPolicy
{
    public static bool IsTestRequested(
        ClientUpdateChannelSelection commandLine,
        string? persistedPreference)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        if (commandLine.WasExplicitlySelected)
            return commandLine.Channel == ClientUpdateChannel.Test;
        return ClientUpdateChannelNames.TryParse(
                persistedPreference,
                out ClientUpdateChannel persisted)
            && persisted == ClientUpdateChannel.Test;
    }

    public static bool ShouldPersistExplicitSelection(
        ClientUpdateChannelSelection resolved,
        ClientUpdateChannelSelection commandLine)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(commandLine);
        return resolved.WasExplicitlySelected
            && string.IsNullOrWhiteSpace(resolved.Warning)
            && resolved.Channel == commandLine.Channel;
    }

    public static ClientUpdateChannelSelection Resolve(
        IEnumerable<string>? args,
        string? persistedPreference)
    {
        ClientUpdateChannelSelection commandLine = ClientUpdateChannelParser.Parse(args);
        if (commandLine.WasExplicitlySelected)
            return commandLine;
        if (ClientUpdateChannelNames.TryParse(
                persistedPreference,
                out ClientUpdateChannel persisted))
        {
            return new ClientUpdateChannelSelection(persisted, false, commandLine.Warning);
        }
        return new ClientUpdateChannelSelection(
            ClientUpdateChannel.Stable,
            false,
            "Persisted update-channel preference is invalid; falling back to stable.");
    }
}
