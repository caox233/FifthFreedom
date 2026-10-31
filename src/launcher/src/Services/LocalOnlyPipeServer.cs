using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SplinterCellCNLauncher.Services;

// A pipe whose peer PID can be interpreted in this machine's process namespace.
// A current-user ACL alone does not exclude remote SMB pipe clients.
internal static class LocalOnlyPipeServer
{
    internal static NamedPipeServerStream Create(string name, Func<IOException> invalid)
    {
        if (!OperatingSystem.IsWindows()) throw invalid();
        using var current = System.Security.Principal.WindowsIdentity.GetCurrent();
        string sid = current.User?.Value ?? throw invalid();
        IntPtr security = IntPtr.Zero; SafePipeHandle? handle = null; bool transferred = false;
        try
        {
            // Exact current-user full-control DACL, protected from inheritance.
            string sddl = $"O:{sid}D:P(A;;0x001F019F;;;{sid})";
            if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out security, out _)) throw invalid();
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = security, Inherit = 0 };
            handle = CreateNamedPipeW(@"\\.\pipe\" + name,
                0x00000003 | 0x40000000 | 0x00080000, // duplex, overlapped, first instance
                0x00000008, // byte/wait mode plus PIPE_REJECT_REMOTE_CLIENTS
                1, 4096, 4096, 0, ref attributes);
            if (handle.IsInvalid) throw invalid();
            var stream = new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, handle);
            transferred = true; return stream;
        }
        finally
        {
            if (!transferred) handle?.Dispose();
            if (security != IntPtr.Zero) _ = LocalFree(security);
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { internal int Length; internal IntPtr Descriptor; internal int Inherit; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string text, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafePipeHandle CreateNamedPipeW(string name, uint openMode, uint pipeMode, uint maximumInstances, uint outputSize, uint inputSize, uint timeout, ref SecurityAttributes attributes);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
