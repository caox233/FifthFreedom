using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace SplinterCellCNLauncher.Services;

internal static class DualStackHttpClient
{
    private static readonly TimeSpan AddressConnectTimeout = TimeSpan.FromSeconds(2);

    internal static HttpClient Create(string userAgent, TimeSpan requestTimeout)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(4),
            ConnectCallback = static (context, cancellationToken) =>
                ConnectPreferredAddressAsync(context.DnsEndPoint, cancellationToken)
        };
        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = requestTimeout
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        return client;
    }

    internal static async ValueTask<Stream> ConnectPreferredAddressAsync(
        DnsEndPoint endpoint,
        CancellationToken cancellationToken,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolver = null,
        Func<IPAddress, int, CancellationToken, ValueTask<Stream>>? connector = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        resolver ??= static (host, token) => Dns.GetHostAddressesAsync(host, token);
        connector ??= ConnectSocketAsync;

        IPAddress[] resolved = IPAddress.TryParse(endpoint.Host, out IPAddress? literal)
            ? [literal]
            : await resolver(endpoint.Host, cancellationToken).ConfigureAwait(false);
        IPAddress[] addresses = resolved
            .Where(static address => address.AddressFamily is
                AddressFamily.InterNetworkV6 or AddressFamily.InterNetwork)
            .Distinct()
            .OrderBy(static address =>
                address.AddressFamily == AddressFamily.InterNetworkV6 ? 0 : 1)
            .ToArray();
        if (addresses.Length == 0)
            throw new HttpRequestException($"域名没有可用的 IPv6 或 IPv4 地址：{endpoint.Host}");

        Exception? lastError = null;
        foreach (IPAddress address in addresses)
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt.CancelAfter(AddressConnectTimeout);
            try
            {
                return await connector(address, endpoint.Port, attempt.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                lastError = ex;
            }
            catch (SocketException ex)
            {
                lastError = ex;
            }
            catch (IOException ex)
            {
                lastError = ex;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new HttpRequestException(
            $"无法通过 IPv6 或 IPv4 连接服务器：{endpoint.Host}:{endpoint.Port}",
            lastError);
    }

    private static async ValueTask<Stream> ConnectSocketAsync(
        IPAddress address,
        int port,
        CancellationToken cancellationToken)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true
        };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken)
                .ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
