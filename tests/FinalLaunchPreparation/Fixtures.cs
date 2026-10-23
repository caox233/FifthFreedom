using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ScblLan;
using SplinterCellCNLauncher.Services;

// Only diagnostics, deployment resource access and the forbidden game-start
// endpoint are substituted. Auth, HTTP/2 transport, package, config bytes,
// preparation workflow and pipe ownership are actual linked production code.
namespace SplinterCellCNLauncher.Services
{
    internal static class LogService { internal static void Info(string _) { } internal static void Error(string _) { } }
    internal sealed record RequestedConfigurationProjection;
    internal static class RequestedConfigurationSnapshot { internal static RequestedConfigurationProjection Prepare(byte[] _) => new(); }
    internal sealed class GameLaunchService
    {
        internal Task<Process> StartGameAsync(string gameDir, string executable, LaunchPipe pipe, string? pluginDirectory = null, bool observerRequested = false)
            => throw new InvalidOperationException("Synthetic tests never start a game.");
    }
}
namespace ScblLan
{
    internal static class BundledOriginalLoader
    {
        internal static string Sha256 => throw new InvalidOperationException("No deployment in authentication tests.");
        internal static void Create(string _) => throw new InvalidOperationException("No deployment in authentication tests.");
    }
}

internal sealed class SyntheticPackage : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ff-public-session-" + Guid.NewGuid().ToString("N"));
    internal ReleasePackage Package { get; }
    internal SyntheticPackage()
    {
        Directory.CreateDirectory(root);
        var rows = new List<object>();
        foreach (var item in new[] { ("launcher", "FifthFreedom.exe"), ("main", "runtime/uplay_r1_loader.dll"),
                                    ("gameplay", "runtime/scbl_gameplay.dll"), ("server", "runtime/FifthFreedom.Server.exe") })
        {
            string path = Path.Combine(root, item.Item2); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            byte[] bytes = Encoding.ASCII.GetBytes($"Synthetic nonexecuted package fixture\0SCBL_RELEASE_V1|{item.Item1}|{ReleaseBuildIdentity.ReleaseId}|{ReleaseBuildIdentity.CompatId}|END_SCBl\0");
            File.WriteAllBytes(path, bytes);
            rows.Add(new { role = item.Item1, path = item.Item2, release_id = ReleaseBuildIdentity.ReleaseId,
                compat_id = ReleaseBuildIdentity.CompatId, bytes = bytes.Length, sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() });
        }
        Directory.CreateDirectory(Path.Combine(root, "package"));
        File.WriteAllBytes(Path.Combine(root, "package/RELEASE-CATALOG.json"), JsonSerializer.SerializeToUtf8Bytes(new {
            schema = 1, release_id = ReleaseBuildIdentity.ReleaseId, compat_id = ReleaseBuildIdentity.CompatId, components = rows }));
        Package = ReleasePackage.Verify(Path.Combine(root, "FifthFreedom.exe"));
    }
    public void Dispose() => Directory.Delete(root, recursive: true);
}

