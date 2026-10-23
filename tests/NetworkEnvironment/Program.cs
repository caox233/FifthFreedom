using System.Text.Json;
using SplinterCellCNLauncher.Services;

static class Program
{
    private static readonly NetworkEnvironmentSelection Join = NetworkEnvironmentSelection.ForLan(LanRole.Join);
    private static readonly NetworkEnvironmentSelection Host = NetworkEnvironmentSelection.ForLan(LanRole.Host);
    private static readonly NetworkEnvironmentSelection Fast = NetworkEnvironmentSelection.ForPublic(PublicTransport.Fast);
    private static readonly NetworkEnvironmentSelection Compatible = NetworkEnvironmentSelection.ForPublic(PublicTransport.Compatible);
    private static int _passed;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static TaskCompletionSource<T> Gate<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Case(string name, Func<Task> test)
    {
        await test().WaitAsync(TimeSpan.FromSeconds(10));
        Console.WriteLine("PASS " + name);
        _passed++;
    }
    private static async Task Main()
    {
        await Case("LAN roles and Public transports form disjoint selections", () =>
        {
            Check(Join.LanRole == LanRole.Join && Join.PublicTransport is null, "join selection");
            Check(Host.LanRole == LanRole.Host && Host.PublicTransport is null, "host selection");
            Check(Fast.PublicTransport == PublicTransport.Fast && Fast.LanRole is null, "fast selection");
            Check(Compatible.PublicTransport == PublicTransport.Compatible && Compatible.LanRole is null, "compatible selection");
            try { NetworkEnvironmentSelection.ForLan((LanRole)999); throw new Exception("invalid role accepted"); }
            catch (ArgumentOutOfRangeException) { }
            try { NetworkEnvironmentSelection.ForPublic((PublicTransport)999); throw new Exception("invalid transport accepted"); }
            catch (ArgumentOutOfRangeException) { }
            return Task.CompletedTask;
        });

        await Case("construction and initial snapshot have no connection side effects", async () =>
        {
            var lan = new FakeAdapter(NetworkEnvironmentKind.Lan);
            var pub = new FakeAdapter(NetworkEnvironmentKind.Public);
            await using var controller = new NetworkEnvironmentController(lan, () => default, pub);
            Check(controller.Snapshot.Selection is null && !controller.Snapshot.IsReady && controller.Snapshot.CanSwitch, "idle state");
            Check(lan.Calls == 0 && pub.Calls == 0, "constructor connected");
        });

        await Case("LAN connection does not invoke Public and owns only its returned lease", async () =>
        {
            var lan = new FakeAdapter(NetworkEnvironmentKind.Lan);
            var pub = new FakeAdapter(NetworkEnvironmentKind.Public);
            var unrelated = new FakeLease();
            var own = new FakeLease(latency: 12);
            lan.Enqueue(own);
            var controller = new NetworkEnvironmentController(lan, () => default, pub);
            var result = await controller.ChangeAsync(Join);
            Check(result.Outcome == NetworkEnvironmentChangeOutcome.Applied && result.Snapshot.IsReady, "LAN not applied");
            Check(result.Snapshot.LatencyMilliseconds == 12 && pub.Calls == 0, "wrong adapter or latency");
            await controller.DisposeAsync();
            Check(own.Disposals == 1 && unrelated.Disposals == 0, "ownership boundary");
        });

        await Case("default Public adapter is unavailable without teardown or fallback", async () =>
        {
            var lan = new FakeAdapter(NetworkEnvironmentKind.Lan); var own = new FakeLease(); lan.Enqueue(own);
            await using var controller = new NetworkEnvironmentController(lan, () => default);
            await controller.ChangeAsync(Host);
            var result = await controller.ChangeAsync(Fast);
            Check(result.Outcome == NetworkEnvironmentChangeOutcome.Unavailable, "Public was enabled");
            Check(result.Snapshot.Selection == Host && result.Snapshot.IsReady && own.Disposals == 0 && lan.Calls == 1, "implicit fallback or teardown");
            Check(result.Snapshot.Status == NetworkEnvironmentStatus.Unavailable, "missing unavailable status");
        });

        await Case("switch stops the owned LAN lease before creating Public resources", async () =>
        {
            var lan = new FakeAdapter(NetworkEnvironmentKind.Lan); var pub = new FakeAdapter(NetworkEnvironmentKind.Public);
            var old = new FakeLease(); var target = new FakeLease(); lan.Enqueue(old);
            pub.Enqueue((_, _) => { Check(old.Disposals == 1, "overlapped LAN/Public resources"); return Task.FromResult<INetworkEnvironmentLease>(target); });
            await using var controller = new NetworkEnvironmentController(lan, () => default, pub);
            await controller.ChangeAsync(Host); var result = await controller.ChangeAsync(Compatible);
            Check(result.Outcome == NetworkEnvironmentChangeOutcome.Applied && result.Snapshot.Selection == Compatible, "wrong transport");
            Check(pub.Selections.Single() == Compatible && target.Disposals == 0, "selection or ownership");
        });

        await Case("unchanged ready selection neither reconnects nor closes its lease", async () =>
        {
            var lan = new FakeAdapter(NetworkEnvironmentKind.Lan); var lease = new FakeLease(); lan.Enqueue(lease);
            await using var controller = new NetworkEnvironmentController(lan, () => default);
            await controller.ChangeAsync(Join); var result = await controller.ChangeAsync(Join);
            Check(result.Outcome == NetworkEnvironmentChangeOutcome.Unchanged && lan.Calls == 1 && lease.Disposals == 0, "unchanged transaction");
        });

        await Case("lost lease readiness is visible and same-selection reconnect is not skipped", async () =>
        {
            var lan = new FakeAdapter(NetworkEnvironmentKind.Lan);
            var original = new FakeLease(latency: 10); var restored = new FakeLease(latency: 20);
            lan.Enqueue(original); lan.Enqueue(restored);
            await using var controller = new NetworkEnvironmentController(lan, () => default);
            await controller.ChangeAsync(Join);
            original.LatencyMilliseconds = 15;
            Check(controller.Snapshot.LatencyMilliseconds == 15, "live latency not sampled");
            original.IsReady = false;
            Check(!controller.Snapshot.IsReady && controller.Snapshot.Status == NetworkEnvironmentStatus.Disconnected &&
                  controller.Snapshot.LatencyMilliseconds is null, "lost connection falsely ready");
            var result = await controller.ChangeAsync(Join);
            Check(result.Outcome == NetworkEnvironmentChangeOutcome.Applied && lan.Calls == 2 && original.Disposals == 1 &&
                  result.Snapshot.IsReady && result.Snapshot.LatencyMilliseconds == 20, "same-selection reconnect skipped");
        });

        foreach (bool ownedLaunch in new[] { false, true })
        {
            await Case(ownedLaunch ? "owned launch blocks mode changes" : "active game blocks mode changes", async () =>
            {
                var activity = new NetworkEnvironmentActivity(); var lan = new FakeAdapter(NetworkEnvironmentKind.Lan);
                var old = new FakeLease(); lan.Enqueue(old);
                await using var controller = new NetworkEnvironmentController(lan, () => activity);
                await controller.ChangeAsync(Join);
                activity = new(!ownedLaunch, ownedLaunch);
                var result = await controller.ChangeAsync(Host);
                Check(result.Outcome == (ownedLaunch ? NetworkEnvironmentChangeOutcome.RejectedOwnedLaunch : NetworkEnvironmentChangeOutcome.RejectedGameActive), "did not reject");
                Check(!controller.Snapshot.CanSwitch && lan.Calls == 1 && old.Disposals == 0, "active resource touched");
            });
        }

        await Case("failed connection restores the previous selection and reports the failure", async () =>
        {
            var lan = new FakeAdapter(NetworkEnvironmentKind.Lan); var pub = new FakeAdapter(NetworkEnvironmentKind.Public);
            var old = new FakeLease(); var restored = new FakeLease(); lan.Enqueue(old); lan.Enqueue(restored);
            pub.Enqueue((_, _) => throw new InvalidOperationException("SECRET-TOKEN-MUST-NOT-ESCAPE"));
            await using var controller = new NetworkEnvironmentController(lan, () => default, pub);
            await controller.ChangeAsync(Join); var result = await controller.ChangeAsync(Fast);
            Check(result.Outcome == NetworkEnvironmentChangeOutcome.ConnectionFailed && result.Rollback == NetworkEnvironmentRollback.Restored, "failure/rollback lost");
            Check(result.Snapshot.Selection == Join && result.Snapshot.IsReady && result.Snapshot.Status == NetworkEnvironmentStatus.ConnectionFailed, "false target state");
            Check(old.Disposals == 1 && restored.Disposals == 0 && lan.Calls == 2, "restoration ownership");
            Check(!JsonSerializer.Serialize(result).Contains("SECRET-TOKEN"), "raw exception leaked");
        });

        await Case("not-ready candidate is disposed before rollback", async () =>
        {
            var lan = new FakeAdapter(NetworkEnvironmentKind.Lan); var pub = new FakeAdapter(NetworkEnvironmentKind.Public);
            var candidate = new FakeLease(ready: false); lan.Enqueue(new FakeLease()); pub.Enqueue(candidate);
            lan.Enqueue((_, _) => { Check(candidate.Disposals == 1, "rollback overlaps failed candidate"); return Task.FromResult<INetworkEnvironmentLease>(new FakeLease()); });
            await using var controller = new NetworkEnvironmentController(lan, () => default, pub);
            await controller.ChangeAsync(Join); var result = await controller.ChangeAsync(Fast);
            Check(result.Rollback == NetworkEnvironmentRollback.Restored && result.Outcome == NetworkEnvironmentChangeOutcome.ConnectionFailed, "not-ready accepted");
        });

        await Case("failed rollback leaves a visible non-ready state", async () =>
        {
            var lan = new FakeAdapter(NetworkEnvironmentKind.Lan); var pub = new FakeAdapter(NetworkEnvironmentKind.Public);
            lan.Enqueue(new FakeLease()); lan.Enqueue((_, _) => throw new IOException("rollback unavailable"));
            pub.Enqueue((_, _) => throw new IOException("target unavailable"));
            await using var controller = new NetworkEnvironmentController(lan, () => default, pub);
            await controller.ChangeAsync(Join); var result = await controller.ChangeAsync(Fast);
            Check(result.Rollback == NetworkEnvironmentRollback.Failed && !result.Snapshot.IsReady && result.Snapshot.Status == NetworkEnvironmentStatus.RollbackFailed, "rollback falsely green");
        });

        await Case("initial failure does not silently connect an alternate environment", async () =>
        {
            var lan = new FakeAdapter(NetworkEnvironmentKind.Lan); var pub = new FakeAdapter(NetworkEnvironmentKind.Public);
            pub.Enqueue((_, _) => throw new IOException("unavailable"));
            await using var controller = new NetworkEnvironmentController(lan, () => default, pub);
            var result = await controller.ChangeAsync(Fast);
            Check(lan.Calls == 0 && result.Rollback == NetworkEnvironmentRollback.NotNeeded && !result.Snapshot.IsReady && result.Snapshot.Selection is null, "implicit LAN fallback");
        });

        await Case("cancelled late candidate is cleaned and prior runtime is restored", async () =>
        {
            var lan = new FakeAdapter(NetworkEnvironmentKind.Lan); var pub = new FakeAdapter(NetworkEnvironmentKind.Public);
            var completion = Gate<INetworkEnvironmentLease>(); var entered = Gate<bool>(); var target = new FakeLease();
            lan.Enqueue(new FakeLease()); lan.Enqueue(new FakeLease());
            pub.Enqueue((_, _) => { entered.TrySetResult(true); return completion.Task; });
            await using var controller = new NetworkEnvironmentController(lan, () => default, pub);
            await controller.ChangeAsync(Join); using var cancel = new CancellationTokenSource();
            var change = controller.ChangeAsync(Fast, cancel.Token); await entered.Task;
            Check(controller.Snapshot.IsSwitching && !controller.Snapshot.CanSwitch, "switch state missing");
            cancel.Cancel(); completion.SetResult(target); var result = await change;
            Check(result.Outcome == NetworkEnvironmentChangeOutcome.Cancelled && result.Rollback == NetworkEnvironmentRollback.Restored, "cancel lost");
            Check(target.Disposals == 1 && controller.Snapshot.Selection == Join && controller.Snapshot.IsReady, "late target published");
        });

        await Case("close prevents late candidate readiness and never reconnects the previous mode", async () =>
        {
            var lan = new FakeAdapter(NetworkEnvironmentKind.Lan); var pub = new FakeAdapter(NetworkEnvironmentKind.Public);
            var completion = Gate<INetworkEnvironmentLease>(); var entered = Gate<bool>(); var target = new FakeLease();
            var old = new FakeLease(); lan.Enqueue(old);
            pub.Enqueue((_, _) => { entered.TrySetResult(true); return completion.Task; });
            var controller = new NetworkEnvironmentController(lan, () => default, pub);
            await controller.ChangeAsync(Host); var change = controller.ChangeAsync(Fast); await entered.Task;
            var close = controller.DisposeAsync().AsTask();
            Check(controller.Snapshot.IsClosed && !controller.Snapshot.IsReady && !controller.Snapshot.CanSwitch, "close state late");
            completion.SetResult(target); var result = await change; await close;
            Check(result.Outcome == NetworkEnvironmentChangeOutcome.Closed && lan.Calls == 1 && old.Disposals == 1 && target.Disposals == 1, "late candidate survived close");
            Check(controller.Snapshot.Status == NetworkEnvironmentStatus.Closed, "late callback overwrote closed state");
        });

        await Case("close also prevents a late rollback lease from becoming ready", async () =>
        {
            var lan = new FakeAdapter(NetworkEnvironmentKind.Lan); var pub = new FakeAdapter(NetworkEnvironmentKind.Public);
            var rollback = Gate<INetworkEnvironmentLease>(); var rollbackEntered = Gate<bool>(); var restored = new FakeLease();
            lan.Enqueue(new FakeLease());
            lan.Enqueue((_, _) => { rollbackEntered.TrySetResult(true); return rollback.Task; });
            pub.Enqueue((_, _) => throw new IOException("target unavailable"));
            var controller = new NetworkEnvironmentController(lan, () => default, pub);
            await controller.ChangeAsync(Join); var change = controller.ChangeAsync(Fast); await rollbackEntered.Task;
            var close = controller.DisposeAsync().AsTask(); rollback.SetResult(restored); var result = await change; await close;
            Check(result.Outcome == NetworkEnvironmentChangeOutcome.Closed && restored.Disposals == 1 && !controller.Snapshot.IsReady, "late rollback leaked");
        });

        await Case("concurrent change requests serialize instead of overlapping adapters", async () =>
        {
            var lan = new FakeAdapter(NetworkEnvironmentKind.Lan); var pub = new FakeAdapter(NetworkEnvironmentKind.Public);
            var completion = Gate<INetworkEnvironmentLease>(); var entered = Gate<bool>(); var first = new FakeLease();
            lan.Enqueue((_, _) => { entered.TrySetResult(true); return completion.Task; });
            pub.Enqueue((_, _) => { Check(first.Disposals == 1, "concurrent overlap"); return Task.FromResult<INetworkEnvironmentLease>(new FakeLease()); });
            await using var controller = new NetworkEnvironmentController(lan, () => default, pub);
            var firstChange = controller.ChangeAsync(Join); await entered.Task; var secondChange = controller.ChangeAsync(Compatible);
            Check(pub.Calls == 0 && !secondChange.IsCompleted, "queued request entered early");
            completion.SetResult(first); await firstChange; var secondResult = await secondChange;
            Check(secondResult.Outcome == NetworkEnvironmentChangeOutcome.Applied && controller.Snapshot.Selection == Compatible, "serialized final state");
        });

        await Case("cancelled queued request never invokes its adapter", async () =>
        {
            var lan = new FakeAdapter(NetworkEnvironmentKind.Lan); var pub = new FakeAdapter(NetworkEnvironmentKind.Public);
            var completion = Gate<INetworkEnvironmentLease>(); var entered = Gate<bool>();
            lan.Enqueue((_, _) => { entered.TrySetResult(true); return completion.Task; });
            await using var controller = new NetworkEnvironmentController(lan, () => default, pub);
            var first = controller.ChangeAsync(Join); await entered.Task; using var cancel = new CancellationTokenSource();
            var queued = controller.ChangeAsync(Fast, cancel.Token); cancel.Cancel(); var result = await queued;
            Check(result.Outcome == NetworkEnvironmentChangeOutcome.Cancelled && pub.Calls == 0 && controller.Snapshot.IsSwitching, "queued cancellation mutated active change");
            completion.SetResult(new FakeLease()); await first;
        });

        await Case("close is idempotent and subsequent changes remain closed", async () =>
        {
            var lan = new FakeAdapter(NetworkEnvironmentKind.Lan); var own = new FakeLease(); lan.Enqueue(own);
            var controller = new NetworkEnvironmentController(lan, () => default);
            await controller.ChangeAsync(Join);
            await Task.WhenAll(controller.DisposeAsync().AsTask(), controller.DisposeAsync().AsTask());
            var result = await controller.ChangeAsync(Host);
            Check(own.Disposals == 1 && lan.Calls == 1 && result.Outcome == NetworkEnvironmentChangeOutcome.Closed, "non-idempotent close");
        });

        await Case("failed owned cleanup prevents activation and is retried only on close", async () =>
        {
            var lan = new FakeAdapter(NetworkEnvironmentKind.Lan); var pub = new FakeAdapter(NetworkEnvironmentKind.Public);
            var own = new FakeLease { DisposeFailures = 1 }; lan.Enqueue(own);
            var controller = new NetworkEnvironmentController(lan, () => default, pub);
            await controller.ChangeAsync(Join); var result = await controller.ChangeAsync(Fast);
            Check(result.Snapshot.Status == NetworkEnvironmentStatus.CleanupFailed && !result.Snapshot.IsReady && pub.Calls == 0, "started despite failed cleanup");
            await controller.ChangeAsync(Host); Check(lan.Calls == 1, "overlapped uncertain owned resource");
            await controller.DisposeAsync(); Check(own.Disposals == 2, "failed owned resource not retried");
        });

        await Case("close reports failed owned cleanup and preserves it for retry", async () =>
        {
            var lan = new FakeAdapter(NetworkEnvironmentKind.Lan);
            var lease = new FakeLease { DisposeFailures = 1 }; lan.Enqueue(lease);
            var controller = new NetworkEnvironmentController(lan, () => default);
            await controller.ChangeAsync(Join);
            bool failureReported = false;
            try { await controller.DisposeAsync(); }
            catch (InvalidOperationException exception)
            {
                failureReported = exception.Message == "Owned network resources could not be closed; retry disposal.";
            }
            Check(failureReported && controller.Snapshot.IsClosed && !controller.Snapshot.IsReady, "close falsely succeeded");
            await controller.DisposeAsync();
            Check(lease.Disposals == 2 && lan.Calls == 1, "failed owned cleanup lost or reconnected");
        });

        await Case("candidate cleanup failure cannot start a rollback listener", async () =>
        {
            var lan = new FakeAdapter(NetworkEnvironmentKind.Lan); var pub = new FakeAdapter(NetworkEnvironmentKind.Public);
            var candidate = new FakeLease(ready: false) { DisposeFailures = 1 }; lan.Enqueue(new FakeLease()); pub.Enqueue(candidate);
            var controller = new NetworkEnvironmentController(lan, () => default, pub);
            await controller.ChangeAsync(Join); var result = await controller.ChangeAsync(Fast);
            Check(result.Snapshot.Status == NetworkEnvironmentStatus.CleanupFailed && lan.Calls == 1 && !result.Snapshot.IsReady, "rollback overlaps failed resource");
            await controller.DisposeAsync(); Check(candidate.Disposals == 2, "candidate ownership lost");
        });

        foreach (bool ownedLaunch in new[] { false, true })
        {
            await Case(ownedLaunch ? "late owned launch blocks candidate commit" : "late active game blocks candidate commit", async () =>
            {
                var activity = new NetworkEnvironmentActivity();
                var lan = new FakeAdapter(NetworkEnvironmentKind.Lan); var pub = new FakeAdapter(NetworkEnvironmentKind.Public);
                var completion = Gate<INetworkEnvironmentLease>(); var entered = Gate<bool>(); var candidate = new FakeLease();
                lan.Enqueue(new FakeLease()); lan.Enqueue(new FakeLease());
                pub.Enqueue((_, _) => { entered.TrySetResult(true); return completion.Task; });
                await using var controller = new NetworkEnvironmentController(lan, () => activity, pub);
                await controller.ChangeAsync(Join); var change = controller.ChangeAsync(Fast); await entered.Task;
                activity = new(!ownedLaunch, ownedLaunch); completion.SetResult(candidate); var result = await change;
                Check(result.Outcome == (ownedLaunch ? NetworkEnvironmentChangeOutcome.RejectedOwnedLaunch : NetworkEnvironmentChangeOutcome.RejectedGameActive), "late activity ignored");
                Check(candidate.Disposals == 1 && result.Rollback == NetworkEnvironmentRollback.Restored && result.Snapshot.Selection == Join, "late active candidate published");
            });
        }

        await Case("snapshot schema contains only bounded presentation state", () =>
        {
            string[] allowed = ["Selection", "IsReady", "IsSwitching", "IsClosed", "Status", "LatencyMilliseconds", "GameActive", "OwnedLaunch", "CanSwitch"];
            var actual = typeof(NetworkEnvironmentSnapshot).GetProperties().Select(p => p.Name).OrderBy(x => x);
            Check(actual.SequenceEqual(allowed.OrderBy(x => x)), "snapshot schema added sensitive/unreviewed fields");
            Check(typeof(NetworkEnvironmentSnapshot).GetProperties().All(p => p.PropertyType != typeof(string)), "arbitrary text in snapshot");
            return Task.CompletedTask;
        });

        await Case("pre-cancelled request performs no connection", async () =>
        {
            var lan = new FakeAdapter(NetworkEnvironmentKind.Lan); await using var controller = new NetworkEnvironmentController(lan, () => default);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            var result = await controller.ChangeAsync(Join, cancelled.Token);
            Check(result.Outcome == NetworkEnvironmentChangeOutcome.Cancelled && lan.Calls == 0 && controller.Snapshot.Status == NetworkEnvironmentStatus.Idle, "pre-cancel side effect");
        });

        await Case("invalid latency is not presented as a measurement", async () =>
        {
            var lan = new FakeAdapter(NetworkEnvironmentKind.Lan); lan.Enqueue(new FakeLease(latency: -1));
            await using var controller = new NetworkEnvironmentController(lan, () => default);
            var result = await controller.ChangeAsync(Join);
            Check(result.Snapshot.IsReady && result.Snapshot.LatencyMilliseconds is null, "invalid latency published");
        });

        await Case("constructor rejects an adapter for the wrong environment", () =>
        {
            try
            {
                _ = new NetworkEnvironmentController(new FakeAdapter(NetworkEnvironmentKind.Public), () => default);
                throw new Exception("wrong adapter accepted");
            }
            catch (ArgumentException) { }
            return Task.CompletedTask;
        });
        await Case("configured LAN Join can replace Host while offline without false readiness", async () =>
        {
            var lan = new FakeAdapter(NetworkEnvironmentKind.Lan);
            var host = new FakeLease(); lan.Enqueue(host);
            int closes = 0;
            var join = new LanEnvironmentLease(true, () => { closes++; return Task.CompletedTask; }); lan.Enqueue(join);
            await using var controller = new NetworkEnvironmentController(lan, () => default);
            await controller.ChangeAsync(Host);
            var result = await controller.ChangeAsync(Join);
            Check(result.Outcome == NetworkEnvironmentChangeOutcome.Applied && result.Rollback == NetworkEnvironmentRollback.NotNeeded,
                "offline Join incorrectly rolled back");
            Check(result.Snapshot.Selection == Join && !result.Snapshot.IsReady && result.Snapshot.Status == NetworkEnvironmentStatus.Disconnected,
                "offline Join falsely ready");
            Check(host.Disposals == 1 && closes == 0, "old host ownership not released");
            var unchanged = await controller.ChangeAsync(Join);
            Check(unchanged.Outcome == NetworkEnvironmentChangeOutcome.Unchanged && lan.Calls == 2, "configured role unexpectedly reactivated");
            join.ReportCapabilities(true, 17);
            Check(controller.Snapshot.IsReady && controller.Snapshot.Status == NetworkEnvironmentStatus.Ready && controller.Snapshot.LatencyMilliseconds == 17,
                "real observation did not restore readiness");
            join.ReportCapabilities(false);
            Check(!controller.Snapshot.IsReady && controller.Snapshot.Status == NetworkEnvironmentStatus.Disconnected && controller.Snapshot.LatencyMilliseconds is null,
                "lost capabilities kept readiness");
        });
        await Case("configured Join marker cannot admit a not-ready Host or Public candidate", async () =>
        {
            foreach (var selection in new[] {Host, Fast})
            {
                int closes = 0;
                var invalid = new LanEnvironmentLease(true, () => { closes++; return Task.CompletedTask; });
                var lan = new FakeAdapter(NetworkEnvironmentKind.Lan); var pub = new FakeAdapter(NetworkEnvironmentKind.Public);
                (selection == Host ? lan : pub).Enqueue(invalid);
                await using var controller = new NetworkEnvironmentController(lan, () => default, pub);
                var result = await controller.ChangeAsync(selection);
                Check(result.Outcome == NetworkEnvironmentChangeOutcome.ConnectionFailed && !result.Snapshot.IsReady && closes == 1,
                    "configured marker widened readiness admission");
            }
        });
        await Case("failed Host activation restores explicitly configured offline Join", async () =>
        {
            var lan = new FakeAdapter(NetworkEnvironmentKind.Lan);
            lan.Enqueue(new LanEnvironmentLease(true, () => Task.CompletedTask));
            lan.Enqueue(new FakeLease(ready: false));
            lan.Enqueue(new LanEnvironmentLease(true, () => Task.CompletedTask));
            await using var controller = new NetworkEnvironmentController(lan, () => default);
            await controller.ChangeAsync(Join);
            var result = await controller.ChangeAsync(Host);
            Check(result.Outcome == NetworkEnvironmentChangeOutcome.ConnectionFailed && result.Rollback == NetworkEnvironmentRollback.Restored
                && result.Snapshot.Selection == Join && !result.Snapshot.IsReady, "offline rollback misreported");
        });
        await Case("production LAN lease closes once and rejects late capability results", async () =>
        {
            int closes = 0;
            var entered = Gate<bool>(); var finish = Gate<bool>();
            var lease = new LanEnvironmentLease(true, async () => { closes++; entered.SetResult(true); await finish.Task; });
            lease.ReportCapabilities(true, 5);
            Task first = lease.DisposeAsync().AsTask(); await entered.Task;
            lease.ReportCapabilities(true, 12);
            Check(!lease.IsReady && !lease.IsConfigured && lease.LatencyMilliseconds is null, "closing role was republished");
            Task second = lease.DisposeAsync().AsTask(); finish.SetResult(true); await Task.WhenAll(first, second);
            lease.ReportCapabilities(true);
            Check(closes == 1 && !lease.IsReady && !lease.IsConfigured, "lease cleanup duplicated or late observation accepted");
        });
        await Case("production LAN lease retains failed owned cleanup for retry", async () =>
        {
            int closes = 0;
            var lease = new LanEnvironmentLease(false, () => { if (++closes == 1) throw new IOException("synthetic cleanup"); return Task.CompletedTask; });
            lease.ReportCapabilities(true, 2);
            try { await lease.DisposeAsync(); throw new Exception("failure hidden"); } catch (IOException) { }
            Check(!lease.IsReady, "failed close reports readiness");
            await lease.DisposeAsync();
            Check(closes == 2, "failed cleanup ownership lost");
        });
        await Case("production host lease observes owned process exit without waiting for a new probe", async () =>
        {
            bool alive = true;
            await using var lease = new LanEnvironmentLease(false, () => Task.CompletedTask, () => alive);
            lease.ReportCapabilities(true, 5);
            Check(lease.IsReady && lease.LatencyMilliseconds == 5, "live owned host not ready");
            alive = false;
            Check(!lease.IsReady && lease.LatencyMilliseconds is null && !lease.IsConfigured, "exited host still presented as ready");
        });
        Console.WriteLine($"RESULT {_passed} network environment cases passed. No real networking, game or process launch.");
    }
}

