using SplinterCellCNLauncher.Models;
using SplinterCellCNLauncher.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SplinterCellCNLauncher.ApplicationLayer;

public enum ClientUpdateHostCommandKind
{
    Continue,
    RestartLauncher,
    ReplaceLauncher
}

public sealed record PreparedClientUpdatePrompt(
    bool HasPendingUpdates,
    string ComponentGeneration,
    UpdateAnnouncementSnapshot? Announcement,
    IReadOnlyList<string> PendingComponents);

public sealed record ClientUpdateHostCommand(
    ClientUpdateHostCommandKind Kind,
    IReadOnlyList<string> Components,
    LocalClientUpdateService.UpdatePackageInfo? UpdatePackage)
{
    public static ClientUpdateHostCommand Continue()
        => new(ClientUpdateHostCommandKind.Continue, Array.Empty<string>(), null);

    public static ClientUpdateHostCommand Restart(IReadOnlyList<string> components)
        => new(ClientUpdateHostCommandKind.RestartLauncher, components, null);

    public static ClientUpdateHostCommand Replace(LocalClientUpdateService.UpdatePackageInfo package)
        => new(ClientUpdateHostCommandKind.ReplaceLauncher, Array.Empty<string>(), package);
}

internal sealed class ClientUpdateOverallProgress
{
    private readonly object _sync = new();
    private bool _downloadObserved;
    private bool _downloadCompleted;
    private double _lastPercentage;

    public ClientUpdateProgress BeginAttempt()
    {
        lock (_sync)
        {
            _downloadObserved = false;
            _downloadCompleted = false;
            _lastPercentage = 0d;
            return Create(ClientUpdateProgressStage.FetchingDescriptor, 0d);
        }
    }

    public ClientUpdateProgress Advance(ClientUpdateProgressStage stage)
        => Advance(new ClientUpdateProgress(stage, true));

    public ClientUpdateProgress Advance(ClientUpdateProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        lock (_sync)
        {
            if (progress.Stage == ClientUpdateProgressStage.Downloading)
            {
                _downloadObserved = true;
                _downloadCompleted = progress.BytesTotal > 0
                    && progress.BytesCompleted >= progress.BytesTotal;
            }
            double candidate = progress.Stage switch
            {
                ClientUpdateProgressStage.FetchingDescriptor => 5d,
                ClientUpdateProgressStage.VerifyingDescriptor => 10d,
                ClientUpdateProgressStage.CheckingComponents => 20d,
                ClientUpdateProgressStage.Downloading => 20d + (progress.Percentage * 0.6d),
                ClientUpdateProgressStage.VerifyingArtifacts =>
                    _downloadObserved && !_downloadCompleted ? _lastPercentage : 85d,
                ClientUpdateProgressStage.Activating => 95d,
                ClientUpdateProgressStage.Completed => 100d,
                _ => _lastPercentage,
            };
            _lastPercentage = progress.Stage == ClientUpdateProgressStage.Completed
                ? 100d
                : Math.Max(_lastPercentage, Math.Clamp(candidate, 0d, 99d));
            return Create(progress.Stage, _lastPercentage, progress.BytesCompleted, progress.BytesTotal);
        }
    }

    private static ClientUpdateProgress Create(
        ClientUpdateProgressStage stage,
        double percentage,
        long bytesCompleted = 0,
        long bytesTotal = 0)
        => new(
            stage,
            IsIndeterminate: false,
            bytesCompleted,
            bytesTotal,
            OverallPercentage: percentage);
}

