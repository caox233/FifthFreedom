using System.IO.Compression;
using System.Text;
using SplinterCellCNLauncher.Services;

namespace SCBL.Client.Tests;

public sealed class DiagnosticExportRedactionTests : IDisposable
{
    private const string FakeSecret = "SCBL_FIXED_FAKE_NETWORK_SECRET_20260808";
    private const string FakeAccountId = "11111111-2222-4333-8444-555555555555";
    private const string FakeDisplayName = "SCBL_FAKE_DISPLAY_NAME_NEVER_REAL_20260809";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "scbl-diagnostic-redaction-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void ExportContractIncludesTheFailureTimePeerAndRouteSnapshot()
    {
        Assert.Equal("last-network-failure.json", DiagnosticExportService.FailureSnapshotFileName);
    }

    [Fact]
    public void EasyTier_export_tree_and_zip_remove_all_secret_encodings_but_keep_topology()
    {
        string exportRoot = Path.Combine(_root, "export");
        string commandsRoot = Path.Combine(exportRoot, "commands");
        string persistentRoot = Path.Combine(exportRoot, "files", "persistent", "network");
        Directory.CreateDirectory(commandsRoot);
        Directory.CreateDirectory(persistentRoot);

        var inputs = new Dictionary<string, string>
        {
            [Path.Combine(commandsRoot, "easytier-node-config.json")] =
                """
                {
                  "hostname": "POWERPC",
                  "virtual_ipv4": "10.66.0.3/24",
                  "config": "instance_name = \"scbl-public\"\nnetwork_secret = \"SCBL_FIXED_FAKE_NETWORK_SECRET_20260808\""
                }
                """,
            [Path.Combine(commandsRoot, "easytier-node-info.json")] =
                """
                {
                  "node_id": "peer-node-a",
                  "virtual_ipv4": "10.66.0.4",
                  "detail": "network_secret = \\\"SCBL_FIXED_FAKE_NETWORK_SECRET_20260808\\\""
                }
                """,
            [Path.Combine(commandsRoot, "easytier-route-verbose.json")] =
                """
                {
                  "routes": [{
                    "peer_id": "peer-route-a",
                    "route_metric": 7,
                    "config": "network_secret = \\\\\\\"SCBL_FIXED_FAKE_NETWORK_SECRET_20260808\\\\\\\""
                  }]
                }
                """,
            [Path.Combine(persistentRoot, "scbl-easytier-client.toml")] =
                """
                instance_name = "scbl-public"
                network_secret = "SCBL_FIXED_FAKE_NETWORK_SECRET_20260808"
                ipv4 = "10.66.0.3/24"
                """,
            [Path.Combine(commandsRoot, "direct-json-field.json")] =
                """
                {"network_secret":"SCBL_FIXED_FAKE_NETWORK_SECRET_20260808","peer_id":"peer-json-a"}
                """,
            [Path.Combine(commandsRoot, "escaped-json-field.json")] =
                """
                "{\"network_secret\":\"SCBL_FIXED_FAKE_NETWORK_SECRET_20260808\",\"route_metric\":7}"
                """
        };

        foreach ((string path, string input) in inputs)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string redacted = DiagnosticExportService.RedactForExport(input);
            Assert.False(
                redacted.Contains(FakeSecret, StringComparison.Ordinal),
                $"Secret remained in {Path.GetFileName(path)}.");
            File.WriteAllText(path, redacted, new UTF8Encoding(false));
        }

        string treeText = string.Join(
            "\n",
            Directory.EnumerateFiles(exportRoot, "*", SearchOption.AllDirectories)
                .Select(File.ReadAllText));
        Assert.DoesNotContain(FakeSecret, treeText, StringComparison.Ordinal);
        Assert.Contains("***REDACTED***", treeText, StringComparison.Ordinal);
        Assert.Contains("10.66.0.3/24", treeText, StringComparison.Ordinal);
        Assert.Contains("10.66.0.4", treeText, StringComparison.Ordinal);
        Assert.Contains("peer-route-a", treeText, StringComparison.Ordinal);
        Assert.Contains("\"route_metric\": 7", treeText, StringComparison.Ordinal);

        string zipPath = Path.Combine(_root, "diagnostics.zip");
        ZipFile.CreateFromDirectory(exportRoot, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);

