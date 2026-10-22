using SplinterCellCNLauncher.ApplicationLayer;
using SplinterCellCNLauncher.Models;

namespace SplinterCellCNLauncher;

public partial class MainWindow
{
    private TestChannelTransitionCoordinator? _testChannelTransitionCoordinator;

    private partial bool IsTestChannelContinuationConfirmationRequired()
        => GetTestChannelTransitionCoordinator().IsContinuationConfirmationRequired;

    private partial async Task<TestChannelUiCommandResult> ApplyTestChannelStartupChoiceAsync(
        bool continueTestChannel)
    {
        TestChannelTransitionResult result = continueTestChannel
            ? await GetTestChannelTransitionCoordinator().ContinueTestAsync()
            : await GetTestChannelTransitionCoordinator().ReturnToStableAsync();
        return CompleteTestChannelCommand(result);
    }

    private partial async Task<TestChannelUiCommandResult> RequestEnterTestChannelAsync()
    {
        TestChannelTransitionResult result = await GetTestChannelTransitionCoordinator()
            .EnterTestAsync();
        return CompleteTestChannelCommand(result);
    }

    private TestChannelTransitionCoordinator GetTestChannelTransitionCoordinator()
        => _testChannelTransitionCoordinator ??= new TestChannelTransitionCoordinator(
            App.ComponentUpdateChannel,
            () => ClientUpdateChannelNames.ParseOrStable(
                _settings.ClientUpdateChannelPreference),
            SaveClientUpdateChannelPreference,
            channel => _localUpdateService.ScheduleLauncherRestartAfterExit(
                Environment.ProcessId,
                channel),
            () => new TestChannelTransitionSafety(
                IsBusy: _isBusy,
                IsLaunchFlowActive: _isLaunchFlowActive,
                IsUpdating: _isUpdating,
                IsGameStarting: _isGameStarting,
                IsGameRunning: _isGameRunning,
                IsGameProcessDetected: IsAnyBlacklistGameProcessRunning(),
                IsClosing: _allowClose || _closeCleanupInProgress),
            _clientUpdateCoordinator.OperationGate);

    private void SaveClientUpdateChannelPreference(ClientUpdateChannel channel)
    {
        string previous = _settings.ClientUpdateChannelPreference;
        _settings.ClientUpdateChannelPreference = ClientUpdateChannelNames.ToName(channel);
        try
        {
            _settingsService.Save(_settings);
        }
        catch
        {
            _settings.ClientUpdateChannelPreference = previous;
            throw;
        }
    }

    private TestChannelUiCommandResult CompleteTestChannelCommand(
        TestChannelTransitionResult result)
    {
        if (result == TestChannelTransitionResult.StopStartup)
            Close();
        return result switch
        {
            TestChannelTransitionResult.ContinueStartup =>
                TestChannelUiCommandResult.ContinueStartup,
            TestChannelTransitionResult.StopStartup =>
                TestChannelUiCommandResult.StopStartup,
            _ => TestChannelUiCommandResult.Failed
        };
    }
}
