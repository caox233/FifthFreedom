using SplinterCellCNLauncher.Models;
using SplinterCellCNLauncher.Services;

namespace SplinterCellCNLauncher;

public partial class MainWindow
{
    private void ApplyServerDescriptor(ServerDescriptorSnapshot snapshot)
    {
        _serverRuntime = ServerRuntimeConfiguration.Create(snapshot, _serverProfile);
        _settings.TunnelSecret = _serverRuntime.NetworkSecret;
        _settings.EasyTierNetworkName = _serverRuntime.NetworkName;
        _settings.EasyTierWssPort = _serverRuntime.NetworkPort;
        _settings.EasyTierQuicPort = _serverRuntime.NetworkPort;
        _settings.LastServerVirtualIp = _serverRuntime.ServerVirtualIp;
        LogService.Info(
            $"Server descriptor accepted: server={snapshot.Descriptor.Server.Id}, mode={snapshot.Descriptor.Server.Mode}, "
            + $"source={snapshot.Source}, sha256={snapshot.ContentSha256}, client={snapshot.Descriptor.Client.Version}");
    }

    private async Task<ServerDescriptorSnapshot> RefreshServerDescriptorAsync(
        CancellationToken cancellationToken)
    {
        ServerDescriptorSnapshot snapshot = await _serverDescriptorClient
            .GetAsync(cancellationToken)
            .ConfigureAwait(true);
        ApplyServerDescriptor(snapshot);
        return snapshot;
    }

    private string GetServerDescriptorObservationId()
        => _serverRuntime?.DescriptorSnapshot.ContentSha256 ?? "";
}
