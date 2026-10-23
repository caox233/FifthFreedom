using System.Text;
using System.Text.Json;

namespace SplinterCellCNLauncher.Services;

/// <summary>Caller expectations from the future owned native-run handoff, not decoded-token authority.</summary>
public sealed class PublicNativeRunExpectation
{
    public uint UserId { get; }
    internal string Username { get; }
    internal string SourceIp { get; }
    internal string Product { get; }
    internal string Instance { get; }
    internal bool ObserverRequested { get; }
    internal int GameplayMode => Product == "scbl-lan/2/game/official" ? 1 : 2;
    public PublicNativeRunExpectation(uint userId, string username, string sourceIp, string product,
        string instance, bool observerRequested)
    {
        if (userId == 0 || username is not { Length: > 0 and <= 64 }
            || !username.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')
            || product is not ("scbl-lan/2/game/official" or "scbl-lan/2/game/mod")
            || instance is not { Length: 32 } || instance.All(c => c == '0')
            || !instance.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')) throw Invalid();
        PublicDeploymentProfile.ParseIpv4(sourceIp);
        UserId = userId; Username = username; SourceIp = sourceIp; Product = product;
        Instance = instance; ObserverRequested = observerRequested;
    }
    internal static ArgumentException Invalid() => new("PUBLIC_NATIVE_GAME_SESSION_INVALID");
    public override string ToString() => "PublicNativeRunExpectation (caller supplied; not authentication proof)";
}

/// <summary>
/// In-memory post-native-auth input. Client checks purpose/context consistency;
/// only the HTTPS server verifies signature, expiry, source, revocation and run.
/// No production native-to-launcher token handoff currently supplies this class.
/// </summary>
public sealed class PublicNativeGameSession : IDisposable
{
    internal PublicDeploymentProfile Profile { get; }
    internal ReleasePackage Package { get; }
    internal PublicNativeRunExpectation Expected { get; }
    private string? bearer;
    private readonly CancellationTokenSource lifetime = new();
    internal CancellationToken Lifetime => lifetime.Token;
    public PublicNativeGameSession(PublicDeploymentProfile profile, CredentialIdentity credential,
        string nativeEndpoint, ReleasePackage package, PublicNativeRunExpectation expected, string nativeGameToken)
    {
        if (profile is null || credential is null || package is null || expected is null)
            throw PublicNativeRunExpectation.Invalid();
        if (!CredentialEnvironmentContext.ForPublic(profile).CreateIdentity(nativeEndpoint, expected.Username).Equals(credential)
            || !IsClientAddress(profile, expected.SourceIp)) throw PublicNativeRunExpectation.Invalid();
        NativeGameTokenContract.Validate(nativeGameToken, expected,
            $"1|{package.ReleaseId}|{package.CompatId}|{package.ManifestSha256}");
        Profile = profile; Package = package; Expected = expected; bearer = nativeGameToken;
    }
    internal string BearerFor(PublicDeploymentProfile profile)
    {
        if (Profile.CredentialNamespace != profile.CredentialNamespace) throw PublicNativeRunExpectation.Invalid();
        lifetime.Token.ThrowIfCancellationRequested();
        return Volatile.Read(ref bearer) ?? throw PublicNativeRunExpectation.Invalid();
    }
    internal static bool IsClientAddress(PublicDeploymentProfile profile, string value)
    {
        uint address = PublicDeploymentProfile.ParseIpv4(value);
        string[] parts = profile.OverlayCidr.Split('/');
        uint network = PublicDeploymentProfile.ParseIpv4(parts[0]);
        uint mask = uint.MaxValue << (32 - int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
        return address != network && address != (network | ~mask) && (address & mask) == network;
    }
    public void Dispose()
    {
        lifetime.Cancel();
        Interlocked.Exchange(ref bearer, null);
        // Cancellation remains readable by outstanding queries. Managed strings
        // cannot promise physical zeroization; no token is persisted or logged.
    }
    public override string ToString() => "PublicNativeGameSession (memory only; server authentication required)";
}

internal static class NativeGameTokenContract
{
    // This is purpose/context screening, not signature verification. Keep the
    // native v1 producer and Python consumer as the authentication authorities.
    internal static void Validate(string token, PublicNativeRunExpectation expected, string release)
    {
        try
        {
            if (token is not { Length: > 0 and <= 4096 } || token.Split('.') is not ["v1", var payload, var signature]
                || Decode(signature).Length != 32) throw PublicNativeRunExpectation.Invalid();
            using var document = JsonDocument.Parse(Decode(payload), new() { MaxDepth = 5 });
            var claims = document.RootElement;
            PublicSessionJson.Shape(claims, ["release", "user_id", "username", "source_ip", "issued_at", "expires_at", "auth_version", "game_run"], ["observer_admitted_run"]);
            var run = claims.GetProperty("game_run");
            PublicSessionJson.Shape(run, ["product", "instance"]);
            if (claims.GetProperty("user_id").GetUInt32() != expected.UserId
                || claims.GetProperty("username").GetString() != expected.Username
                || claims.GetProperty("source_ip").GetString() != expected.SourceIp
                || claims.GetProperty("release").GetString() != release
                || run.GetProperty("product").GetString() != expected.Product
                || run.GetProperty("instance").GetString() != expected.Instance
                || claims.GetProperty("auth_version").GetInt64() <= 0) throw PublicNativeRunExpectation.Invalid();
            // Validate integer shapes without treating the local clock as token admission.
            _ = claims.GetProperty("issued_at").GetInt64();
            _ = claims.GetProperty("expires_at").GetInt64();
            string? grant = claims.TryGetProperty("observer_admitted_run", out var value) && value.ValueKind != JsonValueKind.Null
                ? value.GetString() : null;
            if ((grant is not null && grant != expected.Instance) || (expected.ObserverRequested && grant != expected.Instance))
                throw PublicNativeRunExpectation.Invalid();
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException
                                     or KeyNotFoundException or OverflowException or PublicControlException)
        { throw PublicNativeRunExpectation.Invalid(); }
    }
    private static byte[] Decode(string text)
    {
        if (text.Length == 0 || !text.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
            throw PublicNativeRunExpectation.Invalid();
        byte[] bytes = Convert.FromBase64String(text.Replace('-', '+').Replace('_', '/') + new string('=', (4 - text.Length % 4) % 4));
        if (Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') != text)
            throw PublicNativeRunExpectation.Invalid();
        return bytes;
    }
}

internal static class PublicSessionJson
{
    internal static void Shape(JsonElement value, string[] required, string[]? optional = null)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new PublicControlException("PUBLIC_CONTROL_RESPONSE_INVALID");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!seen.Add(property.Name) || (!required.Contains(property.Name, StringComparer.Ordinal)
                && !(optional?.Contains(property.Name, StringComparer.Ordinal) ?? false)))
                throw new PublicControlException("PUBLIC_CONTROL_RESPONSE_INVALID");
        if (required.Any(field => !seen.Contains(field))) throw new PublicControlException("PUBLIC_CONTROL_RESPONSE_INVALID");
    }
}
