using System.Text.Json;
using System.Text.RegularExpressions;
using SplinterCellCNLauncher.Models;
using SplinterCellCNLauncher.Services;

namespace SCBL.Client.Tests;

public sealed class NetworkRuntimeSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "scbl-network-runtime-safety-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ServerReadinessPollsTheActualDataPathUntilItConverges()
    {
        int attempts = 0;
        NetworkReadyResult result = await NetworkOrchestrator.ProbeUntilReadyAsync(
            _ =>
            {
                attempts++;
                return Task.FromResult(attempts < 3
                    ? NetworkReadyResult.Failed(
                        NetworkPhase.ServerFailed,
                        NetworkFailureStage.Server,
                        "route-not-ready")
                    : NetworkReadyResult.Success("10.66.0.4", 7));
            },
            maxAttempts: 8,
            retryDelay: TimeSpan.Zero);

        Assert.True(result.Ok);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task ServerReadinessExhaustionPreservesTheLastConcreteFailure()
    {
        int attempts = 0;
        NetworkReadyResult result = await NetworkOrchestrator.ProbeUntilReadyAsync(
            _ =>
            {
                attempts++;
                return Task.FromResult(NetworkReadyResult.Failed(
                    NetworkPhase.ServerFailed,
                    NetworkFailureStage.Server,
                    $"socket-Unreachable-{attempts}"));
            },
            maxAttempts: 4,
            retryDelay: TimeSpan.Zero);

        Assert.False(result.Ok);
        Assert.Equal(4, attempts);
        Assert.Equal("socket-Unreachable-4", result.Message);
    }

    [Theory]
    [InlineData(false, "", "10.66.0.4", true)]
    [InlineData(true, "10.66.0.4", "10.66.0.4", true)]
    [InlineData(true, "10.66.0.2", "10.66.0.4", false)]
    [InlineData(true, "", "10.66.0.4", false)]
    public void ConnectedRequiresRunningGameAndRuntimeToShareOneOverlayAddress(
        bool gameActive,
        string gameSessionIp,
        string runtimeIp,
        bool expected)
    {
        Assert.Equal(
            expected,
            NetworkOrchestrator.IsActiveGameSessionAddressCompatible(
                gameActive,
                gameSessionIp,
                runtimeIp));
    }

    [Fact]
    public async Task HealthCheckKeepsPerEndpointSocketFailureEvidence()
    {
        var service = new NetworkHealthCheckService(
            (host, port, timeout, bindIp, token) => Task.FromResult(
                new TcpEndpointProbe(
                    false,
                    null,
                    port == 50051 ? "socket-NetworkUnreachable" : "timeout")));

        NetworkReadyResult result = await service.QuickCheckAsync("10.66.0.4");

        Assert.False(result.Ok);
        Assert.Contains("50051/gRPC=socket-NetworkUnreachable", result.Message, StringComparison.Ordinal);
        Assert.Contains("80/config=timeout", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RecoveryUsesEventAndLossSignalsInsteadOfASecondPollingSchedule()
    {
        string source = File.ReadAllText(FindRepositoryFile(
            "client", "ScblPublicLauncher", "Services", "NetworkOrchestrator.cs"));
        int watchdogStart = source.IndexOf("public void StartWatchdog()", StringComparison.Ordinal);
        int watchdogEnd = source.IndexOf("public async Task ShutdownAsync", watchdogStart, StringComparison.Ordinal);
        string watchdog = source[watchdogStart..watchdogEnd];

        Assert.Contains("_networkRecoverySignal.WaitAsync(token)", watchdog, StringComparison.Ordinal);
        Assert.Contains("QuickVerifyAsync(", watchdog, StringComparison.Ordinal);
        Assert.Contains("forceServerPathRefresh: true", watchdog, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Delay(", watchdog, StringComparison.Ordinal);
        Assert.DoesNotContain("watchdogInterval", watchdog, StringComparison.Ordinal);
    }

    [Fact]
    public void NetworkModeTransportContractDoesNotLagAcrossRepeatedSwitches()
    {
        ClientNetworkMode[] modes =
        [
            ClientNetworkMode.Fast,
            ClientNetworkMode.Compatible,
            ClientNetworkMode.Fast,
        ];

        Assert.Equal(
            new[] { "QUIC", "WSS", "QUIC" },
            modes.Select(NetworkOrchestrator.ExpectedServerTransportForMode));
        Assert.True(NetworkOrchestrator.IsExpectedServerTransport(ClientNetworkMode.Fast, "QUIC"));
        Assert.True(NetworkOrchestrator.IsExpectedServerTransport(ClientNetworkMode.Compatible, "WSS"));
        Assert.False(NetworkOrchestrator.IsExpectedServerTransport(ClientNetworkMode.Compatible, "QUIC"));
        Assert.False(NetworkOrchestrator.IsExpectedServerTransport(ClientNetworkMode.Fast, "WSS"));
    }

    [Fact]
    public void TransitionSnapshotsNeverCarryConfirmedTransportFromPreviousRuntime()
    {
        NetworkPhase[] transitionPhases =
        [
            NetworkPhase.Preparing,
            NetworkPhase.TunnelConnecting,
            NetworkPhase.ServerConnecting,
            NetworkPhase.Reconnecting,
        ];

        Assert.All(
            transitionPhases,
            phase =>
            {
                Assert.Empty(NetworkStatusSnapshot.NormalizeTransportMode(phase, "WSS"));
                Assert.Empty(NetworkStatusSnapshot.NormalizeUnderlayAddressFamily(phase, "IPv6"));
                Assert.Equal(0, NetworkStatusSnapshot.NormalizeRuntimeObservationId(phase, 17));
            });
        Assert.Equal(
            "QUIC",
            NetworkStatusSnapshot.NormalizeTransportMode(NetworkPhase.Connected, " QUIC "));
        Assert.Equal(
            "IPv6",
            NetworkStatusSnapshot.NormalizeUnderlayAddressFamily(NetworkPhase.Connected, " ipv6 "));
        Assert.Equal(
            17,
            NetworkStatusSnapshot.NormalizeRuntimeObservationId(NetworkPhase.Connected, 17));
    }

    [Fact]
    public void ConnectedSnapshotRequiresAtomicTransportAddressFamilyAndRuntimeObservation()
    {
        Assert.True(NetworkStatusSnapshot.HasCompleteConnectionDetails(
            NetworkPhase.Connected,
            "QUIC",
            "IPv6",
            7));
        Assert.False(NetworkStatusSnapshot.HasCompleteConnectionDetails(
            NetworkPhase.Connected,
            "",
            "IPv6",
            7));
        Assert.False(NetworkStatusSnapshot.HasCompleteConnectionDetails(
            NetworkPhase.Connected,
            "QUIC",
            "",
            7));
        Assert.False(NetworkStatusSnapshot.HasCompleteConnectionDetails(
            NetworkPhase.Connected,
            "QUIC",
            "IPv6",
            0));
        Assert.False(NetworkStatusSnapshot.HasCompleteConnectionDetails(
            NetworkPhase.ServerConnecting,
            "QUIC",
            "IPv6",
            7));
    }

    [Fact]
    public void ServerPathObservationRejectsIncompleteOrPreviousRuntimeSamples()
    {
        var currentPath = new EasyTierPeerPath(
            PublicTunnelConfig.ServerVirtualIp,
            8,
            " quic ",
            "1",
            UnderlayAddressFamily: " ipv6 ");

        EasyTierServerPathObservation? observation = PublicTunnelService.SelectServerPathObservation(
            currentPath,
            capturedRuntimeObservationId: 12,
            currentRuntimeObservationId: 12);

        Assert.NotNull(observation);
        Assert.Equal("QUIC", observation.TransportMode);
        Assert.Equal("IPv6", observation.UnderlayAddressFamily);
        Assert.Equal(12, observation.RuntimeObservationId);
        Assert.Null(PublicTunnelService.SelectServerPathObservation(
            currentPath,
            capturedRuntimeObservationId: 11,
            currentRuntimeObservationId: 12));
        Assert.Null(PublicTunnelService.SelectServerPathObservation(
            currentPath with { UnderlayAddressFamily = "" },
            capturedRuntimeObservationId: 12,
            currentRuntimeObservationId: 12));
        Assert.Null(PublicTunnelService.SelectServerPathObservation(
            currentPath with { TransportMode = "" },
            capturedRuntimeObservationId: 12,
            currentRuntimeObservationId: 12));
    }

    [Fact]
    public void LegacyTransportOnlyCacheAndDetectorAreRemoved()
    {
        string source = File.ReadAllText(FindRepositoryFile(
            "client", "ScblPublicLauncher", "Services", "PublicTunnelService.cs"));

        Assert.DoesNotContain("DetectServerTransportAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectServerTransportResult", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_lastServerTransport", source, StringComparison.Ordinal);
        Assert.Contains("DetectServerPathObservationAsync", source, StringComparison.Ordinal);
        Assert.Contains("InvalidateRuntimeObservation", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeRebuildInvalidatesOldTransportAndConfirmsNewTransportBeforeConnected()
    {
        string source = File.ReadAllText(FindRepositoryFile(
            "client", "ScblPublicLauncher", "Services", "NetworkOrchestrator.cs"));
        int reset = source.IndexOf(
            "ResetTransportObservationForRuntimeRebuild();",
            StringComparison.Ordinal);
        int start = source.IndexOf(
            "string ip = await _tunnelService.EnsureStartedAsync(",
            StringComparison.Ordinal);
        int firstRebuildTransition = source.IndexOf(
            "if (mode == NetworkEnsureMode.Repair)",
            StringComparison.Ordinal);
        int detect = source.IndexOf(
            "EasyTierServerPathObservation serverPath = await DetectExpectedServerPathAsync(",
            StringComparison.Ordinal);
        int connected = source.IndexOf(
            "MarkConnected(ip, check.LatencyMs, serverPath);",
            StringComparison.Ordinal);

        Assert.True(reset >= 0 && reset < firstRebuildTransition && reset < start);
        Assert.True(detect > start && detect < connected);
        Assert.Contains("DetectServerPathObservationAsync(", source, StringComparison.Ordinal);
        Assert.Contains("_tunnelService.InvalidateRuntimeObservation();", source, StringComparison.Ordinal);
        Assert.Contains("if (_runtimeRebuildInProgress)", source, StringComparison.Ordinal);
        Assert.Contains("_lastUnderlayAddressFamily = \"\";", source, StringComparison.Ordinal);
        Assert.Contains("_lastRuntimeObservationId = 0;", source, StringComparison.Ordinal);
        Assert.Contains("serverPath.RuntimeObservationId != _tunnelService.CurrentRuntimeObservationId", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ActiveGameRecoveryUsesOneBoundedRebuildAfterInPlaceRecovery()
    {
        string source = File.ReadAllText(FindRepositoryFile(
            "client", "ScblPublicLauncher", "Services", "NetworkOrchestrator.cs"));
        int activeGameGuard = source.IndexOf(
            "if (mode == NetworkEnsureMode.Automatic && _isGameSessionActive())",
            StringComparison.Ordinal);
        int inPlaceRecovery = source.IndexOf(
            "WaitForActiveGameRuntimeRecoveryAsync(token)",
            activeGameGuard,
            StringComparison.Ordinal);
        int runtimeReset = source.IndexOf(
            "ResetTransportObservationForRuntimeRebuild();",
            inPlaceRecovery,
            StringComparison.Ordinal);
        int fallbackRestart = source.IndexOf(
            "RestartWithIpv4FallbackAsync(",
            runtimeReset,
            StringComparison.Ordinal);

        Assert.True(activeGameGuard >= 0);
        Assert.True(inPlaceRecovery > activeGameGuard);
        Assert.True(runtimeReset > inPlaceRecovery);
        Assert.True(fallbackRestart > runtimeReset);
        Assert.Contains(
            "forceRuntimeRestart: mode == NetworkEnsureMode.Automatic",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "TryBeginBoundedRuntimeRebuild(",
            source,
            StringComparison.Ordinal);
        Assert.Contains("ref _activeGameRuntimeRebuildAttempted", source, StringComparison.Ordinal);
        Assert.Contains(
            "IsActiveGameSessionAddressCompatible(",
            source,
            StringComparison.Ordinal);
        Assert.Contains("if (!check.Ok && !_isGameSessionActive())", source, StringComparison.Ordinal);
        Assert.Contains("runtimeObservationId == _tunnelService.CurrentRuntimeObservationId", source, StringComparison.Ordinal);
        Assert.Contains("sessionIp.Equals(", source, StringComparison.Ordinal);
        int inPlaceMethodStart = source.IndexOf(
            "private async Task<NetworkReadyResult> WaitForActiveGameRuntimeRecoveryAsync(",
            StringComparison.Ordinal);
        int inPlaceMethodEnd = source.IndexOf(
            "internal static async Task<NetworkReadyResult> WaitForInPlaceRecoveryAsync(",
            inPlaceMethodStart,
            StringComparison.Ordinal);
        string inPlaceMethod = source[inPlaceMethodStart..inPlaceMethodEnd];
        Assert.DoesNotContain(".Stop(", inPlaceMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("RestartWithIpv4FallbackAsync(", inPlaceMethod, StringComparison.Ordinal);
        Assert.Contains("ActiveGameRecoveryMaxAttempts = 6", source, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromSeconds(2)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ActiveGameRuntimeRebuildIsLimitedToOneAttemptUntilRecovery()
    {
        int attempted = 0;

        Assert.True(NetworkOrchestrator.TryBeginBoundedRuntimeRebuild(ref attempted));
        Assert.False(NetworkOrchestrator.TryBeginBoundedRuntimeRebuild(ref attempted));
        Assert.Equal(1, attempted);
    }

    [Fact]
    public void EventDrivenRecoveryCoalescesSignalsAndPreservesAnExhaustedActiveRuntime()
    {
        string source = File.ReadAllText(FindRepositoryFile(
            "client", "ScblPublicLauncher", "Services", "NetworkOrchestrator.cs"));
        int watchdogStart = source.IndexOf("public void StartWatchdog()", StringComparison.Ordinal);
        int watchdogEnd = source.IndexOf("public async Task ShutdownAsync", watchdogStart, StringComparison.Ordinal);
        string watchdog = source[watchdogStart..watchdogEnd];
        int failure = watchdog.IndexOf("if (quick.Ok)", StringComparison.Ordinal);
        int reconnecting = watchdog.IndexOf("Emit(NetworkPhase.Reconnecting", failure, StringComparison.Ordinal);
        int rebuild = watchdog.IndexOf("NetworkEnsureMode.Automatic", reconnecting, StringComparison.Ordinal);

        Assert.Contains("_networkRecoverySignal.WaitAsync(token)", watchdog, StringComparison.Ordinal);
        Assert.DoesNotContain("if (!IsReady)", watchdog, StringComparison.Ordinal);
        Assert.True(failure >= 0 && reconnecting > failure && rebuild > reconnecting);
        Assert.Contains("ClearQueuedRecoverySignals();", watchdog, StringComparison.Ordinal);
        Assert.Contains("Volatile.Read(ref _activeGameRecoveryExhausted) != 0", watchdog, StringComparison.Ordinal);
        Assert.Contains("the current runtime is preserved", watchdog, StringComparison.Ordinal);
        Assert.Contains("if (mode != NetworkEnsureMode.Automatic)", source, StringComparison.Ordinal);
        Assert.Contains(
            "forceRuntimeRestart: mode == NetworkEnsureMode.Automatic",
            source,
            StringComparison.Ordinal);
        Assert.Contains("MarkConnected(result.AssignedIp, result.LatencyMs, serverPath);", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InPlaceRecoveryKeepsOneGenerationUntilProbeSucceeds()
    {
        int attempts = 0;

        NetworkReadyResult result = await NetworkOrchestrator.WaitForInPlaceRecoveryAsync(
            _ =>
            {
                attempts++;
                return Task.FromResult(attempts < 3
                    ? NetworkReadyResult.Failed(
                        NetworkPhase.ServerFailed,
                        NetworkFailureStage.Server,
                        "not ready")
                    : NetworkReadyResult.Success("10.66.0.2", 4));
            },
            () => true,
            maxAttempts: 4,
            retryDelay: TimeSpan.Zero);

        Assert.True(result.Ok);
        Assert.Equal(3, attempts);
        Assert.Equal("10.66.0.2", result.AssignedIp);
    }

    [Fact]
    public async Task InPlaceRecoveryStopsWhenRuntimeGenerationChanges()
    {
        int attempts = 0;
        bool sameGeneration = true;

        NetworkReadyResult result = await NetworkOrchestrator.WaitForInPlaceRecoveryAsync(
            _ =>
            {
                attempts++;
                sameGeneration = false;
                return Task.FromResult(NetworkReadyResult.Failed(
                    NetworkPhase.ServerFailed,
                    NetworkFailureStage.Server,
                    "not ready"));
            },
            () => sameGeneration,
            maxAttempts: 4,
            retryDelay: TimeSpan.Zero);

        Assert.False(result.Ok);
        Assert.Equal(1, attempts);
        Assert.Contains("runtime generation changed", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ipv4FallbackIsSessionOnlyAndHasNoPersistentCooldown()
    {
        string source = File.ReadAllText(FindRepositoryFile(
            "client", "ScblPublicLauncher", "Services", "PublicTunnelService.cs"));
        string orchestrator = File.ReadAllText(FindRepositoryFile(
            "client", "ScblPublicLauncher", "Services", "NetworkOrchestrator.cs"));

        Assert.Contains("_ipv6DisabledForLauncherSession", source, StringComparison.Ordinal);
        Assert.Contains("only for the current Launcher session", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Ipv6FallbackPolicyStore", source, StringComparison.Ordinal);
        Assert.DoesNotContain("24-hour", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ShouldPersistIpv4FallbackCooldown", orchestrator, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidateIpv4FallbackCooldown", orchestrator, StringComparison.Ordinal);
    }

    [Fact]
    public void IdleAutomaticRecoveryRebuildsStaleEasyTierGeneration()
    {
        string source = File.ReadAllText(FindRepositoryFile(
            "client", "ScblPublicLauncher", "Services", "NetworkOrchestrator.cs"));

        Assert.Contains(
            "Automatic recovery requires a new EasyTier runtime generation.",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "forceRuntimeRestart: mode == NetworkEnsureMode.Automatic",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (mode == NetworkEnsureMode.Automatic && _isGameSessionActive())",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EveryConnectedPublicationPathUsesOneCompleteServerPathObservation()
    {
        string source = File.ReadAllText(FindRepositoryFile(
            "client", "ScblPublicLauncher", "Services", "NetworkOrchestrator.cs"));
        MatchCollection connectedCalls = Regex.Matches(
            source,
            @"MarkConnected\([^;{}]+?\);",
            RegexOptions.CultureInvariant);

        Assert.Equal(3, connectedCalls.Count);
        Assert.All(
            connectedCalls.Cast<Match>(),
            call => Assert.Contains("serverPath", call.Value, StringComparison.Ordinal));
        Assert.Contains("serverPath.TransportMode", source, StringComparison.Ordinal);
        Assert.Contains("serverPath.UnderlayAddressFamily", source, StringComparison.Ordinal);
        Assert.Contains("serverPath.RuntimeObservationId", source, StringComparison.Ordinal);
        Assert.Contains("suppressed incomplete Connected snapshot", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LaunchGateUsesStrictPresenceWhileGameMonitorUsesContinuityPresence()
    {
        string source = File.ReadAllText(FindRepositoryFile(
            "client",
            "ScblPublicLauncher",
            "MainWindow.xaml.cs"));

        Assert.Contains(
            "if (!stable && !_tunnelService.IsRunning && !_tunnelService.HasRunningTunnelClientProcess())",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "bool runtimePresent = _tunnelService.HasContinuousRuntimePresence();",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LockedHeartbeatPreservesLastValidStateAndNextTickRecovers()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "route-guard-session.json");
        const string previous = "{\"sessionId\":\"previous\",\"updatedAtUnixMs\":1}";
        const string next = "{\"sessionId\":\"next\",\"updatedAtUnixMs\":2}";
        File.WriteAllText(path, previous);

        using (var lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Exception? error = Record.Exception(
                () => ProcessRouterService.WriteAtomicTextWithRetry(path, next, attempts: 2));
            Assert.True(error is IOException or UnauthorizedAccessException);
            Assert.Equal(previous, File.ReadAllText(path));
        }

        ProcessRouterService.WriteAtomicTextWithRetry(path, next, attempts: 5);

        Assert.Equal(next, File.ReadAllText(path));
        Assert.Empty(Directory.EnumerateFiles(_root, "route-guard-session.json.*.tmp"));
    }

    [Fact]
    public async Task ConcurrentHeartbeatWritersLeaveOneCompleteSnapshotAndNoTempFiles()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "route-guard-session.json");
        const string first = "{\"sessionId\":\"first\",\"gamePids\":[101]}";
        const string second = "{\"sessionId\":\"second\",\"gamePids\":[202]}";

        await Task.WhenAll(
            Task.Run(() => ProcessRouterService.WriteAtomicTextWithRetry(path, first, attempts: 10)),
            Task.Run(() => ProcessRouterService.WriteAtomicTextWithRetry(path, second, attempts: 10)));

        string actual = File.ReadAllText(path);
        using JsonDocument document = JsonDocument.Parse(actual);
        string? sessionId = document.RootElement.GetProperty("sessionId").GetString();
        Assert.Contains(sessionId, new[] { "first", "second" });
        Assert.Empty(Directory.EnumerateFiles(_root, "route-guard-session.json.*.tmp"));
    }

    [Fact]
    public void RouteGuardRuntimeIsResolvedOutsidePortableClientRoot()
    {
        var service = new ProcessRouterService();
        string executable = Path.GetFullPath(service.GetRouterExePath(AppContext.BaseDirectory));
        string clientRoot = Path.GetFullPath(AppContext.BaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        Assert.StartsWith(
            Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)),
            executable,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(executable.StartsWith(clientRoot, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LauncherCloseDetachesWhileGameExitOnlyReleasesAuthorisedPids()
    {
        string orchestrator = File.ReadAllText(FindRepositoryFile(
            "client", "ScblPublicLauncher", "Services", "NetworkOrchestrator.cs"));
        string gameMonitor = File.ReadAllText(FindRepositoryFile(
            "client", "ScblPublicLauncher", "MainWindow.GameProcessMonitor.cs"));
        string router = File.ReadAllText(FindRepositoryFile(
            "client", "ScblPublicLauncher", "Services", "ProcessRouterService.cs"));

        Assert.Contains("_processRouterService.DetachForLauncherExit(reason);", orchestrator, StringComparison.Ordinal);
        Assert.Contains("ReleaseGameSession(\"game process exited\")", gameMonitor, StringComparison.Ordinal);
        Assert.Contains("psi.ArgumentList.Add(\"-idle-exit-timeout\")", router, StringComparison.Ordinal);
        Assert.Contains("psi.ArgumentList.Add(\"5m\")", router, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static string FindRepositoryFile(params string[] relativeSegments)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory != null;
             directory = directory.Parent)
        {
            string candidate = Path.Combine(
                new[] { directory.FullName }.Concat(relativeSegments).ToArray());
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException(
            "Could not locate repository source file for the call-contract test.",
            Path.Combine(relativeSegments));
    }
}
