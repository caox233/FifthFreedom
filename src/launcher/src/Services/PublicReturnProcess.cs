using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SplinterCellCNLauncher.Services;

// A duplicated exact Process.Start handle, not a later PID/name search. Keeping
// this handle alive pins the process object across PID reuse and UI disposal.
internal sealed class PublicReturnProcess : IDisposable
{
    private readonly SafeProcessHandle handle;
    internal uint Pid { get; }
    internal ulong CreationFileTime { get; }
    internal string ImagePath { get; }
    internal bool Alive => WaitForSingleObject(handle, 0) == 0x102;
    private PublicReturnProcess(SafeProcessHandle handle)
    {
        this.handle = handle;
        Pid = GetProcessId(handle);
        if (Pid == 0 || !GetProcessTimes(handle, out ulong creation, out _, out _, out _)) throw PublicReturnProtocol.Invalid();
        CreationFileTime = creation;
        var image = new StringBuilder(32768); uint count = (uint)image.Capacity;
        if (!QueryFullProcessImageName(handle, 0, image, ref count)) throw PublicReturnProtocol.Invalid();
        ImagePath = Path.GetFullPath(image.ToString());
        if (!Alive) throw PublicReturnProtocol.Invalid();
    }
    internal static PublicReturnProcess Capture(Process exactProcess, string expectedPath)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("PUBLIC_RETURN_WINDOWS_REQUIRED");
        if (!DuplicateHandle(GetCurrentProcess(), exactProcess.SafeHandle, GetCurrentProcess(), out SafeProcessHandle owned, 0, false, 2))
            throw PublicReturnProtocol.Invalid();
        try
        {
            var result = new PublicReturnProcess(owned);
            if (!string.Equals(result.ImagePath, Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase)) throw PublicReturnProtocol.Invalid();
            return result;
        }
        catch { owned.Dispose(); throw; }
    }
    internal bool Matches(uint pid, ulong creation) => Pid == pid && CreationFileTime == creation && Alive;
    internal static (uint Pid, ulong Creation) ObserveAcknowledged(Process process)
    {
        if (!GetProcessTimes(process.SafeHandle, out ulong creation, out _, out _, out _)) throw PublicReturnProtocol.Invalid();
        return (GetProcessId(process.SafeHandle), creation);
    }
    public void Dispose() => handle.Dispose();
    public override string ToString() => "PublicReturnProcess (pinned owned process)";
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DuplicateHandle(IntPtr sourceProcess, SafeProcessHandle source, IntPtr targetProcess, out SafeProcessHandle target, uint access, bool inherit, uint options);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetProcessId(SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(SafeProcessHandle process, out ulong creation, out ulong exit, out ulong kernel, out ulong user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
}
