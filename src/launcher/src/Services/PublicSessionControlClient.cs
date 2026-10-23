using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace SplinterCellCNLauncher.Services;

public sealed class PublicControlException : Exception
{
    internal PublicControlException(string code) : base(code) { }
}

public sealed class PublicSessionMember
{
    public uint UserId { get; }
    public string SourceIp { get; }
    internal PublicSessionMember(uint id, string ip) => (UserId, SourceIp) = (id, ip);
    public override string ToString() => "PublicSessionMember";
}

public sealed class PublicSessionSnapshot
{
    public uint SessionId { get; }
    public uint SessionType { get; }
    public uint HostUserId { get; }
    public string HostUsername { get; }
    public int GameplayMode { get; }
    public ReadOnlyCollection<PublicSessionMember> Members { get; }
    internal PublicSessionSnapshot(uint id, uint type, uint host, string username, int mode, List<PublicSessionMember> members)
        => (SessionId, SessionType, HostUserId, HostUsername, GameplayMode, Members) = (id, type, host, username, mode, members.AsReadOnly());
    public override string ToString() => "PublicSessionSnapshot";
}

/// <summary>
/// Exact read-only TLS control contract. No login, password, issuer key, token
/// conversion, automatic retry or UI readiness. Supply only a caller-owned
/// native game session; account authentication cannot manufacture this input.
/// </summary>
public sealed class PublicSessionControlClient : IDisposable
{
    private readonly PublicDeploymentProfile profile;
    private readonly HttpClient http;
    private readonly TimeSpan requestTimeout;
    private readonly CancellationTokenSource lifetime = new();
    internal const int MaximumResponseBytes = 512 * 1024;
    public PublicSessionControlClient(PublicDeploymentProfile profile)
        : this(profile ?? throw PublicNativeRunExpectation.Invalid(), CreateHandler(), TimeSpan.FromSeconds(8)) { }
    // Linked-source tests supply an isolated temporary CA chain policy, without
    // any certificate-acceptance callback or change to the platform trust store.
    internal PublicSessionControlClient(PublicDeploymentProfile profile, SocketsHttpHandler handler, TimeSpan timeout)
    {
        this.profile = profile ?? throw PublicNativeRunExpectation.Invalid();
        if (handler.SslOptions.RemoteCertificateValidationCallback is not null
            || timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(8)) throw PublicNativeRunExpectation.Invalid();
        handler.AllowAutoRedirect = false; handler.UseCookies = false; handler.UseProxy = false;
        handler.AutomaticDecompression = DecompressionMethods.None;
        handler.ConnectTimeout = TimeSpan.FromSeconds(3); handler.MaxResponseHeadersLength = 16;
        http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        requestTimeout = timeout;
    }
    private static SocketsHttpHandler CreateHandler() => new(); // Standard TLS name and chain validation.

