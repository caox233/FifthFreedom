using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace SplinterCellCNLauncher.Services;

internal sealed record EasyTierCommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut);

internal interface IEasyTierCommandRunner
{
    Task<EasyTierCommandResult> RunAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

internal interface IEasyTierCliProcess : IDisposable
{
    int ExitCode { get; }
    Task<string> ReadOutputAsync();
    Task<string> ReadErrorAsync();
    Task WaitForExitAsync(CancellationToken cancellationToken);
    void KillTree();
    bool WaitForExit(int milliseconds);
}

internal interface IEasyTierCliProcessFactory
{
    IEasyTierCliProcess Start(ProcessStartInfo startInfo);
}

internal sealed class SystemEasyTierCliProcessFactory : IEasyTierCliProcessFactory
{
    public IEasyTierCliProcess Start(ProcessStartInfo startInfo)
        => new SystemEasyTierCliProcess(
            Process.Start(startInfo)
            ?? throw new InvalidOperationException("easytier-cli 启动失败。"));
}

internal sealed class SystemEasyTierCliProcess : IEasyTierCliProcess
{
    private readonly Process _process;

    public SystemEasyTierCliProcess(Process process) => _process = process;
    public int ExitCode => _process.ExitCode;
    public Task<string> ReadOutputAsync() => _process.StandardOutput.ReadToEndAsync();
    public Task<string> ReadErrorAsync() => _process.StandardError.ReadToEndAsync();
    public Task WaitForExitAsync(CancellationToken cancellationToken)
        => _process.WaitForExitAsync(cancellationToken);
    public void KillTree() => _process.Kill(entireProcessTree: true);
    public bool WaitForExit(int milliseconds) => _process.WaitForExit(milliseconds);
    public void Dispose() => _process.Dispose();
}

internal sealed class SystemEasyTierCommandRunner : IEasyTierCommandRunner
{
    private readonly IEasyTierCliProcessFactory _factory;

    public SystemEasyTierCommandRunner(IEasyTierCliProcessFactory? factory = null)
        => _factory = factory ?? new SystemEasyTierCliProcessFactory();

    public async Task<EasyTierCommandResult> RunAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using IEasyTierCliProcess process = _factory.Start(startInfo);
        Task<string> outputTask = process.ReadOutputAsync();
        Task<string> errorTask = process.ReadErrorAsync();
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            TerminateBounded(process, "EasyTier CLI cancellation cleanup");
            await ObservePipeCompletionAsync(outputTask, errorTask).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
                cancellationToken.ThrowIfCancellationRequested();
            return new EasyTierCommandResult(-1, string.Empty, string.Empty, true);
        }

        string output = await outputTask.ConfigureAwait(false);
        string error = await errorTask.ConfigureAwait(false);
        return new EasyTierCommandResult(process.ExitCode, output, error, false);
    }

    private static void TerminateBounded(IEasyTierCliProcess process, string operation)
    {
        try
        {
            process.KillTree();
        }
        catch (Exception ex)
        {
            LogService.Info($"{operation}: kill failed ({ex.GetType().Name}).");
        }
        try
        {
            if (!process.WaitForExit(500))
                LogService.Info($"{operation}: process did not exit within 500ms.");
        }
        catch (Exception ex)
        {
            LogService.Info($"{operation}: bounded wait failed ({ex.GetType().Name}).");
        }
    }

    private static async Task ObservePipeCompletionAsync(params Task<string>[] tasks)
    {
        try
        {
            await Task.WhenAll(tasks)
                .WaitAsync(TimeSpan.FromMilliseconds(500))
                .ConfigureAwait(false);
        }
        catch
        {
            // Cleanup never logs pipe content and remains bounded after process termination.
        }
    }
}

internal sealed class EasyTierCliClient
{
    private readonly IEasyTierCommandRunner _runner;

