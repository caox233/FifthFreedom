using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace SplinterCellCNLauncher.Services;

// Declared independent bytes for one process attempt. This is not publisher,
// dependency-license, configuration-policy or Public-readiness admission.
internal sealed class OwnedCoreLaunchInputs
{
    internal string Executable { get; }
    internal string Configuration { get; }
    private readonly long imageLength, configurationLength;
    private readonly string imageHash, configurationHash;
    internal OwnedCoreLaunchInputs(string executable, long imageLength, string imageHash,
        string configuration, long configurationLength, string configurationHash)
    {
        Executable = Canonical(executable); Configuration = Canonical(configuration);
        if (imageLength is <= 0 or > 256 * 1024 * 1024 || configurationLength is <= 0 or > 1024 * 1024
            || !Digest(imageHash) || !Digest(configurationHash) || Executable.Equals(Configuration, StringComparison.OrdinalIgnoreCase)) throw Invalid();
        this.imageLength = imageLength; this.imageHash = imageHash;
        this.configurationLength = configurationLength; this.configurationHash = configurationHash;
    }
    internal static IOException Invalid() => new("OWNED_CORE_INPUTS_INVALID");
    private static bool Digest(string value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string Canonical(string value)
    {
        if (string.IsNullOrEmpty(value) || !Path.IsPathFullyQualified(value) || value.Any(char.IsControl)) throw Invalid();
        var full = Path.GetFullPath(value);
        if (!string.Equals(value, full, StringComparison.OrdinalIgnoreCase)) throw Invalid();
        if (OperatingSystem.IsWindows() && (full.Length < 4 || full[1] != ':' || full[2] != '\\'
            || full.AsSpan(2).Contains(':') || full.Split('\\').Skip(1).Any(s => s.Length == 0 || s.EndsWith('.') || s.EndsWith(' ')))) throw Invalid();
        return full;
    }
    internal PinnedInputs Pin()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("OWNED_CORE_WINDOWS_REQUIRED");
        var pinned = new PinnedInputs(this);
        try { pinned.Add(Executable, imageLength, imageHash); pinned.Add(Configuration, configurationLength, configurationHash); return pinned; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        { pinned.Dispose(); throw Invalid(); }
        catch { pinned.Dispose(); throw; }
    }
    public override string ToString() => "OwnedCoreLaunchInputs (declared private inputs)";

    internal sealed class PinnedInputs(OwnedCoreLaunchInputs specification) : IDisposable
    {
        internal OwnedCoreLaunchInputs Specification { get; } = specification;
        private readonly List<IDisposable> handles = new();
        private readonly HashSet<string> directories = new(StringComparer.OrdinalIgnoreCase);
        internal void Add(string path, long length, string digest)
        {
            var directory = Path.GetDirectoryName(path)!;
            var chain = new Stack<string>();
            for (string? current = directory; current is not null; current = Path.GetDirectoryName(current)) chain.Push(current);
            // Pin ancestors from the root outward before opening a descendant.
            // No-delete sharing prevents an ancestor rename from changing the
            // path passed to CreateProcess while the verified file remains open.
            foreach (var item in chain)
                if (directories.Add(item))
                {
                    var handle = Open(item, 0x80, 3, 0x02200000);
                    try { Inspect(handle, directory: true); handles.Add(handle); }
                    catch { handle.Dispose(); throw; }
                }
            var input = Open(path, 0x80000000, 1, 0x00200000);
            try
            {
                Inspect(input, directory: false);
                var stream = new FileStream(input, FileAccess.Read);
                handles.Add(stream);
                if (stream.Length != length || Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant() != digest
                    || stream.Length != length) throw Invalid();
            }
            catch { input.Dispose(); throw; }
        }
        private static SafeFileHandle Open(string path, uint access, uint share, uint flags)
        {
            var handle = CreateFileW(path, access, share, IntPtr.Zero, 3, flags, IntPtr.Zero);
            if (handle.IsInvalid) { handle.Dispose(); throw Invalid(); }
            return handle;
        }
        private static void Inspect(SafeFileHandle handle, bool directory)
        {
            if (GetFileType(handle) != 1 || !GetFileInformationByHandle(handle, out var information)
                || (information.Attributes & 0x400) != 0 || ((information.Attributes & 0x10) != 0) != directory) throw Invalid();
        }
        public void Dispose()
        {
            for (int i = handles.Count - 1; i >= 0; i--) handles[i].Dispose();
            handles.Clear();
        }
        [StructLayout(LayoutKind.Sequential)] private struct FileInformation
        {
            internal uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
            internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetFileType(SafeFileHandle file);
    }
}
