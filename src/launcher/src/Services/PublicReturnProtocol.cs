using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SplinterCellCNLauncher.Services;

// This channel transports native state for one owned process; it does not verify
// server signatures or authorize an overlay, game launch, or Public UI readiness.
internal static class PublicReturnProtocol
{
    internal const string EnvironmentKey = "FF_PUBLIC_RETURN_V1";
    internal const int MaximumFrame = 8192;
    internal static IOException Invalid() => new("PUBLIC_RETURN_REJECTED");
    internal static bool Hex(string? value, int length) => value?.Length == length
        && value.Any(c => c != '0') && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static async Task<JsonDocument> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        byte[] length = new byte[4];
        await stream.ReadExactlyAsync(length, deadline.Token).ConfigureAwait(false);
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(length);
        if (size is 0 or > MaximumFrame) throw Invalid();
        byte[] body = new byte[size];
        try
        {
            await stream.ReadExactlyAsync(body, deadline.Token).ConfigureAwait(false);
            // JsonDocument rejects invalid JSON UTF-8; explicitly reject replacement
            // handling too, rather than allowing malformed wire names to alias.
            string json = new UTF8Encoding(false, true).GetString(body);
            return JsonDocument.Parse(json, new() { MaxDepth = 5 });
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException) { throw Invalid(); }
        finally { CryptographicOperations.ZeroMemory(body); }
    }
    internal static async Task WriteAsync(Stream stream, object frame, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(frame);
        try
        {
            if (body.Length is 0 or > MaximumFrame) throw Invalid();
            byte[] prefix = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(prefix, body.Length);
            await stream.WriteAsync(prefix, deadline.Token).ConfigureAwait(false);
            await stream.WriteAsync(body, deadline.Token).ConfigureAwait(false);
            await stream.FlushAsync(deadline.Token).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(body); }
    }
    internal static void Shape(JsonElement frame, params string[] fields)
    {
        try
        {
            PublicSessionJson.Shape(frame, fields);
            if (frame.GetProperty("schema").GetUInt32() != 1) throw Invalid();
        }
        catch (Exception error) when (error is PublicControlException or InvalidOperationException or FormatException or OverflowException)
        { throw Invalid(); }
    }
}

internal sealed class PublicReturnState : IDisposable
{
    private readonly Func<string, string, string, string, PublicNativeGameSession> create;
    private readonly object gate = new();
    private string? instance, lastFingerprint, lastKind;
    private ulong generation;
    private PublicNativeGameSession? current;
    private bool disposed;
    internal PublicReturnState(Func<string, string, string, string, PublicNativeGameSession> create) => this.create = create;
    internal PublicNativeGameSession? Current { get { lock (gate) return current; } }
    internal void Hello(string gameInstance)
    {
        lock (gate)
        {
            if (disposed || !PublicReturnProtocol.Hex(gameInstance, 32) || (instance != null && instance != gameInstance))
                throw PublicReturnProtocol.Invalid();
            instance = gameInstance;
        }
    }
    internal void Apply(JsonElement frame, bool firstFrame)
    {
        lock (gate)
        {
            if (disposed || instance == null) throw PublicReturnProtocol.Invalid();
            string? kind = frame.GetProperty("kind").GetString();
            if (kind == "authenticated") PublicReturnProtocol.Shape(frame, "schema", "kind", "generation", "account_id", "username", "token");
            else if (kind is "invalidate" or "heartbeat") PublicReturnProtocol.Shape(frame, "schema", "kind", "generation");
            else throw PublicReturnProtocol.Invalid();
            if (!frame.GetProperty("generation").TryGetUInt64(out ulong next)) throw PublicReturnProtocol.Invalid();
            if (next == 0 || next < generation) throw PublicReturnProtocol.Invalid();
            if (kind == "heartbeat")
            {
                if (firstFrame || next != generation) throw PublicReturnProtocol.Invalid();
                return;
            }
            string? token = kind == "authenticated" ? frame.GetProperty("token").GetString() : null;
            if (kind == "authenticated" && token is not { Length: > 0 and <= 4096 }) throw PublicReturnProtocol.Invalid();
            string? accountId = kind == "authenticated" ? frame.GetProperty("account_id").GetString() : null;
            string? username = kind == "authenticated" ? frame.GetProperty("username").GetString() : null;
            if (kind == "authenticated" && (!PublicNativeAccountAnchor.ValidAccountId(accountId!) || username is not { Length: > 0 and <= 64 }
                || !username.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'))) throw PublicReturnProtocol.Invalid();
            string fingerprint = kind == "authenticated" ? Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[] { accountId, username, token }))) : "invalidate";
            if (next == generation && (kind != lastKind || fingerprint != lastFingerprint)) throw PublicReturnProtocol.Invalid();
            // Reconnection may replay only the same state, after EOF has cancelled
            // the previous context. Repeated frames never replace an active context.
            if (next == generation && current != null) return;
            current?.Dispose(); current = null;
            // A newer well-shaped auth attempt burns the old generation even
            // when its account/token context is rejected; reconnect cannot revive
            // the previous successful login after that failed newer attempt.
            generation = next; lastKind = "rejected"; lastFingerprint = null;
            if (kind == "authenticated") current = create(instance, accountId!, username!, token!);
            lastKind = kind; lastFingerprint = fingerprint;
        }
    }
    internal void Disconnected() { lock (gate) { current?.Dispose(); current = null; } }
    public void Dispose() { lock (gate) { disposed = true; current?.Dispose(); current = null; lastFingerprint = null; } }
    public override string ToString() => "PublicReturnState (private owned-run memory)";
}
