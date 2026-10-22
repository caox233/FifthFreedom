using SplinterCellCNLauncher.ApplicationLayer;
using SplinterCellCNLauncher.Models;

namespace SCBL.Client.Tests;

public sealed class TestChannelTransitionCoordinatorTests
{
    private static readonly TestChannelTransitionSafety Safe = new(
        false, false, false, false, false, false, false);

    [Fact]
    public void Only_test_launches_require_continuation_confirmation()
    {
        Assert.False(Create(ClientUpdateChannel.Stable).Coordinator
            .IsContinuationConfirmationRequired);
        Assert.True(Create(ClientUpdateChannel.Test).Coordinator
            .IsContinuationConfirmationRequired);
    }

    [Fact]
    public async Task Continue_test_persists_preference_without_restart()
    {
        Fixture fixture = Create(
            ClientUpdateChannel.Test,
            ClientUpdateChannel.Stable);

        TestChannelTransitionResult result = await fixture.Coordinator
            .ContinueTestAsync();

        Assert.Equal(TestChannelTransitionResult.ContinueStartup, result);
        Assert.Equal(ClientUpdateChannel.Test, fixture.Preference);
        Assert.Empty(fixture.Restarts);
    }

    [Theory]
    [InlineData(ClientUpdateChannel.Stable, ClientUpdateChannel.Test)]
    [InlineData(ClientUpdateChannel.Test, ClientUpdateChannel.Stable)]
    public async Task Channel_switch_persists_target_and_stops_for_restart(
        ClientUpdateChannel current,
        ClientUpdateChannel target)
    {
        Fixture fixture = Create(current, current);

        TestChannelTransitionResult result = current == ClientUpdateChannel.Stable
            ? await fixture.Coordinator.EnterTestAsync()
            : await fixture.Coordinator.ReturnToStableAsync();

        Assert.Equal(TestChannelTransitionResult.StopStartup, result);
        Assert.Equal(target, fixture.Preference);
        Assert.Equal([target], fixture.Restarts);
    }

    [Theory]
    [MemberData(nameof(UnsafeStates))]
    public async Task Unsafe_state_rejects_switch_without_persistence_or_restart(
        TestChannelTransitionSafety safety)
    {
        Fixture fixture = Create(
            ClientUpdateChannel.Stable,
            ClientUpdateChannel.Stable,
            safety);

        TestChannelTransitionResult result = await fixture.Coordinator.EnterTestAsync();

        Assert.Equal(TestChannelTransitionResult.Failed, result);
        Assert.Equal(ClientUpdateChannel.Stable, fixture.Preference);
        Assert.Empty(fixture.Restarts);
        Assert.Equal(0, fixture.SaveCalls);
    }

    [Theory]
    [InlineData(ClientUpdateChannel.Stable)]
    [InlineData(ClientUpdateChannel.Test)]
    public async Task Restart_failure_rolls_back_persisted_preference(
        ClientUpdateChannel current)
    {
        Fixture fixture = Create(current, current);
        fixture.RestartSucceeds = false;

        TestChannelTransitionResult result = current == ClientUpdateChannel.Stable
            ? await fixture.Coordinator.EnterTestAsync()
            : await fixture.Coordinator.ReturnToStableAsync();

        Assert.Equal(TestChannelTransitionResult.Failed, result);
        Assert.Equal(current, fixture.Preference);
        Assert.Equal(2, fixture.SaveCalls);
    }

    [Fact]
    public async Task Active_update_operation_rejects_channel_switch_without_side_effects()
    {
        Fixture fixture = Create(ClientUpdateChannel.Stable);
        using IDisposable updateLease = Assert.IsAssignableFrom<IDisposable>(
            fixture.Gate.TryEnterClientUpdate());

        TestChannelTransitionResult result = await fixture.Coordinator.EnterTestAsync();

        Assert.Equal(TestChannelTransitionResult.Failed, result);
        Assert.Equal(ClientUpdateChannel.Stable, fixture.Preference);
        Assert.Equal(0, fixture.SaveCalls);
        Assert.Empty(fixture.Restarts);
    }

    public static IEnumerable<object[]> UnsafeStates()
    {
        yield return [new TestChannelTransitionSafety(true, false, false, false, false, false, false)];
        yield return [new TestChannelTransitionSafety(false, true, false, false, false, false, false)];
        yield return [new TestChannelTransitionSafety(false, false, true, false, false, false, false)];
        yield return [new TestChannelTransitionSafety(false, false, false, true, false, false, false)];
        yield return [new TestChannelTransitionSafety(false, false, false, false, true, false, false)];
        yield return [new TestChannelTransitionSafety(false, false, false, false, false, true, false)];
        yield return [new TestChannelTransitionSafety(false, false, false, false, false, false, true)];
    }

    private static Fixture Create(
        ClientUpdateChannel current,
        ClientUpdateChannel? preference = null,
        TestChannelTransitionSafety? safety = null)
        => new(current, preference ?? current, safety ?? Safe);

    private sealed class Fixture
    {
        public Fixture(
            ClientUpdateChannel current,
            ClientUpdateChannel preference,
            TestChannelTransitionSafety safety)
        {
            Preference = preference;
            Gate = new UpdateChannelOperationGate();
            Coordinator = new TestChannelTransitionCoordinator(
                current,
                () => Preference,
                channel =>
                {
                    SaveCalls++;
                    Preference = channel;
                },
                channel =>
                {
                    Restarts.Add(channel);
                    return RestartSucceeds;
                },
                () => safety,
                Gate);
        }

        public TestChannelTransitionCoordinator Coordinator { get; }
        public UpdateChannelOperationGate Gate { get; }
        public ClientUpdateChannel Preference { get; private set; }
        public int SaveCalls { get; private set; }
        public bool RestartSucceeds { get; set; } = true;
        public List<ClientUpdateChannel> Restarts { get; } = new();
    }
}
