using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Scbl.Server;

public sealed class ServerConfiguration
{
    public int SchemaVersion { get; set; } = 1;
    public string ServerId { get; set; } = "scbl-server";
    public string Mode { get; set; } = "stable";
    public string PublicHost { get; set; } = "127.0.0.1";
    public int GamePort { get; set; } = 11010;
    public int UpdatePort { get; set; } = 18080;
    public int ControlPort { get; set; } = 19080;
    public string VirtualIp { get; set; } = "10.66.0.1";
    public string VirtualNetwork { get; set; } = "10.66.0.0/24";
    public int Mtu { get; set; } = 1380;
    public bool EnableIpv6 { get; set; } = true;
    public bool StartAutomatically { get; set; } = true;
    public string EasyTierNetworkName { get; set; } = "scbl-public";
    public string EasyTierInstanceName { get; set; } = "scbl-public-server";
    public string EasyTierInstanceId { get; set; } = "00000000-0000-0000-0000-000000000001";
    public int EasyTierRpcPort { get; set; } = 15966;
    public bool DdnsEnabled { get; set; }
    public int DdnsManagementPort { get; set; } = 9876;
    public int HeartbeatTtlSeconds { get; set; } = 20;
    public int ModeAssertionTtlSeconds { get; set; } = 90;

    public static ServerConfiguration LoadOrCreate(ServerPaths paths)
    {
        ServerConfiguration? existing = AtomicJson.Read<ServerConfiguration>(paths.Config);
        if (existing is not null)
        {
            existing.Validate(throwOnError: true);
            return existing;
        }
        ServerConfiguration created = new();
        created.Save(paths);
        return created;
    }

    public static ServerConfiguration LoadForEditing(ServerPaths paths) =>
        AtomicJson.Read<ServerConfiguration>(paths.Config) ?? new ServerConfiguration();

    public void Save(ServerPaths paths)
    {
        Validate(throwOnError: true);
        AtomicJson.Write(paths.Config, this);
        if (!File.Exists(paths.Secrets))
        {
            AtomicJson.Write(paths.Secrets, ServerSecrets.Create());
        }
    }

    public IReadOnlyList<string> Validate(bool throwOnError = false)
    {
        List<string> errors = new();
        if (SchemaVersion != 1) errors.Add("不支持的配置版本。");
        if (!Regex.IsMatch(ServerId, "^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$")) errors.Add("服务器 ID 格式无效。");
        if (Mode is not ("stable" or "test")) errors.Add("服务器模式只能是 stable 或 test。");
        List<(string Name, int Port)> ports = [("游戏", GamePort), ("更新", UpdatePort), ("控制", ControlPort), ("EasyTier RPC", EasyTierRpcPort)];
        if (DdnsEnabled) ports.Add(("DDNS-Go 管理", DdnsManagementPort));
        foreach ((string name, int port) in ports)
        {
            if (port is < 1 or > 65535) errors.Add($"{name}端口必须在 1-65535 之间。");
        }
        if (ports.Select(item => item.Port).Distinct().Count() != ports.Count) errors.Add("服务端使用的端口必须互不相同。");
        if (!IPAddress.TryParse(VirtualIp, out IPAddress? virtualIp) || virtualIp.AddressFamily != AddressFamily.InterNetwork) errors.Add("虚拟 IP 必须是 IPv4 地址。");
        if (!TryParseIpv4Network(VirtualNetwork, out uint network, out int prefixLength)) errors.Add("虚拟网段格式无效。");
        else if (virtualIp is not null && (ToUInt32(virtualIp) & PrefixMask(prefixLength)) != network) errors.Add("虚拟 IP 必须位于配置的虚拟网段内。");
        if (Mtu is < 576 or > 9000) errors.Add("MTU 必须在 576-9000 之间。");
        if (HeartbeatTtlSeconds is < 10 or > 3600) errors.Add("心跳 TTL 必须在 10-3600 秒之间。");
        if (ModeAssertionTtlSeconds < HeartbeatTtlSeconds || ModeAssertionTtlSeconds > 3600) errors.Add("模式断言保留期必须不小于心跳 TTL 且不超过 3600 秒。");
        if (string.IsNullOrWhiteSpace(PublicHost) || Uri.CheckHostName(PublicHost) == UriHostNameType.Unknown) errors.Add("服务器地址必须是域名或 IP，不能包含端口和路径。");
        if (!Regex.IsMatch(EasyTierNetworkName, "^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$")) errors.Add("EasyTier 网络名称格式无效。");
        if (!Regex.IsMatch(EasyTierInstanceName, "^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$")) errors.Add("EasyTier 实例名称格式无效。");
        if (!Guid.TryParseExact(EasyTierInstanceId, "D", out _)) errors.Add("EasyTier 实例 ID 必须是标准 UUID。");
        if (throwOnError && errors.Count != 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        return errors;
    }

    public int VirtualPrefixLength => TryParseIpv4Network(VirtualNetwork, out _, out int prefixLength)
        ? prefixLength
        : throw new InvalidDataException("虚拟网段格式无效。");

    public string BuildPublicUpdateBaseUrl()
    {
        if (string.IsNullOrWhiteSpace(PublicHost) || Uri.CheckHostName(PublicHost) == UriHostNameType.Unknown) throw new InvalidDataException("服务器地址无效。");
        return new UriBuilder(Uri.UriSchemeHttp, PublicHost, UpdatePort).Uri.GetLeftPart(UriPartial.Authority);
    }

    private static bool TryParseIpv4Network(string value, out uint network, out int prefixLength)
    {
        network = 0;
        prefixLength = 0;
        string[] fields = value.Split('/', StringSplitOptions.TrimEntries);
        if (fields.Length != 2 || !IPAddress.TryParse(fields[0], out IPAddress? address) || address.AddressFamily != AddressFamily.InterNetwork ||
            !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out prefixLength) || prefixLength is < 1 or > 32) return false;
        uint raw = ToUInt32(address);
        uint mask = PrefixMask(prefixLength);
        network = raw & mask;
        return raw == network;
    }

    private static uint PrefixMask(int prefixLength) => prefixLength == 32 ? uint.MaxValue : uint.MaxValue << (32 - prefixLength);

    private static uint ToUInt32(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }
}

public sealed record ServerSecrets(string NetworkSecret, string AuthTokenKeyHex, string TicketKeyHex)
{
    public static ServerSecrets Create() => new(
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant());

    public static ServerSecrets Load(ServerPaths paths)
    {
        ServerSecrets value = AtomicJson.Read<ServerSecrets>(paths.Secrets)
            ?? throw new InvalidDataException("服务端密钥文件不存在或无法读取。");
        value.Validate();
        return value;
    }

    public void Validate()
    {
        try
        {
            if (Convert.FromBase64String(NetworkSecret).Length != 32) throw new FormatException();
            if (!Regex.IsMatch(AuthTokenKeyHex, "^[0-9a-fA-F]{64}$") || !Regex.IsMatch(TicketKeyHex, "^[0-9a-fA-F]{64}$")) throw new FormatException();
        }
        catch (FormatException)
        {
            throw new InvalidDataException("服务端密钥文件格式无效。");
        }
    }
}
