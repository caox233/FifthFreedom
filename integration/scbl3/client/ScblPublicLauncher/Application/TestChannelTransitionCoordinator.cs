using SplinterCellCNLauncher.Models;

namespace SplinterCellCNLauncher.ApplicationLayer;

public sealed class UpdateChannelOperationGate
{
    private const int ClientUpdateOwner = 1;
    private const int ChannelTransitionOwner = 2;
    private int _owner;

    public IDisposable? TryEnterClientUpdate()
        => TryEnter(ClientUpdateOwner);

    public IDisposable? TryEnterChannelTransition()
        => TryEnter(ChannelTransitionOwner);

    private IDisposable? TryEnter(int owner)
        => Interlocked.CompareExchange(ref _owner, owner, 0) == 0
            ? new Lease(this, owner)
            : null;

    private void Release(int owner)
    {
        if (Interlocked.CompareExchange(ref _owner, 0, owner) != owner)
            throw new InvalidOperationException("Update/channel operation gate ownership was lost.");
    }

    private sealed class Lease(UpdateChannelOperationGate gate, int owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                gate.Release(owner);
        }
    }
}

public enum TestChannelTransitionResult
{
    ContinueStartup,
    StopStartup,
    Failed
}

public sealed record TestChannelTransitionSafety(
    bool IsBusy,
    bool IsLaunchFlowActive,
    bool IsUpdating,
    bool IsGameStarting,
    bool IsGameRunning,
    bool IsGameProcessDetected,
    bool IsClosing)
{
    public bool CanSwitch =>
        !IsBusy
        && !IsLaunchFlowActive
        && !IsUpdating
        && !IsGameStarting
        && !IsGameRunning
        && !IsGameProcessDetected
        && !IsClosing;
}

public sealed class TestChannelTransitionCoordinator
{
    private readonly ClientUpdateChannel _currentChannel;
    private readonly Func<ClientUpdateChannel> _loadPreference;
    private readonly Action<ClientUpdateChannel> _savePreference;
    private readonly Func<ClientUpdateChannel, bool> _scheduleRestart;
    private readonly Func<TestChannelTransitionSafety> _getSafety;
    private readonly UpdateChannelOperationGate _operationGate;
    private readonly SemaphoreSlim _commandGate = new(1, 1);

    public TestChannelTransitionCoordinator(
        ClientUpdateChannel currentChannel,
        Func<ClientUpdateChannel> loadPreference,
        Action<ClientUpdateChannel> savePreference,
        Func<ClientUpdateChannel, bool> scheduleRestart,
        Func<TestChannelTransitionSafety> getSafety,
        UpdateChannelOperationGate? operationGate = null)
    {
        _currentChannel = currentChannel;
        _loadPreference = loadPreference;
        _savePreference = savePreference;
        _scheduleRestart = scheduleRestart;
        _getSafety = getSafety;
        _operationGate = operationGate ?? new UpdateChannelOperationGate();
    }

    public bool IsContinuationConfirmationRequired
        => _currentChannel == ClientUpdateChannel.Test;

    public async Task<TestChannelTransitionResult> ContinueTestAsync()
    {
        await _commandGate.WaitAsync().ConfigureAwait(false);
        try
        {
            using IDisposable? operationLease = _operationGate.TryEnterChannelTransition();
            if (operationLease is null)
                return TestChannelTransitionResult.Failed;
            if (_currentChannel != ClientUpdateChannel.Test || !_getSafety().CanSwitch)
                return TestChannelTransitionResult.Failed;
            if (!_getSafety().CanSwitch)
                return TestChannelTransitionResult.Failed;
            try
            {
                if (_loadPreference() != ClientUpdateChannel.Test)
                    _savePreference(ClientUpdateChannel.Test);
                return TestChannelTransitionResult.ContinueStartup;
            }
            catch
            {
                return TestChannelTransitionResult.Failed;
            }
        }
        finally
        {
            _commandGate.Release();
        }
    }

    public Task<TestChannelTransitionResult> ReturnToStableAsync()
        => TransitionAndRestartAsync(
            expectedCurrent: ClientUpdateChannel.Test,
            target: ClientUpdateChannel.Stable);

    public Task<TestChannelTransitionResult> EnterTestAsync()
        => TransitionAndRestartAsync(
            expectedCurrent: ClientUpdateChannel.Stable,
            target: ClientUpdateChannel.Test);

    private async Task<TestChannelTransitionResult> TransitionAndRestartAsync(
        ClientUpdateChannel expectedCurrent,
        ClientUpdateChannel target)
    {
        await _commandGate.WaitAsync().ConfigureAwait(false);
        try
        {
            using IDisposable? operationLease = _operationGate.TryEnterChannelTransition();
            if (operationLease is null)
                return TestChannelTransitionResult.Failed;
            if (_currentChannel != expectedCurrent || !_getSafety().CanSwitch)
                return TestChannelTransitionResult.Failed;
            if (!_getSafety().CanSwitch)
                return TestChannelTransitionResult.Failed;

            ClientUpdateChannel previous;
            try
            {
                previous = _loadPreference();
                if (previous != target)
                    _savePreference(target);
            }
            catch
            {
                return TestChannelTransitionResult.Failed;
            }

            if (!_getSafety().CanSwitch)
            {
                TryRestorePreference(previous, target);
                return TestChannelTransitionResult.Failed;
            }

            bool scheduled;
            try
            {
                scheduled = _scheduleRestart(target);
            }
            catch
            {
                scheduled = false;
            }
            if (scheduled)
                return TestChannelTransitionResult.StopStartup;

            TryRestorePreference(previous, target);
            return TestChannelTransitionResult.Failed;
        }
        finally
        {
            _commandGate.Release();
        }
    }

    private void TryRestorePreference(
        ClientUpdateChannel previous,
        ClientUpdateChannel target)
    {
        try
        {
            if (previous != target)
                _savePreference(previous);
        }
        catch
        {
            // The command still fails closed. A later startup resolves only
            // the persisted value that actually reached atomic storage.
        }
    }
}
