using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace SplinterCellCNLauncher.Services;

public sealed class NetworkHealthCheckService
{
    private readonly Func<string, int, TimeSpan, string, CancellationToken, Task<TcpEndpointProbe>> _probe;
    private readonly Func<string> _getServerAddress;
    private readonly Func<int> _getAuthPort;
    private readonly Func<int> _getConfigPort;
    private readonly Func<int> _getControlPort;

    public NetworkHealthCheckService(
        Func<string>? getServerAddress = null,
        Func<int>? getAuthPort = null,
        Func<int>? getConfigPort = null,
        Func<int>? getControlPort = null)
        : this(
            TryOpenTcpConnectionAsync,
            getServerAddress,
            getAuthPort,
            getConfigPort,
            getControlPort)
    {
    }

    internal NetworkHealthCheckService(
        Func<string, int, TimeSpan, string, CancellationToken, Task<TcpEndpointProbe>> probe,
        Func<string>? getServerAddress = null,
        Func<int>? getAuthPort = null,
        Func<int>? getConfigPort = null,
        Func<int>? getControlPort = null)
    {
        _probe = probe;
        _getServerAddress = getServerAddress ?? (() => PublicTunnelConfig.ServerVirtualIp);
        _getAuthPort = getAuthPort ?? (() => 50051);
        _getConfigPort = getConfigPort ?? (() => 80);
        _getControlPort = getControlPort ?? (() => PublicTunnelConfig.ControlPlanePort);
    }

    public async Task<NetworkReadyResult> QuickCheckAsync(string bindIp, CancellationToken cancellationToken = default)
    {
        if (!IsValidScblClientIp(bindIp))
            return NetworkReadyResult.Failed(NetworkPhase.NetworkFailed, NetworkFailureStage.Network, "Invalid or empty SCBL client IP.");

        string server = _getServerAddress();
        int authPort = _getAuthPort();
        int configPort = _getConfigPort();
        var grpc = await _probe(server, authPort, TimeSpan.FromMilliseconds(650), bindIp, cancellationToken).ConfigureAwait(false);
        if (grpc.Ok)
            return NetworkReadyResult.Success(bindIp, grpc.LatencyMs);

        // gRPC 偶发较慢时，用 config 端口兜底判断隧道到服务端已通。
        var config = await _probe(server, configPort, TimeSpan.FromMilliseconds(650), bindIp, cancellationToken).ConfigureAwait(false);
        if (config.Ok)
            return NetworkReadyResult.Success(bindIp, config.LatencyMs);

        return NetworkReadyResult.Failed(
            NetworkPhase.ServerFailed,
            NetworkFailureStage.Server,
            $"Server quick check failed: {authPort}/gRPC={grpc.Failure}, {configPort}/config={config.Failure}.");
    }

    public async Task<NetworkReadyResult> DetailedCheckAsync(string bindIp, CancellationToken cancellationToken = default)
    {
        if (!IsValidScblClientIp(bindIp))
            return NetworkReadyResult.Failed(NetworkPhase.NetworkFailed, NetworkFailureStage.Network, "Invalid or empty SCBL client IP.");

        string server = _getServerAddress();
        int authPort = _getAuthPort();
        int configPort = _getConfigPort();
        int controlPort = _getControlPort();
        var checks = new[]
        {
            (Name: $"{authPort}/gRPC", Port: authPort, Core: true),
            (Name: $"{configPort}/config", Port: configPort, Core: true),
            (Name: $"{controlPort}/control", Port: controlPort, Core: false),
        };

        var tasks = new List<Task<(string Name, bool Core, TcpEndpointProbe Probe)>>();
        foreach (var check in checks)
        {
            tasks.Add(Task.Run(async () =>
            {
                var result = await _probe(server, check.Port, TimeSpan.FromMilliseconds(700), bindIp, cancellationToken).ConfigureAwait(false);
                return (check.Name, check.Core, Probe: result);
            }, cancellationToken));
        }

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        var failed = new List<string>();
        long? latency = null;
        foreach (var r in results)
        {
            if (!r.Probe.Ok)
                failed.Add($"{r.Name}={r.Probe.Failure}");
            latency ??= r.Probe.LatencyMs;
        }

        // 只要核心 gRPC 或 config 可用，就认为服务器主链路可用，辅助端口失败写日志即可。
        bool coreOk = Array.Exists(results, x => x.Core && x.Probe.Ok);
        if (coreOk)
        {
            if (failed.Count > 0)
                LogService.Error("Detailed network check has auxiliary failures: " + string.Join(", ", failed));
            return NetworkReadyResult.Success(bindIp, latency);
        }

        return NetworkReadyResult.Failed(
            NetworkPhase.ServerFailed,
            NetworkFailureStage.Server,
            "Server detailed check failed. Failed ports: " + string.Join(", ", failed));
    }

    public static bool IsValidScblClientIp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        return PublicTunnelConfig.IsScblClientIp(value);
    }

    private static async Task<TcpEndpointProbe> TryOpenTcpConnectionAsync(string host, int port, TimeSpan timeout, string bindIp, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linkedCts.CancelAfter(timeout);
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };
            if (IPAddress.TryParse(bindIp, out var ip))
                socket.Bind(new IPEndPoint(ip, 0));

            await socket.ConnectAsync(new DnsEndPoint(host, port), linkedCts.Token).ConfigureAwait(false);
            sw.Stop();
            return socket.Connected
                ? new TcpEndpointProbe(true, sw.ElapsedMilliseconds, "ok")
                : new TcpEndpointProbe(false, null, "not-connected");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            sw.Stop();
            return new TcpEndpointProbe(false, null, "timeout");
        }
        catch (SocketException ex)
        {
            sw.Stop();
            return new TcpEndpointProbe(false, null, $"socket-{ex.SocketErrorCode}");
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new TcpEndpointProbe(false, null, ex.GetType().Name);
        }
    }
}

internal sealed record TcpEndpointProbe(bool Ok, long? LatencyMs, string Failure);
