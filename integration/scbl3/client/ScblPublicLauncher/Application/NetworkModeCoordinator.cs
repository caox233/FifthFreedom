using SplinterCellCNLauncher.Models;
using SplinterCellCNLauncher.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace SplinterCellCNLauncher.ApplicationLayer;

public enum NetworkModeChangeOutcome
{
    Unchanged,
    RejectedGameActive,
    Applied,
    ConnectionFailed
}

public sealed record NetworkModeState(
    ClientNetworkMode Mode,
    bool IsGameActive,
    bool IsChanging)
{
    public bool CanChange => !IsGameActive && !IsChanging;
}

public sealed record NetworkModeChangeResult(
    NetworkModeChangeOutcome Outcome,
    ClientNetworkMode Mode,
    NetworkReadyResult? NetworkResult)
{
    public bool Succeeded
        => Outcome is NetworkModeChangeOutcome.Unchanged or NetworkModeChangeOutcome.Applied;
}

/// <summary>
/// Owns network-mode persistence and the explicit EasyTier repair needed to apply it.
/// The coordinator is UI-agnostic and never falls back to another transport.
/// </summary>
public sealed class NetworkModeCoordinator
{
    private readonly Func<ClientNetworkMode> _readMode;
    private readonly Action<ClientNetworkMode> _stageMode;
    private readonly Action _persistSettings;
    private readonly Func<bool> _isGameActive;
    private readonly Func<CancellationToken, Task<NetworkReadyResult>> _rebuildNetwork;
    private readonly SemaphoreSlim _changeLock = new(1, 1);
    private int _isChanging;

    public NetworkModeCoordinator(
        Func<ClientNetworkMode> readMode,
        Action<ClientNetworkMode> stageMode,
        Action persistSettings,
        Func<bool> isGameActive,
        Func<CancellationToken, Task<NetworkReadyResult>> rebuildNetwork)
    {
        _readMode = readMode ?? throw new ArgumentNullException(nameof(readMode));
        _stageMode = stageMode ?? throw new ArgumentNullException(nameof(stageMode));
        _persistSettings = persistSettings ?? throw new ArgumentNullException(nameof(persistSettings));
        _isGameActive = isGameActive ?? throw new ArgumentNullException(nameof(isGameActive));
        _rebuildNetwork = rebuildNetwork ?? throw new ArgumentNullException(nameof(rebuildNetwork));
    }

    public NetworkModeState State
        => new(_readMode(), _isGameActive(), Volatile.Read(ref _isChanging) != 0);

    public async Task<NetworkModeChangeResult> ChangeAsync(
        ClientNetworkMode requestedMode,
        CancellationToken cancellationToken = default)
    {
        ClientNetworkMode target = requestedMode == ClientNetworkMode.Compatible
            ? ClientNetworkMode.Compatible
            : ClientNetworkMode.Fast;

        await _changeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Exchange(ref _isChanging, 1);
        try
        {
            ClientNetworkMode previous = _readMode();
            if (previous == target)
            {
                return new NetworkModeChangeResult(
                    NetworkModeChangeOutcome.Unchanged,
                    previous,
                    null);
            }

            if (_isGameActive())
            {
                return new NetworkModeChangeResult(
                    NetworkModeChangeOutcome.RejectedGameActive,
                    previous,
                    null);
            }

            _stageMode(target);
            NetworkReadyResult result;
            try
            {
                result = await _rebuildNetwork(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await RestorePreviousRuntimeBestEffortAsync(previous).ConfigureAwait(false);
                throw;
            }
            if (result.Ok)
            {
                _persistSettings();
                return new NetworkModeChangeResult(
                    NetworkModeChangeOutcome.Applied,
                    target,
                    result);
            }

            // Mode changes are transactional. A failed target transport must not become
            // the next-launch default or leave a mismatched EasyTier runtime behind.
            await RestorePreviousRuntimeBestEffortAsync(previous).ConfigureAwait(false);
            return new NetworkModeChangeResult(
                NetworkModeChangeOutcome.ConnectionFailed,
                previous,
                result);
        }
        finally
        {
            Interlocked.Exchange(ref _isChanging, 0);
            _changeLock.Release();
        }
    }

    private async Task RestorePreviousRuntimeBestEffortAsync(ClientNetworkMode previous)
    {
        try
        {
            _stageMode(previous);
            await _rebuildNetwork(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try
            {
                LogService.Warning(
                    "Network mode rollback did not restore connectivity: " + ex.GetType().Name);
            }
            catch
            {
                // Best-effort rollback must never replace the target rebuild failure.
            }
        }
    }
}
