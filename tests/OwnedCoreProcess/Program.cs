using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using SplinterCellCNLauncher.Services;

internal static partial class Program
{
    private static int ordinal, passed, skipped;
    private static string stage = "INPUT";
    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 0) return await Helper(args);
        try
        {
            await Case("reject malformed independent hashes", () => {
                foreach (string hash in new[] { "", new string('A', 64), new string('0', 63) })
                    Reject(() => Inputs(imageHash: hash));
                return Task.CompletedTask;
            });
            await Case("reject unbounded input sizes", () => {
                foreach (long size in new[] { 0L, -1, 256L * 1024 * 1024 + 1 }) Reject(() => Inputs(imageLength: size));
                Reject(() => Inputs(configLength: 1024 * 1024 + 1)); return Task.CompletedTask;
            });
            await Case("reject relative and aliased input paths", () => {
                Reject(() => Inputs(image: "relative.exe"));
                Reject(() => Inputs(image: Path.Combine(Path.GetTempPath(), "child", "..", "image.exe")));
                Reject(() => Inputs(config: Path.Combine(Path.GetTempPath(), "image.exe")));
                return Task.CompletedTask;
            });
            await Case("private input representation contains no paths or digests", () => {
                string value = Inputs().ToString(); Check(!value.Contains(Path.GetTempPath()) && !value.Contains(new string('0', 64)));
                return Task.CompletedTask;
            });
            await Case("pre-cancellation creates nothing", async () => {
                using var stop = new CancellationTokenSource(); stop.Cancel();
                try { await using var owned = OwnedCoreProcess.CreateSuspended(Inputs(), stop.Token); throw new Exception(); }
                catch (OperationCanceledException) { }
            });
            await Case("suspended exact identity executes only after resume and stops idempotently", async () => {
                Check(!Environment.Is64BitProcess); // Actual launcher architecture, not just a publish argument.
                await using var fixture = new Fixture();
                await using var owned = fixture.Create();
                Check(owned.IsAlive && owned.Matches(owned.ProcessId, owned.CreationFileTime));
                Check(!owned.Matches(owned.ProcessId, owned.CreationFileTime + 1));
                await Task.Delay(150); Check(!File.Exists(fixture.Ready));
                stage = "RESUME"; await owned.ResumeAsync(); await Ready(fixture.Ready);
                stage = "STOP"; await owned.StopAsync(); await owned.StopAsync(); Check(!owned.IsAlive);
            }, windows: true);
            await Case("wrong image digest prevents child creation", async () => {
                await using var fixture = new Fixture();
                Reject(() => OwnedCoreProcess.CreateSuspended(fixture.Inputs(wrongImage: true)));
                Check(!File.Exists(fixture.Ready));
            }, windows: true);
            await Case("wrong config digest prevents child creation", async () => {
                await using var fixture = new Fixture();
                Reject(() => OwnedCoreProcess.CreateSuspended(fixture.Inputs(wrongConfig: true)));
                Check(!File.Exists(fixture.Ready));
            }, windows: true);
            await Case("verified configuration cannot be replaced during owned lifetime", async () => {
                await using var fixture = new Fixture();
                await using var owned = fixture.Create();
                Reject(() => File.WriteAllText(fixture.Config, "replaced")); Reject(() => File.Delete(fixture.Config));
                await owned.StopAsync(); File.WriteAllText(fixture.Config, "released");
            }, windows: true);
            await Case("verified path ancestors cannot be renamed during owned lifetime", async () => {
                await using var fixture = new Fixture();
                await using var owned = fixture.Create();
                Reject(() => Directory.Move(fixture.Root, fixture.Root + "-moved"));
                await owned.StopAsync();
            }, windows: true);
            await Case("cancelled resume kills the still-suspended exact child", async () => {
                await using var fixture = new Fixture();
                await using var owned = fixture.Create();
                using var stop = new CancellationTokenSource(); stop.Cancel();
                try { await owned.ResumeAsync(stop.Token); throw new Exception(); } catch (OperationCanceledException) { }
                Check(!owned.IsAlive && !File.Exists(fixture.Ready));
            }, windows: true);
            await Case("disposing before resume never executes child code", async () => {
                await using var fixture = new Fixture();
                var owned = fixture.Create();
                await owned.DisposeAsync(); Check(!owned.IsAlive && !File.Exists(fixture.Ready));
            }, windows: true);
            await Case("natural child exit releases ownership without claiming liveness", async () => {
                await using var fixture = new Fixture("natural");
                await using var owned = fixture.Create();
                await owned.ResumeAsync(); await Ready(fixture.Ready);
                await Until(() => !owned.IsAlive); Check(!owned.Matches(owned.ProcessId, owned.CreationFileTime));
                await owned.StopAsync();
            }, windows: true);
            await Case("ambient network and return-channel environment is not inherited", async () => {
                string[] keys = ["ET_NETWORK_SECRET", "FF_PUBLIC_RETURN_V1", "FF_OWNED_CORE_READ_V1", "OWNED_CORE_SENTINEL"];
                string?[] previous = keys.Select(Environment.GetEnvironmentVariable).ToArray();
                try {
                    foreach (string key in keys) Environment.SetEnvironmentVariable(key, "synthetic-not-inherited");
                    await using var fixture = new Fixture(); await using var owned = fixture.Create();
                    await owned.ResumeAsync(); var state = await Ready(fixture.Ready); Check(state.AmbientAbsent);
                } finally { for (int i = 0; i < keys.Length; i++) Environment.SetEnvironmentVariable(keys[i], previous[i]); }
            }, windows: true);
            await Case("reparse input cannot become a pinned configuration", async () => {
                await using var fixture = new Fixture();
                string link = Path.Combine(fixture.Root, "linked.json"); File.CreateSymbolicLink(link, fixture.Config);
                Reject(() => OwnedCoreProcess.CreateSuspended(fixture.Inputs(config: link)));
            }, windows: true);
            await Case("parent death kills exact child and descendant without cleanup callbacks", async () => {
                await using var fixture = new Fixture("spawn");
                using var parent = Spawn("--parent", fixture.Config);
                try {
                    var childState = await Ready(fixture.Ready); var grandState = await Ready(fixture.DescendantReady);
                    await Until(() => File.Exists(fixture.ParentReady));
                    using var child = Observe(childState); using var grandchild = Observe(grandState);
                    stage = "PARENT_DEATH"; parent.Kill(entireProcessTree: false); await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); await grandchild.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                } finally { await End(parent); }
            }, windows: true);
            await Case("explicit stop contains descendants while leaving a foreign same-image child", async () => {
                await using var fixture = new Fixture("spawn");
                string foreignReady = Path.Combine(fixture.Root, "foreign.json"); using var foreign = Spawn("--descendant", foreignReady);
                try {
                    await Ready(foreignReady);
                    await using var owned = fixture.Create();
                    await owned.ResumeAsync(); await Ready(fixture.Ready); var state = await Ready(fixture.DescendantReady);
                    using var descendant = Observe(state);
                    await owned.StopAsync(); await descendant.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    Check(!foreign.HasExited);
                } finally { await End(foreign); }
            }, windows: true);
            Console.WriteLine($"RESULT {passed} passed; {skipped} skipped"); return 0;
        }
        catch { return 1; }
    }
    private static async Task Case(string name, Func<Task> action, bool windows = false)
    {
        ordinal++; stage = "INPUT";
        if (windows && !OperatingSystem.IsWindows()) { skipped++; Console.WriteLine("SKIP Windows-only: " + name); return; }
        try { await action(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception error)
        {
            string kind = error.GetType().Name;
            string[] allowed = ["IOException", "UnauthorizedAccessException", "PlatformNotSupportedException", "OperationCanceledException", "TaskCanceledException", "TimeoutException", "InvalidOperationException", "ArgumentException", "Exception", "JsonException"];
            if (!allowed.Contains(kind)) kind = "OtherException";
            string[] codes = ["OWNED_CORE_INPUTS_INVALID", "OWNED_CORE_WINDOWS_REQUIRED", "OWNED_CORE_JOB_FAILED",
                "OWNED_CORE_ATTRIBUTES_FAILED", "OWNED_CORE_ENVIRONMENT_FAILED", "OWNED_CORE_CREATE_FAILED",
                "OWNED_CORE_MEMBERSHIP_FAILED", "OWNED_CORE_CLEANUP_FAILED", "OWNED_CORE_STATE_INVALID",
                "OWNED_CORE_RESUME_FAILED", "PUBLIC_RETURN_REJECTED"];
            string code = codes.Contains(error.Message, StringComparer.Ordinal) ? error.Message : "OTHER";
            Console.WriteLine($"CI_MANAGED_FAILURE suite=OwnedCoreProcess case={ordinal:00} exception={kind}");
            Console.WriteLine($"CI_OWNED_CORE_FAILURE case={ordinal:00} phase={stage} code={code}");
            throw;
        }
    }
    private static void Check(bool value) { if (!value) throw new Exception(); }
    private static void Reject(Action action) { try { action(); } catch (IOException) { return; } catch (UnauthorizedAccessException) { return; } throw new Exception(); }
    private static OwnedCoreLaunchInputs Inputs(string? image = null, long imageLength = 1, string? imageHash = null, string? config = null, long configLength = 1)
        => new(image ?? Path.Combine(Path.GetTempPath(), "image.exe"), imageLength, imageHash ?? new string('0', 64),
            config ?? Path.Combine(Path.GetTempPath(), "configuration.json"), configLength, new string('1', 64));
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
    private static async Task Until(Func<bool> condition)
    {
        long end = Environment.TickCount64 + 10000;
        while (!condition()) { if (Environment.TickCount64 >= end) throw new TimeoutException(); await Task.Delay(20); }
    }
    private sealed record ChildState(int Pid, long CreationTicks, bool AmbientAbsent);
    private static void Mark(string path)
    {
        using var self = Process.GetCurrentProcess();
        bool absent = new[] { "ET_NETWORK_SECRET", "FF_PUBLIC_RETURN_V1", "FF_OWNED_CORE_READ_V1", "OWNED_CORE_SENTINEL" }.All(k => Environment.GetEnvironmentVariable(k) is null);
        string temporary = path + ".partial";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new ChildState(self.Id, self.StartTime.ToUniversalTime().Ticks, absent)));
        File.Move(temporary, path);
    }
    private static async Task<ChildState> Ready(string path)
    {
        stage = "CHILD_READY"; await Until(() => File.Exists(path)); return JsonSerializer.Deserialize<ChildState>(File.ReadAllText(path))!;
    }
    private static Process Observe(ChildState state)
    {
        var child = Process.GetProcessById(state.Pid);
        try { _ = child.SafeHandle; Check(child.StartTime.ToUniversalTime().Ticks == state.CreationTicks && !child.HasExited); return child; }
        catch { child.Dispose(); throw; }
    }
    private static Process Spawn(string operation, string argument)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(operation); start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new Exception();
    }
    private static async Task End(Process process)
    {
        if (!process.HasExited) process.Kill(entireProcessTree: false);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }
    private sealed record Config(string Mode, string Ready, string DescendantReady, string ParentReady);
    private static async Task<int> Helper(string[] args)
    {
        try
        {
            if (args.Length == 2 && args[0] == "--descendant") { Mark(args[1]); await Task.Delay(35000); return 0; }
            if (args.Length == 2 && args[0] == "--parent")
            {
                var config = JsonSerializer.Deserialize<Config>(File.ReadAllText(args[1]))!;
                var input = new OwnedCoreLaunchInputs(Environment.ProcessPath!, new FileInfo(Environment.ProcessPath!).Length, Hash(Environment.ProcessPath!), args[1], new FileInfo(args[1]).Length, Hash(args[1]));
                await using var child = OwnedCoreProcess.CreateSuspended(input);
                await child.ResumeAsync(); Mark(config.ParentReady); await Task.Delay(35000); return 0;
            }
            if (args.Length != 7 || args[0] != "--config-file" || args[2] != "--disable-env-parsing" || args[3] != "--console-log-level" || args[4] != "off" || args[5] != "--file-log-level" || args[6] != "off") return 2;
            var settings = JsonSerializer.Deserialize<Config>(File.ReadAllText(args[1]))!;
            if (settings.Mode == "spawn") { using var descendant = Spawn("--descendant", settings.DescendantReady); }
            Mark(settings.Ready);
            if (settings.Mode != "natural") await Task.Delay(35000);
            return 0;
        }
        catch { Console.WriteLine("OWNED_CORE_SYNTHETIC_HELPER_REJECTED"); return 2; }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "ff-owned-core-" + Guid.NewGuid().ToString("N"));
        internal string Config => Path.Combine(Root, "configuration.json");
        internal string Ready => Path.Combine(Root, "child.json");
        internal string DescendantReady => Path.Combine(Root, "descendant.json");
        internal string ParentReady => Path.Combine(Root, "parent.json");
        private readonly List<OwnedCoreProcess> owners = new();
        internal OwnedCoreProcess Create() { var owner = OwnedCoreProcess.CreateSuspended(Inputs()); owners.Add(owner); return owner; }
        internal Fixture(string mode = "idle") { Directory.CreateDirectory(Root); File.WriteAllText(Config, JsonSerializer.Serialize(new Program.Config(mode, Ready, DescendantReady, ParentReady))); }
        internal OwnedCoreLaunchInputs Inputs(bool wrongImage = false, bool wrongConfig = false, string? config = null)
            => new(Environment.ProcessPath!, new FileInfo(Environment.ProcessPath!).Length, wrongImage ? new string('0', 64) : Hash(Environment.ProcessPath!),
                config ?? Config, new FileInfo(Config).Length, wrongConfig ? new string('0', 64) : Hash(Config));
        public async ValueTask DisposeAsync()
        {
            // Drain every exact owner before touching this fresh directory.
            // A failed drain retains the directory and propagates the failure.
            foreach (var owner in owners) await owner.StopAsync();
            await DeleteOwnedDirectoryAsync(Root, () => owners.All(owner => !owner.IsAlive));
        }
    }
}
