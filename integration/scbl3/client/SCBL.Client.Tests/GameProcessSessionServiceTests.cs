using SplinterCellCNLauncher.Services;

namespace SCBL.Client.Tests;

public sealed class GameProcessSessionServiceTests : IDisposable
{
    private readonly string _systemDirectory = Path.Combine(
        Path.GetTempPath(),
        "scbl-game-process-session-tests",
        Guid.NewGuid().ToString("N"),
        "SYSTEM");
    private readonly DateTime _launchUtc = new(2026, 8, 9, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("Blacklist_game.exe", "Blacklist_DX11_game.exe")]
    [InlineData("Blacklist_DX11_game.exe", "Blacklist_game.exe")]
    public void CrossModeReplacementInSameSystemDirectoryIsEligible(
        string initialExecutable,
        string replacementExecutable)
    {
        _ = initialExecutable;
        GameProcessCandidateSnapshot candidate = Candidate(
            pid: 202,
            executable: replacementExecutable,
            parentPid: 101,
            startOffsetSeconds: 6);

        GameProcessCandidateSnapshot? selected = Select(
            new[] { candidate },
            relatedPids: new[] { 101 });

        Assert.Same(candidate, selected);
    }

    [Fact]
    public void SameModeRestartUsesUniqueBoundedCandidate()
    {
        GameProcessCandidateSnapshot candidate = Candidate(
            pid: 303,
            executable: "Blacklist_game.exe",
            parentPid: 0,
            startOffsetSeconds: 8);

        Assert.Same(candidate, Select(new[] { candidate }));
    }

    [Fact]
    public void DelayedRetailReplacementWithinLaunchObservationWindowIsEligible()
    {
        GameProcessCandidateSnapshot candidate = Candidate(
            pid: 304,
            executable: "Blacklist_DX11_game.exe",
            parentPid: 0,
            startOffsetSeconds: 35);

        Assert.Same(candidate, Select(new[] { candidate }));
    }

    [Fact]
    public void PreExistingOtherModeProcessIsExcluded()
    {
        GameProcessCandidateSnapshot candidate = Candidate(
            pid: 404,
            executable: "Blacklist_DX11_game.exe",
            parentPid: 101,
            startOffsetSeconds: 4);

        Assert.Null(Select(
            new[] { candidate },
            excludedPids: new[] { candidate.ProcessId },
            relatedPids: new[] { 101 }));
    }

    [Fact]
    public void SameNameInDifferentDirectoryIsRejected()
    {
        string otherDirectory = Path.Combine(Path.GetDirectoryName(_systemDirectory)!, "OTHER");
        GameProcessCandidateSnapshot candidate = Candidate(
            pid: 505,
            executable: "Blacklist_game.exe",
            parentPid: 101,
            startOffsetSeconds: 5,
            directory: otherDirectory);

        Assert.Null(Select(new[] { candidate }, relatedPids: new[] { 101 }));
    }

    [Fact]
    public void CandidateFromAnotherWindowsSessionIsRejected()
    {
        GameProcessCandidateSnapshot candidate = Candidate(
            pid: 606,
            executable: "Blacklist_DX11_game.exe",
            parentPid: 101,
            startOffsetSeconds: 5,
            sessionId: 18);

        Assert.Null(Select(
            new[] { candidate },
            currentSessionId: 17,
            relatedPids: new[] { 101 }));
    }

    [Theory]
    [InlineData(-3)]
    [InlineData(601)]
    public void CandidateOutsideLaunchWindowIsRejected(int startOffsetSeconds)
    {
        GameProcessCandidateSnapshot candidate = Candidate(
            pid: 707,
            executable: "Blacklist_game.exe",
            parentPid: 101,
            startOffsetSeconds: startOffsetSeconds);

        Assert.Null(Select(new[] { candidate }, relatedPids: new[] { 101 }));
    }

    [Fact]
    public void RelatedChildWinsOverUnrelatedCandidate()
    {
        GameProcessCandidateSnapshot unrelated = Candidate(
            pid: 801,
            executable: "Blacklist_game.exe",
            parentPid: 0,
            startOffsetSeconds: 4);
        GameProcessCandidateSnapshot related = Candidate(
            pid: 802,
            executable: "Blacklist_DX11_game.exe",
            parentPid: 101,
            startOffsetSeconds: 5);

        Assert.Same(
            related,
            Select(new[] { unrelated, related }, relatedPids: new[] { 101 }));
    }