internal interface IClientUpdateOperations
{
    Task<PreparedClientComponentUpdate> PrepareAsync(CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, VerifiedClientComponent>> ReconcileAsync(
        string expectedComponentGeneration,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, VerifiedClientComponent>> GetUnappliedAsync(
        IReadOnlyDictionary<string, VerifiedClientComponent> reconciled,
        CancellationToken cancellationToken);

    void FinalizeLauncherUpdate();
    void ApplyUpdaterAndEasyTier();
    void ApplyRouteGuard();
    void ApplyHooks();
    bool IsLauncherReplacementRequired(VerifiedClientComponent component);
    LocalClientUpdateService.UpdatePackageInfo CreateLauncherPackage(VerifiedClientComponent component);
}

internal sealed class ClientUpdateOperations : IClientUpdateOperations
{
    private readonly ClientComponentUpdateService _service;
    private readonly LauncherRuntimeContext _runtimeContext;

    public ClientUpdateOperations(
        ClientComponentUpdateService service,
        LauncherRuntimeContext runtimeContext)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _runtimeContext = runtimeContext ?? throw new ArgumentNullException(nameof(runtimeContext));
    }

    public Task<PreparedClientComponentUpdate> PrepareAsync(CancellationToken cancellationToken)
        => _service.PrepareSelectedChannelUpdateAsync(cancellationToken);

    public Task<IReadOnlyDictionary<string, VerifiedClientComponent>> ReconcileAsync(
        string expectedComponentGeneration,
        CancellationToken cancellationToken)
        => _service.ReconcileSelectedChannelAsync(expectedComponentGeneration, cancellationToken);

    public Task<IReadOnlyDictionary<string, VerifiedClientComponent>> GetUnappliedAsync(
        IReadOnlyDictionary<string, VerifiedClientComponent> reconciled,
        CancellationToken cancellationToken)
        => _service.GetUnappliedReconciledComponentsAsync(reconciled, cancellationToken);

    public void FinalizeLauncherUpdate()
        => StagedComponentBootstrapService.FinalizeLauncherUpdate(_runtimeContext.UpdateChannelName);

    public void ApplyUpdaterAndEasyTier()
        => StagedComponentBootstrapService.ApplyUpdaterAndEasyTier(_runtimeContext.UpdateChannelName);

    public void ApplyRouteGuard()
        => StagedComponentBootstrapService.ApplyRouteGuard(_runtimeContext.UpdateChannelName);

    public void ApplyHooks()
        => StagedComponentBootstrapService.ApplyHooks(_runtimeContext.UpdateChannelName);

    public bool IsLauncherReplacementRequired(VerifiedClientComponent component)
        => LocalClientUpdateService.IsLauncherReplacementRequired(component);

    public LocalClientUpdateService.UpdatePackageInfo CreateLauncherPackage(
        VerifiedClientComponent component)
        => LocalClientUpdateService.CreateLauncherComponentPackage(component);
}

public sealed class ClientUpdateCoordinator
{
    private readonly IClientUpdateOperations _operations;
    private readonly Action _stopComponentMonitor;
    private readonly Func<string, Task> _shutdownNetworkAsync;
    private readonly Func<CancellationToken, Task<UpdateAnnouncementSnapshot>> _getUpdateAnnouncementAsync;
    private readonly ClientUpdateOverallProgress _overallProgress = new();
    private readonly UpdateChannelOperationGate _operationGate;

    public event EventHandler<ClientUpdateProgress>? ProgressChanged;

    internal ClientUpdateCoordinator(
        ClientComponentUpdateService service,
        LauncherRuntimeContext runtimeContext,
        IUpdateAnnouncementReader announcementReader,
        Action stopComponentMonitor,
        Func<string, Task> shutdownNetworkAsync)
        : this(
            new ClientUpdateOperations(service, runtimeContext),
            stopComponentMonitor,
            shutdownNetworkAsync,
            RequireAnnouncementReader(announcementReader),
            new UpdateChannelOperationGate())
    {
        service.ProgressChanged += (_, progress) => PublishProgress(_overallProgress.Advance(progress));
    }

    internal ClientUpdateCoordinator(
        IClientUpdateOperations operations,
        Action stopComponentMonitor,
        Func<string, Task> shutdownNetworkAsync,
        Func<CancellationToken, Task<UpdateAnnouncementSnapshot>> getUpdateAnnouncementAsync,
        UpdateChannelOperationGate? operationGate = null)
    {
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _stopComponentMonitor = stopComponentMonitor ?? throw new ArgumentNullException(nameof(stopComponentMonitor));
        _shutdownNetworkAsync = shutdownNetworkAsync ?? throw new ArgumentNullException(nameof(shutdownNetworkAsync));
        _getUpdateAnnouncementAsync = getUpdateAnnouncementAsync
            ?? throw new ArgumentNullException(nameof(getUpdateAnnouncementAsync));
        _operationGate = operationGate ?? new UpdateChannelOperationGate();
    }

    internal UpdateChannelOperationGate OperationGate => _operationGate;

    internal IDisposable? TryBeginUpdateOperation()
        => _operationGate.TryEnterClientUpdate();

