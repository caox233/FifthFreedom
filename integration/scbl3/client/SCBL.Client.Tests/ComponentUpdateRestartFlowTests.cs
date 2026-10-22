using SplinterCellCNLauncher.ApplicationLayer;
using SplinterCellCNLauncher.Models;
using SplinterCellCNLauncher.Services;

namespace SCBL.Client.Tests;

public sealed class ComponentUpdateRestartFlowTests
{
    [Fact]
    public void OverallProgressUsesCumulativeBytesAcrossUnequalArtifacts()
    {
        var downloads = new ClientComponentDownloadProgress(
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
            {
                ["small"] = 100,
                ["large"] = 300,
            });
        var overall = new ClientUpdateOverallProgress();
        ClientUpdateProgress smallResumed = downloads.AdvanceArtifact("small", 40);
        ClientUpdateProgress smallCompleted = downloads.AdvanceArtifact("small", 100);
        ClientUpdateProgress largeAdvanced = downloads.AdvanceArtifact("large", 150);
        ClientUpdateProgress allCompleted = downloads.AdvanceArtifact("large", 300);
        ClientUpdateProgress[] progress =
        [
            overall.BeginAttempt(),
            overall.Advance(new ClientUpdateProgress(ClientUpdateProgressStage.FetchingDescriptor, true)),
            overall.Advance(new ClientUpdateProgress(ClientUpdateProgressStage.VerifyingDescriptor, true)),
            overall.Advance(new ClientUpdateProgress(ClientUpdateProgressStage.CheckingComponents, true)),
            overall.Advance(smallResumed),
            overall.Advance(smallCompleted),
            overall.Advance(ClientUpdateProgressStage.VerifyingArtifacts),
            overall.Advance(largeAdvanced),
            overall.Advance(allCompleted),
            overall.Advance(ClientUpdateProgressStage.VerifyingArtifacts),
            overall.Advance(ClientUpdateProgressStage.Activating),
            overall.Advance(ClientUpdateProgressStage.Completed),
        ];

        Assert.Equal((40L, 400L), (smallResumed.BytesCompleted, smallResumed.BytesTotal));
        Assert.Equal((100L, 400L), (smallCompleted.BytesCompleted, smallCompleted.BytesTotal));
        Assert.Equal((250L, 400L), (largeAdvanced.BytesCompleted, largeAdvanced.BytesTotal));
        Assert.Equal((400L, 400L), (allCompleted.BytesCompleted, allCompleted.BytesTotal));
        Assert.True(progress[5].Percentage < 80d);
        Assert.Equal(progress[5].Percentage, progress[6].Percentage);
        Assert.True(progress[7].Percentage > progress[5].Percentage);
        Assert.Equal(80d, progress[8].Percentage);
        Assert.Equal(0d, progress[0].Percentage);
        Assert.Equal(100d, progress[^1].Percentage);
        Assert.All(progress, item => Assert.False(item.IsIndeterminate));
        Assert.All(progress[..^1], item => Assert.True(item.Percentage < 100d));
        Assert.All(
            progress.Zip(progress.Skip(1)),
            pair => Assert.True(pair.First.Percentage <= pair.Second.Percentage));
    }

    [Fact]
    public void BundleFallbackSwitchesToIndividualBytePlanWithoutDoubleCounting()
    {
        var downloads = new ClientComponentDownloadProgress(
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
            {
                ["small"] = 100,
                ["large"] = 300,
            });

        ClientUpdateProgress bundle = downloads.AdvanceBundle(700, 1_000);
        downloads.UseIndividualFallback();
        ClientUpdateProgress first = downloads.AdvanceArtifact("small", 100);
        ClientUpdateProgress second = downloads.AdvanceArtifact("large", 150);
        ClientUpdateProgress completed = downloads.AdvanceArtifact("large", 300);

        Assert.Equal((700L, 1_000L), (bundle.BytesCompleted, bundle.BytesTotal));
        Assert.Equal((100L, 400L), (first.BytesCompleted, first.BytesTotal));
        Assert.Equal((250L, 400L), (second.BytesCompleted, second.BytesTotal));
        Assert.Equal((400L, 400L), (completed.BytesCompleted, completed.BytesTotal));
    }

