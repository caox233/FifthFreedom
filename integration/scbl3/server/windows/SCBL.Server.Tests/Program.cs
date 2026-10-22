using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Scbl.Server.Tests;

internal static class Program
{
    private static int _passed;
    private static int _total;

    private static int Main()
    {
        Run("config roundtrip and secret persistence", ConfigRoundtrip);
        Run("invalid legacy config remains editable", InvalidLegacyConfigRemainsEditable);
        Run("default server data root is isolated from client machine state", DefaultServerRootIsIsolated);
        Run("config rejects collisions", ConfigRejectsCollision);
        Run("config validates network and public endpoints", ConfigValidatesNetworkAndEndpoints);
        Run("DDNS management endpoint is explicit and isolated", DdnsManagementEndpointIsExplicit);
        Run("secret file rejects malformed values", SecretsRejectMalformedValues);
        Run("path traversal is rejected", PathTraversalRejected);
        Run("package hash mismatch fails closed", PackageHashMismatch);
        Run("runtime activation keeps rollback pointer", RuntimeActivationAndRollback);
        Run("repeated full install reuses unchanged runtime", RepeatedFullInstallReusesRuntime);
        Run("partial update preserves unchanged runtime", PartialUpdatePreservesRuntime);
        Run("bootstrap replacement is complete", BootstrapReplacement);
        Run("partial bootstrap preserves unchanged files", PartialBootstrapPreservesFiles);
        Run("backup retention keeps three", BackupRetention);
        Run("backup restore verifies sidecar", BackupRestoreVerifiesSidecar);
        Run("zero-sized component is rejected", ZeroSizedComponentRejected);
        Run("duplicate destination is rejected", DuplicateDestinationRejected);
        Run("backup creation rejects missing runtime pointer", BackupRejectsMissingRuntimePointer);
        Run("backup keeps referenced runtime", BackupProtectsReferencedRuntime);
        Run("external pointer detects local drift", ExternalPointerDetectsDrift);
        Run("runtime environments expose only required secrets", RuntimeEnvironmentIsLeastPrivilege);
        Run("file snapshot restores exact state", FileSnapshotRestoresExactState);
        Run("service state parser uses numeric SCM state", ServiceStateParserUsesNumericState);
        Run("ordinary update rejects downgrade", OrdinaryUpdateRejectsDowngrade);
        Run("package requires one bounded manifest", PackageRequiresOneManifest);
        Run("known component contract is enforced", KnownComponentContractIsEnforced);
        Run("backup includes sqlite side files", BackupIncludesSqliteSideFiles);
        Run("external pointer rejects escaped directory", ExternalPointerRejectsEscape);
        Run("runtime template uses configured prefix and dual-stack listeners", RuntimeTemplateUsesConfiguredPrefix);
        Run("server descriptor projects the selected client channel", ServerDescriptorProjectsSelectedChannel);
        Run("server descriptor drift retains previous public document", ServerDescriptorDriftRetainsPreviousDocument);
        Run("client publication verifies components and full package", ClientPublicationVerifiesComponentsAndFullPackage);
        Console.WriteLine($"SCBL Windows Server tests: {_passed}/{_total} passed");
        return 0;
    }

    private static void Run(string name, Action test)
    {
        _total++;
        test();
        _passed++;
        Console.WriteLine("PASS " + name);
    }

    private static void ConfigRoundtrip()
    {
        using TemporaryRoot temporary = new();
        ServerPaths paths = temporary.Paths;
        ServerConfiguration configuration = ServerConfiguration.LoadOrCreate(paths);
        configuration.ServerId = "windows-test";
        configuration.Save(paths);
        ServerSecrets first = AtomicJson.Read<ServerSecrets>(paths.Secrets)!;
        configuration.Save(paths);
        ServerSecrets second = AtomicJson.Read<ServerSecrets>(paths.Secrets)!;
        Assert(first == second, "secret changed on repeated save");
        Assert(ServerConfiguration.LoadOrCreate(paths).ServerId == "windows-test", "config did not persist");
    }

    private static void InvalidLegacyConfigRemainsEditable()
    {
        using TemporaryRoot temporary = new();
        ServerConfiguration legacy = new() { PublicHost = "" };
        AtomicJson.Write(temporary.Paths.Config, legacy);
        Expect<InvalidDataException>(() => ServerConfiguration.LoadOrCreate(temporary.Paths));
        ServerConfiguration editable = ServerConfiguration.LoadForEditing(temporary.Paths);
        editable.PublicHost = "server.example.test";
        editable.Save(temporary.Paths);
        Assert(ServerConfiguration.LoadOrCreate(temporary.Paths).PublicHost == "server.example.test", "legacy config could not be repaired through the editor");
    }

    private static void DefaultServerRootIsIsolated()
    {
        ServerPaths paths = new();
        string clientMachineRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SCBL");
        Assert(!string.Equals(paths.DataRoot, clientMachineRoot, StringComparison.OrdinalIgnoreCase), "server data root overlaps client machine state");
        Assert(string.Equals(Path.GetFileName(paths.DataRoot), "SCBL Server", StringComparison.Ordinal), "unexpected default server data root");
    }

