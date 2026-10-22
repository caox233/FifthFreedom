using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SplinterCellCNLauncher.Services;

/// <summary>
/// Builds a support bundle without stopping or reconfiguring the game network.
/// Text files are copied through a redaction pass; game saves and credentials are never included.
/// </summary>
public sealed class DiagnosticExportService
{
    internal const string FailureSnapshotFileName = "last-network-failure.json";
    private static readonly Regex ProtectedJsonFieldRegex = new(
        "(?i)(\\\"(?:Password|PasswordProtected|TunnelSecret|TunnelSecretProtected|network_secret)\\\"\\s*:\\s*)\\\"[^\\\"]*\\\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CompleteGuidRegex = new(
        "(?i)\\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex HooksIdentityFieldRegex = new(
        "(?ix)(\\b(?:account_id|sender_id|guest_id|proof_sender_id|username|display_name|sender_name|friend_name|sender)\\b\\s*[:=]\\s*)(?:\\\"(?:\\\\.|[^\\\"])*\\\"|[^\\s,}\\]]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex RawSessionVectorRegex = new(
        "(?is)\\bdata\\s*:\\s*\\[(?<values>(?:\\s*\\d{1,3}\\s*,?){32,})\\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex LegacyEventTypeRegex = new(
        "\\b(?:PartyGameInviteAccepted|FriendsPrivateBlobInviteAccepted|FriendsGameInviteAccepted|PartyGameInvite|UplayGameSession|FriendsFriendListSnapshot|UserAccountSharing|FriendsFriendListUpdated)\\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task<string> ExportAsync(
        string launcherBaseDirectory,
        string launcherVersion,
        string assignedVirtualIp,
        string gameDirectory,
        bool gameSessionActive,
        CancellationToken cancellationToken = default)
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string workRoot = Path.Combine(LogService.DiagnosticsDirectory, "work", $"SCBL_Diagnostics_{stamp}_{Guid.NewGuid():N}");
        string exportDirectory = LogService.DiagnosticsDirectory;
        Directory.CreateDirectory(exportDirectory);

        string zipPath = MakeUniquePath(Path.Combine(exportDirectory, $"SCBL_Diagnostics_{stamp}.zip"));
        Directory.CreateDirectory(workRoot);
        try
        {
            string filesDir = Path.Combine(workRoot, "files");
            string commandsDir = Path.Combine(workRoot, "commands");
            Directory.CreateDirectory(filesDir);
            Directory.CreateDirectory(commandsDir);

            string summary = BuildSummary(
                launcherBaseDirectory,
                launcherVersion,
                assignedVirtualIp,
                gameDirectory,
                gameSessionActive);
            await File.WriteAllTextAsync(Path.Combine(workRoot, "summary.txt"), summary, new UTF8Encoding(false), cancellationToken);

            await CopyKnownTextFilesAsync(launcherBaseDirectory, filesDir, cancellationToken);
            await CollectCommandsAsync(launcherBaseDirectory, commandsDir, cancellationToken);

            await File.WriteAllTextAsync(
                Path.Combine(workRoot, "README.txt"),
                "SCBL Public diagnostic bundle. Password, tunnel-secret, network-secret, identity, and raw session payload fields are redacted. " +
                "No game save files are included.\r\n",
                new UTF8Encoding(false),
                cancellationToken);

            ZipFile.CreateFromDirectory(workRoot, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
            return zipPath;
        }
        finally
        {
            try { Directory.Delete(workRoot, recursive: true); } catch { }
        }
    }

