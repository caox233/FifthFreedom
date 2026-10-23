using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace SplinterCellCNLauncher.Services;

/// <summary>
/// Optional Public-only return channel. Its inputs require an independently
/// captured account/run context; this does not enable Public launch or routing.
/// Only the exact created process that acknowledged configuration can publish.
/// </summary>
public sealed class PublicNativeReturn : IAsyncDisposable
{
    private readonly ReleasePackage package;
    private readonly PublicSessionControlClient client;
    private readonly PublicReturnPipe server;
    private int prepared;
    public PublicNativeReturn(PublicDeploymentProfile profile, CredentialIdentity credential, string nativeEndpoint,
        ReleasePackage package, PublicNativeAccountAnchor account, string clientOverlayIp, string product, bool observerRequested)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(account);
        string username = account.Username;
        // Validate independent values now, before any process or pipe is created.
        _ = new PublicNativeRunExpectation(1, username, clientOverlayIp, product, new string('1', 32), observerRequested);
        if (!CredentialEnvironmentContext.ForPublic(profile).CreateIdentity(nativeEndpoint, username).Equals(credential)
            || !PublicNativeGameSession.IsClientAddress(profile, clientOverlayIp)) throw PublicNativeRunExpectation.Invalid();
        this.package = package;
        client = new PublicSessionControlClient(profile);
        server = new PublicReturnPipe((instance, responseAccountId, responseUsername, token) => PublicNativeGameSession.FromOwnedNativeResponse(
            profile, credential, nativeEndpoint, package, account, clientOverlayIp, product, instance, observerRequested, responseAccountId, responseUsername, token));
    }
    // Missing handoff is explicit; never falls back to an account token/password.
    public Task<System.Collections.ObjectModel.ReadOnlyCollection<PublicSessionSnapshot>> QueryCurrentAsync(CancellationToken cancellationToken = default)
    {
        var session = server.Current ?? throw new PublicControlException("PUBLIC_NATIVE_GAME_SESSION_MISSING");
        return client.QueryAsync(session, cancellationToken);
    }
    internal void Prepare(ProcessStartInfo start, Guid launchId)
    {
        start.Environment.Remove(PublicReturnProtocol.EnvironmentKey);
        if (Interlocked.Exchange(ref prepared, 1) != 0 || !OperatingSystem.IsWindows()) throw PublicReturnProtocol.Invalid();
        using var self = Process.GetCurrentProcess();
        using var owner = PublicReturnProcess.Capture(self, Environment.ProcessPath ?? throw PublicReturnProtocol.Invalid());
        var actual = ReleasePackage.Verify(owner.ImagePath);
        if (actual.ManifestSha256 != package.ManifestSha256 || actual.PackageRoot != package.PackageRoot) throw PublicReturnProtocol.Invalid();
        string digest;
        using (var image = File.OpenRead(owner.ImagePath)) digest = Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant();
        server.Start(launchId);
        start.Environment[PublicReturnProtocol.EnvironmentKey] = JsonSerializer.Serialize(new {
            schema = 1, pipe = server.Name, launch_id = launchId.ToString("N"), launcher_pid = owner.Pid,
            launcher_creation_filetime = owner.CreationFileTime, launcher_path = owner.ImagePath, launcher_sha256 = digest });
    }
    internal void CaptureCreated(Process exact, string executable) => server.CaptureCreated(exact, executable);
    internal void ConfigurationAcknowledged(Process peer) => server.ConfigurationAcknowledged(peer);
    public async ValueTask DisposeAsync() { client.Dispose(); await server.DisposeAsync().ConfigureAwait(false); }
    public override string ToString() => "PublicNativeReturn (owned channel; Public readiness not established)";
}