    [Fact]
    public void FailedAttemptNeverReportsCompletionAndRetryStartsAtZero()
    {
        var tracker = new ClientUpdateOverallProgress();
        ClientUpdateProgress started = tracker.BeginAttempt();
        ClientUpdateProgress failed = tracker.Advance(
            new ClientUpdateProgress(ClientUpdateProgressStage.Downloading, false, 75, 100));
        ClientUpdateProgress retried = tracker.BeginAttempt();

        Assert.Equal(0d, started.Percentage);
        Assert.InRange(failed.Percentage, 0d, 99d);
        Assert.NotEqual(ClientUpdateProgressStage.Completed, failed.Stage);
        Assert.Equal(0d, retried.Percentage);
        Assert.NotEqual(ClientUpdateProgressStage.Completed, retried.Stage);
    }

    [Fact]
    public async Task PrepareFinalizesPreviousLauncherBeforeReadingSignedManifest()
    {
        var operations = new FakeOperations();
        var coordinator = CreateCoordinator(operations);

        PreparedClientUpdatePrompt prepared = await coordinator.PrepareAsync();

        Assert.True(prepared.HasPendingUpdates);
        Assert.Equal(operations.Prepared.ComponentGeneration, prepared.ComponentGeneration);
        Assert.Same(operations.Announcement, prepared.Announcement);
        Assert.Equal(["FinalizeLauncher", "Prepare", "GetAnnouncement"], operations.Calls);
    }

    [Fact]
    public async Task ConfirmedLauncherBatchUsesExactPreparedShaAndConvergesBeforeSingleHandoff()
    {
        var operations = new FakeOperations
        {
            Reconciled = Components("easytier", "hooks", "launcher", "route-guard", "updater"),
            Unapplied = Components("launcher"),
            LauncherReplacementRequired = true,
        };
        var coordinator = CreateCoordinator(operations);

        ClientUpdateHostCommand command = await coordinator.ApplyConfirmedAsync(operations.Prompt);

        Assert.Equal(ClientUpdateHostCommandKind.ReplaceLauncher, command.Kind);
        Assert.NotNull(command.UpdatePackage);
        Assert.Equal(
            [
                $"Reconcile:{operations.Prepared.ComponentGeneration}",
                "FinalizeLauncher",
                "IsLauncherReplacementRequired",
                "StopMonitor",
                "Shutdown:launcher component replacement",
                "ApplyUpdaterAndEasyTier",
                "ApplyRouteGuard",
                "ApplyHooks",
                "FinalizeLauncher",
                "GetUnapplied",
                "CreateLauncherPackage",
            ],
            operations.Calls);
        Assert.Equal(1, operations.CreatePackageCount);
    }

    [Fact]
    public async Task ConfirmedBackgroundLauncherUpdateSurvivesMonitorTokenCancellationAtCommit()
    {
        using var polling = new CancellationTokenSource();
        var operations = new FakeOperations
        {
            Reconciled = Components("launcher"),
            Unapplied = Components("launcher"),
            LauncherReplacementRequired = true,
            RejectCancelledGetUnappliedToken = true,
        };
        var coordinator = new ClientUpdateCoordinator(
            operations,
            () =>
            {
                operations.Calls.Add("StopMonitor");
                polling.Cancel();
            },
            reason =>
            {
                operations.Calls.Add("Shutdown:" + reason);
                return Task.CompletedTask;
            },
            cancellationToken => Task.FromResult(operations.Announcement));

        ClientUpdateHostCommand command = await coordinator.ApplyConfirmedAsync(
            operations.Prompt,
            polling.Token);

        Assert.True(polling.IsCancellationRequested);
        Assert.Equal(ClientUpdateHostCommandKind.ReplaceLauncher, command.Kind);
        Assert.Equal(1, operations.CreatePackageCount);
        Assert.False(operations.LastGetUnappliedToken.CanBeCanceled);
        Assert.Contains("Shutdown:launcher component replacement", operations.Calls);
        Assert.Contains("CreateLauncherPackage", operations.Calls);
    }

