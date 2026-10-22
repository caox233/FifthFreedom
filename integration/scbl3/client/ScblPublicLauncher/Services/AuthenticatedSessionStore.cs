using System.Net;

namespace SplinterCellCNLauncher.Services;

public sealed record AuthenticatedSession(
    string AccountId,
    string CanonicalUsername,
    string SessionToken,
    string BindIp);

/// <summary>
/// Holds the current authenticated session in process memory only. Tokens are
/// never persisted and are returned only for the overlay IP used at login.
/// </summary>
public sealed class AuthenticatedSessionStore
{
    private readonly object _sync = new();
    private AuthenticatedSession? _session;

    public static AuthenticatedSessionStore Shared { get; } = new();

    public bool TryUpdate(
        string accountId,
        string canonicalUsername,
        string sessionToken,
        string bindIp)
    {
        if (string.IsNullOrWhiteSpace(accountId)
            || string.IsNullOrWhiteSpace(canonicalUsername)
            || string.IsNullOrWhiteSpace(sessionToken)
            || sessionToken.Length > 4096
            || !TryNormalizeBindIp(bindIp, out string normalizedBindIp))
        {
            Clear();
            return false;
        }

        var session = new AuthenticatedSession(
            accountId.Trim(),
            canonicalUsername.Trim(),
            sessionToken,
            normalizedBindIp);
        lock (_sync)
            _session = session;
        return true;
    }

    public bool TryGetForBindIp(string bindIp, out AuthenticatedSession session)
    {
        session = null!;
        if (!TryNormalizeBindIp(bindIp, out string normalizedBindIp))
            return false;
        lock (_sync)
        {
            if (_session is null
                || !_session.BindIp.Equals(normalizedBindIp, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            session = _session;
            return true;
        }
    }

    public bool TryGetCurrentBindIp(out string bindIp)
    {
        lock (_sync)
        {
            bindIp = _session?.BindIp ?? string.Empty;
            return !string.IsNullOrWhiteSpace(bindIp);
        }
    }

    public void Clear()
    {
        lock (_sync)
            _session = null;
    }

    private static bool TryNormalizeBindIp(string bindIp, out string normalizedBindIp)
    {
        normalizedBindIp = "";
        if (!IPAddress.TryParse(bindIp, out IPAddress? address)
            || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
            || !PublicTunnelConfig.IsScblClientIp(address.ToString()))
        {
            return false;
        }
        normalizedBindIp = address.ToString();
        return true;
    }
}
