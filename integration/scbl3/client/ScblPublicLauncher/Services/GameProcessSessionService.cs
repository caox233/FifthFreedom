using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace SplinterCellCNLauncher.Services;

internal sealed record GameProcessCandidate(
    int ProcessId,
    int ParentProcessId,
    string ImagePath,
    DateTime StartUtc,
    int SessionId);

internal sealed record GameProcessCandidateSnapshot(
    int ProcessId,
    int ParentProcessId,
    string ImagePath,
    DateTime StartUtc,
    int SessionId);

internal sealed class GameProcessCandidateObservationTracker
{
    private GameProcessCandidate? _lastCandidate;
    private int _consecutiveObservations;

    internal bool Observe(GameProcessCandidate candidate, int requiredObservations)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (requiredObservations <= 0)
            throw new ArgumentOutOfRangeException(nameof(requiredObservations));

        if (_lastCandidate == candidate)
        {
            _consecutiveObservations++;
        }
        else
        {
            _lastCandidate = candidate;
            _consecutiveObservations = 1;
        }

        return _consecutiveObservations >= requiredObservations;
    }

    internal void Reset()
    {
        _lastCandidate = null;
        _consecutiveObservations = 0;
    }
}

/// <summary>
/// Identifies game processes created during one launcher-owned start attempt without
/// falling back to global process-name ownership. Candidates are restricted to the two
/// retail game executables in the selected SYSTEM directory, the current Windows session,
/// and a bounded launch-time window.
/// </summary>
public sealed class GameProcessSessionService
{
    internal const int LaunchObservationWindowSeconds = 600;

    private static readonly string[] AllowedExecutableNames =
    {
        "Blacklist_game.exe",
        "Blacklist_DX11_game.exe"
    };

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint Th32csSnapProcess = 0x00000002;
    private const int ProcessPathBufferLength = 32768;
    private const int MaxPath = 260;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    public HashSet<int> CaptureExistingMatchingProcessIds(string expectedGamePath)
    {
        string systemDirectory = GetValidatedSystemDirectory(expectedGamePath);
        int currentWindowsSessionId = GetCurrentWindowsSessionId();
        return EnumerateCandidateSnapshots(systemDirectory)
            .Where(candidate => IsAllowedCandidatePath(systemDirectory, candidate.ImagePath))
            .Where(candidate => candidate.SessionId == currentWindowsSessionId)
            .Select(candidate => candidate.ProcessId)
            .ToHashSet();
    }

    public IReadOnlyList<int> FindNewMatchingProcessIds(
        string expectedGamePath,
        DateTime launchStartedUtc,
        IReadOnlyCollection<int> excludedPids)
    {
        return FindNewMatchingProcesses(
                expectedGamePath,
                launchStartedUtc.AddSeconds(-2),
                launchStartedUtc.AddSeconds(LaunchObservationWindowSeconds),
                excludedPids,
                Array.Empty<int>())
            .Select(candidate => candidate.ProcessId)
            .ToArray();
    }

    internal IReadOnlyList<GameProcessCandidate> FindNewMatchingProcesses(
        string expectedGamePath,
        DateTime minimumStartUtc,
        DateTime admissionDeadlineUtc,
        IReadOnlyCollection<int> excludedPids,
        IReadOnlyCollection<int> relatedPids)
    {
        if (DateTime.UtcNow > admissionDeadlineUtc)
            return Array.Empty<GameProcessCandidate>();

        string systemDirectory = GetValidatedSystemDirectory(expectedGamePath);
        IReadOnlyList<GameProcessCandidateSnapshot> snapshots = EnumerateCandidateSnapshots(systemDirectory);
        GameProcessCandidateSnapshot? candidate = SelectEligibleCandidate(
            systemDirectory,
            minimumStartUtc,
            admissionDeadlineUtc,
            GetCurrentWindowsSessionId(),
            excludedPids,
            relatedPids,
            snapshots);
        return candidate == null
            ? Array.Empty<GameProcessCandidate>()
            : new[]
            {
                new GameProcessCandidate(
                    candidate.ProcessId,
                    candidate.ParentProcessId,
                    candidate.ImagePath,
                    candidate.StartUtc,
                    candidate.SessionId)
            };
    }

