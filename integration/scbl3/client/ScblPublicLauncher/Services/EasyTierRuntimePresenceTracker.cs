namespace SplinterCellCNLauncher.Services;

/// <summary>
/// Debounces one transient negative ownership probe for read-only game continuity
/// monitoring. Lifecycle mutation and startup reuse must continue to use the
/// definitive process probe directly.
/// </summary>
internal sealed class EasyTierRuntimePresenceTracker
{
    internal static readonly TimeSpan DefaultNegativeGrace = TimeSpan.FromSeconds(5);

    private readonly object _sync = new();
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeSpan _negativeGrace;
    private DateTimeOffset? _lastPositiveUtc;

    public EasyTierRuntimePresenceTracker(
        Func<DateTimeOffset>? utcNow = null,
        TimeSpan? negativeGrace = null)
    {
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _negativeGrace = negativeGrace ?? DefaultNegativeGrace;
        if (_negativeGrace < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(negativeGrace));
    }

    public bool Observe(bool runtimePresent)
    {
        DateTimeOffset now = _utcNow();
        lock (_sync)
        {
            if (runtimePresent)
            {
                _lastPositiveUtc = now;
                return true;
            }

            if (!_lastPositiveUtc.HasValue)
                return false;
            TimeSpan age = now - _lastPositiveUtc.Value;
            return age >= TimeSpan.Zero && age <= _negativeGrace;
        }
    }

    public void Reset()
    {
        lock (_sync)
            _lastPositiveUtc = null;
    }
}
