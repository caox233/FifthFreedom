using System.Globalization;
using System.Net;
using System.Text.Json;

namespace SplinterCellCNLauncher.Services;

/// <summary>
/// Explicit non-secret deployment configuration, not server authentication or a
/// readiness assertion. No endpoint, tunnel credential or runtime has a default.
/// Normal TLS verification remains mandatory when a transport is implemented.
/// </summary>
public sealed class PublicDeploymentProfile
{
    public string DeploymentId { get; }
    public Uri ControlOrigin { get; }
    public string OverlayCidr { get; }
    public string ServerOverlayIp { get; }
    public PublicTransport Transport { get; }
    public Uri PeerUri { get; }
    public PublicDeploymentRuntimePins RuntimePins { get; }
    internal string CredentialNamespace { get; }

    public PublicDeploymentProfile(string deploymentId, string controlOrigin, string overlayCidr,
        string serverOverlayIp, PublicTransport transport, string peerUri, PublicDeploymentRuntimePins runtimePins)
    {
        if (string.IsNullOrEmpty(deploymentId) || deploymentId.Length > 64
            || !deploymentId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')) throw Invalid();
        if (!Enum.IsDefined(transport) || runtimePins is null) throw Invalid();
        ControlOrigin = ParseAuthority(controlOrigin, "https");
        PeerUri = ParseAuthority(peerUri, transport == PublicTransport.Fast ? "quic" : "wss", requirePort: true);
        if (overlayCidr is null || overlayCidr.Split('/') is not [var networkText, var prefixText]
            || !int.TryParse(prefixText, NumberStyles.None, CultureInfo.InvariantCulture, out int prefix)
            || prefix is < 8 or > 30 || prefix.ToString(CultureInfo.InvariantCulture) != prefixText) throw Invalid();
        uint network = ParseIpv4(networkText), server = ParseIpv4(serverOverlayIp);
        uint mask = uint.MaxValue << (32 - prefix);
        uint broadcast = network | ~mask;
        if ((network & mask) != network || (server & mask) != network || server == network || server == broadcast) throw Invalid();
        // Exclude an overlay that encompasses reserved, loopback or link-local
        // space as well as invalid individual server addresses.
        foreach (var blocked in new[] { (0x00000000u, 0x00ffffffu), (0x7f000000u, 0x7fffffffu),
            (0xa9fe0000u, 0xa9feffffu), (0xe0000000u, uint.MaxValue) })
            if (network <= blocked.Item2 && broadcast >= blocked.Item1) throw Invalid();
        DeploymentId = deploymentId; OverlayCidr = networkText + "/" + prefixText;
        ServerOverlayIp = serverOverlayIp; Transport = transport; RuntimePins = runtimePins;
        // A stable credential namespace survives transport/runtime updates but
        // never silently moves across deployment IDs or HTTPS authorities.
        CredentialNamespace = JsonSerializer.Serialize(new[] { DeploymentId, ControlOrigin.AbsoluteUri, OverlayCidr, ServerOverlayIp });
    }

    internal string CanonicalNativeEndpoint(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128 || value != value.Trim()
            || value.Split(':') is not [var host, var portText]
            || !int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out int port)
            || port is < 1 or > 65535 || portText != port.ToString(CultureInfo.InvariantCulture)
            || ParseIpv4(host) != ParseIpv4(ServerOverlayIp)) throw Invalid();
        return host + ":" + portText;
    }

    private static Uri ParseAuthority(string value, string scheme, bool requirePort = false)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 512 || value != value.Trim()
            || value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) || value.Contains('\\')
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != scheme
            || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || string.IsNullOrEmpty(uri.Host) || uri.HostNameType == UriHostNameType.Unknown
            || uri.HostNameType == UriHostNameType.Basic || uri.Port is 0 or > 65535) throw Invalid();
        string authority = value[(value.IndexOf("://", StringComparison.Ordinal) + 3)..].TrimEnd('/');
        if (requirePort && (uri.Port < 1 || (!authority.StartsWith('[') && !authority.Contains(':'))
            || (authority.StartsWith('[') && !authority.Contains("]:")))) throw Invalid();
        // Reject normalized-away paths (e.g. /a/../), escaped authority text and
        // aliases instead of allowing them to hide a different input identity.
        if (authority.Contains('/') || authority.Contains('%') || authority.EndsWith('.')) throw Invalid();
        if (!authority.StartsWith('['))
        {
            string literal = authority.Split(':')[0];
            if (IPAddress.TryParse(literal, out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                ParseIpv4(literal);
                if (literal != uri.Host) throw Invalid();
            }
        }
        return new UriBuilder(uri) { Host = uri.IdnHost.ToLowerInvariant(), Path = "/" }.Uri;
    }

    internal static uint ParseIpv4(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Split('.') is not [var a, var b, var c, var d]) throw Invalid();
        uint address = 0;
        foreach (string part in new[] { a, b, c, d })
        {
            if (!byte.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out byte octet)
                || octet.ToString(CultureInfo.InvariantCulture) != part) throw Invalid();
            address = (address << 8) | octet;
        }
        return address;
    }
    private static ArgumentException Invalid() => new("PUBLIC_DEPLOYMENT_INVALID");
    public override string ToString() => "PublicDeploymentProfile (explicit configuration; not readiness)";
}
