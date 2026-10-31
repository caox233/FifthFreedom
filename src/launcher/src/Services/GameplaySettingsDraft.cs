using SplinterCellCNLauncher.Models;

namespace SplinterCellCNLauncher.Services;

/// <summary>An uncommitted pair of gameplay preferences; it owns no game or network resources.</summary>
public sealed class GameplaySettingsDraft
{
    private readonly (string Mode, double Fov) original;
    public string Mode { get; private set; }
    public double Fov { get; private set; }
    public bool IsActive { get; private set; } = true;
    public bool IsDirty => Mode != original.Mode || Fov != original.Fov;

    public GameplaySettingsDraft(LauncherSettings committed)
    {
        original = Values(committed);
        Mode = original.Mode;
        Fov = original.Fov;
    }

    public void Update(string? mode, double fov)
    {
        EnsureActive();
        // Use the existing persisted normalization contract, including its
        // unchanged Official/MOD names and 1.0–1.3 range/default 1.2.
        var value = Values(new LauncherSettings { GameplayMode = mode ?? "", FovScale = fov });
        Mode = value.Mode;
        Fov = value.Fov;
    }

    public void Cancel()
    {
        Mode = original.Mode;
        Fov = original.Fov;
        IsActive = false;
    }

    public void Commit(LauncherSettings current, Action<LauncherSettings> persist)
    {
        ArgumentNullException.ThrowIfNull(persist);
        CommitCore(current, persist);
    }

    // Preview callers explicitly choose the in-memory operation. No persistence
    // delegate is accepted, so a preview save cannot accidentally invoke it.
    public void CommitPreview(LauncherSettings current) => CommitCore(current, null);

    private void CommitCore(LauncherSettings current, Action<LauncherSettings>? persist)
    {
        EnsureActive();
        if (Values(current) != original) throw new InvalidOperationException("GAMEPLAY_SETTINGS_CHANGED");
        // Capture unrelated settings at confirmation time, not when the page
        // opened. Never mutate the live object before the atomic writer returns.
        var candidate = LauncherSettingsCodec.Deserialize(LauncherSettingsCodec.Serialize(current));
        candidate.GameplayMode = Mode;
        candidate.FovScale = Fov;
        persist?.Invoke(candidate);
        current.GameplayMode = Mode;
        current.FovScale = Fov;
        IsActive = false;
    }

    private static (string Mode, double Fov) Values(LauncherSettings settings)
    {
        var normalized = LauncherSettingsCodec.Deserialize(LauncherSettingsCodec.Serialize(settings));
        return (normalized.GameplayMode, normalized.FovScale);
    }

    private void EnsureActive()
    {
        if (!IsActive) throw new InvalidOperationException("GAMEPLAY_DRAFT_CLOSED");
    }
}
