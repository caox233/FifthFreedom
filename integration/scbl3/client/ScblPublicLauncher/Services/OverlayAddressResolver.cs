using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SplinterCellCNLauncher.Services;

internal sealed record EasyTierAdapterSnapshot(
    string Name,
    IReadOnlyList<string> Ipv4Addresses);

internal interface IEasyTierNetworkSnapshotProvider
{
    IReadOnlyList<EasyTierAdapterSnapshot> Capture();
}

internal sealed class SystemEasyTierNetworkSnapshotProvider : IEasyTierNetworkSnapshotProvider
{
    public IReadOnlyList<EasyTierAdapterSnapshot> Capture()
    {
        var snapshots = new List<EasyTierAdapterSnapshot>();
        foreach (NetworkInterface networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!networkInterface.Name.StartsWith(
                    PublicTunnelConfig.TunnelName,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            string[] addresses = networkInterface.GetIPProperties().UnicastAddresses
                .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(address => address.Address.ToString())
                .ToArray();
            snapshots.Add(new EasyTierAdapterSnapshot(networkInterface.Name, addresses));
        }
        return snapshots;
    }
}

internal sealed class OverlayAddressResolver
{
    private static readonly Regex ScblIpRegex = new(
        @"\b10\.66\.0\.(?:[1-9]|[1-9][0-9]|1[0-9]{2}|2[0-4][0-9]|25[0-4])(?:/24)?\b",
        RegexOptions.Compiled);

    private readonly EasyTierCliClient _cliClient;
    private readonly IEasyTierNetworkSnapshotProvider _networkSnapshots;

    public OverlayAddressResolver(
        EasyTierCliClient cliClient,
        IEasyTierNetworkSnapshotProvider? networkSnapshots = null)
    {
        _cliClient = cliClient;
        _networkSnapshots = networkSnapshots ?? new SystemEasyTierNetworkSnapshotProvider();
    }

    public async Task<string> ResolveAsync(string launcherBaseDir, TimeSpan timeout)
    {
        string cliPath = EasyTierCliClient.GetCliPath(launcherBaseDir);
        if (File.Exists(cliPath))
        {
            string output = await _cliClient.QueryNodeJsonAsync(cliPath, timeout)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(output))
            {
                string? address = ExtractScblIpFromJson(output) ?? ExtractScblIp(output);
                if (NetworkHealthCheckService.IsValidScblClientIp(address))
                    return address!;
            }
        }

        string adapterAddress = ReadFromAdapter();
        return NetworkHealthCheckService.IsValidScblClientIp(adapterAddress)
            ? adapterAddress
            : string.Empty;
    }

    public string ReadFromAdapter()
    {
        try
        {
            foreach (EasyTierAdapterSnapshot adapter in _networkSnapshots.Capture())
            {
                if (!adapter.Name.StartsWith(
                        PublicTunnelConfig.TunnelName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                foreach (string address in adapter.Ipv4Addresses)
                {
                    if (NetworkHealthCheckService.IsValidScblClientIp(address))
                        return address;
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Info("EasyTier adapter IP query not ready: " + ex.Message);
        }
        return string.Empty;
    }

    internal static string? ExtractScblIpFromJson(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return FindIp(document.RootElement);
        }
        catch
        {
            return null;
        }

        static string? FindIp(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                string? address = ExtractScblIp(element.GetString() ?? string.Empty);
                return NetworkHealthCheckService.IsValidScblClientIp(address)
                    ? address
                    : null;
            }
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    string? address = FindIp(property.Value);
                    if (address != null)
                        return address;
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement child in element.EnumerateArray())
                {
                    string? address = FindIp(child);
                    if (address != null)
                        return address;
                }
            }
            return null;
        }
    }

    internal static string? ExtractScblIp(string text)
    {
        Match match = ScblIpRegex.Match(text ?? string.Empty);
        return match.Success ? match.Value.Split('/')[0] : null;
    }
}
