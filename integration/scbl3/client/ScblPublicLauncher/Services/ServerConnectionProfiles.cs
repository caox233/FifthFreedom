using SplinterCellCNLauncher.Models;
using System;

namespace SplinterCellCNLauncher.Services;

public sealed record ServerConnectionProfile(
    ClientUpdateChannel Channel,
    string PublicEndpoint,
    int UpdatePort)
{
    public string ChannelName => ClientUpdateChannelNames.ToName(Channel);

    public Uri DescriptorUri => new(
        new Uri(PublicTunnelConfig.BuildPublicUpdateBaseUrl(PublicEndpoint, UpdatePort)),
        "scbl-server.json");
}

public static class ServerConnectionProfiles
{
    public static ServerConnectionProfile Resolve(
        LauncherSettings settings,
        ClientUpdateChannel channel)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return channel == ClientUpdateChannel.Test
            ? new ServerConnectionProfile(
                channel,
                PublicTunnelConfig.NormalizePublicEndpoint(
                    string.IsNullOrWhiteSpace(settings.TestPublicEndpoint)
                        ? PublicTunnelConfig.DefaultTestPublicEndpoint
                        : settings.TestPublicEndpoint),
                NormalizePort(settings.TestPublicUpdatePort, PublicTunnelConfig.DefaultTestPublicUpdatePort))
            : new ServerConnectionProfile(
                channel,
                PublicTunnelConfig.NormalizePublicEndpoint(settings.PublicEndpoint),
                NormalizePort(settings.PublicUpdatePort, PublicTunnelConfig.DefaultPublicUpdatePort));
    }

    private static int NormalizePort(int value, int fallback)
        => value is > 0 and <= 65535 ? value : fallback;
}
