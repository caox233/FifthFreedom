// Only unrelated diagnostic projection and the retained executable-name constant
// are substituted. LaunchPipe framing, peer validation and ACK callbacks are the
// exact production implementation linked into this test executable.
namespace ScblLan
{
    internal static class GameRuntime { internal static readonly string[] ExecutableNames = ["Blacklist_DX11_game.exe"]; }
}
namespace SplinterCellCNLauncher.Services
{
    internal sealed record RequestedConfigurationProjection;
    internal static class RequestedConfigurationSnapshot
    {
        internal static RequestedConfigurationProjection Prepare(byte[] _) => new();
    }
}
