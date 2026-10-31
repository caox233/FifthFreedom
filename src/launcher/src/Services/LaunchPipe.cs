using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ScblLan;

public sealed class LaunchPipe : IAsyncDisposable
{
    public string Name { get; } = "scbl-lan-" + Guid.NewGuid().ToString("N");
    private readonly NamedPipeServerStream pipe;
    private readonly byte[] config;
    internal SplinterCellCNLauncher.Services.RequestedConfigurationProjection RequestedConfigurationProjection { get; }
    private readonly string expectedExecutable;
    private readonly DateTime started = DateTime.UtcNow;
    private int disposed;
    public LaunchPipe(string config, string executable)
    {
        this.config = Encoding.UTF8.GetBytes(config); expectedExecutable = Path.GetFullPath(executable);
        RequestedConfigurationProjection = SplinterCellCNLauncher.Services.RequestedConfigurationSnapshot.Prepare(this.config);
        pipe = new(Name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }
    public event Action<Process>? PeerValidated;
    internal event Action<Process>? ConfigurationAcknowledged;
    public async Task DeliverAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed != 0,this);
        await pipe.WaitForConnectionAsync(ct);
        try {
        using var peerTimeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
        peerTimeout.CancelAfter(TimeSpan.FromSeconds(10));
        ct=peerTimeout.Token;
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid)) throw new IOException("无法确认游戏进程身份。");
        using var client = Process.GetProcessById(checked((int)pid));
        string actual = client.MainModule?.FileName ?? "";
        string expectedDir = Path.GetDirectoryName(expectedExecutable)!;
        if (!string.Equals(Path.GetDirectoryName(actual), expectedDir, StringComparison.OrdinalIgnoreCase)
            || !GameRuntime.ExecutableNames.Contains(Path.GetFileName(actual), StringComparer.OrdinalIgnoreCase)
            || client.StartTime.ToUniversalTime() < started.AddSeconds(-1))
            throw new IOException("拒绝向非本次游戏进程交付认证信息。");
        // Diagnostic observers cannot block or alter credential delivery.
        if(PeerValidated is { } observers) foreach(Action<Process> observer in observers.GetInvocationList())
            try { observer(client); } catch { }
        await pipe.WriteAsync(BitConverter.GetBytes(config.Length), ct);
        await pipe.WriteAsync(config, ct);
        await pipe.FlushAsync(ct);
        byte[] acknowledgement = new byte[1];
        await pipe.ReadExactlyAsync(acknowledgement, ct);
        if (acknowledgement[0] != 1) throw new IOException("游戏未确认收到配置。");
        // Separate from diagnostic peer observation: Public admission may only
        // use a successful ACK, and still requires an independently owned handle.
        if(ConfigurationAcknowledged is { } acknowledgers) foreach(Action<Process> observer in acknowledgers.GetInvocationList())
            try { observer(client); } catch { }
        } finally {if(pipe.IsConnected) pipe.Disconnect();}
        // Keep only the in-memory frame for a later retail handoff process.
        // DisposeAsync zeroes it when the launch session ends.
    }
    public async ValueTask DisposeAsync() { if(Interlocked.Exchange(ref disposed,1)!=0) return;CryptographicOperations.ZeroMemory(config); await pipe.DisposeAsync(); }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
}
