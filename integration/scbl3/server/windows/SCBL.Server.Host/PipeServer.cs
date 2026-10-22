using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace Scbl.Server.Host;

public sealed class PipeServer : IAsyncDisposable
{
    private readonly ServerRuntime _runtime;
    private readonly ServerLog _log;
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _listener;

    public PipeServer(ServerRuntime runtime, ServerLog log) { _runtime = runtime; _log = log; }

    public void Start() => _listener = Task.Run(ListenAsync);

    private async Task ListenAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            NamedPipeServerStream pipe = new(PipeProtocol.PipeName, PipeDirection.InOut, 8, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            try
            {
                await pipe.WaitForConnectionAsync(_cancellation.Token).ConfigureAwait(false);
                _ = Task.Run(() => HandleAsync(pipe, _cancellation.Token));
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { await pipe.DisposeAsync().ConfigureAwait(false); return; }
            catch (Exception error) { await pipe.DisposeAsync().ConfigureAwait(false); _log.Write("pipe", error.Message); }
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await using (pipe.ConfigureAwait(false))
        {
            using CancellationTokenSource requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            bool administrator = false;
            try
            {
                pipe.RunAsClient(() =>
                {
                    using WindowsIdentity identity = WindowsIdentity.GetCurrent();
                    administrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                });
            }
            catch (IOException) { }
            using StreamReader reader = new(pipe, leaveOpen: true);
            using StreamWriter writer = new(pipe, leaveOpen: true) { AutoFlush = true };
            try
            {
                string? line = await ReadBoundedLineAsync(reader, 8192, requestTimeout.Token).ConfigureAwait(false);
                PipeRequest? request = line is null ? null : JsonSerializer.Deserialize<PipeRequest>(line);
                PipeResponse response = request is null
                    ? new(false, "请求无效。")
                    : await DispatchAsync(request, administrator).ConfigureAwait(false);
                await writer.WriteLineAsync(JsonSerializer.Serialize(response).AsMemory(), requestTimeout.Token).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(new PipeResponse(false, error.Message))).ConfigureAwait(false);
            }
        }
    }

    private static async Task<string?> ReadBoundedLineAsync(StreamReader reader, int maximumCharacters, CancellationToken cancellationToken)
    {
        StringBuilder value = new();
        char[] buffer = new char[1];
        while (value.Length <= maximumCharacters)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) return value.Length == 0 ? null : value.ToString().TrimEnd('\r');
            if (buffer[0] == '\n') return value.ToString().TrimEnd('\r');
            value.Append(buffer[0]);
        }
        throw new InvalidDataException("管理请求超过大小限制。");
    }

    private Task<PipeResponse> DispatchAsync(PipeRequest request, bool administrator)
    {
        string command = request.Command.Trim().ToLowerInvariant();
        PipeResponse response;
        if (command == "status") response = new(true, "ok", _runtime.Status());
        else if (command == "logs" && administrator) response = new(true, "ok", Lines: _runtime.RecentLogs(int.TryParse(request.Argument, out int count) ? count : 200));
        else if (command == "logs") response = new(false, "读取服务端日志需要管理员权限。");
        else if (!administrator) response = new(false, "管理操作要求 SCBL.Server.exe 以管理员身份运行。");
        else response = new(false, "未知命令。");
        return Task.FromResult(response);
    }

    public async ValueTask DisposeAsync()
    {
        _cancellation.Cancel();
        if (_listener is not null)
        {
            try { await _listener.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _cancellation.Dispose();
    }
}