    internal static GameProcessCandidateSnapshot? SelectEligibleCandidate(
        string systemDirectory,
        DateTime minimumStartUtc,
        DateTime admissionDeadlineUtc,
        int currentWindowsSessionId,
        IReadOnlyCollection<int> excludedPids,
        IReadOnlyCollection<int> relatedPids,
        IReadOnlyCollection<GameProcessCandidateSnapshot> snapshots)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemDirectory);
        ArgumentNullException.ThrowIfNull(excludedPids);
        ArgumentNullException.ThrowIfNull(relatedPids);
        ArgumentNullException.ThrowIfNull(snapshots);

        if (admissionDeadlineUtc < minimumStartUtc)
            return null;

        var excluded = excludedPids.Count == 0 ? new HashSet<int>() : excludedPids.ToHashSet();
        var related = relatedPids.Count == 0 ? new HashSet<int>() : relatedPids.ToHashSet();
        GameProcessCandidateSnapshot[] eligible = snapshots
            .Where(candidate => candidate.ProcessId > 0)
            .Where(candidate => !excluded.Contains(candidate.ProcessId))
            .Where(candidate => candidate.SessionId == currentWindowsSessionId)
            .Where(candidate => candidate.StartUtc >= minimumStartUtc && candidate.StartUtc <= admissionDeadlineUtc)
            .Where(candidate => IsAllowedCandidatePath(systemDirectory, candidate.ImagePath))
            .OrderBy(candidate => candidate.StartUtc)
            .ThenBy(candidate => candidate.ProcessId)
            .ToArray();

        if (eligible.Length == 0)
            return null;

        GameProcessCandidateSnapshot[] lineageMatches = eligible
            .Where(candidate => candidate.ParentProcessId > 0 && related.Contains(candidate.ParentProcessId))
            .ToArray();
        if (lineageMatches.Length == 1)
            return lineageMatches[0];
        if (lineageMatches.Length > 1)
            return null;

        // Some retail launch chains exit the selected process before Windows snapshots expose
        // a usable ancestry chain. The fallback remains bounded by directory, executable
        // whitelist, Windows session, launch window, pre-launch baseline and uniqueness. The
        // caller additionally requires two consecutive observations before ownership changes.
        return eligible.Length == 1 ? eligible[0] : null;
    }

    private static IReadOnlyList<GameProcessCandidateSnapshot> EnumerateCandidateSnapshots(string systemDirectory)
    {
        IReadOnlyDictionary<int, int> parentPids = CaptureParentProcessIds();
        var matches = new List<GameProcessCandidateSnapshot>();

        foreach (string executableName in AllowedExecutableNames)
        {
            string processName = Path.GetFileNameWithoutExtension(executableName);
            Process[] processes = Array.Empty<Process>();
            try
            {
                try
                {
                    processes = Process.GetProcessesByName(processName);
                }
                catch (Exception ex)
                {
                    LogService.Warning($"Game process candidate scan failed for {processName}: {ex.Message}");
                    continue;
                }

                foreach (Process process in processes)
                {
                    try
                    {
                        if (process.HasExited)
                            continue;
                        if (!TryGetProcessImagePath(process, out string actualPath))
                            continue;
                        if (!IsAllowedCandidatePath(systemDirectory, actualPath))
                            continue;

                        matches.Add(new GameProcessCandidateSnapshot(
                            process.Id,
                            parentPids.GetValueOrDefault(process.Id),
                            actualPath,
                            process.StartTime.ToUniversalTime(),
                            process.SessionId));
                    }
                    catch
                    {
                        // Fail closed: a process that cannot be fully verified is never adopted.
                    }
                }
            }
            finally
            {
                foreach (Process process in processes)
                    process.Dispose();
            }
        }

        return matches;
    }

    private static string GetValidatedSystemDirectory(string expectedGamePath)
    {
        string expectedFullPath = Path.GetFullPath(expectedGamePath);
        string fileName = Path.GetFileName(expectedFullPath);
        if (!AllowedExecutableNames.Contains(fileName, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("The expected game executable is not allowlisted.", nameof(expectedGamePath));
        return Path.GetDirectoryName(expectedFullPath)
            ?? throw new ArgumentException("The expected game path has no parent directory.", nameof(expectedGamePath));
    }

    private static int GetCurrentWindowsSessionId()
    {
        using Process currentProcess = Process.GetCurrentProcess();
        return currentProcess.SessionId;
    }

    private static bool IsAllowedCandidatePath(string systemDirectory, string actualPath)
    {
        try
        {
            string actualFullPath = Path.GetFullPath(actualPath);
            string? actualDirectory = Path.GetDirectoryName(actualFullPath);
            string actualFileName = Path.GetFileName(actualFullPath);
            return actualDirectory != null
                && PathsEqual(systemDirectory, actualDirectory)
                && AllowedExecutableNames.Contains(actualFileName, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool PathsEqual(string expectedPath, string actualPath)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedPath)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(actualPath)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyDictionary<int, int> CaptureParentProcessIds()
    {
        var result = new Dictionary<int, int>();
        IntPtr snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == InvalidHandleValue)
            return result;

        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snapshot, ref entry))
                return result;
            do
            {
                if (entry.th32ProcessID > 0 && entry.th32ProcessID <= int.MaxValue)
                {
                    result[(int)entry.th32ProcessID] = entry.th32ParentProcessID <= int.MaxValue
                        ? (int)entry.th32ParentProcessID
                        : 0;
                }
                entry.dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>();
            }
            while (Process32Next(snapshot, ref entry));
        }
        catch
        {
            result.Clear();
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return result;
    }

    private static bool TryGetProcessImagePath(Process process, out string path)
    {
        path = "";
        try
        {
            string? mainModulePath = process.MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(mainModulePath))
            {
                path = mainModulePath;
                return true;
            }
        }
        catch
        {
        }

        IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, process.Id);
        if (handle == IntPtr.Zero)
            return false;
        try
        {
            var buffer = new StringBuilder(ProcessPathBufferLength);
            int size = buffer.Capacity;
            if (!QueryFullProcessImageName(handle, 0, buffer, ref size) || size <= 0)
                return false;
            path = buffer.ToString(0, size);
            return !string.IsNullOrWhiteSpace(path);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxPath)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint processAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        IntPtr processHandle,
        int flags,
        StringBuilder executablePath,
        ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
