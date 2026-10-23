using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SplinterCellCNLauncher.Services;

internal sealed class RuntimeInputs
{
    internal const string Source = "10.254.253.1", WrongSource = "10.254.253.2";
    internal string ProbeId { get; }
    internal string CorePath { get; }
    internal long CoreBytes { get; }
    internal string CoreSha256 { get; }
    internal string SourceTreeSha256 { get; }
    internal string RecipeSha256 { get; }
    internal string ProfileSha256 { get; }
    internal string Instance { get; } = Guid.NewGuid().ToString("D");
    internal string AdapterGuid { get; } = Guid.NewGuid().ToString("N");
    internal string AdapterName => "ff-" + AdapterGuid;
    internal string Configuration => Path.Combine(Path.GetDirectoryName(CorePath)!, "owned-inert.toml");
    private readonly byte[] template;
    internal static IOException Invalid() => new("CORE_RUNTIME_INPUT_INVALID");
    internal static byte[] Resource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name) ?? throw Invalid();
        if (stream.Length is <= 0 or > 65536) throw Invalid();
        byte[] data = new byte[checked((int)stream.Length)]; stream.ReadExactly(data); return data;
    }
    internal static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
    private static string Digest(JsonElement root, string key)
    {
        string value = OwnedCoreReadProtocol.Text(root, key);
        if (!OwnedCoreReadProtocol.Hex(value, 64)) throw Invalid(); return value;
    }
    internal RuntimeInputs(byte[] manifest, byte[] recipe, byte[] profile)
    {
        if (manifest.Length is <= 0 or > 4096 || recipe.Length is <= 0 or > 65536 || profile.Length is <= 0 or > 16384) throw Invalid();
        using var parsed = JsonDocument.Parse(manifest, new JsonDocumentOptions { MaxDepth = 2 });
        JsonElement root = parsed.RootElement;
        OwnedCoreReadProtocol.Fields(root, "schema", "probe_id", "core_path", "core_bytes", "core_sha256", "source_tree_sha256", "build_recipe_sha256", "inert_profile_sha256");
        if (!root.GetProperty("schema").TryGetInt32(out int schema) || schema != 1) throw Invalid();
        ProbeId = OwnedCoreReadProtocol.Text(root,"probe_id");
        if (!OwnedCoreReadProtocol.Hex(ProbeId,32)) throw Invalid();
        CorePath = OwnedCoreLaunchInputs.Canonical(OwnedCoreReadProtocol.Text(root,"core_path",2048));
        if (Path.GetFileName(CorePath) != "easytier-core.exe" || Path.GetFileName(Path.GetDirectoryName(CorePath)) != "ff-core-runtime-" + ProbeId) throw Invalid();
        if (!root.GetProperty("core_bytes").TryGetInt64(out long length) || length is <= 0 or > 256 * 1024 * 1024) throw Invalid();
        CoreBytes = length;
        CoreSha256 = Digest(root,"core_sha256"); SourceTreeSha256 = Digest(root,"source_tree_sha256");
        RecipeSha256 = Digest(root,"build_recipe_sha256"); ProfileSha256 = Digest(root,"inert_profile_sha256");
        if (RecipeSha256 != Sha(recipe) || ProfileSha256 != Sha(profile)) throw Invalid();
        using var pinnedRecipe = JsonDocument.Parse(recipe);
        if (pinnedRecipe.RootElement.GetProperty("kind").GetString() != "independent-windows-inert-owned-read-build-only"
            || pinnedRecipe.RootElement.GetProperty("expected_prepared").GetProperty("source_tree_sha256").GetString() != SourceTreeSha256
            || pinnedRecipe.RootElement.GetProperty("inert_profile").GetProperty("sha256").GetString() != ProfileSha256
            || pinnedRecipe.RootElement.GetProperty("inert_profile").GetProperty("bytes").GetInt32() != profile.Length) throw Invalid();
        template = profile.ToArray(); _ = ConfigurationBytes();
    }
    internal byte[] ConfigurationBytes()
    {
        string text;
        try { text = new UTF8Encoding(false, true).GetString(template); }
        catch (DecoderFallbackException) { throw Invalid(); }
        const string marker = "[flags]\n";
        if (text.Contains('\r') || text.Contains("instance_id", StringComparison.Ordinal) || text.Contains("dev_name", StringComparison.Ordinal)
            || text.Split(marker, StringSplitOptions.None).Length != 2) throw Invalid();
        return Encoding.UTF8.GetBytes($"instance_id = \"{Instance}\"\n" + text.Replace(marker, marker + $"dev_name = \"{AdapterName}\"\n", StringComparison.Ordinal));
    }
    internal OwnedCoreLaunchInputs CreateSpecification(byte[] configuration) => new(CorePath, CoreBytes, CoreSha256,
        Configuration, configuration.Length, Sha(configuration), OwnedCoreDependencyInputs.ForOfficialWintun0141Amd64(Path.GetDirectoryName(CorePath)!), OwnedCoreRpcMode.LoopbackProbe);
    public override string ToString() => "Owned runtime test inputs (private paths; no admission)";
}