    private static void ConfigRejectsCollision()
    {
        ServerConfiguration configuration = new() { GamePort = 11010, UpdatePort = 11010 };
        Assert(configuration.Validate().Count != 0, "port collision accepted");
    }

    private static void ConfigValidatesNetworkAndEndpoints()
    {
        Assert(new ServerConfiguration { VirtualNetwork = "10.999.0.0/24" }.Validate().Count != 0, "invalid CIDR accepted");
        Assert(new ServerConfiguration { VirtualIp = "10.67.0.1", VirtualNetwork = "10.66.0.0/24" }.Validate().Count != 0, "virtual IP outside CIDR accepted");
        Assert(new ServerConfiguration { PublicHost = "example.test:11010" }.Validate().Count != 0, "server host with port accepted");
        Assert(new ServerConfiguration { PublicHost = "" }.Validate().Count != 0, "empty server host accepted");
        Assert(new ServerConfiguration { PublicHost = "example.test", UpdatePort = 18081 }.BuildPublicUpdateBaseUrl() == "http://example.test:18081", "update URL was not derived from host and port");
    }

    private static void DdnsManagementEndpointIsExplicit()
    {
        ServerConfiguration config = new() { DdnsEnabled = true, DdnsManagementPort = 9876 };
        Assert(config.Validate().Count == 0, "valid DDNS management endpoint was rejected");
        config.DdnsManagementPort = config.UpdatePort;
        Assert(config.Validate().Count != 0, "DDNS management port collision was accepted");
        config.DdnsEnabled = false;
        Assert(config.Validate().Count == 0, "disabled DDNS port affected server validation");
    }

    private static void SecretsRejectMalformedValues()
    {
        using TemporaryRoot temporary = new();
        AtomicJson.Write(temporary.Paths.Secrets, new ServerSecrets("not-base64", "00", "11"));
        Expect<InvalidDataException>(() => ServerSecrets.Load(temporary.Paths));
    }

    private static void PathTraversalRejected()
    {
        Expect<InvalidDataException>(() => PathSafety.ResolveRelative(Path.GetTempPath(), "../escape"));
        Expect<InvalidDataException>(() => PathSafety.ResolveRelative(Path.GetTempPath(), "C:/escape"));
    }

