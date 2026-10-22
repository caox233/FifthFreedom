using System.Text;
using System.Text.RegularExpressions;

namespace SplinterCellCNLauncher.Services;

public sealed class ControlPlaneSigningSecretProvider
{
    private readonly object _sync = new();
    private string _cachedSecret = "";
    private DateTime _cachedWriteUtc = DateTime.MinValue;
    private int _mismatchLogged;

    public string GetActiveSecret(string configuredSecret)
    {
        string runtimePath = Path.Combine(
            LogService.PersistentDataDirectory,
            "network",
            "scbl-easytier-client.toml");
        try
        {
            if (!File.Exists(runtimePath))
                return configuredSecret;

            DateTime writeUtc = File.GetLastWriteTimeUtc(runtimePath);
            lock (_sync)
            {
                if (_cachedWriteUtc == writeUtc && !string.IsNullOrWhiteSpace(_cachedSecret))
                    return _cachedSecret;

                string toml = File.ReadAllText(runtimePath, Encoding.UTF8);
                Match match = Regex.Match(
                    toml,
                    @"(?im)^\s*network_secret\s*=\s*""(?<value>[^""]*)""\s*$",
                    RegexOptions.CultureInvariant);
                string runtimeSecret = match.Success ? match.Groups["value"].Value.Trim() : "";
                if (string.IsNullOrWhiteSpace(runtimeSecret))
                    return configuredSecret;

                _cachedSecret = runtimeSecret;
                _cachedWriteUtc = writeUtc;
                if (!runtimeSecret.Equals(configuredSecret, StringComparison.Ordinal)
                    && Interlocked.Exchange(ref _mismatchLogged, 1) == 0)
                {
                    LogService.Warning(
                        "The saved tunnel secret differs from the active EasyTier runtime secret; " +
                        "control-plane signing will follow the active runtime without logging either value.");
                }
                return runtimeSecret;
            }
        }
        catch (Exception ex)
        {
            LogService.Info(
                "Unable to read the active EasyTier secret for control-plane signing; using protected settings: " +
                ex.Message);
            return configuredSecret;
        }
    }
}
