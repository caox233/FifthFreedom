using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace SplinterCellCNLauncher.Services;

// A single owned attempt. Closing/EOF/cancellation permanently ends it. Neither
// this channel nor its historical responses constitute Public admission.
internal sealed class OwnedCoreReadChannel : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly OwnedCoreReadDescriptor descriptor;
    private readonly NamedPipeServerStream pipe;
    private readonly OwnedCoreProcess owner;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim requestGate = new(1,1);
    private Task? receiving, watching, disposing;
    private TaskCompletionSource<JsonDocument>? pending;
    private ulong sequence;
    private bool closed, cancellationFailed;
    private Task cancellationNotification = Task.CompletedTask;
    internal CancellationToken Lifetime => lifetime.Token;
    internal bool IsOpen { get { lock (gate) return !closed && owner.IsAlive; } }
    internal bool IsOwnedProcessAlive => owner.IsAlive;
    // End only this read attempt, giving the real endpoint EOF. Input pins and
    // exact job ownership remain held until DisposeAsync. This does not confirm
    // Instance/NIC cleanup; callers must independently observe it, then drain.
    internal void EndReads() => Close();
    private OwnedCoreReadChannel(OwnedCoreReadDescriptor descriptor, NamedPipeServerStream pipe, OwnedCoreProcess owner)
    { this.descriptor = descriptor; this.pipe = pipe; this.owner = owner; }
    internal static async Task<OwnedCoreReadChannel> StartAsync(OwnedCoreLaunchInputs inputs, string instance, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var descriptor = OwnedCoreReadDescriptor.Create(instance);
        NamedPipeServerStream? pipe = null; OwnedCoreProcess? owner = null; OwnedCoreReadChannel? result = null;
        try
        {
            pipe = LocalOnlyPipeServer.Create(descriptor.Pipe, OwnedCoreReadProtocol.Invalid);
            owner = OwnedCoreProcess.CreateSuspended(inputs,cancellationToken,descriptor);
            result = new(descriptor,pipe,owner);
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,result.lifetime.Token);
            startup.CancelAfter(TimeSpan.FromSeconds(10));
            result.watching = result.WatchOwner();
            await owner.ResumeAsync(startup.Token).ConfigureAwait(false);
            await pipe.WaitForConnectionAsync(startup.Token).ConfigureAwait(false);
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle,out uint pid) || !owner.Matches(pid,owner.CreationFileTime)) throw OwnedCoreReadProtocol.Invalid();
            string nonce = OwnedCoreReadProtocol.Nonce();
            await OwnedCoreReadProtocol.Write(pipe,OwnedCoreReadProtocol.Challenge(descriptor.Attempt,nonce,instance),startup.Token).ConfigureAwait(false);
            using (var hello = await OwnedCoreReadProtocol.Read(pipe,startup.Token).ConfigureAwait(false))
                OwnedCoreReadProtocol.Hello(hello.RootElement,descriptor.Attempt,nonce,instance);
            startup.Token.ThrowIfCancellationRequested();
            if (!owner.IsAlive) throw OwnedCoreReadProtocol.Invalid();
            result.receiving = result.Receive();
            return result;
        }
        catch
        {
            if (result is not null) await result.DisposeAsync().ConfigureAwait(false);
            else { pipe?.Dispose(); if (owner is not null) await owner.DisposeAsync().ConfigureAwait(false); descriptor.Dispose(); }
            throw;
        }
    }
    internal async Task<OwnedCoreNicSnapshot?> ObserveAsync(string source, CancellationToken cancellationToken = default)
    {
        source = OwnedCoreReadProtocol.Source(source);
        cancellationToken.ThrowIfCancellationRequested();
        // No hidden queue: only one outstanding request is allowed.
        if (!await requestGate.WaitAsync(0,cancellationToken).ConfigureAwait(false)) throw OwnedCoreReadProtocol.Invalid();
        TaskCompletionSource<JsonDocument>? response = null; JsonDocument? document = null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(3));
            string nonce = OwnedCoreReadProtocol.Nonce(); ulong request;
            lock (gate)
            {
                if (closed || !owner.IsAlive || sequence == ulong.MaxValue) throw OwnedCoreReadProtocol.Invalid();
                request = ++sequence;
                response = pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            await OwnedCoreReadProtocol.Write(pipe,new { schema = 1, kind = "read", attempt = descriptor.Attempt,
                nonce, instance = descriptor.Instance, request, source },deadline.Token).ConfigureAwait(false);
            document = await response.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            var snapshot = OwnedCoreReadProtocol.Response(document.RootElement,descriptor.Attempt,nonce,descriptor.Instance,request,source);
            lock (gate)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (closed || !owner.IsAlive) throw OwnedCoreReadProtocol.Invalid();
                return snapshot;
            }
        }
        catch { Close(); throw; }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(pending,response)) pending = null;
                if (document is null && response?.Task.IsCompletedSuccessfully == true) document = response.Task.Result;
            }
            document?.Dispose(); requestGate.Release();
        }
    }
    private async Task Receive()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var document = await OwnedCoreReadProtocol.Read(pipe,lifetime.Token).ConfigureAwait(false);
                lock (gate)
                {
                    if (closed || pending is null || !pending.TrySetResult(document)) { document.Dispose(); throw OwnedCoreReadProtocol.Invalid(); }
                }
            }
        }
        catch { Close(); }
    }
    private async Task WatchOwner()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                if (!owner.IsAlive || !descriptor.Alive) { Close(); return; }
                await Task.Delay(50,lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }
    private void Close()
    {
        lock (gate)
        {
            if (closed) return;
            closed = true;
            // Public token callbacks are arbitrary caller code. Run them off
            // this gate and never let a throwing callback bypass owned cleanup.
            try { cancellationNotification = lifetime.CancelAsync(); }
            catch { cancellationFailed = true; }
            try { pipe.Dispose(); }
            catch { cancellationFailed = true; }
            pending?.TrySetException(OwnedCoreReadProtocol.Invalid());
        }
    }
    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            Close();
            if (disposing is null || disposing.IsFaulted) disposing = Drain();
            return new(disposing);
        }
    }
    private async Task Drain()
    {
        // First discharge exact kernel process ownership. User cancellation
        // callbacks cannot defer this step or retain the child's input pins.
        await owner.StopAsync().ConfigureAwait(false);
        bool failed = false;
        try { if (receiving is not null) await receiving.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch { failed = true; }
        try { if (watching is not null) await watching.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch { failed = true; }
        if (!await requestGate.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false))
            throw new IOException("OWNED_CORE_READ_CLEANUP_FAILED");
        try { descriptor.Dispose(); }
        finally { requestGate.Release(); }
        try { await cancellationNotification.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch { failed = true; }
        if (failed || cancellationFailed) throw new IOException("OWNED_CORE_READ_CLEANUP_FAILED");
        // The token remains safe to inspect after disposal. No callback can
        // reopen the closed attempt or acquire a new process/session owner.
    }
    public override string ToString() => "OwnedCoreReadChannel (historical reads only; no admission)";
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe,out uint pid);
}
