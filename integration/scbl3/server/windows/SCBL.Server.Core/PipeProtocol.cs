namespace Scbl.Server;

public static class PipeProtocol
{
    public const string PipeName = "SCBL.Server.Host.v1";
}

public sealed record PipeRequest(string Command, string? Argument = null);
public sealed record PipeResponse(bool Success, string Message, ServerStatus? Status = null, IReadOnlyList<string>? Lines = null);
