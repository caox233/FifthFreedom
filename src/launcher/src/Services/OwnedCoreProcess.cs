using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SplinterCellCNLauncher.Services;

// A process primitive only. No actual EasyTier launch is wired to the Public UI.
// The job is assigned by CreateProcess, before any child instruction executes.
internal sealed class OwnedCoreProcess : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly JobHandle job;
    private readonly SafeProcessHandle process;
    private readonly SafeWaitHandle thread;
    private readonly PublicReturnProcess identity;
    private readonly OwnedCoreLaunchInputs.PinnedInputs inputs;
    private Task? stopping;
    private bool resumed, closed;
    internal uint ProcessId => identity.Pid;
    internal ulong CreationFileTime => identity.CreationFileTime;
    internal bool IsAlive { get { lock (gate) return !closed && identity.Alive; } }
    internal bool Matches(uint pid, ulong creation) { lock (gate) return !closed && identity.Matches(pid, creation); }
    private OwnedCoreProcess(JobHandle job, SafeProcessHandle process, SafeWaitHandle thread,
        PublicReturnProcess identity, OwnedCoreLaunchInputs.PinnedInputs inputs)
    { this.job = job; this.process = process; this.thread = thread; this.identity = identity; this.inputs = inputs; }
    private static IOException Failed(string code) => new(code);

    internal static OwnedCoreProcess CreateSuspended(OwnedCoreLaunchInputs specification, CancellationToken cancellationToken = default, OwnedCoreReadDescriptor? readDescriptor = null)
    {
        ArgumentNullException.ThrowIfNull(specification);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindowsVersionAtLeast(10)) throw new PlatformNotSupportedException("OWNED_CORE_WINDOWS_REQUIRED");
        var inputs = specification.Pin();
        JobHandle? job = null; SafeProcessHandle? process = null; SafeWaitHandle? thread = null; PublicReturnProcess? identity = null;
        IntPtr attributes = IntPtr.Zero, jobs = IntPtr.Zero, environment = IntPtr.Zero;
        bool initialized = false, transferred = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            job = CreateJobObjectW(IntPtr.Zero, null);
            if (job.IsInvalid) throw Failed("OWNED_CORE_JOB_FAILED");
            var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } };
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>())) throw Failed("OWNED_CORE_JOB_FAILED");
            nuint size = 0;
            _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            if (size is 0 or > 65536) throw Failed("OWNED_CORE_ATTRIBUTES_FAILED");
            attributes = Marshal.AllocHGlobal(checked((int)size));
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw Failed("OWNED_CORE_ATTRIBUTES_FAILED");
            initialized = true;
            jobs = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(jobs, job.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(attributes, 0, (nuint)0x2000D, jobs, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw Failed("OWNED_CORE_ATTRIBUTES_FAILED");
            // No ambient ET_*, credentials, DLL paths or Public-return descriptor.
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (string.IsNullOrWhiteSpace(windows) || string.IsNullOrWhiteSpace(Environment.SystemDirectory)) throw Failed("OWNED_CORE_ENVIRONMENT_FAILED");
            var temporary = Path.GetDirectoryName(specification.Configuration)!;
            if (readDescriptor is not null && !readDescriptor.Alive) throw Failed("OWNED_CORE_ENVIRONMENT_FAILED");
            string extra = readDescriptor is null ? "" : $"{OwnedCoreReadDescriptor.Variable}={readDescriptor.Serialized}\0";
            string block = extra + $"PATH={Environment.SystemDirectory}\0SystemRoot={windows}\0TEMP={temporary}\0TMP={temporary}\0WINDIR={windows}\0\0";
            environment = Marshal.StringToHGlobalUni(block);
            var startup = new StartupInfoEx { Startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfoEx>() }, Attributes = attributes };
            string command = $"\"{specification.Executable}\" --config-file \"{specification.Configuration}\" --disable-env-parsing --console-log-level off --file-log-level off" + specification.FixedRpcArguments;
            cancellationToken.ThrowIfCancellationRequested();
            if (!CreateProcessW(specification.Executable, new StringBuilder(command), IntPtr.Zero, IntPtr.Zero, false,
                0x00080000 | 0x00000400 | 0x00000004 | 0x08000000, environment,
                Path.GetDirectoryName(specification.Executable)!, ref startup, out var created)) throw Failed("OWNED_CORE_CREATE_FAILED");
            process = new SafeProcessHandle(created.Process, true);
            thread = new SafeWaitHandle(created.Thread, true);
            if (!IsProcessInJob(process, job, out bool inJob) || !inJob) throw Failed("OWNED_CORE_MEMBERSHIP_FAILED");
            identity = PublicReturnProcess.Capture(process, specification.Executable);
            cancellationToken.ThrowIfCancellationRequested();
            var result = new OwnedCoreProcess(job, process, thread, identity, inputs);
            transferred = true;
            return result;
        }
        finally
        {
            if (initialized) DeleteProcThreadAttributeList(attributes);
            if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
            if (jobs != IntPtr.Zero) Marshal.FreeHGlobal(jobs);
            if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
            if (!transferred)
            {
                // Closing our sole, non-inheritable job handle kills only this
                // attempt and its descendants, including a still-suspended child.
                job?.Dispose();
                // Membership validation may itself have failed. Retain authority
                // over the handle returned by this CreateProcess even then.
                if (process is not null && WaitForSingleObject(process, 0) == 0x102) _ = TerminateProcess(process, 1);
                bool exited = process is null || WaitForSingleObject(process, 5000) == 0;
                identity?.Dispose(); thread?.Dispose(); process?.Dispose(); inputs.Dispose();
                if (!exited) throw Failed("OWNED_CORE_CLEANUP_FAILED");
            }
        }
    }

    internal async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            lock (gate)
            {
                if (closed || stopping is not null || resumed) throw Failed("OWNED_CORE_STATE_INVALID");
                cancellationToken.ThrowIfCancellationRequested();
                if (!identity.Alive || !IsProcessInJob(process, job, out bool inJob) || !inJob) throw Failed("OWNED_CORE_MEMBERSHIP_FAILED");
                if (ResumeThread(thread) != 1) throw Failed("OWNED_CORE_RESUME_FAILED");
                resumed = true;
                thread.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        catch { await StopAsync().ConfigureAwait(false); throw; }
    }
    internal Task StopAsync()
    {
        lock (gate)
        {
            if (closed) return Task.CompletedTask;
            if (stopping is null || stopping.IsFaulted) stopping = DrainAsync();
            return stopping;
        }
    }
    private async Task DrainAsync()
    {
        long deadline = Environment.TickCount64 + 5000;
        if (!TerminateJobObject(job, 1)) throw Failed("OWNED_CORE_CLEANUP_FAILED");
        while (true)
        {
            uint wait = WaitForSingleObject(process, 0);
            if (!QueryInformationJobObject(job, 1, out var accounting, (uint)Marshal.SizeOf<BasicAccounting>(), IntPtr.Zero))
                throw Failed("OWNED_CORE_CLEANUP_FAILED");
            if (wait == 0 && accounting.ActiveProcesses == 0) break;
            if (wait is not (0 or 0x102) || Environment.TickCount64 >= deadline) throw Failed("OWNED_CORE_CLEANUP_FAILED");
            await Task.Delay(20).ConfigureAwait(false);
        }
        lock (gate)
        {
            job.Dispose(); identity.Dispose(); thread.Dispose(); process.Dispose(); inputs.Dispose(); closed = true;
        }
    }
    public ValueTask DisposeAsync() => new(StopAsync());
    public override string ToString() => "OwnedCoreProcess (exact owned process; no Public admission)";

    private sealed class JobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private JobHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {
        internal long ProcessTime, JobTime; internal uint Flags; internal nuint MinimumWorkingSet, MaximumWorkingSet;
        internal uint ActiveProcesses; internal nuint Affinity; internal uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    {
        internal BasicLimits Basic; internal ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        internal nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BasicAccounting
    {
        internal long TotalUserTime, TotalKernelTime, PeriodUserTime, PeriodKernelTime;
        internal uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        internal int Size; internal string? Reserved, Desktop, Title;
        internal int X, Y, XSize, YSize, XChars, YChars, Fill, Flags;
        internal short Show, ReservedBytes; internal IntPtr Reserved2, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { internal StartupInfo Startup; internal IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { internal IntPtr Process, Thread; internal uint Pid, Tid; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern JobHandle CreateJobObjectW(IntPtr security, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(JobHandle job, int informationClass, ref ExtendedLimits limits, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref nuint bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute, IntPtr value, nuint bytes, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInformation information);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsProcessInJob(SafeProcessHandle process, JobHandle job, out bool result);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(JobHandle job, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryInformationJobObject(JobHandle job, int informationClass, out BasicAccounting accounting, uint length, IntPtr returnedLength);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(SafeWaitHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(SafeProcessHandle process, uint code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