    private static string BuildSummary(
        string launcherBaseDirectory,
        string launcherVersion,
        string assignedVirtualIp,
        string gameDirectory,
        bool gameSessionActive)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"GeneratedAt={DateTimeOffset.Now:O}");
        sb.AppendLine($"LauncherVersion={launcherVersion}");
        sb.AppendLine($"LauncherBaseDirectory={launcherBaseDirectory}");
        string executablePath = Environment.ProcessPath ?? "";
        sb.AppendLine($"LauncherExecutablePath={executablePath}");
        sb.AppendLine($"LauncherExecutableSha256={ComputeSha256(executablePath)}");
        sb.AppendLine($"PersistentDataDirectory={LogService.PersistentDataDirectory}");
        sb.AppendLine($"AssignedVirtualIp={assignedVirtualIp}");
        sb.AppendLine($"GameDirectory={gameDirectory}");
        sb.AppendLine($"GameSessionActive={gameSessionActive}");
        sb.AppendLine($"OS={RuntimeInformation.OSDescription}");
        sb.AppendLine($"OSArchitecture={RuntimeInformation.OSArchitecture}");
        sb.AppendLine($"ProcessArchitecture={RuntimeInformation.ProcessArchitecture}");
        sb.AppendLine($"Framework={RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"MachineName={Environment.MachineName}");
        sb.AppendLine($"UserInteractive={Environment.UserInteractive}");
        sb.AppendLine($"Is64BitOperatingSystem={Environment.Is64BitOperatingSystem}");
        sb.AppendLine($"Is64BitProcess={Environment.Is64BitProcess}");
        return sb.ToString();
    }

    private static string ComputeSha256(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return "";
            using FileStream stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch
        {
            return "";
        }
    }

    private static async Task CopyKnownTextFilesAsync(string launcherBaseDirectory, string destinationRoot, CancellationToken cancellationToken)
    {
        var candidates = new List<(string Source, string RelativeName)>();
        for (int index = 0; index <= 3; index++)
        {
            string suffix = index == 0 ? "" : $".{index}";
            candidates.Add((LogService.LogPath + suffix, Path.Combine("logs", "scbl-public.log" + suffix)));
        }
        candidates.Add((Path.Combine(LogService.LogDirectory, "updater.log"), Path.Combine("logs", "updater.log")));
        candidates.Add((Path.Combine(LogService.LogDirectory, "game", "bl-tracing.log"), Path.Combine("logs", "game", "bl-tracing.log")));
        candidates.Add((Path.Combine(LogService.LogDirectory, "game", "hooks-party-trace.log"), Path.Combine("logs", "game", "hooks-party-trace.log")));

        candidates.Add((Path.Combine(launcherBaseDirectory, "runtime", "assigned-ip.txt"), Path.Combine("runtime", "assigned-ip.txt")));
        candidates.Add((Path.Combine(LogService.PersistentDataDirectory, "runtime", "route-guard-session.json"), Path.Combine("runtime", "route-guard-session.json")));
        candidates.Add((Path.Combine(LogService.PersistentDataDirectory, "runtime", FailureSnapshotFileName), Path.Combine("runtime", FailureSnapshotFileName)));
        candidates.Add((Path.Combine(LogService.PersistentDataDirectory, "runtime", "route-guard-health.json"), Path.Combine("runtime", "route-guard-health.json")));
        candidates.Add((Path.Combine(LogService.PersistentDataDirectory, "runtime", "game-network-quality.json"), Path.Combine("runtime", "game-network-quality.json")));
        candidates.Add((BroadcastProbeService.StatusFilePath, Path.Combine("runtime", "broadcast-probe-status.json")));
        candidates.Add((Path.Combine(LogService.ConfigDirectory, "launcher_settings.json"), Path.Combine("persistent", "config", "launcher_settings.json")));
        candidates.Add((Path.Combine(LogService.PersistentDataDirectory, "network", "runtime-profile.json"), Path.Combine("persistent", "network", "runtime-profile.json")));
        candidates.Add((Path.Combine(LogService.PersistentDataDirectory, "network", "scbl-easytier-client.toml"), Path.Combine("persistent", "network", "scbl-easytier-client.toml")));

        foreach ((string source, string relativeName) in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(source))
                continue;

            try
            {
                string text = await ReadSharedTextAsync(source, cancellationToken);
                text = RedactForExport(text, relativeName);
                string destination = Path.Combine(destinationRoot, relativeName);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await File.WriteAllTextAsync(destination, text, new UTF8Encoding(false), cancellationToken);
            }
            catch (Exception ex)
            {
                string errorFile = Path.Combine(destinationRoot, relativeName + ".copy-error.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(errorFile)!);
                await File.WriteAllTextAsync(errorFile, ex.Message, new UTF8Encoding(false), cancellationToken);
            }
        }
    }

    private static async Task<string> ReadSharedTextAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    internal static string RedactForExport(string value)
    {
        string safe = LogService.SanitizeSensitiveText(value);
        return ProtectedJsonFieldRegex.Replace(safe, "$1\"***REDACTED***\"");
    }

    internal static string RedactForExport(string value, string relativeName)
    {
        string safe = RedactForExport(value);
        return IsHooksLog(relativeName) ? RedactLegacyHooksLog(safe) : safe;
    }

    private static bool IsHooksLog(string relativeName)
    {
        string fileName = Path.GetFileName(relativeName);
        return fileName.Equals("bl-tracing.log", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("hooks-party-trace.log", StringComparison.OrdinalIgnoreCase);
    }

    private static string RedactLegacyHooksLog(string value)
    {
        string safe = RawSessionVectorRegex.Replace(value, match =>
        {
            int byteCount = Regex.Matches(match.Groups["values"].Value, "\\d{1,3}").Count;
            return $"data: [***REDACTED***] data_bytes={byteCount}";
        });
        safe = HooksIdentityFieldRegex.Replace(safe, "$1\"<identity-redacted>\"");
        safe = CompleteGuidRegex.Replace(safe, "<identity-redacted>");

        var output = new StringBuilder(safe.Length);
        using var reader = new StringReader(safe);
        string? line;
        int lineNumber = 0;
        while ((line = reader.ReadLine()) != null)
        {
            lineNumber++;
            if (IsLegacyDebugLine(line))
            {
                Match eventType = LegacyEventTypeRegex.Match(line);
                output.Append("[SCBL diagnostic export] legacy_event_redacted=true line=");
                output.Append(lineNumber);
                output.Append(" event_type=");
                output.Append(eventType.Success ? eventType.Value : "unknown");
                AppendSafeNumericField(output, line, "kind");
                AppendSafeNumericField(output, line, "invitation_id");
                AppendSafeNumericField(output, line, "target_session_id");
                AppendSafeNumericField(output, line, "data_bytes");
                AppendSafeNumericField(output, line, "flags");
                AppendSafeTargetSession(output, line);
                AppendSafeBooleanField(output, line, "invite_only");
                output.AppendLine();
                continue;
            }

            output.AppendLine(line);
        }

        safe = output.ToString();
        if (ContainsForbiddenLegacyHooksMaterial(safe))
        {
            return "[SCBL diagnostic export] legacy_hooks_log_rejected=true " +
                "reason=sensitive_legacy_format_remained_after_redaction\r\n";
        }
        return safe;
    }

    private static bool IsLegacyDebugLine(string line)
    {
        return line.Contains("New event ", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Returning friends:", StringComparison.OrdinalIgnoreCase)
            || line.Contains("PartyGameInvite {", StringComparison.OrdinalIgnoreCase)
            || line.Contains("UplayGameSession {", StringComparison.OrdinalIgnoreCase)
            || line.Contains("data: [***REDACTED***]", StringComparison.OrdinalIgnoreCase);
    }

    private static void AppendSafeNumericField(StringBuilder output, string line, string fieldName)
    {
        Match match = Regex.Match(
            line,
            $"(?i)\\b{Regex.Escape(fieldName)}\\b\\s*[:=]\\s*(?<value>\\d+)",
            RegexOptions.CultureInvariant);
        if (!match.Success)
            return;
        output.Append(' ');
        output.Append(fieldName);
        output.Append('=');
        output.Append(match.Groups["value"].Value);
    }

    private static void AppendSafeTargetSession(StringBuilder output, string line)
    {
        if (Regex.IsMatch(line, "(?i)\\btarget_session_id\\b", RegexOptions.CultureInvariant))
            return;
        Match match = Regex.Match(
            line,
            "(?i)\\bUplayGameSession\\s*\\{[^}]*?\\bid\\s*:\\s*(?<value>\\d+)",
            RegexOptions.CultureInvariant);
        if (!match.Success)
            return;
        output.Append(" target_session_id=");
        output.Append(match.Groups["value"].Value);
    }

    private static void AppendSafeBooleanField(StringBuilder output, string line, string fieldName)
    {
        Match match = Regex.Match(
            line,
            $"(?i)\\b{Regex.Escape(fieldName)}\\b\\s*[:=]\\s*(?<value>true|false)",
            RegexOptions.CultureInvariant);
        if (!match.Success)
            return;
        output.Append(' ');
        output.Append(fieldName);
        output.Append('=');
        output.Append(match.Groups["value"].Value.ToLowerInvariant());
    }

    private static bool ContainsForbiddenLegacyHooksMaterial(string value)
    {
        if (CompleteGuidRegex.IsMatch(value) || RawSessionVectorRegex.IsMatch(value))
            return true;
        if (value.Contains("PartyGameInvite {", StringComparison.OrdinalIgnoreCase)
            || value.Contains("UplayGameSession {", StringComparison.OrdinalIgnoreCase)
            || value.Contains("New event ", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Returning friends:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return HooksIdentityFieldRegex.Matches(value).Cast<Match>()
            .Any(match => !match.Value.Contains("<identity-redacted>", StringComparison.Ordinal));
    }

    private static async Task CollectCommandsAsync(string launcherBaseDirectory, string destinationRoot, CancellationToken cancellationToken)
    {
        string easyTierCli = ClientInstallLayout.Resolve(
            launcherBaseDirectory,
            ClientInstallLayout.EasyTierCliRelativePath);
        var commands = new List<CommandSpec>
        {
            new CommandSpec("ipconfig-all.txt", "ipconfig.exe", new[] { "/all" }),
            new CommandSpec("route-print-ipv4.txt", "route.exe", new[] { "print", "-4" }),
            new CommandSpec("netstat-ano.txt", "netstat.exe", new[] { "-ano" }),
            new CommandSpec("tasklist.txt", "tasklist.exe", new[] { "/v" }),
            new CommandSpec(
                "powershell-network.txt",
                "powershell.exe",
                new[]
                {
                    "-NoProfile",
                    "-NonInteractive",
                    "-Command",
                    "$ErrorActionPreference='Continue'; " +
                    "'=== Get-NetAdapter ==='; Get-NetAdapter | Sort-Object ifIndex | Format-Table -AutoSize | Out-String -Width 260; " +
                    "'=== Get-NetIPConfiguration ==='; Get-NetIPConfiguration | Format-List | Out-String -Width 260; " +
                    "'=== Get-NetRoute IPv4 ==='; Get-NetRoute -AddressFamily IPv4 | Sort-Object InterfaceIndex,DestinationPrefix | Format-Table -AutoSize | Out-String -Width 260; '=== Get-NetRoute IPv6 ==='; Get-NetRoute -AddressFamily IPv6 | Sort-Object InterfaceIndex,DestinationPrefix | Format-Table -AutoSize | Out-String -Width 260; '=== IPv6 addresses ==='; Get-NetIPAddress -AddressFamily IPv6 | Sort-Object InterfaceIndex | Format-Table -AutoSize | Out-String -Width 260; " +
                    "'=== SCBL processes ==='; Get-CimInstance Win32_Process | Where-Object {$_.Name -in @('easytier-core.exe','scbl-process-router.exe','SCBL.exe','Blacklist_game.exe','Blacklist_DX11_game.exe')} | Select-Object ProcessId,ParentProcessId,Name,ExecutablePath,CommandLine | Format-List | Out-String -Width 300"
                })
        };

        if (File.Exists(easyTierCli))
        {
            string[] baseArgs = { "-p", PublicTunnelConfig.EasyTierRpcEndpoint, "-n", PublicTunnelConfig.EasyTierInstanceName };
            commands.Add(new CommandSpec("easytier-peer-table.txt", easyTierCli, baseArgs.Concat(new[] { "peer" }).ToArray()));
            commands.Add(new CommandSpec("easytier-route-table.txt", easyTierCli, baseArgs.Concat(new[] { "route" }).ToArray()));
            commands.Add(new CommandSpec("easytier-peer-verbose.json", easyTierCli, new[] { "-p", PublicTunnelConfig.EasyTierRpcEndpoint, "-v", "-o", "json", "-n", PublicTunnelConfig.EasyTierInstanceName, "peer" }, true));
            commands.Add(new CommandSpec("easytier-route-verbose.json", easyTierCli, new[] { "-p", PublicTunnelConfig.EasyTierRpcEndpoint, "-v", "-o", "json", "-n", PublicTunnelConfig.EasyTierInstanceName, "route" }, true));
            commands.Add(new CommandSpec("easytier-node-info.json", easyTierCli, new[] { "-p", PublicTunnelConfig.EasyTierRpcEndpoint, "-v", "-o", "json", "-n", PublicTunnelConfig.EasyTierInstanceName, "node", "info" }, true));
            commands.Add(new CommandSpec("easytier-node-config.json", easyTierCli, new[] { "-p", PublicTunnelConfig.EasyTierRpcEndpoint, "-v", "-o", "json", "-n", PublicTunnelConfig.EasyTierInstanceName, "node", "config" }, true));
        }

        foreach (CommandSpec command in commands)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string output = await RunCommandAsync(command, cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(destinationRoot, command.OutputName),
                RedactForExport(output),
                new UTF8Encoding(false),
                cancellationToken);
        }
    }

    private static async Task<string> RunCommandAsync(CommandSpec command, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = command.FileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in command.Arguments)
            psi.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(psi);
            if (process == null)
                return $"Failed to start {command.FileName}.";

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(12));
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return $"Command timed out: {command.FileName} {string.Join(" ", command.Arguments)}";
            }

            string stdout = await stdoutTask;
            string stderr = await stderrTask;
            if (command.RawOutput && process.ExitCode == 0 && string.IsNullOrWhiteSpace(stderr))
                return stdout;
            return $"Command: {command.FileName} {string.Join(" ", command.Arguments)}\r\nExitCode: {process.ExitCode}\r\n\r\n{stdout}\r\n{stderr}";
        }
        catch (Exception ex)
        {
            return $"Command failed: {command.FileName}\r\n{ex}";
        }
    }

    private static string MakeUniquePath(string path)
    {
        if (!File.Exists(path))
            return path;

        string directory = Path.GetDirectoryName(path) ?? ".";
        string name = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        for (int index = 2; ; index++)
        {
            string candidate = Path.Combine(directory, $"{name}_{index}{extension}");
            if (!File.Exists(candidate))
                return candidate;
        }
    }

    private sealed record CommandSpec(string OutputName, string FileName, string[] Arguments, bool RawOutput = false);
}
