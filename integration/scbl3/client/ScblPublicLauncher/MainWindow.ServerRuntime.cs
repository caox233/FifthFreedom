using SplinterCellCNLauncher.ApplicationLayer;
using SplinterCellCNLauncher.Models;
using SplinterCellCNLauncher.Services;

namespace SplinterCellCNLauncher;

public partial class MainWindow
{
    private string GetLauncherBaseDirectory()
        => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private string GetConfiguredPublicEndpoint()
        => _serverRuntime?.PublicEndpoint ?? _serverProfile.PublicEndpoint;

    private string GetConfiguredTunnelSecret()
        => _serverRuntime?.NetworkSecret
            ?? PublicTunnelConfig.NormalizeTunnelSecret(_settings.TunnelSecret);

    private string GetServerVirtualIp()
        => _serverRuntime?.ServerVirtualIp
            ?? (string.IsNullOrWhiteSpace(_settings.LastServerVirtualIp)
                ? PublicTunnelConfig.ServerVirtualIp
                : _settings.LastServerVirtualIp);

    private int GetControlPlanePort()
        => _serverRuntime?.ControlPlanePort ?? PublicTunnelConfig.ControlPlanePort;

    private string GetDedicatedAuthAddress()
        => $"http://{GetServerVirtualIp()}:{_serverRuntime?.DedicatedAuthPort ?? 50051}";

    private string GetControlPlaneSigningSecret()
        => _controlPlaneSigningSecretProvider.GetActiveSecret(GetConfiguredTunnelSecret());

    private EasyTierClientOptions GetEasyTierClientOptions()
        => new(
            _settings.EasyTierInstanceId,
            _serverRuntime?.NetworkName ?? _settings.EasyTierNetworkName,
            LatencyFirst: false,
            EnableP2P: true,
            StableRelayMode: false,
            EnableUdpBroadcastRelay: true,
            ForceGameVirtualAdapter: _settings.ForceGameVirtualAdapter,
            WssPort: _serverRuntime?.NetworkPort ?? _settings.EasyTierWssPort,
            QuicPort: _serverRuntime?.NetworkPort ?? _settings.EasyTierQuicPort,
            NetworkMode: ClientNetworkModeNames.ParseOrFast(_settings.NetworkMode));

    internal NetworkModeState GetNetworkModeState()
        => _networkModeCoordinator.State;

    internal Task<NetworkModeChangeResult> ChangeNetworkModeAsync(
        ClientNetworkMode mode,
        CancellationToken cancellationToken = default)
        => _networkModeCoordinator.ChangeAsync(mode, cancellationToken);

    private void InitializeLocalStateAfterVersionCheck()
    {
        if (_gameLocatorService.IsValidGameDirectory(_settings.GameDirectory))
        {
            _gameDir = _settings.GameDirectory;
            LogService.Info($"Loaded saved game directory: {_gameDir}");
        }
        else
        {
            string? detectedDir = _gameLocatorService.TryAutoFindGameDirectory();
            if (!string.IsNullOrWhiteSpace(detectedDir))
            {
                _gameDir = detectedDir;
                _settings.GameDirectory = detectedDir;
                LogService.Info($"Auto detected game directory: {detectedDir}");
            }
            else
            {
                _gameDir = "";
                LogService.Info("Game directory was not auto detected.");
            }
        }

        _settingsService.Save(_settings);
        LogService.Info("Public launcher ready.");
        LogService.Info($"Settings path: {_settingsService.SettingsPath}");
        LogService.Info($"Public endpoint loaded: {GetConfiguredPublicEndpoint()}");
    }

    private void SaveRuntimeSettings()
    {
        _settings.GameDirectory = _gameDir;
        _settings.GameExecutable = GetSelectedGameExecutable();
        _settings.LastAssignedVirtualIp = _assignedIp;
        _settings.LastServerVirtualIp = GetServerVirtualIp();
        if (App.ComponentUpdateChannel == ClientUpdateChannel.Test)
        {
            _settings.TestPublicEndpoint = _serverProfile.PublicEndpoint;
            _settings.TestPublicUpdatePort = _serverProfile.UpdatePort;
        }
        else
        {
            _settings.PublicEndpoint = _serverProfile.PublicEndpoint;
            _settings.PublicUpdatePort = _serverProfile.UpdatePort;
        }
        _settings.TunnelSecret = GetConfiguredTunnelSecret();
        _settingsService.Save(_settings);
    }
}