    private static void PackageHashMismatch()
    {
        using TemporaryRoot temporary = new();
        string archivePath = Path.Combine(temporary.Root, "bad.zip");
        PackageManifest manifest = Manifest("1.0.0", new string('0', 64));
        using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            Write(archive, "scbl-windows-package.json", JsonSerializer.SerializeToUtf8Bytes(manifest));
            Write(archive, "artifacts/runtime/runtime.bin", [1]);
        }
        Expect<InvalidDataException>(() => new PackageDeployment(temporary.Paths, new ServerLog(temporary.Paths, "test")).Stage(archivePath));
    }

    private static void RuntimeActivationAndRollback()
    {
        using TemporaryRoot temporary = new();
        PackageDeployment deployment = new(temporary.Paths, new ServerLog(temporary.Paths, "test"));
        using StagedPackage first = StageValid(temporary, "1.0.0", [1], full: true);
        deployment.ActivateRuntime(first);
        using StagedPackage second = StageValid(temporary, "1.0.1", [2]);
        deployment.ActivateRuntime(second);
        Assert(AtomicJson.Read<ReleasePointer>(temporary.Paths.CurrentReleasePointer)!.Version == "1.0.1", "second release not active");
        deployment.RollbackRuntime();
        Assert(AtomicJson.Read<ReleasePointer>(temporary.Paths.CurrentReleasePointer)!.Version == "1.0.0", "rollback did not restore first release");
    }

    private static void RepeatedFullInstallReusesRuntime()
    {
        using TemporaryRoot temporary = new();
        PackageDeployment deployment = new(temporary.Paths, new ServerLog(temporary.Paths, "test"));
        using StagedPackage first = StageValid(temporary, "1.0.0", [1], full: true);
        ReleasePointer original = deployment.ActivateRuntime(first);
        using StagedPackage repeated = StageValid(temporary, "1.0.0", [1], full: true);
        ReleasePointer reused = deployment.ActivateRuntime(repeated);
        Assert(reused.Directory == original.Directory, "unchanged full runtime was duplicated");
        Assert(Directory.GetDirectories(temporary.Paths.Releases).Length == 1, "repeated full install created another runtime directory");
    }

    private static void BackupRetention()
    {
        using TemporaryRoot temporary = new();
        using (StagedPackage package = StageValid(temporary, "1.0.0", [1], full: true))
            new PackageDeployment(temporary.Paths, new ServerLog(temporary.Paths, "test")).ActivateRuntime(package);
        ServerConfiguration.LoadOrCreate(temporary.Paths);
        BackupManager manager = new(temporary.Paths, new ServerLog(temporary.Paths, "test"));
        for (int index = 0; index < 5; index++)
        {
            manager.Create();
            Thread.Sleep(1100);
        }
        Assert(Directory.GetFiles(temporary.Paths.Backups, "*.zip").Length == 3, "backup retention is not three");
    }

    private static void BootstrapReplacement()
    {
        using TemporaryRoot temporary = new();
        File.WriteAllText(Path.Combine(temporary.Paths.Bootstrap, "obsolete.txt"), "old");
        string stagedRoot = Path.Combine(temporary.Root, "bootstrap-stage");
        string stagedFile = Path.Combine(stagedRoot, "artifacts", "bootstrap", "SCBL.Server.Host.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(stagedFile)!);
        File.WriteAllBytes(stagedFile, [7, 8, 9]);
        PackageManifest manifest = new()
        {
            SchemaVersion = 1,
            PackageType = "windows-full",
            ReleaseVersion = "1.0.0",
            Components =
            [
                new PackageComponent
                {
                    Id = "server.host",
                    Version = "1.0.0",
                    Path = "artifacts/bootstrap/SCBL.Server.Host.exe",
                    Destination = "SCBL.Server.Host.exe",
                    Target = "bootstrap",
                    Size = 3,
                    Sha256 = Hashing.Sha256(stagedFile),
                },
            ],
        };
        using StagedPackage package = new(stagedRoot, manifest);
        new PackageDeployment(temporary.Paths, new ServerLog(temporary.Paths, "test")).InstallBootstrap(package);
        Assert(!File.Exists(Path.Combine(temporary.Paths.Bootstrap, "obsolete.txt")), "obsolete bootstrap file survived replacement");
        Assert(File.ReadAllBytes(Path.Combine(temporary.Paths.Bootstrap, "SCBL.Server.Host.exe")).SequenceEqual(new byte[] { 7, 8, 9 }), "new bootstrap was not installed");
    }

    private static void PartialBootstrapPreservesFiles()
    {
        using TemporaryRoot temporary = new();
        string unchanged = Path.Combine(temporary.Paths.Bootstrap, "SCBL.Server.Host.exe");
        File.WriteAllBytes(unchanged, [1]);
        string stagedRoot = Path.Combine(temporary.Root, "bootstrap-update");
        string stagedFile = Path.Combine(stagedRoot, "artifacts", "bootstrap", "SCBL.Server.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(stagedFile)!);
        File.WriteAllBytes(stagedFile, [2]);
        PackageManifest manifest = new()
        {
            SchemaVersion = 1,
            PackageType = "windows-update",
            ReleaseVersion = "1.0.1",
            Components =
            [
                new PackageComponent
                {
                    Id = "server.manager",
                    Version = "1.0.1",
                    Path = "artifacts/bootstrap/SCBL.Server.exe",
                    Destination = "SCBL.Server.exe",
                    Target = "bootstrap",
                    Size = 1,
                    Sha256 = Hashing.Sha256(stagedFile),
                },
            ],
        };
        using StagedPackage package = new(stagedRoot, manifest);
        new PackageDeployment(temporary.Paths, new ServerLog(temporary.Paths, "test")).InstallBootstrap(package);
        Assert(File.ReadAllBytes(unchanged).SequenceEqual(new byte[] { 1 }), "unchanged bootstrap file was lost");
        Assert(File.ReadAllBytes(Path.Combine(temporary.Paths.Bootstrap, "SCBL.Server.exe")).SequenceEqual(new byte[] { 2 }), "manager update was not installed");
    }

    private static void BackupRestoreVerifiesSidecar()
    {
        using TemporaryRoot temporary = new();
        using (StagedPackage package = StageValid(temporary, "1.0.0", [1], full: true))
            new PackageDeployment(temporary.Paths, new ServerLog(temporary.Paths, "test")).ActivateRuntime(package);
        ServerConfiguration config = ServerConfiguration.LoadOrCreate(temporary.Paths);
        config.ServerId = "before";
        config.Save(temporary.Paths);
        BackupManager manager = new(temporary.Paths, new ServerLog(temporary.Paths, "test"));
        string backup = manager.Create();
        config.ServerId = "after";
        config.Save(temporary.Paths);
        manager.Restore(backup);
        Assert(ServerConfiguration.LoadOrCreate(temporary.Paths).ServerId == "before", "backup did not restore config");
        File.WriteAllText(backup + ".sha256", new string('0', 64));
        Expect<InvalidDataException>(() => manager.Restore(backup));
    }

    private static void ZeroSizedComponentRejected()
    {
        PackageManifest manifest = Manifest("1.0.0", new string('0', 64));
        manifest.Components[0].Size = 0;
        Expect<InvalidDataException>(() => manifest.Validate(fullPackage: false));
    }

    private static void DuplicateDestinationRejected()
    {
        PackageManifest manifest = Manifest("1.0.0", new string('0', 64));
        PackageComponent duplicate = new()
        {
            Id = "server.other",
            Version = "1.0.0",
            Path = "artifacts/runtime/other.bin",
            Destination = "runtime.bin",
            Target = "runtime",
            Size = 1,
            Sha256 = new string('1', 64),
        };
        manifest.Components.Add(duplicate);
        Expect<InvalidDataException>(() => manifest.Validate(fullPackage: false));
        duplicate.Destination = "runtime.bin/child";
        Expect<InvalidDataException>(() => manifest.Validate(fullPackage: false));
    }

    private static void BackupRejectsMissingRuntimePointer()
    {
        using TemporaryRoot temporary = new();
        ServerConfiguration config = ServerConfiguration.LoadOrCreate(temporary.Paths);
        config.ServerId = "backup";
        config.Save(temporary.Paths);
        AtomicJson.Write(temporary.Paths.CurrentReleasePointer, new ReleasePointer("1.0.0", "missing-release", DateTimeOffset.UtcNow));
        BackupManager manager = new(temporary.Paths, new ServerLog(temporary.Paths, "test"));
        Expect<InvalidOperationException>(() => manager.Create());
    }

    private static void BackupProtectsReferencedRuntime()
    {
        using TemporaryRoot temporary = new();
        PackageDeployment deployment = new(temporary.Paths, new ServerLog(temporary.Paths, "test"));
        using (StagedPackage first = StageValid(temporary, "1.0.0", [1], full: true)) deployment.ActivateRuntime(first);
        string protectedDirectory = AtomicJson.Read<ReleasePointer>(temporary.Paths.CurrentReleasePointer)!.Directory;
        _ = new BackupManager(temporary.Paths, new ServerLog(temporary.Paths, "test")).Create();
        for (int version = 1; version <= 4; version++)
        {
            using StagedPackage update = StageValid(temporary, $"1.0.{version}", [(byte)(version + 1)]);
            deployment.ActivateRuntime(update);
        }
        Assert(Directory.Exists(Path.Combine(temporary.Paths.Releases, protectedDirectory)), "runtime referenced by retained backup was pruned");
    }

    private static void ExternalPointerDetectsDrift()
    {
        using TemporaryRoot temporary = new();
        string release = Path.Combine(temporary.Paths.EasyTier, "releases", "1.0.0");
        Directory.CreateDirectory(release);
        string core = Path.Combine(release, "easytier-core.exe");
        string cli = Path.Combine(release, "easytier-cli.exe");
        string packet = Path.Combine(release, "Packet.dll");
        string wintun = Path.Combine(release, "wintun.dll");
        File.WriteAllBytes(core, [1, 2, 3]);
        File.WriteAllBytes(cli, [4, 5, 6]);
        File.WriteAllBytes(packet, [10, 11, 12]);
        File.WriteAllBytes(wintun, [7, 8, 9]);
        AtomicJson.Write(Path.Combine(temporary.Paths.EasyTier, "current.json"), new ExternalPointer(
            "1.0.0", "1.0.0", DateTimeOffset.UtcNow, new string('0', 64),
            new Dictionary<string, string>
            {
                ["easytier-core.exe"] = Hashing.Sha256(core),
                ["easytier-cli.exe"] = Hashing.Sha256(cli),
                ["Packet.dll"] = Hashing.Sha256(packet),
                ["wintun.dll"] = Hashing.Sha256(wintun),
            }));
        ExternalComponentManager manager = new(temporary.Paths, new ServerLog(temporary.Paths, "test"));
        Assert(manager.FindEasyTierCore() == core, "valid external component was not found");
        File.Delete(wintun);
        Assert(manager.FindEasyTierCore() is null, "EasyTier without its tunnel driver dependency was accepted");
    }

    private static void RuntimeEnvironmentIsLeastPrivilege()
    {
        using TemporaryRoot temporary = new();
        ServerConfiguration config = new();
        ServerSecrets secrets = ServerSecrets.Create();
        Dictionary<string, string> dedicated = RuntimeEnvironment.BuildDedicated(temporary.Paths, config, secrets);
        Dictionary<string, string> control = RuntimeEnvironment.BuildControl(temporary.Paths, config, secrets, "1.0.0");
        Dictionary<string, string> update = RuntimeEnvironment.BuildUpdate();
        Assert(!dedicated.ContainsKey("SCBL_SECRET"), "Dedicated received the EasyTier/control secret");
        Assert(dedicated.ContainsKey("SCBL_AUTH_TOKEN_KEY"), "Dedicated did not receive its required auth key");
        Assert(control.ContainsKey("SCBL_SECRET") && control.ContainsKey("SCBL_AUTH_TOKEN_KEY"), "Control plane secrets are incomplete");
        Assert(!update.Keys.Any(key => key.Contains("SECRET", StringComparison.OrdinalIgnoreCase) || key.Contains("TOKEN", StringComparison.OrdinalIgnoreCase)), "Update server received an unrelated secret");
    }

    private static void FileSnapshotRestoresExactState()
    {
        using TemporaryRoot temporary = new();
        string existing = Path.Combine(temporary.Root, "existing.txt");
        string absent = Path.Combine(temporary.Root, "absent.txt");
        File.WriteAllText(existing, "before");
        FileStateSnapshot existingSnapshot = FileStateSnapshot.Capture(existing);
        FileStateSnapshot absentSnapshot = FileStateSnapshot.Capture(absent);
        File.WriteAllText(existing, "after");
        File.WriteAllText(absent, "created");
        existingSnapshot.Restore();
        absentSnapshot.Restore();
        Assert(File.ReadAllText(existing) == "before", "existing file was not restored");
        Assert(!File.Exists(absent), "new file was not removed by rollback");
    }

    private static void ServiceStateParserUsesNumericState()
    {
        Assert(WindowsServiceInstaller.ParseStateOutput("        STATE              : 4  RUNNING") == ServiceState.Running, "running state was not parsed");
        Assert(WindowsServiceInstaller.ParseStateOutput("        状态               : 3  正在停止") == ServiceState.StopPending, "localized pending state was not parsed");
    }

    private static void OrdinaryUpdateRejectsDowngrade()
    {
        using TemporaryRoot temporary = new();
        PackageDeployment deployment = new(temporary.Paths, new ServerLog(temporary.Paths, "test"));
        using (StagedPackage current = StageValid(temporary, "2.0.0", [1], full: true)) deployment.ActivateRuntime(current);
        string packagePath = Path.Combine(temporary.Root, "downgrade.zip");
        PackageManifest manifest = Manifest("1.9.9", Hashing.Sha256(CreatePayload(temporary.Root, [2])));
        byte[] manifestPayload = JsonSerializer.SerializeToUtf8Bytes(manifest);
        using (ZipArchive archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            Write(archive, "scbl-windows-package.json", manifestPayload);
            Write(archive, "artifacts/runtime/runtime.bin", [2]);
        }
        Expect<InvalidDataException>(() => deployment.Stage(packagePath));
    }

    private static void PackageRequiresOneManifest()
    {
        using TemporaryRoot temporary = new();
        string packagePath = Path.Combine(temporary.Root, "duplicate-manifest.zip");
        PackageManifest manifest = Manifest("1.0.0", Hashing.Sha256(CreatePayload(temporary.Root, [1])));
        using (ZipArchive archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            Write(archive, "scbl-windows-package.json", JsonSerializer.SerializeToUtf8Bytes(manifest));
            Write(archive, "SCBL-WINDOWS-PACKAGE.JSON", JsonSerializer.SerializeToUtf8Bytes(manifest));
            Write(archive, "artifacts/runtime/runtime.bin", [1]);
        }
        Expect<InvalidDataException>(() => new PackageDeployment(temporary.Paths, new ServerLog(temporary.Paths, "test")).Stage(packagePath));
    }

    private static void KnownComponentContractIsEnforced()
    {
        PackageManifest manifest = Manifest("1.0.0", new string('0', 64));
        manifest.Components[0].Destination = "other.exe";
        Expect<InvalidDataException>(() => manifest.Validate(fullPackage: false));
    }

    private static void BackupIncludesSqliteSideFiles()
    {
        using TemporaryRoot temporary = new();
        using (StagedPackage package = StageValid(temporary, "1.0.0", [1], full: true))
            new PackageDeployment(temporary.Paths, new ServerLog(temporary.Paths, "test")).ActivateRuntime(package);
        File.WriteAllBytes(temporary.Paths.Database, [1]);
        File.WriteAllBytes(temporary.Paths.Database + "-wal", [2]);
        File.WriteAllBytes(temporary.Paths.Database + "-shm", [3]);
        string backup = new BackupManager(temporary.Paths, new ServerLog(temporary.Paths, "test")).Create();
        using ZipArchive archive = ZipFile.OpenRead(backup);
        Assert(archive.GetEntry("data/dedicated/5th-echelon.db-wal") is not null, "SQLite WAL missing from backup");
        Assert(archive.GetEntry("data/dedicated/5th-echelon.db-shm") is not null, "SQLite SHM missing from backup");
    }

    private static void ExternalPointerRejectsEscape()
    {
        using TemporaryRoot temporary = new();
        string outside = Path.Combine(temporary.Root, "outside");
        Directory.CreateDirectory(outside);
        string binary = Path.Combine(outside, "easytier-core.exe");
        File.WriteAllBytes(binary, [1]);
        AtomicJson.Write(Path.Combine(temporary.Paths.EasyTier, "current.json"), new ExternalPointer(
            "1.0.0", "..\\..\\..\\outside", DateTimeOffset.UtcNow, new string('0', 64),
            new Dictionary<string, string> { ["easytier-core.exe"] = Hashing.Sha256(binary) }));
        Assert(new ExternalComponentManager(temporary.Paths, new ServerLog(temporary.Paths, "test")).FindEasyTierCore() is null, "escaped external pointer accepted");
    }

    private static void RuntimeTemplateUsesConfiguredPrefix()
    {
        ServerConfiguration config = new()
        {
            VirtualIp = "10.66.0.1",
            VirtualNetwork = "10.66.0.0/25",
            GamePort = 11010,
            EnableIpv6 = true,
        };
        string rendered = RuntimeTemplates.EasyTier(config, ServerSecrets.Create());
        Assert(rendered.Contains("ipv4 = \"10.66.0.1/25\"", StringComparison.Ordinal), "EasyTier template ignored configured prefix");
        Assert(rendered.Contains("listeners = [\"wss://0.0.0.0:11010\", \"quic://0.0.0.0:11010\"]", StringComparison.Ordinal), "EasyTier template did not use the shared dual-stack listeners");
        Assert(!rendered.Contains("[::]", StringComparison.Ordinal), "EasyTier template emitted duplicate IPv6 listeners");
        Assert(rendered.Contains("enable_ipv6 = true", StringComparison.Ordinal), "EasyTier template did not enable IPv6");

        config.EnableIpv6 = false;
        string ipv4Only = RuntimeTemplates.EasyTier(config, ServerSecrets.Create());
        Assert(ipv4Only.Contains("listeners = [\"wss://0.0.0.0:11010\", \"quic://0.0.0.0:11010\"]", StringComparison.Ordinal), "IPv4-only template changed the listener contract");
        Assert(ipv4Only.Contains("enable_ipv6 = false", StringComparison.Ordinal), "EasyTier template did not disable IPv6");
    }

    private static void ServerDescriptorProjectsSelectedChannel()
    {
        using TemporaryRoot temporary = new();
        ServerConfiguration config = new()
        {
            ServerId = "windows-stable",
            Mode = "stable",
            PublicHost = "server.example.test",
        };
        ServerSecrets secrets = ServerSecrets.Create();
        WriteClientChannel(temporary.Paths, "stable", "3.0.14", [1, 2, 3]);
        string descriptor = new ServerDescriptorPublisher(temporary.Paths).Publish(config, secrets);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(descriptor));
        JsonElement root = document.RootElement;
        Assert(root.GetProperty("server").GetProperty("mode").GetString() == "stable", "descriptor selected wrong channel");
        Assert(root.GetProperty("client").GetProperty("version").GetString() == "3.0.14", "descriptor launcher version mismatch");
        Assert(root.GetProperty("network").GetProperty("secret").GetString() == secrets.NetworkSecret, "descriptor network secret mismatch");
        Assert(root.GetProperty("announcements").GetProperty("update").GetProperty("version").GetString() == "3.0.14", "default update announcement mismatch");
    }

    private static void ServerDescriptorDriftRetainsPreviousDocument()
    {
        using TemporaryRoot temporary = new();
        ServerConfiguration config = new() { PublicHost = "server.example.test" };
        ServerSecrets secrets = ServerSecrets.Create();
        string artifact = WriteClientChannel(temporary.Paths, "stable", "3.0.14", [1, 2, 3]);
        ServerDescriptorPublisher publisher = new(temporary.Paths);
        string descriptor = publisher.Publish(config, secrets);
        byte[] before = File.ReadAllBytes(descriptor);
        File.WriteAllBytes(artifact, [9, 9, 9]);
        Expect<InvalidDataException>(() => publisher.Publish(config, secrets));
        Assert(File.ReadAllBytes(descriptor).SequenceEqual(before), "invalid dependency replaced previous descriptor");
    }

    private static void ClientPublicationVerifiesComponentsAndFullPackage()
    {
        using TemporaryRoot temporary = new();
        ServerConfiguration config = new() { Mode = "stable", PublicHost = "server.example.test" };
        ServerSecrets secrets = ServerSecrets.Create();
        Dictionary<string, (string Version, string File, string Mode, byte[] Payload)> componentInputs = new(StringComparer.Ordinal)
        {
            ["launcher"] = ("3.0.7", "SCBL.exe", "updater-restart", [1]),
            ["hooks"] = ("2.0.149", "uplay_r1_loader.dll", "before-game-start", [2]),
            ["route-guard"] = ("2.0.6", "route-guard.zip", "next-launch", [3]),
            ["easytier"] = ("2.6.5", "easytier-windows-x86_64.zip", "next-launch", [4]),
            ["updater"] = ("2.0.11", "SCBL.Updater.exe", "next-launch", [5]),
            ["client-files"] = ("2.0.29", "client-files.zip", "immediate", [6]),
        };
        ClientPublicationManager publisher = new(temporary.Paths);

        Dictionary<string, byte[]> fullFiles = new(StringComparer.Ordinal)
        {
            ["SCBL.exe"] = componentInputs["launcher"].Payload,
            ["SCBL.Updater.exe"] = componentInputs["updater"].Payload,
            ["components/hooks/uplay_r1_loader.dll"] = componentInputs["hooks"].Payload,
            ["components/hooks/uplay_r1_loader.dll.sha256"] = [7],
            ["components/hooks/mod/scbl_gameplay.dll"] = [8],
            ["components/hooks/mod/scbl_gameplay.dll.sha256"] = [9],
            ["components/network/easytier-core.exe"] = [10],
            ["components/network/easytier-cli.exe"] = [11],
            ["components/network/Packet.dll"] = [12],
            ["components/network/wintun.dll"] = [13],
            ["components/route-guard/scbl-process-router.exe"] = [14],
            ["components/route-guard/WinDivert.dll"] = [15],
            ["components/route-guard/WinDivert64.payload.sys"] = [16],
        };
        Dictionary<string, object> receipts = new(StringComparer.Ordinal)
        {
            ["launcher"] = Receipt(componentInputs["launcher"], new Dictionary<string, string> { ["SCBL.exe"] = Sha256(fullFiles["SCBL.exe"]) }),
            ["hooks"] = Receipt(componentInputs["hooks"], new Dictionary<string, string> { ["components/hooks/uplay_r1_loader.dll"] = Sha256(fullFiles["components/hooks/uplay_r1_loader.dll"]) }),
            ["route-guard"] = Receipt(componentInputs["route-guard"], new Dictionary<string, string>
            {
                ["components/route-guard/scbl-process-router.exe"] = Sha256(fullFiles["components/route-guard/scbl-process-router.exe"]),
                ["components/route-guard/WinDivert.dll"] = Sha256(fullFiles["components/route-guard/WinDivert.dll"]),
                ["components/route-guard/WinDivert64.payload.sys"] = Sha256(fullFiles["components/route-guard/WinDivert64.payload.sys"]),
            }),
            ["easytier"] = Receipt(componentInputs["easytier"], new Dictionary<string, string>
            {
                ["components/network/easytier-core.exe"] = Sha256(fullFiles["components/network/easytier-core.exe"]),
                ["components/network/easytier-cli.exe"] = Sha256(fullFiles["components/network/easytier-cli.exe"]),
                ["components/network/Packet.dll"] = Sha256(fullFiles["components/network/Packet.dll"]),
                ["components/network/wintun.dll"] = Sha256(fullFiles["components/network/wintun.dll"]),
            }),
            ["updater"] = Receipt(componentInputs["updater"], new Dictionary<string, string> { ["SCBL.Updater.exe"] = Sha256(fullFiles["SCBL.Updater.exe"]) }),
            ["client-files"] = Receipt(componentInputs["client-files"], new Dictionary<string, string>()),
        };
        string fullPath = Path.Combine(temporary.Root, "SCBL-Client-v3.0.7-win-x86.zip");
        using (ZipArchive full = ZipFile.Open(fullPath, ZipArchiveMode.Create))
        {
            var manifest = new
            {
                schemaVersion = 1,
                clientVersion = "3.0.7",
                componentVersions = componentInputs.ToDictionary(item => item.Key, item => item.Value.Version, StringComparer.Ordinal),
                componentReceipts = receipts,
                files = fullFiles.Select(item => new { path = item.Key, size = item.Value.LongLength, sha256 = Sha256(item.Value) }).ToArray(),
            };
            Write(full, "client_package_manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest));
            foreach ((string path, byte[] payload) in fullFiles) Write(full, path, payload);
        }
        ClientArchivePublication fullResult = publisher.PublishArchive(fullPath, "stable", config, secrets);
        Assert(fullResult.InputKind == "full", "full package was not detected");
        Assert(fullResult.Version == "3.0.7", "full package version mismatch");
        Assert(File.Exists(Path.Combine(temporary.Paths.ClientUpdates, "SCBL.zip")), "fixed client package alias missing");
        Assert(Hashing.Sha256(fullPath) != Hashing.Sha256(Path.Combine(temporary.Paths.ClientUpdates, "SCBL.zip")), "full package was copied without regenerating the normalized SCBL.zip");
        Assert(File.Exists(Path.Combine(temporary.Paths.ClientUpdates, "scbl-server.json")), "server descriptor missing after client publication");
        Assert(publisher.Status("stable").FullPackageVersion == "3.0.7", "published client status mismatch");
        Assert(publisher.Status("stable").Components.Count == 6, "full package did not generate six component updates");

        byte[] updatedLauncher = [42, 43];
        string patchPath = Path.Combine(temporary.Root, "launcher-update.zip");
        using (ZipArchive patch = ZipFile.Open(patchPath, ZipArchiveMode.Create))
        {
            var records = new[]
            {
                new
                {
                    component = "launcher",
                    version = "3.0.8",
                    path = "artifacts/launcher/SCBL.exe",
                    file = "SCBL.exe",
                    updateMode = "updater-restart",
                    size = updatedLauncher.LongLength,
                    sha256 = Sha256(updatedLauncher),
                },
            };
            Write(patch, "scbl-client-patch.json", JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, packageType = "scbl-client-components-patch", components = records }));
            Write(patch, "artifacts/launcher/SCBL.exe", updatedLauncher);
        }
        string previousAliasHash = Hashing.Sha256(Path.Combine(temporary.Paths.ClientUpdates, "SCBL.zip"));
        ClientArchivePublication patchResult = publisher.PublishArchive(patchPath, "stable", config, secrets);
        Assert(patchResult.InputKind == "patch", "patch ZIP was not detected");
        Assert(patchResult.Version == "3.0.8", "patch did not advance the generated full package");
        Assert(previousAliasHash != Hashing.Sha256(Path.Combine(temporary.Paths.ClientUpdates, "SCBL.zip")), "patch did not regenerate SCBL.zip");
        Assert(publisher.Status("stable").FullPackageVersion == "3.0.8", "patch-generated full package status mismatch");

        string validAliasHash = Hashing.Sha256(Path.Combine(temporary.Paths.ClientUpdates, "SCBL.zip"));
        string invalidPatchPath = Path.Combine(temporary.Root, "invalid-update.zip");
        using (ZipArchive invalidPatch = ZipFile.Open(invalidPatchPath, ZipArchiveMode.Create))
        {
            var records = new[]
            {
                new
                {
                    component = "launcher",
                    version = "3.0.9",
                    path = "artifacts/launcher/SCBL.exe",
                    file = "SCBL.exe",
                    updateMode = "updater-restart",
                    size = 1,
                    sha256 = new string('0', 64),
                },
            };
            Write(invalidPatch, "scbl-client-patch.json", JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, packageType = "scbl-client-components-patch", components = records }));
            Write(invalidPatch, "artifacts/launcher/SCBL.exe", [99]);
        }
        Expect<InvalidDataException>(() => publisher.PublishArchive(invalidPatchPath, "stable", config, secrets));
        Assert(validAliasHash == Hashing.Sha256(Path.Combine(temporary.Paths.ClientUpdates, "SCBL.zip")), "failed patch changed SCBL.zip");
    }

    private static object Receipt((string Version, string File, string Mode, byte[] Payload) component, Dictionary<string, string> files) => new
    {
        version = component.Version,
        sha256 = Sha256(component.Payload),
        files,
    };

    private static string Sha256(byte[] payload) => Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

    private static string WriteClientChannel(ServerPaths paths, string channel, string version, byte[] content)
    {
        string relative = $"components/artifacts/launcher/{version}/SCBL.exe";
        string artifact = Path.Combine(paths.ClientUpdates, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        File.WriteAllBytes(artifact, content);
        var manifest = new
        {
            schemaVersion = 2,
            channel,
            generatedAt = "2026-08-26T00:00:00Z",
            components = new Dictionary<string, object>
            {
                ["launcher"] = new
                {
                    version,
                    sha256 = Hashing.Sha256(artifact),
                    size = content.LongLength,
                    url = "/" + relative,
                    updateMode = "next-launch",
                    required = true,
                },
            },
            files = new Dictionary<string, object>(),
        };
        string manifestPath = Path.Combine(paths.ClientUpdates, "components", "channels", channel, "client_components_v2.json");
        AtomicFile.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));
        return artifact;
    }

    private static string CreatePayload(string root, byte[] payload)
    {
        string path = Path.Combine(root, "digest-input-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, payload);
        return path;
    }

    private static void PartialUpdatePreservesRuntime()
    {
        using TemporaryRoot temporary = new();
        PackageDeployment deployment = new(temporary.Paths, new ServerLog(temporary.Paths, "test"));
        using (StagedPackage first = StageValid(temporary, "1.0.0", [1], full: true)) deployment.ActivateRuntime(first);
        string current = temporary.Paths.RequireCurrentRelease();
        File.WriteAllText(Path.Combine(current, "unchanged.txt"), "keep");
        using (StagedPackage second = StageValid(temporary, "1.0.1", [2])) deployment.ActivateRuntime(second);
        string updated = temporary.Paths.RequireCurrentRelease();
        Assert(File.ReadAllText(Path.Combine(updated, "unchanged.txt")) == "keep", "unchanged runtime file was lost");
        Assert(File.ReadAllBytes(Path.Combine(updated, "dedicated", "SCBL.Dedicated.exe")).SequenceEqual(new byte[] { 2 }), "changed runtime file was not replaced");
    }

    private static StagedPackage StageValid(TemporaryRoot temporary, string version, byte[] payload, bool full = false)
    {
        string directory = Path.Combine(temporary.Root, "stage-" + version + "-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(directory, "artifacts", "runtime", "runtime.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllBytes(source, payload);
        PackageManifest manifest = Manifest(version, Hashing.Sha256(source));
        manifest.PackageType = full ? "windows-full" : "windows-update";
        return new StagedPackage(directory, manifest);
    }

    private static PackageManifest Manifest(string version, string digest) => new()
    {
        SchemaVersion = 1,
        PackageType = "windows-update",
        ReleaseVersion = version,
        Components =
        [
            new PackageComponent
            {
                Id = "server.dedicated",
                Version = version,
                Path = "artifacts/runtime/runtime.bin",
                Destination = "dedicated/SCBL.Dedicated.exe",
                Target = "runtime",
                Size = 1,
                Sha256 = digest,
            },
        ],
    };

    private static void Write(ZipArchive archive, string name, byte[] payload)
    {
        using Stream output = archive.CreateEntry(name).Open();
        output.Write(payload);
    }

    private static void Assert(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }
}

internal sealed class TemporaryRoot : IDisposable
{
    public TemporaryRoot()
    {
        Root = Path.Combine(Path.GetTempPath(), "scbl-windows-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Paths = new ServerPaths(Path.Combine(Root, "program"), Path.Combine(Root, "data"));
        Paths.EnsureLayout();
    }
    public string Root { get; }
    public ServerPaths Paths { get; }
    public void Dispose() => Directory.Delete(Root, recursive: true);
}
