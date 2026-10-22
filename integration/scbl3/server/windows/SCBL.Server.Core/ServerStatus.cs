namespace Scbl.Server;

public sealed record ComponentStatus(string Name, bool Running, int? ProcessId, int RestartCount, DateTimeOffset? StartedAtUtc);

public sealed record ServerStatus(
    bool Running,
    string Version,
    string Mode,
    string ServerId,
    string PublicHost,
    int GamePort,
    int UpdatePort,
    int ControlPort,
    IReadOnlyList<ComponentStatus> Components,
    DateTimeOffset CollectedAtUtc);
