using System.Net;
using System.Net.Sockets;
using ScblLan;

namespace SplinterCellCNLauncher.Services;

/// <summary>
/// Historical endpoints read from an actual connected API socket. This is not a
/// lease, interface ownership, route continuity or encrypted-overlay proof.
/// </summary>
internal sealed class ApiTransportObservation
{
    internal Guid ConnectionObservationId { get; } = Guid.NewGuid();
    internal DateTimeOffset ObservedUtc { get; } = DateTimeOffset.UtcNow;
    internal string LocalAddress { get; }
    internal int LocalPort { get; }
    internal string RemoteAddress { get; }
    internal int RemotePort { get; }
    private ApiTransportObservation(IPEndPoint local, IPEndPoint remote)
    { LocalAddress = local.Address.ToString(); LocalPort = local.Port; RemoteAddress = remote.Address.ToString(); RemotePort = remote.Port; }
    internal static ApiTransportObservation? TryCapture(Socket connected)
    {
        try
        {
            return connected.LocalEndPoint is IPEndPoint local && connected.RemoteEndPoint is IPEndPoint remote
                && local.Port > 0 && remote.Port > 0 ? new(local, remote) : null;
        }
        catch (Exception error) when (error is SocketException or ObjectDisposedException) { return null; }
    }
    public override string ToString() => "ApiTransportObservation (historical; not runtime admission)";
}

/// <summary>Actual final login response and its verified package; contains no password or token.</summary>
internal sealed class FinalLoginObservation
{
    internal string AccountId { get; }
    internal string ReturnedUsername { get; }
    internal ReleasePackage Package { get; }
    internal ApiTransportObservation? Transport { get; }
    internal FinalLoginObservation(string accountId, string returnedUsername, ReleasePackage package, ApiTransportObservation? transport)
    { AccountId = accountId; ReturnedUsername = returnedUsername; Package = package; Transport = transport; }
    public override string ToString() => "FinalLoginObservation (captured response; not Public readiness)";
}

// Exactly one preparation owns the pending pipe. Transfer occurs only after the
// existing launch service confirms adoption; all earlier failures dispose it.
internal sealed class PreparedGameConfiguration : IAsyncDisposable
{
    private LaunchPipe? pipe;
    internal FinalLoginObservation FinalLogin { get; }
    internal LaunchPipe Pipe => Volatile.Read(ref pipe) ?? throw new ObjectDisposedException(nameof(PreparedGameConfiguration));
    internal PreparedGameConfiguration(LaunchPipe pipe, FinalLoginObservation finalLogin)
    { this.pipe = pipe; FinalLogin = finalLogin; }
    internal LaunchPipe TransferOwnership() => Interlocked.Exchange(ref pipe, null) ?? throw new ObjectDisposedException(nameof(PreparedGameConfiguration));
    public async ValueTask DisposeAsync()
    {
        var owned = Interlocked.Exchange(ref pipe, null);
        if (owned != null) await owned.DisposeAsync().ConfigureAwait(false);
    }
    public override string ToString() => "PreparedGameConfiguration (private per-attempt owner)";
}