internal sealed class PublicReturnPipe : IAsyncDisposable
{
    internal string Name { get; } = "ff-public-return-" + Guid.NewGuid().ToString("N");
    private readonly PublicReturnState state;
    private readonly CancellationTokenSource stop = new();
    private readonly TaskCompletionSource admitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object gate = new();
    private PublicReturnProcess? owner;
    private (uint Pid, ulong Creation)? pendingAck;
    private Guid launchId;
    private Task pump = Task.CompletedTask, watch = Task.CompletedTask;
    private Task? disposal;
    private bool started, closed;
    internal PublicNativeGameSession? Current => state.Current;
    internal PublicReturnPipe(Func<string, string, string, string, PublicNativeGameSession> create) => state = new(create);
    internal void Start(Guid id)
    {
        lock (gate)
        {
            if (started || closed || id == Guid.Empty || !OperatingSystem.IsWindows()) throw PublicReturnProtocol.Invalid();
            started = true; launchId = id; pump = Task.Run(ServeAsync);
        }
    }
    internal void CaptureCreated(Process exact, string executable)
    {
        var captured = PublicReturnProcess.Capture(exact, executable);
        lock (gate)
        {
            if (!started || closed || owner != null) { captured.Dispose(); throw PublicReturnProtocol.Invalid(); }
            owner = captured;
            if (pendingAck is { } ack && owner.Matches(ack.Pid, ack.Creation)) admitted.TrySetResult();
            watch = Task.Run(WatchOwnerAsync);
        }
    }
    internal void ConfigurationAcknowledged(Process peer)
    {
        var observed = PublicReturnProcess.ObserveAcknowledged(peer);
        lock (gate)
        {
            if (closed) return;
            if (owner == null) pendingAck = observed;
            else if (owner.Matches(observed.Pid, observed.Creation)) admitted.TrySetResult();
            // A retail successor is intentionally not admitted by name or path.
        }
    }
    private async Task WatchOwnerAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                lock (gate) if (owner == null || !owner.Alive) { state.Disconnected(); stop.Cancel(); return; }
                await Task.Delay(100, stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }
    private async Task ServeAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(Name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop.Token).ConfigureAwait(false);
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                handshake.CancelAfter(TimeSpan.FromSeconds(10));
                // A connection may precede Process.Start returning to this thread,
                // but no challenge/token read is permitted before the exact ACK.
                await admitted.Task.WaitAsync(handshake.Token).ConfigureAwait(false);
                if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid)) throw PublicReturnProtocol.Invalid();
                lock (gate) if (owner == null || owner.Pid != pid || !owner.Alive) throw PublicReturnProtocol.Invalid();
                string challenge = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
                await PublicReturnProtocol.WriteAsync(pipe, new { schema = 1, kind = "challenge", launch_id = launchId.ToString("N"), challenge }, handshake.Token).ConfigureAwait(false);
                using (var hello = await PublicReturnProtocol.ReadAsync(pipe, handshake.Token).ConfigureAwait(false))
                {
                    var frame = hello.RootElement;
                    PublicReturnProtocol.Shape(frame, "schema", "kind", "launch_id", "challenge", "game_instance");
                    if (frame.GetProperty("kind").GetString() != "hello" || frame.GetProperty("launch_id").GetString() != launchId.ToString("N")
                        || frame.GetProperty("challenge").GetString() != challenge) throw PublicReturnProtocol.Invalid();
                    state.Hello(frame.GetProperty("game_instance").GetString()!);
                }
                handshake.CancelAfter(Timeout.InfiniteTimeSpan);
                bool first = true;
                while (!stop.IsCancellationRequested)
                {
                    using var frame = await PublicReturnProtocol.ReadAsync(pipe, stop.Token).ConfigureAwait(false);
                    lock (gate)
                    {
                        if (closed || owner == null || !owner.Alive) throw PublicReturnProtocol.Invalid();
                        state.Apply(frame.RootElement, first); first = false;
                    }
                }
            }
            catch (Exception)
            {
                state.Disconnected();
                // Fail closed; never print frames or exception text. Bound retry
                // frequency if a name collision or OS pipe error persists.
                try { await Task.Delay(100, stop.Token).ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
            finally { state.Disconnected(); }
        }
    }
    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposal != null) return new(disposal);
            closed = true; state.Dispose(); stop.Cancel();
            disposal = DrainAsync(); return new(disposal);
        }
    }
    private async Task DrainAsync()
    {
        await Task.WhenAll(pump, watch).ConfigureAwait(false);
        lock (gate) { owner?.Dispose(); owner = null; pendingAck = null; }
        stop.Dispose();
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
}
