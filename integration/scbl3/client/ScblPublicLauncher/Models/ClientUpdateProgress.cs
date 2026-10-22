namespace SplinterCellCNLauncher.Models;

public enum ClientUpdateProgressStage
{
    FetchingDescriptor,
    VerifyingDescriptor,
    CheckingComponents,
    Downloading,
    VerifyingArtifacts,
    Activating,
    Completed,
}

public sealed record ClientUpdateProgress(
    ClientUpdateProgressStage Stage,
    bool IsIndeterminate,
    long BytesCompleted = 0,
    long BytesTotal = 0,
    double? OverallPercentage = null)
{
    public double Percentage => OverallPercentage.HasValue
        ? Math.Clamp(OverallPercentage.Value, 0d, 100d)
        : BytesTotal > 0
            ? Math.Clamp(BytesCompleted * 100d / BytesTotal, 0d, 100d)
            : 0d;
}
