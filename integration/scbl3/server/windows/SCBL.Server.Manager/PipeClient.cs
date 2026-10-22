using System.IO.Pipes;
using System.Text.Json;

namespace Scbl.Server.Manager;

public static class PipeClient
{
    public static async Task<PipeResponse> SendAsync(string command, string? argument = null, CancellationToken cancellationToken = default)
    {
        await using NamedPipeClientStream pipe = new(".", PipeProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        using StreamWriter writer = new(pipe, leaveOpen: true) { AutoFlush = true };
        using StreamReader reader = new(pipe, leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(new PipeRequest(command, argument)).AsMemory(), timeout.Token).ConfigureAwait(false);
        string? line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
        return line is null
            ? new PipeResponse(false, "服务未返回结果。")
            : JsonSerializer.Deserialize<PipeResponse>(line, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new PipeResponse(false, "服务返回无效结果。");
    }
}
