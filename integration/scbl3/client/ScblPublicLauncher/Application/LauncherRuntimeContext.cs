using SplinterCellCNLauncher.Models;

namespace SplinterCellCNLauncher.ApplicationLayer;

/// <summary>
/// Immutable process-wide inputs selected before the WPF composition root is created.
/// Services consume this value instead of reaching back into <c>App</c>.
/// </summary>
public sealed record LauncherRuntimeContext(ClientUpdateChannel UpdateChannel)
{
    public string UpdateChannelName => ClientUpdateChannelNames.ToName(UpdateChannel);
}