    public async Task<ReadOnlyCollection<PublicSessionSnapshot>> QueryAsync(PublicNativeGameSession session, CancellationToken cancellationToken = default)
    {
        if (session is null) throw PublicNativeRunExpectation.Invalid();
        cancellationToken.ThrowIfCancellationRequested();
        string token = session.BearerFor(profile);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.Lifetime, lifetime.Token);
        timeout.CancelAfter(requestTimeout);
        try
        {
            var package = session.Package;
            var role = new { release_id = package.ReleaseId, compat_id = package.CompatId };
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(new {
                protocol_version = 1,
                package = new { schema = 1, release_id = package.ReleaseId, compat_id = package.CompatId,
                    manifest_sha256 = package.ManifestSha256, launcher = role, main_dll = role, gameplay_dll = role, server = role },
                observer_requested = session.Expected.ObserverRequested
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(profile.ControlOrigin, "/v1/sessions")) {
                Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Content = new ByteArrayContent(body)
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.ConnectionClose = true;
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK) throw new PublicControlException("PUBLIC_CONTROL_REQUEST_REJECTED");
            if (response.Version != HttpVersion.Version11 || response.Content.Headers.ContentType?.ToString() != "application/json"
                || response.Content.Headers.ContentEncoding.Count != 0 || response.Headers.TransferEncoding.Count != 0
                || response.Content.Headers.ContentLength is not long length || length is <= 0 or > MaximumResponseBytes
                || response.Headers.CacheControl?.NoStore != true)
                throw new PublicControlException("PUBLIC_CONTROL_RESPONSE_INVALID");
            using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            byte[] bytes = new byte[checked((int)length)];
            await stream.ReadExactlyAsync(bytes, timeout.Token).ConfigureAwait(false);
            byte[] tail = new byte[1];
            if (await stream.ReadAsync(tail, timeout.Token).ConfigureAwait(false) != 0)
                throw new PublicControlException("PUBLIC_CONTROL_RESPONSE_INVALID");
            var result = ParseSnapshot(bytes, session);
            timeout.Token.ThrowIfCancellationRequested(); // Do not publish a late/disposed run snapshot.
            return result;
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested || session.Lifetime.IsCancellationRequested || lifetime.IsCancellationRequested)
                throw new OperationCanceledException("PUBLIC_CONTROL_CANCELLED", cancellationToken);
            throw new PublicControlException("PUBLIC_CONTROL_TIMEOUT");
        }
        catch (Exception error) when (error is HttpRequestException or IOException or ObjectDisposedException)
        { throw new PublicControlException("PUBLIC_CONTROL_TRANSPORT_FAILED"); }
    }

    private static ReadOnlyCollection<PublicSessionSnapshot> ParseSnapshot(byte[] bytes, PublicNativeGameSession context)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 7 });
            var root = document.RootElement;
            PublicSessionJson.Shape(root, ["schema", "sessions"]);
            var sessions = root.GetProperty("sessions");
            if (root.GetProperty("schema").GetInt32() != 1 || sessions.ValueKind != JsonValueKind.Array || sessions.GetArrayLength() > 2048)
                throw new PublicControlException("PUBLIC_CONTROL_RESPONSE_INVALID");
            var result = new List<PublicSessionSnapshot>();
            var ids = new HashSet<uint>(); int totalMembers = 0;
            foreach (var row in sessions.EnumerateArray())
            {
                PublicSessionJson.Shape(row, ["session_id", "session_type", "host_user_id", "host_username", "gameplay_mode", "members"]);
                uint id = row.GetProperty("session_id").GetUInt32(), type = row.GetProperty("session_type").GetUInt32(), host = row.GetProperty("host_user_id").GetUInt32();
                string? username = row.GetProperty("host_username").GetString();
                int mode = row.GetProperty("gameplay_mode").GetInt32();
                var members = row.GetProperty("members");
                if (id == 0 || host == 0 || !ids.Add(id) || mode != context.Expected.GameplayMode
                    || username is not { Length: > 0 and <= 64 } || !username.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')
                    || (host == context.Expected.UserId && username != context.Expected.Username)
                    || members.ValueKind != JsonValueKind.Array || members.GetArrayLength() < 2
                    || (totalMembers += members.GetArrayLength()) > 4096)
                    throw new PublicControlException("PUBLIC_CONTROL_RESPONSE_INVALID");
                var users = new HashSet<uint>(); var sources = new HashSet<string>(StringComparer.Ordinal);
                var admitted = new List<PublicSessionMember>();
                foreach (var member in members.EnumerateArray())
                {
                    PublicSessionJson.Shape(member, ["user_id", "source_ip"]);
                    uint user = member.GetProperty("user_id").GetUInt32();
                    string? ip = member.GetProperty("source_ip").GetString();
                    if (user == 0 || ip is null || !users.Add(user) || !sources.Add(ip)
                        || !PublicNativeGameSession.IsClientAddress(context.Profile, ip)
                        || (user == context.Expected.UserId && ip != context.Expected.SourceIp))
                        throw new PublicControlException("PUBLIC_CONTROL_RESPONSE_INVALID");
                    admitted.Add(new PublicSessionMember(user, ip));
                }
                if (!users.Contains(host) || !users.Contains(context.Expected.UserId))
                    throw new PublicControlException("PUBLIC_CONTROL_RESPONSE_INVALID");
                result.Add(new PublicSessionSnapshot(id, type, host, username, mode, admitted));
            }
            return result.AsReadOnly();
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException
                                     or OverflowException or KeyNotFoundException or ArgumentException)
        { throw new PublicControlException("PUBLIC_CONTROL_RESPONSE_INVALID"); }
    }
    public void Dispose() { lifetime.Cancel(); http.Dispose(); }
    public override string ToString() => "PublicSessionControlClient (read only; no readiness assertion)";
}