    [Fact]
    public void AmbiguousUnrelatedCandidatesFailClosed()
    {
        GameProcessCandidateSnapshot first = Candidate(
            pid: 901,
            executable: "Blacklist_game.exe",
            parentPid: 0,
            startOffsetSeconds: 4);
        GameProcessCandidateSnapshot second = Candidate(
            pid: 902,
            executable: "Blacklist_DX11_game.exe",
            parentPid: 0,
            startOffsetSeconds: 5);

        Assert.Null(Select(new[] { first, second }));
    }

    [Fact]
    public void CandidateRequiresTwoConsecutiveIdentityObservations()
    {
        var tracker = new GameProcessCandidateObservationTracker();
        GameProcessCandidate firstIdentity = RuntimeCandidate(
            pid: 1001,
            executable: "Blacklist_DX11_game.exe",
            startOffsetSeconds: 5);
        GameProcessCandidate reusedPidIdentity = RuntimeCandidate(
            pid: 1001,
            executable: "Blacklist_DX11_game.exe",
            startOffsetSeconds: 6);

        Assert.False(tracker.Observe(firstIdentity, requiredObservations: 2));
        Assert.True(tracker.Observe(firstIdentity, requiredObservations: 2));

        tracker.Reset();
        Assert.False(tracker.Observe(firstIdentity, requiredObservations: 2));
        Assert.False(tracker.Observe(reusedPidIdentity, requiredObservations: 2));
        Assert.True(tracker.Observe(reusedPidIdentity, requiredObservations: 2));
    }

    [Fact]
    public void MonitorPublishesAdoptedPidAndValidatesTheActualExecutable()
    {
        string source = File.ReadAllText(FindRepositoryFile(
            "client",
            "ScblPublicLauncher",
            "MainWindow.GameProcessMonitor.cs"));

        Assert.Contains("AddLauncherOwnedGamePid(candidate.ProcessId);", source, StringComparison.Ordinal);
        Assert.Contains(
            "_processRouterService.UpdateGameSession(liveOwnedPids, allowEmptyGamePids: true);",
            source,
            StringComparison.Ordinal);
        Assert.Contains("_gameRuntimeValidationService.ValidateAsync(", source, StringComparison.Ordinal);
        Assert.Contains("validatedProcessId,", source, StringComparison.Ordinal);
        Assert.Contains("activeGamePath,", source, StringComparison.Ordinal);
        Assert.Contains("expectedHookPath,", source, StringComparison.Ordinal);
        Assert.Contains("token)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_gameRuntimeValidationService.Validate(", source, StringComparison.Ordinal);
        Assert.Contains("_processRouterService.Stop(\"game launch timed out\");", source, StringComparison.Ordinal);
        Assert.Contains("DateTime candidateAdmissionDeadline = deadline;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GameProcessHandoffWindowSeconds", source, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        string? root = Directory.GetParent(_systemDirectory)?.FullName;
        if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private GameProcessCandidateSnapshot? Select(
        IReadOnlyCollection<GameProcessCandidateSnapshot> snapshots,
        IReadOnlyCollection<int>? excludedPids = null,
        IReadOnlyCollection<int>? relatedPids = null,
        int currentSessionId = 17)
    {
        return GameProcessSessionService.SelectEligibleCandidate(
            _systemDirectory,
            _launchUtc.AddSeconds(-2),
            _launchUtc.AddSeconds(GameProcessSessionService.LaunchObservationWindowSeconds),
            currentSessionId,
            excludedPids ?? Array.Empty<int>(),
            relatedPids ?? Array.Empty<int>(),
            snapshots);
    }

    private GameProcessCandidateSnapshot Candidate(
        int pid,
        string executable,
        int parentPid,
        int startOffsetSeconds,
        int sessionId = 17,
        string? directory = null)
    {
        return new GameProcessCandidateSnapshot(
            pid,
            parentPid,
            Path.Combine(directory ?? _systemDirectory, executable),
            _launchUtc.AddSeconds(startOffsetSeconds),
            sessionId);
    }

    private GameProcessCandidate RuntimeCandidate(
        int pid,
        string executable,
        int startOffsetSeconds)
    {
        return new GameProcessCandidate(
            ProcessId: pid,
            ParentProcessId: 101,
            ImagePath: Path.Combine(_systemDirectory, executable),
            StartUtc: _launchUtc.AddSeconds(startOffsetSeconds),
            SessionId: 17);
    }

    private static string FindRepositoryFile(params string[] relativeSegments)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory != null;
             directory = directory.Parent)
        {
            string candidate = Path.Combine(
                new[] { directory.FullName }.Concat(relativeSegments).ToArray());
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException(
            "Could not locate repository source file for the game-process monitor contract test.",
            Path.Combine(relativeSegments));
    }
}