    public async Task<PreparedClientUpdatePrompt> PrepareAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PublishProgress(_overallProgress.BeginAttempt());
        _operations.FinalizeLauncherUpdate();
        PreparedClientComponentUpdate prepared =
            await _operations.PrepareAsync(cancellationToken).ConfigureAwait(false);
        if (prepared.PendingUpdates.Count == 0)
        {
            PublishProgress(_overallProgress.Advance(ClientUpdateProgressStage.Completed));
            return new PreparedClientUpdatePrompt(
                HasPendingUpdates: false,
                prepared.ComponentGeneration,
                Announcement: null,
                PendingComponents: Array.Empty<string>());
        }

        UpdateAnnouncementSnapshot announcement =
            await _getUpdateAnnouncementAsync(cancellationToken).ConfigureAwait(false);
        string[] pendingComponents = prepared.PendingUpdates
            .Select(update => update.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new PreparedClientUpdatePrompt(
            HasPendingUpdates: true,
            prepared.ComponentGeneration,
            announcement,
            pendingComponents);
    }

    public async Task<ClientUpdateHostCommand> ApplyConfirmedAsync(
        PreparedClientUpdatePrompt prepared,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        cancellationToken.ThrowIfCancellationRequested();
        if (!prepared.HasPendingUpdates
            || prepared.Announcement is null
            || prepared.PendingComponents is null
            || prepared.PendingComponents.Count == 0)
            throw new InvalidOperationException("A confirmed update requires a prepared server announcement snapshot.");

        IReadOnlyDictionary<string, VerifiedClientComponent> components =
            await _operations.ReconcileAsync(prepared.ComponentGeneration, cancellationToken)
                .ConfigureAwait(false);
        _operations.FinalizeLauncherUpdate();
        PublishProgress(_overallProgress.Advance(ClientUpdateProgressStage.Activating));

        if (components.TryGetValue(
                ClientComponentUpdateService.LauncherComponentName,
                out VerifiedClientComponent? launcher)
            && _operations.IsLauncherReplacementRequired(launcher))
        {
            // Reaching this point commits the player-confirmed update to a host handoff.
            // Stopping the background monitor cancels its polling token, which may also be
            // the token that entered this method. That cancellation must not abort the
            // already-committed replacement after the network runtime has been stopped.
            cancellationToken.ThrowIfCancellationRequested();
            _stopComponentMonitor();
            await _shutdownNetworkAsync("launcher component replacement").ConfigureAwait(false);

            ApplyStagedComponents();
            IReadOnlyDictionary<string, VerifiedClientComponent> unapplied =
                await _operations.GetUnappliedAsync(components, CancellationToken.None)
                    .ConfigureAwait(false);
            string[] blocking = unapplied.Keys
                .Where(name => !name.Equals(
                    ClientComponentUpdateService.LauncherComponentName,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (blocking.Length > 0)
            {
                throw new InvalidOperationException(
                    $"Launcher update handoff blocked because component activation did not converge: {string.Join(',', blocking)}");
            }
            if (!unapplied.ContainsKey(ClientComponentUpdateService.LauncherComponentName))
            {
                PublishProgress(_overallProgress.Advance(ClientUpdateProgressStage.Completed));
                return ClientUpdateHostCommand.Continue();
            }

            LocalClientUpdateService.UpdatePackageInfo package =
                _operations.CreateLauncherPackage(launcher);
            LogService.Info(
                $"Launcher replacement command ready: version={launcher.Version}");
            return ClientUpdateHostCommand.Replace(package);
        }

        ApplyStagedComponents();
        // A confirmed component batch must always converge through one host restart,
        // even when immediate activation already produced exact receipts. Returning
        // Continue here previously left the old Launcher process alive after files-only
        // updates because those bundles are applied during reconciliation and are not
        // returned in the staged component dictionary.
        cancellationToken.ThrowIfCancellationRequested();
        _stopComponentMonitor();
        await _shutdownNetworkAsync("component update activation restart").ConfigureAwait(false);
        return ClientUpdateHostCommand.Restart(prepared.PendingComponents.ToArray());
    }

    private void ApplyStagedComponents()
    {
        _operations.ApplyUpdaterAndEasyTier();
        _operations.ApplyRouteGuard();
        _operations.ApplyHooks();
        _operations.FinalizeLauncherUpdate();
    }

    private void PublishProgress(ClientUpdateProgress progress)
        => ProgressChanged?.Invoke(this, progress);

    private static Func<CancellationToken, Task<UpdateAnnouncementSnapshot>> RequireAnnouncementReader(
        IUpdateAnnouncementReader announcementReader)
    {
        ArgumentNullException.ThrowIfNull(announcementReader);
        return announcementReader.GetRequiredAsync;
    }
}
