namespace ScblLan;

public sealed record ServerAddress(string Host, int Port)
{
    public static ServerAddress Parse(string text)
    {
        text = text.Trim();
        if (text.Length == 0) throw new ArgumentException("请填写服务器 IPv4 或域名。");
        if (!text.Contains("://")) text = "http://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var u) || u.Scheme != "http" || u.UserInfo.Length > 0
            || u.AbsolutePath != "/" || u.Query.Length > 0 || u.Fragment.Length > 0 || u.HostNameType == UriHostNameType.IPv6)
            throw new ArgumentException("请输入 IPv4 或域名，可附加 API 端口；首版使用可信 LAN 上的直连 HTTP。");
        bool explicitPort = text[(text.IndexOf("://", StringComparison.Ordinal) + 3)..].TrimEnd('/').Contains(':');
        int port = explicitPort ? u.Port : 50051;
        if (port < 1) throw new ArgumentException("端口必须在 1–65535 范围内。");
        return new(u.Host, port);
    }
}
