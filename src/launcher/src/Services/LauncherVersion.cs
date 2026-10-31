namespace SplinterCellCNLauncher.Services;

// Use the published assembly version for both initial XAML and language refresh.
public static class LauncherVersion
{
    public static string Display => typeof(LauncherVersion).Assembly.GetName().Version?.ToString(2) ?? "unknown";
    public static string Badge => $"{Display} · DX11";
}