    public EasyTierCliClient(IEasyTierCommandRunner? runner = null)
        => _runner = runner ?? new SystemEasyTierCommandRunner();

    public static string GetCliPath(string launcherBaseDir)
        => ClientInstallLayout.Resolve(launcherBaseDir, ClientInstallLayout.EasyTierCliRelativePath);

    public Task<string> QueryPeerTableAsync(string cliPath, TimeSpan timeout)
        => RunAsync(CreateStartInfo(cliPath, verboseJson: false, "peer"), timeout);

    public Task<string> QueryPeersJsonAsync(string cliPath, TimeSpan timeout)
        => RunAsync(CreateStartInfo(cliPath, verboseJson: true, "peer"), timeout);

    public Task<string> QueryRoutesJsonAsync(string cliPath, TimeSpan timeout)
        => RunAsync(CreateStartInfo(cliPath, verboseJson: true, "route"), timeout);

    public Task<string> QueryNodeJsonAsync(string cliPath, TimeSpan timeout)
        => RunAsync(CreateStartInfo(cliPath, verboseJson: false, "node", json: true), timeout);

    internal static ProcessStartInfo CreateStartInfo(
        string cliPath,
        bool verboseJson,
        string command,
        bool json = false)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = cliPath,
            WorkingDirectory = Path.GetDirectoryName(cliPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add(PublicTunnelConfig.EasyTierRpcEndpoint);
        if (verboseJson)
            startInfo.ArgumentList.Add("-v");
        if (verboseJson || json)
        {
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add("json");
        }
        startInfo.ArgumentList.Add("-n");
        startInfo.ArgumentList.Add(PublicTunnelConfig.EasyTierInstanceName);
        startInfo.ArgumentList.Add(command);
        return startInfo;
    }

    private async Task<string> RunAsync(ProcessStartInfo startInfo, TimeSpan timeout)
    {
        EasyTierCommandResult result = await _runner.RunAsync(startInfo, timeout)
            .ConfigureAwait(false);
        if (result.TimedOut)
            return string.Empty;
        if (result.ExitCode != 0)
        {
            if (!string.IsNullOrWhiteSpace(result.StandardError))
            {
                LogService.Info(
                    "EasyTier CLI query not ready: "
                    + LogService.SanitizeSensitiveText(result.StandardError.Trim()));
            }
            return string.Empty;
        }
        return result.StandardOutput;
    }
}

internal static class EasyTierCliParsers
{
    public static IReadOnlyList<string> ParseVirtualPeerIps(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return Array.Empty<string>();

        var ips = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using JsonDocument document = JsonDocument.Parse(output);
            Collect(document.RootElement);
        }
        catch
        {
            return Array.Empty<string>();
        }

        return ips
            .Where(PublicTunnelConfig.IsScblClientIp)
            .OrderBy(ip => int.TryParse(
                ip[(ip.LastIndexOf('.') + 1)..],
                out int octet) ? octet : int.MaxValue)
            .ToArray();