        byte[] zipBytes = File.ReadAllBytes(zipPath);
        byte[] secretBytes = Encoding.UTF8.GetBytes(FakeSecret);
        Assert.True(zipBytes.AsSpan().IndexOf(secretBytes) < 0, "The raw ZIP contains the fixed fake secret.");

        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        string archiveText = string.Join(
            "\n",
            archive.Entries.Select(entry =>
            {
                using Stream stream = entry.Open();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return reader.ReadToEnd();
            }));
        Assert.DoesNotContain(FakeSecret, archiveText, StringComparison.Ordinal);
        Assert.Contains("10.66.0.3/24", archiveText, StringComparison.Ordinal);
        Assert.Contains("10.66.0.4", archiveText, StringComparison.Ordinal);
        Assert.Contains("peer-route-a", archiveText, StringComparison.Ordinal);
        Assert.Contains("peer-json-a", archiveText, StringComparison.Ordinal);
    }

    [Fact]
    public void Legacy_hooks_logs_remove_identity_debug_events_and_raw_session_vectors_but_keep_safe_fields()
    {
        string vector = string.Join(", ", Enumerable.Range(0, 496).Select(index => (index * 37 + 11) % 256));
        string compactDebug =
            $"2026-08-09 INFO New event PartyGameInvite {{ sender_id: \"{FakeAccountId}\", " +
            $"sender_name: \"{FakeDisplayName}\", kind: 2, invitation_id: 17, " +
            $"game_session: Some(UplayGameSession {{ id: 460, data: [{vector}], flags: 3, invite_only: false }}) }}";
        string multilineDebug =
            $"2026-08-09 INFO UplayGameSession {{\r\n  account_id: \"{FakeAccountId}\",\r\n" +
            $"  username: \"{FakeDisplayName}\",\r\n  data: [\r\n    {vector}\r\n  ],\r\n" +
            "  flags: 3,\r\n  invite_only: false\r\n}\r\n" +
            $"2026-08-09 DEBUG Returning friends: [UplayFriend {{ account_id: \"{FakeAccountId}\", username: \"{FakeDisplayName}\" }}]\r\n" +
            "event_type=PartyTransition stage=transition-after kind=2 target_session_id=460 members=2 changed=true\r\n";

        string compactRedacted = DiagnosticExportService.RedactForExport(
            compactDebug,
            Path.Combine("logs", "game", "bl-tracing.log"));
        string multilineRedacted = DiagnosticExportService.RedactForExport(
            multilineDebug,
            Path.Combine("logs", "game", "hooks-party-trace.log"));
        string combined = compactRedacted + multilineRedacted;

        Assert.DoesNotContain(FakeAccountId, combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(FakeDisplayName, combined, StringComparison.Ordinal);
        Assert.DoesNotContain("PartyGameInvite {", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("UplayGameSession {", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("Returning friends:", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("data: [11, 48, 85", combined, StringComparison.Ordinal);
        Assert.Contains("legacy_event_redacted=true", combined, StringComparison.Ordinal);
        Assert.Contains("event_type=PartyGameInvite", compactRedacted, StringComparison.Ordinal);
        Assert.Contains("kind=2", compactRedacted, StringComparison.Ordinal);
        Assert.Contains("invitation_id=17", compactRedacted, StringComparison.Ordinal);
        Assert.Contains("target_session_id=460", compactRedacted, StringComparison.Ordinal);
        Assert.Contains("data_bytes=496", compactRedacted, StringComparison.Ordinal);
        Assert.Contains("flags=3", compactRedacted, StringComparison.Ordinal);
        Assert.Contains("invite_only=false", compactRedacted, StringComparison.Ordinal);
        Assert.Contains(
            "event_type=PartyTransition stage=transition-after kind=2 target_session_id=460 members=2 changed=true",
            multilineRedacted,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Legacy_hooks_export_tree_and_zip_have_zero_fixed_identity_or_vector_hits()
    {
        string exportRoot = Path.Combine(_root, "legacy-hooks-export");
        string gameLogs = Path.Combine(exportRoot, "files", "logs", "game");
        Directory.CreateDirectory(gameLogs);
        string vector = string.Join(", ", Enumerable.Range(0, 496).Select(index => (index * 19 + 7) % 256));
        string input =
            $"New event PartyGameInvite {{ sender_id: \"{FakeAccountId}\", sender_name: \"{FakeDisplayName}\", " +
            $"kind: 2, invitation_id: 23, game_session: Some(UplayGameSession {{ id: 467, data: [{vector}], flags: 5, invite_only: true }}) }}";

        foreach (string fileName in new[] { "bl-tracing.log", "hooks-party-trace.log" })
        {
            string redacted = DiagnosticExportService.RedactForExport(
                input,
                Path.Combine("logs", "game", fileName));
            File.WriteAllText(Path.Combine(gameLogs, fileName), redacted, new UTF8Encoding(false));
        }

        string treeText = string.Join(
            "\n",
            Directory.EnumerateFiles(exportRoot, "*", SearchOption.AllDirectories).Select(File.ReadAllText));
        Assert.DoesNotContain(FakeAccountId, treeText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(FakeDisplayName, treeText, StringComparison.Ordinal);
        Assert.DoesNotContain(vector, treeText, StringComparison.Ordinal);
        Assert.Contains("target_session_id=467", treeText, StringComparison.Ordinal);
        Assert.Contains("data_bytes=496", treeText, StringComparison.Ordinal);

        string zipPath = Path.Combine(_root, "legacy-hooks-diagnostics.zip");
        ZipFile.CreateFromDirectory(exportRoot, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
        Assert.True(File.ReadAllBytes(zipPath).AsSpan().IndexOf(Encoding.UTF8.GetBytes(FakeAccountId)) < 0);
        Assert.True(File.ReadAllBytes(zipPath).AsSpan().IndexOf(Encoding.UTF8.GetBytes(FakeDisplayName)) < 0);

        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        string archiveText = string.Join(
            "\n",
            archive.Entries.Select(entry =>
            {
                using Stream stream = entry.Open();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return reader.ReadToEnd();
            }));
        Assert.DoesNotContain(FakeAccountId, archiveText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(FakeDisplayName, archiveText, StringComparison.Ordinal);
        Assert.DoesNotContain(vector, archiveText, StringComparison.Ordinal);
        Assert.Contains("legacy_event_redacted=true", archiveText, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }
}
