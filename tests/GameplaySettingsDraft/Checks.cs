using SplinterCellCNLauncher.Models;
using SplinterCellCNLauncher.Services;

public static class GameplayDraftChecks
{
    private static int passed;
    private static void Check(bool ok, string label)
    {
        if (!ok) throw new InvalidOperationException(label);
        Console.WriteLine("PASS " + label); passed++;
    }
    private static bool Closed(Action action)
    {
        try { action(); return false; } catch (InvalidOperationException) { return true; }
    }

    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "ff-gameplay-draft-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            LogService.AppDataDir = Path.Combine(root, "settings");
            var service = new LauncherSettingsService();
            var live = new LauncherSettings { Username = "owned-fixture", Password = "fixture-never-persist", ServerAddress = "192.0.2.10:50051" };
            Check(live.GameplayMode == "MOD" && live.FovScale == 1.2, "existing default MOD and FOV 1.2 unchanged");
            service.Save(live);
            byte[] original = File.ReadAllBytes(service.SettingsPath);
            int writes = 0;
            void Save(LauncherSettings candidate) { writes++; service.Save(candidate); }
            var draft = new GameplaySettingsDraft(live);
            draft.Update("Official", 1.3);
            Check(draft.IsActive && draft.IsDirty && draft.Mode == "Official" && draft.Fov == 1.3
                && live.GameplayMode == "MOD" && live.FovScale == 1.2 && writes == 0
                && File.ReadAllBytes(service.SettingsPath).SequenceEqual(original), "editing mode/FOV changes only draft, not live settings or disk");
            draft.Update(draft.Mode, 1.2);
            Check(draft.Mode == "Official" && draft.Fov == 1.2 && live.GameplayMode == "MOD" && writes == 0,
                "reset-to-default is still an uncommitted draft edit");
            draft.Cancel();
            Check(!draft.IsActive && draft.Mode == "MOD" && draft.Fov == 1.2 && !draft.IsDirty
                && live.GameplayMode == "MOD" && File.ReadAllBytes(service.SettingsPath).SequenceEqual(original), "cancel restores draft origin and leaves committed bytes unchanged");
            Check(Closed(() => draft.Commit(live, Save)) && Closed(() => draft.Update("Official", 1.3)) && writes == 0,
                "a cancelled draft cannot later save or edit");

            draft = new GameplaySettingsDraft(live); draft.Update("Official", 1.1);
            live.ServerAddress = "192.0.2.20:50051"; live.Language = "en-US"; live.RememberPassword = false;
            bool oldMemoryDuringSave = false;
            draft.Commit(live, candidate =>
            {
                oldMemoryDuringSave = live.GameplayMode == "MOD" && live.FovScale == 1.2 && !ReferenceEquals(candidate, live);
                Save(candidate);
            });
            var loaded = service.Load();
            Check(oldMemoryDuringSave && writes == 1 && !draft.IsActive && live.GameplayMode == "Official" && live.FovScale == 1.1
                && loaded.GameplayMode == "Official" && loaded.FovScale == 1.1, "actual atomic settings save returns before live gameplay values commit");
            Check(loaded.ServerAddress == "192.0.2.20:50051" && loaded.Language == "en-US" && !loaded.RememberPassword
                && live.Password == "fixture-never-persist" && !File.ReadAllText(service.SettingsPath).Contains("fixture-never-persist"),
                "confirmation preserves latest unrelated settings and existing credential exclusion");

            byte[] beforeFailure = File.ReadAllBytes(service.SettingsPath);
            draft = new GameplaySettingsDraft(live); draft.Update("MOD", 1.3);
            bool failed = false;
            try { draft.Commit(live, _ => throw new IOException("owned failure before atomic write")); } catch (IOException) { failed = true; }
            Check(failed && draft.IsActive && draft.IsDirty && live.GameplayMode == "Official" && live.FovScale == 1.1
                && File.ReadAllBytes(service.SettingsPath).SequenceEqual(beforeFailure), "save failure preserves old live/disk values and draft for retry");

            string blocker = Path.Combine(root, "blocked-parent"); File.WriteAllText(blocker, "owned blocking file");
            LogService.AppDataDir = Path.Combine(blocker, "child");
            var blockedService = new LauncherSettingsService(); failed = false;
            try { draft.Commit(live, blockedService.Save); } catch (IOException) { failed = true; }
            Check(failed && draft.IsActive && live.GameplayMode == "Official" && live.FovScale == 1.1
                && File.ReadAllBytes(service.SettingsPath).SequenceEqual(beforeFailure), "real settings service filesystem failure cannot commit the draft");
            LogService.AppDataDir = Path.Combine(root, "settings");
            draft.Commit(live, Save);
            Check(writes == 2 && live.GameplayMode == "MOD" && live.FovScale == 1.3 && service.Load().FovScale == 1.3,
                "explicit retry commits only after successful real persistence");

            draft = new GameplaySettingsDraft(live); draft.Update("Official", 1.0);
            live.FovScale = 1.25;
            int beforeWrites = writes;
            Check(Closed(() => draft.Commit(live, Save)) && writes == beforeWrites && live.FovScale == 1.25 && draft.IsActive,
                "a stale draft cannot overwrite a newer committed gameplay choice");
            draft.Cancel();

            byte[] beforePreview = File.ReadAllBytes(service.SettingsPath);
            var preview = new LauncherSettings { GameplayMode = "Official", FovScale = 1.1 };
            var previewDraft = new GameplaySettingsDraft(preview); previewDraft.Update("MOD", 1.3); previewDraft.Cancel();
            Check(preview.GameplayMode == "Official" && preview.FovScale == 1.1, "preview cancel preserves synthetic committed values");
            previewDraft = new GameplaySettingsDraft(preview); previewDraft.Update("MOD", 1.3); previewDraft.CommitPreview(preview);
            Check(preview.GameplayMode == "MOD" && preview.FovScale == 1.3 && writes == beforeWrites
                && File.ReadAllBytes(service.SettingsPath).SequenceEqual(beforePreview), "preview confirmation changes synthetic memory only and has no persistence delegate");

            foreach (var (input, expected) in new[] { (-1.0, 1.0), (1.0, 1.0), (1.2, 1.2), (1.3, 1.3), (2.0, 1.3), (double.NaN, 1.2), (double.PositiveInfinity, 1.2) })
            {
                draft = new GameplaySettingsDraft(new LauncherSettings()); draft.Update("mod", input);
                Check(draft.Mode == "MOD" && draft.Fov == expected, "existing FOV normalization retained: " + input);
            }
            draft.Update("other", 1.2); Check(draft.Mode == "Official", "existing non-MOD normalization retained");
            Console.WriteLine($"RESULT {passed} gameplay draft checks passed; real owned settings I/O, no WPF/game execution.");
        }
        finally { Directory.Delete(root, true); }
    }
}