        void Collect(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement child in element.EnumerateArray())
                    Collect(child);
                return;
            }
            if (element.ValueKind != JsonValueKind.Object)
                return;

            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (property.NameEquals("ipv4")
                    || property.NameEquals("cidr")
                    || property.NameEquals("ipv4_addr")
                    || property.NameEquals("ipv4Addr"))
                {
                    string value = ReadIpv4Value(property.Value);
                    if (PublicTunnelConfig.IsScblClientIp(value))
                        ips.Add(value);
                }
                Collect(property.Value);
            }
        }
    }

    public static EasyTierPeerPath? ParsePeerPath(string output, string peerIp)
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(output);
            // SCBL 3.0 ships one controlled EasyTier schema. Only the verbose
            // route+peer view exposes default_conn_id, which is required to
            // identify the connection EasyTier actually selected.
            return FindVerbosePeer(document.RootElement);
        }
        catch
        {
            return null;
        }

        EasyTierPeerPath? FindVerbosePeer(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in element.EnumerateArray())
                {
                    EasyTierPeerPath? found = FindVerbosePeer(item);
                    if (found != null)
                        return found;
                }
                return null;
            }
            if (element.ValueKind != JsonValueKind.Object)
                return null;

            if (element.TryGetProperty("route", out JsonElement route)
                && route.ValueKind == JsonValueKind.Object
                && element.TryGetProperty("peer", out JsonElement peer)
                && peer.ValueKind == JsonValueKind.Object)
            {
                string routeIp = ReadIpv4Inet(route, "ipv4_addr");
                if (routeIp.Equals(peerIp, StringComparison.OrdinalIgnoreCase))
                {
                    int routeCost = ReadInt(route, "cost") ?? 0;
                    int? peerId = ReadInt(route, "peer_id");
                    int? nextHop = ReadInt(route, "next_hop_peer_id_latency_first")
                        ?? ReadInt(route, "next_hop_peer_id");
                    bool relayed = routeCost > 1
                        || (peerId.HasValue
                            && nextHop.HasValue
                            && peerId.Value != nextHop.Value);

                    string defaultConnectionId = ReadConnectionId(peer, "default_conn_id");
                    bool hasDefaultConnection = !string.IsNullOrWhiteSpace(defaultConnectionId);
                    JsonElement? selectedConnection = null;
                    if (peer.TryGetProperty("conns", out JsonElement connections)
                        && connections.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement connection in connections.EnumerateArray())
                        {
                            if (connection.ValueKind != JsonValueKind.Object)
                                continue;
                            string connectionId = ReadConnectionId(connection, "conn_id");
                            if (hasDefaultConnection
                                && connectionId.Equals(
                                    defaultConnectionId,
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                selectedConnection = connection;
                                break;
                            }
                        }
                    }

                    // Missing, malformed, or unmatched default_conn_id is not
                    // evidence that another connection is active. Returning no
                    // path keeps Connected fail-closed instead of relabeling a
                    // lower-latency IPv4/IPv6 candidate as the chosen route.
                    if (!hasDefaultConnection || selectedConnection is null)
                        return null;

                    string tunnelType = string.Empty;
                    string remoteAddress = string.Empty;
                    long? directLatencyMilliseconds = null;
                    if (selectedConnection is JsonElement connectionElement)
                    {
                        if (connectionElement.TryGetProperty("tunnel", out JsonElement tunnel)
                            && tunnel.ValueKind == JsonValueKind.Object)
                        {
                            tunnelType = GetString(tunnel, "tunnel_type");
                            remoteAddress = ReadUrlLike(
                                tunnel,
                                "resolved_remote_addr",
                                "remote_addr");
                        }
                        long? latencyMicroseconds = ReadLong(
                            connectionElement,
                            "stats",
                            "latency_us");
                        if (latencyMicroseconds.HasValue)
                        {
                            directLatencyMilliseconds = Math.Max(
                                0,
                                (long)Math.Round(latencyMicroseconds.Value / 1000d));
                        }
                    }
                    int? routeLatency = ReadInt(route, "path_latency_latency_first")
                        ?? ReadInt(route, "path_latency");
                    long? latencyMilliseconds = relayed && routeLatency.HasValue
                        ? Math.Max(0, routeLatency.Value)
                        : directLatencyMilliseconds
                            ?? (routeLatency.HasValue ? Math.Max(0, routeLatency.Value) : null);

                    string mode = ClassifyPeerTransport(
                        relayed ? "relay" : routeCost.ToString(),
                        tunnelType);
                    string addressFamily = relayed
                        ? string.Empty
                        : DetectAddressFamily(remoteAddress);
                    if (relayed)
                        remoteAddress = string.Empty;
                    return new EasyTierPeerPath(
                        peerIp,
                        latencyMilliseconds,
                        mode,
                        relayed ? $"relay({routeCost})" : routeCost.ToString(),
                        nextHop?.ToString() ?? string.Empty,
                        routeCost > 0 ? routeCost : null,
                        addressFamily,
                        remoteAddress);
                }
            }

            foreach (JsonProperty property in element.EnumerateObject())
            {
                EasyTierPeerPath? found = FindVerbosePeer(property.Value);
                if (found != null)
                    return found;
            }
            return null;
        }

    }

    private static string ReadIpv4Value(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
            return (value.GetString() ?? string.Empty).Split('/')[0].Trim();
        if (value.ValueKind != JsonValueKind.Object)
            return string.Empty;
        if (value.TryGetProperty("address", out JsonElement address))
        {
            string nested = ReadIpv4Value(address);
            if (!string.IsNullOrWhiteSpace(nested))
                return nested;
        }
        if (value.TryGetProperty("addr", out JsonElement numeric)
            && numeric.TryGetUInt32(out uint raw))
        {
            return $"{(raw >> 24) & 0xff}.{(raw >> 16) & 0xff}.{(raw >> 8) & 0xff}.{raw & 0xff}";
        }
        foreach (string name in new[] { "value", "ip", "ipv4" })
        {
            if (value.TryGetProperty(name, out JsonElement text)
                && text.ValueKind == JsonValueKind.String)
            {
                return (text.GetString() ?? string.Empty).Split('/')[0].Trim();
            }
        }
        return string.Empty;
    }

    private static string ReadUrlLike(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (!element.TryGetProperty(name, out JsonElement value))
                continue;
            string endpoint = ReadEndpointValue(value, depth: 0);
            if (!string.IsNullOrWhiteSpace(endpoint))
                return endpoint;
        }
        return string.Empty;
    }

    private static string ReadEndpointValue(JsonElement value, int depth)
    {
        if (depth > 4)
            return string.Empty;
        if (value.ValueKind == JsonValueKind.String)
            return value.GetString()?.Trim() ?? string.Empty;
        if (value.ValueKind != JsonValueKind.Object)
            return string.Empty;

        foreach (string name in new[]
        {
            "url",
            "value",
            "addr",
            "address",
            "ip",
            "Ip",
            "socket_addr",
            "SocketAddr",
            "resolved_addr",
        })
        {
            if (!value.TryGetProperty(name, out JsonElement nested))
                continue;
            string endpoint = ReadEndpointValue(nested, depth + 1);
            if (!string.IsNullOrWhiteSpace(endpoint))
                return endpoint;
        }

        // A structured endpoint that is not understood must fail closed. Returning
        // its raw JSON would make punctuation such as multiple ':' characters look
        // like an IPv6 socket address even when the connection is IPv4-only.
        return string.Empty;
    }

    private static string DetectAddressFamily(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return string.Empty;

        string value = address.Trim();
        string host;
        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? endpointUri)
            && !string.IsNullOrWhiteSpace(endpointUri.Host))
        {
            host = endpointUri.Host;
        }
        else if (value.StartsWith("[", StringComparison.Ordinal))
        {
            int closingBracket = value.IndexOf(']');
            if (closingBracket <= 1)
                return string.Empty;
            host = value[1..closingBracket];
        }
        else
        {
            int firstColon = value.IndexOf(':');
            int lastColon = value.LastIndexOf(':');
            host = firstColon > 0 && firstColon == lastColon
                ? value[..firstColon]
                : value;
        }

        host = host.Trim().Trim('[', ']');
        if (!IPAddress.TryParse(host, out IPAddress? ipAddress))
            return string.Empty;
        if (ipAddress.IsIPv4MappedToIPv6)
            return "IPv4";
        return ipAddress.AddressFamily switch
        {
            AddressFamily.InterNetwork => "IPv4",
            AddressFamily.InterNetworkV6 => "IPv6",
            _ => string.Empty,
        };
    }

    private static string ReadIpv4Inet(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value))
            return string.Empty;
        if (value.ValueKind == JsonValueKind.String)
            return (value.GetString() ?? string.Empty).Split('/')[0];
        if (value.ValueKind != JsonValueKind.Object)
            return string.Empty;
        if (value.TryGetProperty("address", out JsonElement address))
        {
            if (address.ValueKind == JsonValueKind.String)
                return address.GetString() ?? string.Empty;
            if (address.ValueKind == JsonValueKind.Object
                && address.TryGetProperty("addr", out JsonElement numeric)
                && numeric.TryGetUInt32(out uint raw))
            {
                return $"{(raw >> 24) & 0xff}.{(raw >> 16) & 0xff}.{(raw >> 8) & 0xff}.{raw & 0xff}";
            }
        }
        return string.Empty;
    }

    private static string GetString(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (!element.TryGetProperty(name, out JsonElement value))
                continue;
            if (value.ValueKind == JsonValueKind.String)
                return value.GetString() ?? string.Empty;
            if (value.ValueKind == JsonValueKind.Number
                || value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return value.GetRawText();
            }
        }
        return string.Empty;
    }

    private static string ReadConnectionId(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
            return string.Empty;
        if (value.ValueKind == JsonValueKind.String)
        {
            string raw = value.GetString()?.Trim() ?? string.Empty;
            return Guid.TryParse(raw, out Guid parsed) && parsed != Guid.Empty
                ? parsed.ToString("D")
                : raw;
        }
        if (value.ValueKind != JsonValueKind.Object
            || !TryReadUuidPart(value, "part1", out uint part1)
            || !TryReadUuidPart(value, "part2", out uint part2)
            || !TryReadUuidPart(value, "part3", out uint part3)
            || !TryReadUuidPart(value, "part4", out uint part4))
        {
            return string.Empty;
        }

        string compact = $"{part1:x8}{part2:x8}{part3:x8}{part4:x8}";
        return Guid.TryParseExact(compact, "N", out Guid objectId)
            && objectId != Guid.Empty
            ? objectId.ToString("D")
            : string.Empty;
    }

    private static bool TryReadUuidPart(
        JsonElement element,
        string name,
        out uint value)
    {
        value = 0;
        return element.TryGetProperty(name, out JsonElement part)
            && part.ValueKind == JsonValueKind.Number
            && part.TryGetUInt32(out value);
    }

    private static int? ReadInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number))
            return number;
        return int.TryParse(value.ToString(), out number) ? number : null;
    }

    private static long? ReadLong(JsonElement element, string objectName, string valueName)
    {
        if (!element.TryGetProperty(objectName, out JsonElement child)
            || child.ValueKind != JsonValueKind.Object
            || !child.TryGetProperty(valueName, out JsonElement value))
        {
            return null;
        }
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number))
            return number;
        return long.TryParse(value.ToString(), out number) ? number : null;
    }

    private static string ClassifyPeerTransport(string cost, string tunnel)
    {
        string normalizedCost = (cost ?? string.Empty).Trim().ToLowerInvariant();
        string normalizedTunnel = (tunnel ?? string.Empty).Trim().ToLowerInvariant();
        bool relayed = normalizedCost.Contains("relay")
            || normalizedTunnel.Contains("relay")
            || (int.TryParse(normalizedCost, out int numericCost) && numericCost > 1);
        string baseMode = normalizedTunnel.Contains("quic") ? "QUIC"
            : normalizedTunnel.Contains("wss") ? "WSS"
            : normalizedTunnel.Contains("ws") ? "WS"
            : normalizedTunnel.Split(
                    ',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(value => value.StartsWith("udp", StringComparison.OrdinalIgnoreCase)) ? "UDP"
            : normalizedTunnel.Contains("tcp") ? "TCP"
            : string.Empty;
        if (relayed)
            return string.IsNullOrWhiteSpace(baseMode) ? "多跳中继" : $"多跳-{baseMode}";
        return baseMode;
    }
}