    [Fact]
    public async Task LauncherBatchWithNonLauncherPendingFailsClosedBeforePackage()
    {
        var operations = new FakeOperations
        {
            Reconciled = Components("launcher", "easytier"),
            Unapplied = Components("launcher", "easytier"),
            LauncherReplacementRequired = true,
        };
        var coordinator = CreateCoordinator(operations);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.ApplyConfirmedAsync(operations.Prompt));

        Assert.Contains("easytier", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, operations.CreatePackageCount);
    }

    [Fact]
    public async Task ImmediateComponentBatchWithExactReceiptsStillReturnsSingleRestart()
    {
        var operations = new FakeOperations
        {
            Reconciled = Components("hooks", "updater"),
            Unapplied = Components(),
        };
        var hostCalls = new List<string>();
        var coordinator = CreateCoordinator(operations, hostCalls);

        ClientUpdateHostCommand command = await coordinator.ApplyConfirmedAsync(operations.Prompt);

        Assert.Equal(ClientUpdateHostCommandKind.RestartLauncher, command.Kind);
        Assert.Equal(["hooks", "updater"], command.Components);
        Assert.Equal(
            ["StopMonitor", "Shutdown:component update activation restart"],
            hostCalls);
    }

    [Fact]
    public async Task FilesOnlyBatchAppliedDuringReconciliationStillReturnsSingleRestart()
    {
        var operations = new FakeOperations
        {
            Prepared = new PreparedClientComponentUpdate(
                "test",
                Hash('9'),
                [new PendingComponentUpdate("client-files", "2.0.26", [], [])]),
            Reconciled = Components(),
            Unapplied = Components(),
        };
        var hostCalls = new List<string>();
        var coordinator = CreateCoordinator(operations, hostCalls);

        PreparedClientUpdatePrompt prepared = await coordinator.PrepareAsync();
        ClientUpdateHostCommand command = await coordinator.ApplyConfirmedAsync(prepared);

        Assert.Equal(ClientUpdateHostCommandKind.RestartLauncher, command.Kind);
        Assert.Equal(["client-files"], command.Components);
        Assert.Equal(
            ["StopMonitor", "Shutdown:component update activation restart"],
            hostCalls);
        Assert.DoesNotContain("GetUnapplied", operations.Calls);
    }

    [Fact]
    public async Task ConfirmedAppliedHooksRestartSurvivesMonitorTokenCancellationAtCommit()
    {
        using var polling = new CancellationTokenSource();
        var operations = new FakeOperations
        {
            Reconciled = Components("hooks"),
            Unapplied = Components(),
            RejectCancelledGetUnappliedToken = true,
        };
        var hostCalls = new List<string>();
        var coordinator = new ClientUpdateCoordinator(
            operations,
            () =>
            {
                hostCalls.Add("StopMonitor");
                polling.Cancel();
            },
            reason =>
            {
                hostCalls.Add("Shutdown:" + reason);
                return Task.CompletedTask;
            },
            cancellationToken => Task.FromResult(operations.Announcement));

        ClientUpdateHostCommand command = await coordinator.ApplyConfirmedAsync(
            operations.Prompt,
            polling.Token);

        Assert.True(polling.IsCancellationRequested);
        Assert.Equal(ClientUpdateHostCommandKind.RestartLauncher, command.Kind);
        Assert.Equal(["hooks"], command.Components);
        Assert.Equal(
            ["StopMonitor", "Shutdown:component update activation restart"],
            hostCalls);
    }

    [Fact]
    public async Task RemainingComponentBatchReturnsSingleRestartAfterStopAndShutdown()
    {
        var operations = new FakeOperations
        {
            Reconciled = Components("easytier", "route-guard"),
            Unapplied = Components("easytier", "route-guard"),
        };
        var hostCalls = new List<string>();
        var coordinator = CreateCoordinator(operations, hostCalls);

        ClientUpdateHostCommand command = await coordinator.ApplyConfirmedAsync(operations.Prompt);

        Assert.Equal(ClientUpdateHostCommandKind.RestartLauncher, command.Kind);
        Assert.Equal(["easytier", "route-guard"], command.Components);
        Assert.Equal(
            ["StopMonitor", "Shutdown:component update activation restart"],
            hostCalls);
    }

    [Fact]
    public async Task ConfirmedBackgroundComponentRestartSurvivesMonitorTokenCancellationAtCommit()
    {
        using var polling = new CancellationTokenSource();
        var operations = new FakeOperations
        {
            Reconciled = Components("route-guard"),
            Unapplied = Components("route-guard"),
        };
        var hostCalls = new List<string>();
        var coordinator = new ClientUpdateCoordinator(
            operations,
            () =>
            {
                hostCalls.Add("StopMonitor");
                polling.Cancel();
            },
            reason =>
            {
                hostCalls.Add("Shutdown:" + reason);
                return Task.CompletedTask;
            },
            cancellationToken => Task.FromResult(operations.Announcement));

        ClientUpdateHostCommand command = await coordinator.ApplyConfirmedAsync(
            operations.Prompt,
            polling.Token);

        Assert.True(polling.IsCancellationRequested);
        Assert.Equal(ClientUpdateHostCommandKind.RestartLauncher, command.Kind);
        Assert.Equal(["route-guard"], command.Components);
        Assert.Equal(
            ["StopMonitor", "Shutdown:component update activation restart"],
            hostCalls);
    }

    [Fact]
    public async Task CancellationBeforeConfirmedUpdateCommitDoesNotStopMonitorOrNetwork()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var operations = new FakeOperations
        {
            Reconciled = Components("launcher"),
            Unapplied = Components("launcher"),
            LauncherReplacementRequired = true,
        };
        var hostCalls = new List<string>();
        var coordinator = CreateCoordinator(operations, hostCalls);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.ApplyConfirmedAsync(operations.Prompt, cancellation.Token));

        Assert.Empty(operations.Calls);
        Assert.Empty(hostCalls);
    }

    [Fact]
    public void SameVersionWithDifferentShaRemainsPending()
    {
        IReadOnlyDictionary<string, VerifiedClientComponent> reconciled = Components("launcher");
        var installed = new Dictionary<string, (string Version, string Sha256)>(StringComparer.OrdinalIgnoreCase)
        {
            ["launcher"] = ("2.2.1", Hash('f')),
        };

        IReadOnlyDictionary<string, VerifiedClientComponent> pending =
            ClientComponentUpdateService.CollectUnappliedReconciledComponents(reconciled, installed);

        Assert.Equal(["launcher"], pending.Keys);
    }

    [Fact]
    public void ExactReceiptDoesNotReplayReconciledComponent()
    {
        IReadOnlyDictionary<string, VerifiedClientComponent> reconciled = Components("launcher");
        var installed = new Dictionary<string, (string Version, string Sha256)>(StringComparer.OrdinalIgnoreCase)
        {
            ["launcher"] = ("2.2.1", Hash('a')),
        };

        IReadOnlyDictionary<string, VerifiedClientComponent> pending =
            ClientComponentUpdateService.CollectUnappliedReconciledComponents(reconciled, installed);

        Assert.Empty(pending);
    }

    [Fact]
    public async Task NewProcessWithExactLauncherReceiptDoesNotRequireConfirmationOrApply()
    {
        var operations = new FakeOperations
        {
            Prepared = new PreparedClientComponentUpdate("test", Hash('b'), []),
        };
        var coordinator = CreateCoordinator(operations);
        var progress = new List<ClientUpdateProgress>();
        coordinator.ProgressChanged += (_, value) => progress.Add(value);

        PreparedClientUpdatePrompt prepared = await coordinator.PrepareAsync();

        Assert.False(prepared.HasPendingUpdates);
        Assert.Null(prepared.Announcement);
        Assert.Equal(["FinalizeLauncher", "Prepare"], operations.Calls);
        Assert.DoesNotContain(operations.Calls, call => call.StartsWith("Reconcile:", StringComparison.Ordinal));
        Assert.Equal(new[] { 0d, 100d }, progress.Select(item => item.Percentage));
        Assert.All(progress, item => Assert.False(item.IsIndeterminate));
    }

    [Fact]
    public async Task AnnouncementFailureStopsBeforeReconciliation()
    {
        var operations = new FakeOperations();
        var coordinator = new ClientUpdateCoordinator(
            operations,
            () => operations.Calls.Add("StopMonitor"),
            reason =>
            {
                operations.Calls.Add("Shutdown:" + reason);
                return Task.CompletedTask;
            },
            cancellationToken => throw new UpdateAnnouncementUnavailableException("unavailable"));
        var progress = new List<ClientUpdateProgress>();
        coordinator.ProgressChanged += (_, value) => progress.Add(value);

        await Assert.ThrowsAsync<UpdateAnnouncementUnavailableException>(
            () => coordinator.PrepareAsync());

        Assert.Equal(["FinalizeLauncher", "Prepare"], operations.Calls);
        Assert.DoesNotContain(operations.Calls, call => call.StartsWith("Reconcile:", StringComparison.Ordinal));
        Assert.DoesNotContain(progress, item => item.Percentage >= 100d);
    }

    [Fact]
    public async Task ApplyRejectsPromptWithoutRequiredAnnouncementSnapshot()
    {
        var operations = new FakeOperations();
        var coordinator = CreateCoordinator(operations);
        var invalid = new PreparedClientUpdatePrompt(
            true,
            operations.Prepared.ComponentGeneration,
            null,
            ["launcher"]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.ApplyConfirmedAsync(invalid));

        Assert.Empty(operations.Calls);
    }

    [Fact]
    public void ApplicationCoordinatorAndWpfAdapterRespectDependencyBoundary()
    {
        string root = FindRepositoryRoot();
        string coordinator = File.ReadAllText(Path.Combine(
            root,
            "client",
            "ScblPublicLauncher",
            "Application",
            "ClientUpdateCoordinator.cs"));
        string adapter = File.ReadAllText(Path.Combine(
            root,
            "client",
            "ScblPublicLauncher",
            "MainWindow.ClientUpdates.cs"));

        Assert.DoesNotContain("System.Windows", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("MainWindow", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("MessageBox", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("ReleaseNotes", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("ReconcileSelectedChannelAsync", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("StagedComponentBootstrapService", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("GetUnappliedReconciledComponentsAsync", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("_networkOrchestrator.ShutdownAsync", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("components.TryGetValue", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("if (components.Count", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildComponentUpdateReleaseNotes", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("ReleaseNotesZh", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("ReleaseNotesEn", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("PendingComponentUpdate", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("prepared.PendingUpdates", adapter, StringComparison.Ordinal);
        Assert.Contains("_clientUpdateCoordinator", adapter, StringComparison.Ordinal);
        Assert.Contains(".PrepareAsync(", adapter, StringComparison.Ordinal);
        Assert.Contains("ApplyConfirmedAsync", adapter, StringComparison.Ordinal);
        Assert.Contains("ExecuteClientUpdateHostCommand", adapter, StringComparison.Ordinal);
        Assert.Contains("announcement.TitleZh", adapter, StringComparison.Ordinal);
        Assert.Contains("announcement.TitleEn", adapter, StringComparison.Ordinal);
        Assert.Contains("announcement.BodyZh", adapter, StringComparison.Ordinal);
        Assert.Contains("announcement.BodyEn", adapter, StringComparison.Ordinal);
        Assert.Contains("立即更新", adapter, StringComparison.Ordinal);
        Assert.Contains("Update Now", adapter, StringComparison.Ordinal);
        Assert.Contains("UpdateAnnouncementUnavailableException", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("AnnouncementService", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("RefreshNetworkBootstrapAsync", adapter, StringComparison.Ordinal);
    }

    private static ClientUpdateCoordinator CreateCoordinator(
        FakeOperations operations,
        List<string>? hostCalls = null)
    {
        hostCalls ??= operations.Calls;
        return new ClientUpdateCoordinator(
            operations,
            () => hostCalls.Add("StopMonitor"),
            reason =>
            {
                hostCalls.Add("Shutdown:" + reason);
                return Task.CompletedTask;
            },
            cancellationToken =>
            {
                operations.Calls.Add("GetAnnouncement");
                return Task.FromResult(operations.Announcement);
            });
    }

    private static IReadOnlyDictionary<string, VerifiedClientComponent> Components(params string[] names)
    {
        var result = new Dictionary<string, VerifiedClientComponent>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in names)
        {
            char hashMarker = name switch
            {
                "easytier" => 'e',
                "hooks" => 'b',
                "launcher" => 'a',
                "route-guard" => 'c',
                "updater" => 'd',
                _ => '1',
            };
            result[name] = new VerifiedClientComponent(
                name,
                "2.2.1",
                Hash(hashMarker),
                $"C:\\staged\\{name}",
                "test",
                new Uri($"https://updates.invalid/{name}"));
        }
        return result;
    }

    private static string Hash(char marker)
        => new(marker, 64);

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "COMPONENT_VERSIONS.json"))
                && Directory.Exists(Path.Combine(current.FullName, "client", "ScblPublicLauncher")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Unable to locate the SCBL repository root.");
    }

    private sealed class FakeOperations : IClientUpdateOperations
    {
        public PreparedClientComponentUpdate Prepared { get; set; } =
            new("test", Hash('9'), [new PendingComponentUpdate("launcher", "2.2.1", [], [])]);

        public UpdateAnnouncementSnapshot Announcement { get; } = new(
            "announcement-1",
            "2.1.4",
            "更新公告",
            "本次更新内容",
            "Update Notice",
            "Changes in this update",
            Hash('7'));

        public PreparedClientUpdatePrompt Prompt
            => new(
                true,
                Prepared.ComponentGeneration,
                Announcement,
                Reconciled.Count > 0
                    ? Reconciled.Keys.ToArray()
                    : Prepared.PendingUpdates.Select(update => update.Name).ToArray());

        public IReadOnlyDictionary<string, VerifiedClientComponent> Reconciled { get; set; } =
            Components();

        public IReadOnlyDictionary<string, VerifiedClientComponent> Unapplied { get; set; } =
            Components();

        public bool LauncherReplacementRequired { get; set; }
        public bool RejectCancelledGetUnappliedToken { get; set; }
        public int CreatePackageCount { get; private set; }
        public CancellationToken LastGetUnappliedToken { get; private set; }
        public List<string> Calls { get; } = [];

        public Task<PreparedClientComponentUpdate> PrepareAsync(CancellationToken cancellationToken)
        {
            Calls.Add("Prepare");
            return Task.FromResult(Prepared);
        }

        public Task<IReadOnlyDictionary<string, VerifiedClientComponent>> ReconcileAsync(
            string expectedComponentGeneration,
            CancellationToken cancellationToken)
        {
            Calls.Add("Reconcile:" + expectedComponentGeneration);
            return Task.FromResult(Reconciled);
        }

        public Task<IReadOnlyDictionary<string, VerifiedClientComponent>> GetUnappliedAsync(
            IReadOnlyDictionary<string, VerifiedClientComponent> reconciled,
            CancellationToken cancellationToken)
        {
            Calls.Add("GetUnapplied");
            LastGetUnappliedToken = cancellationToken;
            if (RejectCancelledGetUnappliedToken)
                cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Unapplied);
        }

        public void FinalizeLauncherUpdate()
            => Calls.Add("FinalizeLauncher");

        public void ApplyUpdaterAndEasyTier()
            => Calls.Add("ApplyUpdaterAndEasyTier");

        public void ApplyRouteGuard()
            => Calls.Add("ApplyRouteGuard");

        public void ApplyHooks()
            => Calls.Add("ApplyHooks");

        public bool IsLauncherReplacementRequired(VerifiedClientComponent component)
        {
            Calls.Add("IsLauncherReplacementRequired");
            return LauncherReplacementRequired;
        }

        public LocalClientUpdateService.UpdatePackageInfo CreateLauncherPackage(
            VerifiedClientComponent component)
        {
            Calls.Add("CreateLauncherPackage");
            CreatePackageCount++;
            return new LocalClientUpdateService.UpdatePackageInfo
            {
                PackagePath = component.FilePath,
                Version = component.Version,
            };
        }
    }
}
