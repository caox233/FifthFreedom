using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace SplinterCellCNLauncher.Services;

public enum LocalHostDiscoveryStatus { Completed, TimedOut, NetworkError, Cancelled, Superseded, InvalidEndpoint }

public sealed record LocalDiscoveredHost(string HostIp, int ApiPort, string HostName, string SessionId)
{
    public string Endpoint => $"{HostIp}:{ApiPort}";
}

public sealed record LocalHostDiscoveryScan(LocalHostDiscoveryStatus Status, IReadOnlyList<LocalDiscoveredHost> Hosts);

public interface ILocalHostDiscoveryTransport
{
    Task<LocalHostDiscoveryScan> SearchAsync(string bindIp, TimeSpan observationWindow, CancellationToken cancellationToken);
}

/// <summary>The existing V1 discovery wire format. This is a hint, not server authentication.</summary>
public static class LocalHostDiscoveryProtocol
{
    public const int Port = 47777;
    public const int MaxPacketBytes = 4096;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static bool TryAddress(string? text, out IPAddress address)
    {
        address = IPAddress.None;
        if (text == null || !IPAddress.TryParse(text.Trim(), out var parsed)
            || parsed.AddressFamily != AddressFamily.InterNetwork
            || parsed.GetAddressBytes()[0] is 0 or >= 224 || parsed.ToString() != text.Trim()) return false;
        address = parsed;
        return true;
    }

    public static byte[] Query(IPAddress bindAddress) => Utf8.GetBytes(string.Join("\n", new[]
    {
        "SCBL_LAN_HOST_QUERY", "Version=1", $"ClientIP={bindAddress}",
        $"RequestedAt={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}"
    }));

    public static LocalDiscoveredHost? Parse(ReadOnlySpan<byte> packet, IPEndPoint remote)
    {
        if (packet.Length is 0 or > MaxPacketBytes || remote.Port is < 1 or > 65535) return null;
        string text;
        try { text = Utf8.GetString(packet); }
        catch (DecoderFallbackException) { return null; }
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0 || !lines[0].Trim().Equals("SCBL_LAN_HOST", StringComparison.OrdinalIgnoreCase)) return null;
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1))
        {
            int split = line.IndexOf('=');
            if (split <= 0) continue;
            if (!fields.TryAdd(line[..split].Trim(), line[(split + 1)..].Trim())) return null;
        }
        if (!fields.TryGetValue("Version", out var version) || version != "1"
            || !fields.TryGetValue("State", out var state) || state != "ServerReady"
            || !fields.TryGetValue("HostIP", out var hostIp) || !TryAddress(hostIp, out var address)
            || !address.Equals(remote.Address)
            || !fields.TryGetValue("ApiPort", out var portText)
            || !int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out int port)
            || port is < 1 or > 65535) return null;
        fields.TryGetValue("HostName", out var name);
        fields.TryGetValue("SessionId", out var session);
        name = string.IsNullOrWhiteSpace(name) ? "Host" : name;
        session = string.IsNullOrWhiteSpace(session) ? address.ToString() : session;
        if (!SafeLabel(name) || !SafeLabel(session)) return null;
        return new(address.ToString(), port, name, session);
    }

    private static bool SafeLabel(string value) => value.Length is > 0 and <= 64
        && !value.Any(c => char.IsControl(c) || c is '\u202a' or '\u202b' or '\u202c' or '\u202d' or '\u202e'
            or '\u2066' or '\u2067' or '\u2068' or '\u2069');
}

/// <summary>One query from the selected interface, followed by a finite observation window.</summary>
public sealed class UdpLocalHostDiscoveryTransport : ILocalHostDiscoveryTransport
{
    private readonly Func<string, IReadOnlyList<IPEndPoint>> targets;
    public UdpLocalHostDiscoveryTransport() : this(ip => HostDiscoveryTargets.GetTargets(ip, LocalHostDiscoveryProtocol.Port)) { }
    internal UdpLocalHostDiscoveryTransport(Func<string, IReadOnlyList<IPEndPoint>> targets) => this.targets = targets;

    public async Task<LocalHostDiscoveryScan> SearchAsync(string bindIp, TimeSpan observationWindow, CancellationToken cancellationToken)
    {
        if (!LocalHostDiscoveryProtocol.TryAddress(bindIp, out var local)) return new(LocalHostDiscoveryStatus.InvalidEndpoint, []);
        if (observationWindow <= TimeSpan.Zero || observationWindow > TimeSpan.FromSeconds(10)) throw new ArgumentOutOfRangeException(nameof(observationWindow));
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.EnableBroadcast = true;
            // Do not share the long-lived 47777 listener or its cached results.
            // Existing hosts reply to the query's source port, including this
            // newly owned ephemeral port; no new protocol or fixed port is added.
            udp.Client.Bind(new IPEndPoint(local, 0));
            using var abort = cancellationToken.Register(udp.Dispose);
            var endpoints = targets(local.ToString());
            if (endpoints.Count == 0) return new(LocalHostDiscoveryStatus.NetworkError, []);
            byte[] query = LocalHostDiscoveryProtocol.Query(local);
            foreach (var endpoint in endpoints)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await udp.SendAsync(query, endpoint, cancellationToken).ConfigureAwait(false);
            }
            using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            window.CancelAfter(observationWindow);
            var hosts = new Dictionary<string, LocalDiscoveredHost>(StringComparer.Ordinal);
            while (true)
            {
                UdpReceiveResult packet;
                try { packet = await udp.ReceiveAsync(window.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && window.IsCancellationRequested)
                {
                    // This is normal completion of the observation interval,
                    // not a socket timeout or a claim that no host exists.
                    return new(LocalHostDiscoveryStatus.Completed, hosts.Values.OrderBy(h => h.Endpoint, StringComparer.Ordinal).ToArray());
                }
                var host = LocalHostDiscoveryProtocol.Parse(packet.Buffer, packet.RemoteEndPoint);
                if (host == null) continue;
                if (hosts.Count >= 64 && !hosts.ContainsKey(host.Endpoint)) continue;
                hosts[host.Endpoint] = host;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
        catch (SocketException) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
        catch (SocketException error) when (error.SocketErrorCode == SocketError.TimedOut) { return new(LocalHostDiscoveryStatus.TimedOut, []); }
        catch (Exception error) when (error is SocketException or NetworkInformationException or IOException)
        {
            // A failed send/listen, even after a partial result, cannot become
            // a successful empty scan. No private endpoint/error text is logged.
            return new(LocalHostDiscoveryStatus.NetworkError, []);
        }
    }
}
