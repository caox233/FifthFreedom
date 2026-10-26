using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using SplinterCellCNLauncher.Services;

internal static partial class Program
{
    private static async Task LocalPipeBoundary()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // Keep the former production constructor as a real kernel negative
        // control: its current-user ACL does not imply local-only transport.
        using var legacy = new NamedPipeServerStream("ff-legacy-boundary-" + Guid.NewGuid().ToString("N"),
            PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Check((ReadPipeInformation(legacy.SafePipeHandle, "LEGACY").NamedPipeType & 2) == 0);
        Console.WriteLine("PIPE_BOUNDARY_PASS legacy_acl_does_not_imply_local_transport");

        string name = "ff-local-boundary-" + Guid.NewGuid().ToString("N");
        using var local = LocalOnlyPipeServer.Create(name, PublicReturnProtocol.Invalid);
        var info = ReadPipeInformation(local.SafePipeHandle, "FACTORY");
        Check(info.NamedPipeType == 2 && info.NamedPipeConfiguration == 2 && info.MaximumInstances == 1 && info.NamedPipeEnd == 1);
        Check(local.IsAsync && GetHandleInformation(local.SafePipeHandle, out uint flags) && (flags & 1) == 0);
        CheckCurrentUserOnly(local.SafePipeHandle);
        Console.WriteLine("PIPE_BOUNDARY_PASS factory_flags_and_current_user_acl");
        await Reject(() => { using var collision = LocalOnlyPipeServer.Create(name, PublicReturnProtocol.Invalid); return Task.CompletedTask; });

        using var sameUser = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        Task accepted = local.WaitForConnectionAsync(deadline.Token);
        await sameUser.ConnectAsync(deadline.Token); await accepted;
        await sameUser.WriteAsync(new byte[] { 0x5a }, deadline.Token);
        byte[] received = new byte[1];
        Check(await local.ReadAsync(received, deadline.Token) == 1 && received[0] == 0x5a);
        Console.WriteLine("PIPE_BOUNDARY_PASS first_instance_and_local_round_trip");

        // Exercise the actual production receiver, not just an independently
        // constructed factory stream. Query its connected client handle, whose
        // local information describes the same kernel pipe and creation mode.
        await using var production = new PublicReturnPipe(Create);
        production.Start(Guid.NewGuid());
        using var client = new NamedPipeClientStream(".", production.Name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(deadline.Token);
        var actual = ReadPipeInformation(client.SafePipeHandle, "PRODUCTION");
        Check(actual.NamedPipeType == 2 && actual.NamedPipeEnd == 0 && production.Current == null);
    }
    private static PipeLocalInformation ReadPipeInformation(SafePipeHandle handle, string role)
    {
        // NT's FILE_PIPE_REJECT_REMOTE_CLIENTS is 2, distinct from Win32's 8.
        // GetNamedPipeInfo only exposes the message/type and end flags.
        int status = NtQueryInformationFile(handle, out var io, out var information,
            (uint)Marshal.SizeOf<PipeLocalInformation>(), 24); // FilePipeLocalInformation
        string type = information.NamedPipeType switch { 0 => "BYTE", 1 => "MESSAGE", 2 => "LOCAL_BYTE", 3 => "LOCAL_MESSAGE", _ => "OTHER" };
        string end = information.NamedPipeEnd switch { 0 => "CLIENT", 1 => "SERVER", _ => "OTHER" };
        Console.WriteLine($"PIPE_BOUNDARY_QUERY role={role} status={(status == 0 ? "SUCCESS" : "FAILURE")} type={type} end={end}");
        Check(status == 0 && io.Result.Status == 0 && io.Information == (nuint)Marshal.SizeOf<PipeLocalInformation>());
        return information;
    }
    private static void CheckCurrentUserOnly(SafePipeHandle handle)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        Check(GetSecurityInfo(handle, 6, 5, out _, out _, out _, out _, out var security) == 0);
        try
        {
            byte[] bytes = new byte[checked((int)GetSecurityDescriptorLength(security))];
            Marshal.Copy(security, bytes, 0, bytes.Length);
            var descriptor = new RawSecurityDescriptor(bytes, 0);
            using var current = WindowsIdentity.GetCurrent();
            Check(descriptor.Owner == current.User && descriptor.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected));
            Check(descriptor.DiscretionaryAcl is { Count: 1 });
            Check(descriptor.DiscretionaryAcl![0] is CommonAce { AceQualifier: AceQualifier.AccessAllowed, AceFlags: AceFlags.None,
                AccessMask: 0x001F019F } ace && ace.SecurityIdentifier == current.User);
        }
        finally { _ = LocalFree(security); }
    }
    [StructLayout(LayoutKind.Explicit)] private struct IoStatusValue
    {
        [FieldOffset(0)] internal int Status;
        [FieldOffset(0)] internal IntPtr Pointer;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoStatusBlock { internal IoStatusValue Result; internal nuint Information; }
    [StructLayout(LayoutKind.Sequential)] private struct PipeLocalInformation
    {
        internal uint NamedPipeType, NamedPipeConfiguration, MaximumInstances, CurrentInstances, InboundQuota,
            ReadDataAvailable, OutboundQuota, WriteQuotaAvailable, NamedPipeState, NamedPipeEnd;
    }
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationFile(SafePipeHandle handle, out IoStatusBlock io, out PipeLocalInformation information, uint length, int informationClass);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetHandleInformation(SafePipeHandle handle, out uint flags);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityInfo(SafePipeHandle handle, int objectType, uint information, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityDescriptorLength(IntPtr security);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