sealed class FakeAdapter(NetworkEnvironmentKind environment) : INetworkEnvironmentAdapter
{
    private readonly Queue<Func<NetworkEnvironmentSelection, CancellationToken, Task<INetworkEnvironmentLease>>> _responses = new();
    public NetworkEnvironmentKind Environment { get; } = environment;
    public bool IsAvailable { get; set; } = true;
    public int Calls { get; private set; }
    public List<NetworkEnvironmentSelection> Selections { get; } = new();
    public void Enqueue(INetworkEnvironmentLease lease) => Enqueue((_, _) => Task.FromResult(lease));
    public void Enqueue(Func<NetworkEnvironmentSelection, CancellationToken, Task<INetworkEnvironmentLease>> response) => _responses.Enqueue(response);
    public Task<INetworkEnvironmentLease> ConnectAsync(NetworkEnvironmentSelection selection, CancellationToken cancellationToken)
    {
        Calls++;
        Selections.Add(selection);
        if (selection.Environment != Environment) throw new Exception("misrouted selection");
        return _responses.Count == 0 ? Task.FromResult<INetworkEnvironmentLease>(new FakeLease()) : _responses.Dequeue()(selection, cancellationToken);
    }
}

sealed class FakeLease(bool ready = true, long? latency = null) : INetworkEnvironmentLease
{
    public bool IsReady { get; set; } = ready;
    public long? LatencyMilliseconds { get; set; } = latency;
    public int Disposals { get; private set; }
    public int DisposeFailures { get; set; }
    public ValueTask DisposeAsync()
    {
        Disposals++;
        if (DisposeFailures-- > 0) throw new IOException("synthetic cleanup failure");
        return ValueTask.CompletedTask;
    }
}
