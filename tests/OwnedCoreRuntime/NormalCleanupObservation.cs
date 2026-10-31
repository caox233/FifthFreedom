// Test harness ordering, not a production capability or a kernel ownership API.
internal readonly record struct CleanupSample(bool CoreAliveBefore, bool CoreAliveAfter,
    bool OwnedPresent, bool OwnedAbsent, bool ForeignEqual);
internal sealed class RuntimeCheckFailure(string code) : Exception { internal string Code { get; } = code; }
internal static class NormalCleanupObservation
{
    private static void Require(bool value,string code) { if(!value) throw new RuntimeCheckFailure(code); }
    private static void RequireLiveStable(CleanupSample sample)
    {
        Require(sample.CoreAliveBefore && sample.CoreAliveAfter,"CORE_EXITED_BEFORE_NIC_ABSENT");
        Require(sample.ForeignEqual,"FOREIGN_KERNEL_CHANGED");
    }
    internal static async Task Run(Func<CleanupSample> read, Action endReads, CancellationToken token, Func<long>? clock = null)
    {
        clock ??= () => Environment.TickCount64;
        long deadline=checked(clock()+8000);
        token.ThrowIfCancellationRequested();
        var armed=read(); token.ThrowIfCancellationRequested();
        Require(clock()<deadline,"NIC_CLEANUP_OBSERVATION_TIMEOUT"); RequireLiveStable(armed);
        // Absence observed before the actual trigger is not evidence of its
        // cleanup. Read again only after the permanent pipe close was invoked.
        Require(armed.OwnedPresent && !armed.OwnedAbsent,"NIC_ABSENT_BEFORE_END_READS");
        token.ThrowIfCancellationRequested(); endReads();
        while(true)
        {
            token.ThrowIfCancellationRequested(); var current=read(); token.ThrowIfCancellationRequested();
            Require(clock()<deadline,"NIC_CLEANUP_OBSERVATION_TIMEOUT"); RequireLiveStable(current);
            if(current.OwnedAbsent) return;
            await Task.Delay(20,token).ConfigureAwait(false);
        }
    }
}
