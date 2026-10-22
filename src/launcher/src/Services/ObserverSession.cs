namespace SplinterCellCNLauncher.Services;

// Window/session state, deliberately absent from LauncherSettings and save data.
internal sealed class ObserverSession
{
    internal bool Requested { get; set; }
    internal bool HostAllows(bool isHost) => isHost && Requested;
    internal bool JoinAsObserver(bool isHost) => !isHost && Requested;
    internal void ChangeRole() => Requested = false;
    internal void Launched(bool isHost) { if (!isHost) Requested = false; }
    internal static void ApplyLaunchEnvironment(IDictionary<string,string?> environment,bool requested)
    {
        // Never inherit another process's opt-in or persist this in settings.
        environment.Remove("SCBL_OBSERVER");
        if(requested) environment["SCBL_OBSERVER"]="1";
    }
    internal static void ValidatePermission(bool requested, bool hostAllows)
    {
        if (requested && !hostAllows) throw new InvalidOperationException("房主未开启 OB 模式。");
    }
    // Native availability is checked by GameplayRuntime for this child process:
    // matching x86 DLL/export before launch, signed engine contracts on loading.
}
