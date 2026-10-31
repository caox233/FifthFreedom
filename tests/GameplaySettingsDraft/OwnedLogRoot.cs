namespace SplinterCellCNLauncher.Services;

// Only the app-data root/log sink is replaced. Tests call the real settings
// service, codec and atomic writer using an explicitly owned temporary folder.
internal static class LogService
{
    internal static string AppDataDir { get; set; } = "";
    internal static void Error(Exception error) => throw new InvalidOperationException("Unexpected fixture load failure", error);
}
