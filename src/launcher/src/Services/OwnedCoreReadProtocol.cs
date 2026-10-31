using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace SplinterCellCNLauncher.Services;

// These are historical observations, never an overlay lease or admission proof.
internal sealed record OwnedCoreNicSnapshot(string Name, string Guid, string Luid, uint Index)
{
    public override string ToString() => "OwnedCoreNicSnapshot (historical kernel observation)";
}

internal static class OwnedCoreReadProtocol
{
    internal const int MaximumFrame = 2048;
    internal static IOException Invalid() => new("OWNED_CORE_READ_REJECTED");
    internal static string Nonce() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    internal static bool Hex(string? value, int count) => value?.Length == count && value.Any(c => c != '0') && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static string Instance(string value)
    {
        if (value is null || value.Length != 36 || !System.Guid.TryParseExact(value, "D", out var parsed) || parsed == System.Guid.Empty || parsed.ToString("D") != value) throw Invalid();
        return value;
    }
    internal static string Source(string value)
    {
        if (value is null || value.Length is < 7 or > 15) throw Invalid();
        string[] fields = value.Split('.');
        if (fields.Length != 4 || fields.Any(x => x.Length is < 1 or > 3 || x.Any(c => c is < '0' or > '9') || x.Length > 1 && x[0] == '0') ||
            !IPAddress.TryParse(value, out var address) || address.AddressFamily != AddressFamily.InterNetwork) throw Invalid();
        byte[] bytes = address.GetAddressBytes();
        if (bytes[0] is 0 or 127 or >= 224 || bytes[0] == 169 && bytes[1] == 254 || value != address.ToString()) throw Invalid();
        return value;
    }
    internal static async Task Write(Stream stream, object frame, CancellationToken token)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(frame);
        if (body.Length is 0 or > MaximumFrame) throw Invalid();
        byte[] prefix = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(prefix, body.Length);
        await stream.WriteAsync(prefix, token).ConfigureAwait(false);
        await stream.WriteAsync(body, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }
    internal static async Task<JsonDocument> Read(Stream stream, CancellationToken token)
    {
        byte[] prefix = new byte[4]; await stream.ReadExactlyAsync(prefix, token).ConfigureAwait(false);
        int count = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (count is < 1 or > MaximumFrame) throw Invalid();
        byte[] body = new byte[count]; await stream.ReadExactlyAsync(body, token).ConfigureAwait(false);
        try { return JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 2 }); }
        catch (JsonException) { throw Invalid(); }
    }
    internal static void Fields(JsonElement root, params string[] fields)
    {
        if (root.ValueKind != JsonValueKind.Object) throw Invalid();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in root.EnumerateObject()) if (!names.Add(item.Name) || !fields.Contains(item.Name, StringComparer.Ordinal)) throw Invalid();
        if (names.Count != fields.Length) throw Invalid();
    }
    internal static string Text(JsonElement root, string name, int limit = 128)
    {
        var value = root.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String) throw Invalid();
        string result = value.GetString()!;
        if (result.Length is 0 || result.Length > limit || result.Any(char.IsControl) || result.Any(char.IsSurrogate)) throw Invalid();
        return result;
    }
    internal static void Common(JsonElement root, string kind, string attempt, string nonce, string instance)
    {
        if (root.GetProperty("schema").ValueKind != JsonValueKind.Number || !root.GetProperty("schema").TryGetInt32(out int schema) || schema != 1 ||
            Text(root,"kind") != kind || Text(root,"attempt") != attempt || Text(root,"nonce") != nonce || Text(root,"instance") != instance) throw Invalid();
    }
    internal static object Challenge(string attempt, string nonce, string instance) => new { schema = 1, kind = "challenge", attempt, nonce, instance };
    internal static void Hello(JsonElement root, string attempt, string nonce, string instance)
    {
        Fields(root,"schema","kind","attempt","nonce","instance"); Common(root,"hello",attempt,nonce,instance);
    }
    internal static OwnedCoreNicSnapshot? Response(JsonElement root, string attempt, string nonce, string instance, ulong request, string source)
    {
        string kind = Text(root,"kind");
        if (kind == "unavailable") Fields(root,"schema","kind","attempt","nonce","instance","request","source","code");
        else if (kind == "observed") Fields(root,"schema","kind","attempt","nonce","instance","request","source","name","guid","luid","index");
        else throw Invalid();
        Common(root,kind,attempt,nonce,instance);
        if (!root.GetProperty("request").TryGetUInt64(out ulong received) || received != request || Text(root,"source") != source) throw Invalid();
        if (kind == "unavailable") { if (Text(root,"code") != "NIC_UNAVAILABLE") throw Invalid(); return null; }
        string guid = Text(root,"guid"), luid = Text(root,"luid"), name = Text(root,"name");
        if (!Hex(guid,32) || !Hex(luid,16) || !root.GetProperty("index").TryGetUInt32(out uint index) || index == 0) throw Invalid();
        return new(name,guid,luid,index);
    }
}
