using System.Diagnostics;

namespace Scbl.Server;

public sealed record ManagedProcessSpec(
    string Name,
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string>? Environment = null,
    bool RestartOnFailure = true);

public sealed class ManagedProcess : IAsyncDisposable
{
    private readonly ManagedProcessSpec _spec;
    private readonly ServerLog _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private Process? _process;
    private bool _stopping;
    private bool _disposed;

    public ManagedProcess(ManagedProcessSpec spec, ServerLog log)
    {
        _spec = spec;
        _log = log;
    }

    public string Name => _spec.Name;
    public int? ProcessId
    {
        get
        {
            Process? process = _process;
            return IsAlive(process) ? process!.Id : null;
        }
    }
    public bool IsRunning => IsAlive(_process);
    public DateTimeOffset? StartedAtUtc { get; private set; }
    public int RestartCount { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsRunning) return;
            _process?.Dispose();
            _process = null;
            if (!File.Exists(_spec.FileName)) throw new FileNotFoundException($"{_spec.Name} 程序不存在。", _spec.FileName);
            Directory.CreateDirectory(_spec.WorkingDirectory);
            _stopping = false;
            ProcessStartInfo start = new()
            {
                FileName = _spec.FileName,
                WorkingDirectory = _spec.WorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string argument in _spec.Arguments) start.ArgumentList.Add(argument);
            if (_spec.Environment is not null)
            {
                foreach ((string key, string value) in _spec.Environment) start.Environment[key] = value;
            }
            Process process = new() { StartInfo = start, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, eventArgs) => { if (eventArgs.Data is not null) _log.Write(_spec.Name, eventArgs.Data); };
            process.ErrorDataReceived += (_, eventArgs) => { if (eventArgs.Data is not null) _log.Write(_spec.Name + "/err", eventArgs.Data); };
            process.Exited += (_, _) => _ = OnExitedAsync(process);
            if (!process.Start()) throw new InvalidOperationException($"无法启动 {_spec.Name}。");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;
            StartedAtUtc = DateTimeOffset.UtcNow;
            _log.Write(_spec.Name, $"started pid={process.Id}");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _stopping = true;
            Process? process = _process;
            if (process is null || process.HasExited) return;
            _log.Write(_spec.Name, "stopping");
            try
            {
                if (process.CloseMainWindow())
                {
                    using CancellationTokenSource graceful = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    graceful.CancelAfter(TimeSpan.FromSeconds(8));
                    await process.WaitForExitAsync(graceful.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _process?.Dispose();
            _process = null;
            _gate.Release();
        }
    }

    private async Task OnExitedAsync(Process exited)
    {
        int code;
        try { code = exited.ExitCode; }
        catch (InvalidOperationException) { return; }
        _log.Write(_spec.Name, $"exited code={code}");
        bool restart;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(_process, exited)) _process = null;
            restart = !_disposed && !_stopping && _spec.RestartOnFailure;
        }
        finally { _gate.Release(); }
        exited.Dispose();
        if (restart) await RestartUntilStartedAsync().ConfigureAwait(false);
    }

    private async Task RestartUntilStartedAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(15, 2 + RestartCount)), _lifetime.Token).ConfigureAwait(false);
                RestartCount++;
                await StartAsync(_lifetime.Token).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                _log.Write(_spec.Name, $"restart attempt {RestartCount} failed: {error.Message}");
            }
        }
    }

    private static bool IsAlive(Process? process)
    {
        if (process is null) return false;
        try { return !process.HasExited; }
        catch (InvalidOperationException) { return false; }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _lifetime.Dispose();
    }
}
