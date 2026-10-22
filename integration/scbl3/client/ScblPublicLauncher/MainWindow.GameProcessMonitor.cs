using SplinterCellCNLauncher.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace SplinterCellCNLauncher;

public partial class MainWindow
{
    private Process? _gameProcess;
    private readonly object _gameProcessSync = new();
    private readonly HashSet<int> _launcherOwnedGamePids = new();
    private CancellationTokenSource? _gameMonitorCts;

    private const int GameStableAppearSeconds = 20;
    private const int InitialStartupRestartAvailableSeconds = 10;
    private const int RelaunchedStartupRestartAvailableSeconds = 4;
    private const int RelaunchedGameStableAppearSeconds = 20;
    private const int GameProcessProbeIntervalMs = 1000;
    private const int GameExitMissingChecks = 2;
    private const int GameProcessCandidateRequiredObservations = 2;

    private void StartGameMonitor(
        string gameExecutable,
        string expectedGamePath,
        string expectedHookPath,
        int initialProcessId,
        DateTime launchSessionStartedUtc,
        IReadOnlyCollection<int> preExistingMatchingPids)
    {
        CancelGameMonitor();
        _gameMonitorCts = new CancellationTokenSource();
        var token = _gameMonitorCts.Token;

        string launchModeLabel = GetSelectedGameLabel();
        var excludedCandidatePids = preExistingMatchingPids.ToHashSet();
        excludedCandidatePids.Add(initialProcessId);
        var relatedGamePids = new HashSet<int> { initialProcessId };
        var candidateObservationTracker = new GameProcessCandidateObservationTracker();

        _ = Task.Run(async () =>
        {
            string expectedProcessName = Path.GetFileNameWithoutExtension(gameExecutable);
            DateTime deadline = launchSessionStartedUtc.AddSeconds(
                GameProcessSessionService.LaunchObservationWindowSeconds);
            DateTime candidateAdmissionDeadline = deadline;
            DateTime? stableSince = null;
            int activeCandidatePid = 0;
            string activeGamePath = expectedGamePath;
            bool waitingForRelaunch = false;
            bool finalProcessConfirmed = false;
            bool runtimeValidated = false;
            bool startupRestartOffered = false;
            int missingChecksAfterRunning = 0;

            LogService.Info(
                $"Waiting for actual game process: {expectedProcessName}, initialPid={initialProcessId}, " +
                $"timeout={GameProcessSessionService.LaunchObservationWindowSeconds}s, initialStable={GameStableAppearSeconds}s, " +
                $"relaunchStable={RelaunchedGameStableAppearSeconds}s");

            while (!token.IsCancellationRequested)
            {
                int[] liveOwnedPids = GetLiveLauncherOwnedGamePids();

                if (!finalProcessConfirmed)
                {
                    if (!waitingForRelaunch)
                    {
                        bool initialProcessAlive = liveOwnedPids.Contains(initialProcessId);
                        _processRouterService.UpdateGameSession(liveOwnedPids, allowEmptyGamePids: true);

                        if (initialProcessAlive)
                        {
                            stableSince ??= DateTime.UtcNow;
                            int stableSeconds = (int)(DateTime.UtcNow - stableSince.Value).TotalSeconds;
                            if (stableSeconds == 0)
                                LogService.Info("Initial game process appeared. Waiting for stable presence...");

                            if (!startupRestartOffered && stableSeconds >= InitialStartupRestartAvailableSeconds)
                            {
                                startupRestartOffered = true;
                                await Dispatcher.InvokeAsync(() => SetStartupRestartAvailable(true));
                            }

                            if (stableSeconds >= GameStableAppearSeconds)
                            {
                                finalProcessConfirmed = true;
                                _processRouterService.UpdateGameSession(liveOwnedPids, allowEmptyGamePids: false);
                                await Dispatcher.InvokeAsync(() => SetGameRunningState(true));
                                LogService.Info($"Initial {launchModeLabel} game process detected and stable. pid={initialProcessId}");
                            }
                        }
                        else
                        {
                            waitingForRelaunch = true;
                            stableSince = null;
                            if (!startupRestartOffered)
                                await Dispatcher.InvokeAsync(() => SetStartupRestartAvailable(false));
                            _processRouterService.UpdateGameSession(Array.Empty<int>(), allowEmptyGamePids: true);
                            LogService.Info(
                                $"Initial game process exited before the {GameStableAppearSeconds}s stable threshold. " +
                                "Keeping Route Guard alive and waiting for the verified relaunched game PID.");
                        }
                    }
                    else
                    {
                        bool activeCandidateAlive = activeCandidatePid > 0 && liveOwnedPids.Contains(activeCandidatePid);
                        if (!activeCandidateAlive)
                        {
                            if (activeCandidatePid > 0)
                            {
                                LogService.Info($"Relaunched game candidate PID={activeCandidatePid} exited before stable confirmation. Continue waiting.");
                                excludedCandidatePids.Add(activeCandidatePid);
                                activeCandidatePid = 0;
                                stableSince = null;
                                if (!startupRestartOffered)
                                    await Dispatcher.InvokeAsync(() => SetStartupRestartAvailable(false));
                            }

                            IReadOnlyList<GameProcessCandidate> candidates = _gameProcessSessionService.FindNewMatchingProcesses(
                                expectedGamePath,
                                launchSessionStartedUtc.AddSeconds(-2),
                                candidateAdmissionDeadline,
                                excludedCandidatePids,
                                relatedGamePids);
                            GameProcessCandidate? candidate = candidates.FirstOrDefault();
                            if (candidate == null)
                            {
                                candidateObservationTracker.Reset();
                            }
                            else if (candidateObservationTracker.Observe(
                                         candidate,
                                         GameProcessCandidateRequiredObservations))
                            {
                                activeCandidatePid = candidate.ProcessId;
                                activeGamePath = candidate.ImagePath;
                                excludedCandidatePids.Add(candidate.ProcessId);
                                relatedGamePids.Add(candidate.ProcessId);
                                AddLauncherOwnedGamePid(candidate.ProcessId);
                                liveOwnedPids = GetLiveLauncherOwnedGamePids();
                                stableSince = DateTime.UtcNow;
                                LogService.Info(
                                    $"Verified relaunched game process adopted: pid={candidate.ProcessId}, " +
                                    $"mode={Path.GetFileNameWithoutExtension(candidate.ImagePath)}");
                                candidateObservationTracker.Reset();
                            }
                        }

                        liveOwnedPids = GetLiveLauncherOwnedGamePids();
                        _processRouterService.UpdateGameSession(liveOwnedPids, allowEmptyGamePids: true);
                        if (activeCandidatePid > 0 && liveOwnedPids.Contains(activeCandidatePid))
                        {
                            stableSince ??= DateTime.UtcNow;
                            int stableSeconds = (int)(DateTime.UtcNow - stableSince.Value).TotalSeconds;
                            if (!startupRestartOffered && stableSeconds >= RelaunchedStartupRestartAvailableSeconds)
                            {
                                startupRestartOffered = true;
                                await Dispatcher.InvokeAsync(() => SetStartupRestartAvailable(true));
                            }
                            if (stableSeconds >= RelaunchedGameStableAppearSeconds)
                            {
                                finalProcessConfirmed = true;
                                _processRouterService.UpdateGameSession(liveOwnedPids, allowEmptyGamePids: false);
                                await Dispatcher.InvokeAsync(() => SetGameRunningState(true));
                                LogService.Info(
                                    $"Relaunched {launchModeLabel} game process detected and stable. pid={activeCandidatePid}");
                            }
                        }
                    }

                    if (!finalProcessConfirmed
                        && !startupRestartOffered
                        && DateTime.UtcNow >= launchSessionStartedUtc.AddSeconds(InitialStartupRestartAvailableSeconds))
                    {
                        startupRestartOffered = true;
                        await Dispatcher.InvokeAsync(() => SetStartupRestartAvailable(true));
                        LogService.Info(
                            "Game startup is still pending after 10 seconds; restart action is now available independently of PID handoff state.");
                    }

                    if (finalProcessConfirmed && !runtimeValidated)
                    {
                        runtimeValidated = true;
                        int validatedProcessId = activeCandidatePid > 0
                            ? activeCandidatePid
                            : initialProcessId;
                        GameRuntimeValidationResult result;
                        try
                        {
                            result = await _gameRuntimeValidationService.ValidateAsync(
                                validatedProcessId,
                                activeGamePath,
                                expectedHookPath,
                                token);
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        {
                            return;
                        }

                        if (!result.IsValid)
                        {
                            LogService.Error("Runtime validation warning: " + result.Warning);
                            await Dispatcher.InvokeAsync(() => ShowInfoDialogAsync(
                                L("联机功能可能未生效", "Online Features May Be Inactive"),
                                L("请结束游戏后重新启动。仍然出现此提示时，请导出诊断信息。",
                                  "End the game and launch it again. If this message returns, export diagnostics.")));
                        }
                    }

                    if (!finalProcessConfirmed && DateTime.UtcNow >= deadline)
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            _processRouterService.Stop("game launch timed out");
                            ClearLauncherOwnedGameTracking();
                            _dxModeCompatibilityService.RestoreAfterGameExit(_gameDir);
                            SetGameRunningState(false);
                            NotifyGameExitedForComponentUpdates();
                            BringLauncherToFront("game launch timeout");
                        });
                        LogService.Error("Game process wait timeout. Launcher restored.");
                        return;
                    }
                }
                else
                {
                    liveOwnedPids = GetLiveLauncherOwnedGamePids();
                    _processRouterService.UpdateGameSession(liveOwnedPids, allowEmptyGamePids: false);
                    if (liveOwnedPids.Length > 0)
                    {
                        missingChecksAfterRunning = 0;
                    }
                    else
                    {
                        missingChecksAfterRunning++;
                        LogService.Info($"Game process missing check {missingChecksAfterRunning}/{GameExitMissingChecks}.");
                        if (missingChecksAfterRunning >= GameExitMissingChecks)
                        {
                            await Dispatcher.InvokeAsync(() =>
                            {
                                _processRouterService.ReleaseGameSession("game process exited");
                                ClearLauncherOwnedGameTracking();
                                _dxModeCompatibilityService.RestoreAfterGameExit(_gameDir);
                                SetGameRunningState(false);
                                NotifyGameExitedForComponentUpdates();
                                BringLauncherToFront("game exited");
                                LogService.Info("Game process exited, launcher restored.");
                            });
                            return;
                        }
                    }
                }

                try
                {
                    await Task.Delay(GameProcessProbeIntervalMs, token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }, token);
    }

    private void AddLauncherOwnedGamePid(int pid)
    {
        if (pid <= 0)
            return;
        lock (_gameProcessSync)
            _launcherOwnedGamePids.Add(pid);
    }

    private static bool IsAnyBlacklistGameProcessRunning()
    {
        foreach (string name in new[] { "Blacklist_game", "Blacklist_DX11_game" })
        {
            Process[] processes = Array.Empty<Process>();
            try
            {
                processes = Process.GetProcessesByName(name);
                if (processes.Length > 0)
                    return true;
            }
            catch
            {
                return true; // fail closed: never update if process state cannot be verified
            }
            finally
            {
                foreach (Process process in processes)
                    process.Dispose();
            }
        }
        return false;
    }

    private int[] GetLiveLauncherOwnedGamePids()
    {
        lock (_gameProcessSync)
        {
            var live = new List<int>();
            foreach (int pid in _launcherOwnedGamePids.ToArray())
            {
                try
                {
                    using Process process = Process.GetProcessById(pid);
                    if (!process.HasExited)
                    {
                        live.Add(pid);
                        continue;
                    }
                }
                catch
                {
                }
                _launcherOwnedGamePids.Remove(pid);
            }
            return live.OrderBy(x => x).ToArray();
        }
    }

    private void CancelGameMonitor()
    {
        try { _gameMonitorCts?.Cancel(); } catch { }
        _gameMonitorCts?.Dispose();
        _gameMonitorCts = null;
    }

    private async Task EndRunningGameWithConfirmAsync()
    {
        var result = await ShowConfirmDialogAsync(
            title: L("结束游戏", "End Game"),
            message: L("游戏仍在运行，是否结束？", "The game is still running. Stop it?"),
            yesText: L("结束游戏", "End Game"),
            noText: L("取消", "Cancel"));
        if (result == MessageBoxResult.Yes)
            await EndRunningGameAsync("user clicked end game");
    }

    private async Task EndRunningGameAsync(string reason)
    {
        if (_isEndingGame)
            return;
        _isEndingGame = true;
        UpdateLaunchConfigurationAvailability();
        try
        {
            CancelGameMonitor();
            KillLauncherOwnedGameProcesses(reason);
            // Remove game PID authorisation immediately. Route Guard remains inert while the
            // Launcher heartbeat is live and self-releases after a later Launcher exit.
            _processRouterService.ReleaseGameSession(reason);
            await Task.Delay(1000);
            _dxModeCompatibilityService.RestoreAfterGameExit(_gameDir);
            SetGameRunningState(false);
            NotifyGameExitedForComponentUpdates();
        }
        finally
        {
            _isEndingGame = false;
            // SetGameRunningState(false) runs while the end-game operation is still active,
            // so its availability refresh intentionally keeps these actions disabled. Refresh
            // once more after clearing the operation flag or they remain disabled indefinitely.
            UpdateCheckNetworkButtonAvailability();
            UpdateLaunchConfigurationAvailability();
            UpdateLaunchButtonAvailability();
        }
    }

    private void ClearLauncherOwnedGameTracking()
    {
        lock (_gameProcessSync)
            _launcherOwnedGamePids.Clear();
        _gameProcess?.Dispose();
        _gameProcess = null;
    }

    private void KillLauncherOwnedGameProcesses(string reason)
    {
        int[] ownedPids;
        lock (_gameProcessSync)
        {
            ownedPids = _launcherOwnedGamePids.ToArray();
            _launcherOwnedGamePids.Clear();
        }
        foreach (int pid in ownedPids)
        {
            try
            {
                using Process process = Process.GetProcessById(pid);
                if (!process.HasExited)
                {
                    LogService.Info($"Killing launcher-owned game process {process.ProcessName}, PID={pid}, reason={reason}");
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex)
            {
                LogService.Error($"Failed to stop launcher-owned game PID={pid}: {ex.Message}");
            }
        }
        ClearLauncherOwnedGameTracking();
    }

}
