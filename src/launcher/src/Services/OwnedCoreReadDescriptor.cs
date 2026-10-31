using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace SplinterCellCNLauncher.Services;

// Explicit per-launch non-secret input. Its image digest is owner consistency,
// not publisher authentication. No arbitrary environment extension is accepted.
internal sealed class OwnedCoreReadDescriptor : IDisposable
{
    internal const string Variable = "FF_OWNED_CORE_READ_V1";
    private readonly PublicReturnProcess parent;
    private readonly FileStream image;
    internal string Serialized { get; }
    internal string Pipe { get; }
    internal string Attempt { get; }
    internal string Instance { get; }
    internal bool Alive => parent.Alive;
    private OwnedCoreReadDescriptor(PublicReturnProcess parent, FileStream image, string pipe, string attempt, string instance, string digest)
    {
        this.parent = parent; this.image = image; Pipe = pipe; Attempt = attempt; Instance = instance;
        Serialized = JsonSerializer.Serialize(new { schema = 1, pipe, attempt, instance, parent_pid = parent.Pid,
            parent_birth = parent.CreationFileTime, parent_path = parent.ImagePath, parent_sha256 = digest });
        if (System.Text.Encoding.UTF8.GetByteCount(Serialized) > 4096) throw OwnedCoreReadProtocol.Invalid();
    }
    internal static OwnedCoreReadDescriptor Create(string instance)
    {
        instance = OwnedCoreReadProtocol.Instance(instance);
        using var current = Process.GetCurrentProcess();
        var parent = PublicReturnProcess.Capture(current, Environment.ProcessPath ?? throw OwnedCoreReadProtocol.Invalid());
        FileStream? image = null;
        try
        {
            image = new FileStream(parent.ImagePath,FileMode.Open,FileAccess.Read,FileShare.Read);
            if (image.Length is < 1 or > 256 * 1024 * 1024) throw OwnedCoreReadProtocol.Invalid();
            string digest = Convert.ToHexStringLower(SHA256.HashData(image));
            string attempt = Guid.NewGuid().ToString("N");
            return new(parent,image,"ff-core-observe-" + Guid.NewGuid().ToString("N"),attempt,instance,digest);
        }
        catch { image?.Dispose(); parent.Dispose(); throw; }
    }
    public void Dispose() { image.Dispose(); parent.Dispose(); }
    public override string ToString() => "OwnedCoreReadDescriptor (explicit single attempt)";
}
