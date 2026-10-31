using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using SplinterCellCNLauncher.Services;

internal static partial class Program
{
    private static int ordinal, passed, skipped;
    private static byte[]? official;
    private static string Dll => Path.Combine(Path.GetDirectoryName(Environment.ProcessPath!)!, "wintun.dll");
    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] != "--wintun-input") return await Child(args);
        bool createdDll = false;
        int result = 1;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Check(args.Length == 2 && args[0] == "--wintun-input" && !Environment.Is64BitProcess);
                using var source = new FileStream(args[1], FileMode.Open, FileAccess.Read, FileShare.Read);
                Check(source.Length == OwnedCoreDependencyInputs.MemberBytes);
                official = new byte[source.Length]; source.ReadExactly(official);
                Check(Digest(official) == OwnedCoreDependencyInputs.MemberSha256);
                using var output = new FileStream(Dll, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                createdDll = true; output.Write(official);
            }
            else Check(args.Length == 0);

            await Case("production catalog matches preserved official origin", () => {
                using var json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "official-origin.json")));
                var archive = json.RootElement.GetProperty("archive");
                Check(archive.GetProperty("source_url").GetString() == OwnedCoreDependencyInputs.SourceUrl);
                Check(archive.GetProperty("bytes").GetInt64() == OwnedCoreDependencyInputs.ArchiveBytes);
                Check(archive.GetProperty("sha256").GetString() == OwnedCoreDependencyInputs.ArchiveSha256);
                var member = json.RootElement.GetProperty("members").EnumerateArray().Single(row => row.GetProperty("path").GetString() == OwnedCoreDependencyInputs.ArchiveMember);
                Check(member.GetProperty("bytes").GetInt64() == OwnedCoreDependencyInputs.MemberBytes);
                Check(member.GetProperty("sha256").GetString() == OwnedCoreDependencyInputs.MemberSha256);
                Check(!json.RootElement.GetProperty("public_admission_allowed").GetBoolean());
                return Task.CompletedTask;
            });
            await Case("dependency origin accepts no caller digest and has no independent disposal", () => {
                var methods = typeof(OwnedCoreDependencyInputs).GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                Check(methods.Single(m => m.Name == "ForOfficialWintun0141Amd64").GetParameters().Length == 1);
                Check(!typeof(IDisposable).IsAssignableFrom(typeof(OwnedCoreDependencyInputs)));
                Check(!typeof(IAsyncDisposable).IsAssignableFrom(typeof(OwnedCoreDependencyInputs)));
                var value = OwnedCoreDependencyInputs.ForOfficialWintun0141Amd64(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
                Check(Path.GetFileName(value.FilePath) == "wintun.dll");
                Check(!value.ToString().Contains(value.FilePath) && !value.ToString().Contains(OwnedCoreDependencyInputs.MemberSha256));
                return Task.CompletedTask;
            });
            await Case("relative and traversal runtime directories reject", () => {
                Reject(() => OwnedCoreDependencyInputs.ForOfficialWintun0141Amd64("relative"));
                Reject(() => OwnedCoreDependencyInputs.ForOfficialWintun0141Amd64(Path.Combine(Path.GetTempPath(), "one", "..", "two")));
                return Task.CompletedTask;
            });
            await Case("dependency must be exactly adjacent to executable", () => {
                var folder = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ff-dependency-declaration"));
                var dep = OwnedCoreDependencyInputs.ForOfficialWintun0141Amd64(folder);
                Reject(() => Specification(Path.Combine(folder, "other", "child.exe"), Path.Combine(folder, "config.json"), dep));
                Reject(() => Specification(Path.Combine(folder, "child.exe"), dep.FilePath, dep));
                Reject(() => Specification(dep.FilePath, Path.Combine(folder, "config.json"), dep));
                return Task.CompletedTask;
            });
            await Case("absent dependency preserves ordinary input declaration", () => {
                var folder = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ff-dependency-declaration"));
                _ = Specification(Path.Combine(folder, "child.exe"), Path.Combine(folder, "config.json"), null);
                return Task.CompletedTask;
            });
            await Case("real official bytes stay locked from suspended creation through drain", async () => {
                await using var fixture = new Fixture();
                await using var owner = fixture.Create();
                Reject(() => File.WriteAllBytes(Dll, official!));
                Reject(() => File.Delete(Dll));
                Reject(() => File.Move(Dll, Dll + ".moved"));
                Check(!File.Exists(fixture.Ready));
                await owner.ResumeAsync(); await Ready(fixture.Ready);
                Reject(() => File.WriteAllBytes(Dll, official!));
                await owner.StopAsync(); File.WriteAllBytes(Dll, official!);
            }, true);
            await Case("failed DLL validation creates no child and releases prior image config pins", async () => {
                await using var fixture = new Fixture(isolated: true);
                await RejectCreate(fixture.Inputs()); Check(!File.Exists(fixture.Ready));
                File.Move(fixture.Image, fixture.Image + ".released");
                File.Move(fixture.Config, fixture.Config + ".released");
                File.Delete(fixture.Dependency);
            }, true);
            await Case("same-sized changed official bytes reject without allowing launch", async () => {
                await using var fixture = new Fixture();
                try
                {
                    var altered = official!.ToArray(); altered[^1] ^= 1; File.WriteAllBytes(Dll, altered);
                    await RejectCreate(fixture.Inputs()); Check(!File.Exists(fixture.Ready));
                    File.WriteAllText(fixture.Config, "released");
                }
                finally { File.WriteAllBytes(Dll, official!); }
            }, true);
            await Case("missing dependency fails before child creation", async () => {
                await using var fixture = new Fixture();
                string held = Dll + ".held"; File.Move(Dll, held);
                try { await RejectCreate(fixture.Inputs()); Check(!File.Exists(fixture.Ready)); }
                finally { File.Move(held, Dll); }
            }, true);
            await Case("reparse DLL input rejects even with correct target bytes", async () => {
                await using var fixture = new Fixture(isolated: true);
                File.Delete(fixture.Dependency); File.CreateSymbolicLink(fixture.Dependency, Dll);
                await RejectCreate(fixture.Inputs()); Check(!File.Exists(fixture.Ready));
                File.Delete(fixture.Dependency);
            }, true);
            await Case("parent image exit cannot release dependency while owned descendant remains", async () => {
                await using var fixture = new Fixture(mode: "spawn-and-exit");
                await using var owner = fixture.Create();
                await owner.ResumeAsync(); await Ready(fixture.Ready); await Ready(fixture.Descendant);
                var state = JsonSerializer.Deserialize<ChildIdentity>(File.ReadAllBytes(fixture.Descendant))!;
                using var descendant = Process.GetProcessById(state.Pid);
                _ = descendant.SafeHandle;
                Check(descendant.StartTime.ToUniversalTime().Ticks == state.Birth && !descendant.HasExited);
                await Until(() => !owner.IsAlive);
                Check(!descendant.HasExited);
                Reject(() => File.WriteAllBytes(Dll, official!));
                Reject(() => File.Delete(Dll));
                await owner.StopAsync();
                await descendant.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                File.WriteAllBytes(Dll, official!);
            }, true);
            await Case("cancelled resume drains suspended child before dependency release", async () => {
                await using var fixture = new Fixture();
                await using var owner = fixture.Create();
                using var stop = new CancellationTokenSource(); stop.Cancel();
                try { await owner.ResumeAsync(stop.Token); throw new Exception(); } catch (OperationCanceledException) { }
                Check(!owner.IsAlive && !File.Exists(fixture.Ready)); File.WriteAllBytes(Dll, official!);
            }, true);
            await Case("explicit no-dependency branch does not silently pin ambient DLL", async () => {
                await using var fixture = new Fixture();
                await using var owner = fixture.Create(withDependency: false);
                File.WriteAllBytes(Dll, official!);
                await owner.StopAsync();
            }, true);
            Console.WriteLine($"RESULT {passed} passed; {skipped} skipped");
            result = 0;
        }
        catch (Exception error) { Failure(error); }
        finally
        {
            if (createdDll)
            {
                try { File.Delete(Dll); }
                catch (Exception error) { ordinal = 0; Failure(error); result = 1; }
            }
        }
        return result;
    }

    private static async Task Case(string name, Func<Task> action, bool windows = false)
    {
        ordinal++;
        if (windows && !OperatingSystem.IsWindows()) { skipped++; Console.WriteLine("SKIP Windows-only: " + name); return; }
        await action(); passed++; Console.WriteLine("PASS " + name);
    }
    private static void Failure(Exception error)
    {
        string kind = error switch { UnauthorizedAccessException => "UnauthorizedAccessException", IOException => "IOException",
            OperationCanceledException => "OperationCanceledException", TimeoutException => "TimeoutException",
            InvalidOperationException => "InvalidOperationException", _ => "OtherException" };
        Console.WriteLine($"CI_MANAGED_FAILURE suite=OwnedCoreDependencies case={(ordinal == 0 ? "SETUP_OR_CLEANUP" : ordinal.ToString("00"))} exception={kind}");
    }
    private static void Check(bool value) { if (!value) throw new InvalidOperationException(); }
    private static string Digest(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    private static string DigestFile(string path) { using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant(); }
    private static void Reject(Action action) { try { action(); } catch (IOException) { return; } catch (UnauthorizedAccessException) { return; } throw new InvalidOperationException(); }
    private static async Task RejectCreate(OwnedCoreLaunchInputs input)
    {
        OwnedCoreProcess owner;
        try { owner = OwnedCoreProcess.CreateSuspended(input); }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }
        // A cleanup failure after unexpected successful creation is not evidence
        // of input rejection and must propagate independently.
        await owner.StopAsync();
        throw new InvalidOperationException();
    }
    private static OwnedCoreLaunchInputs Specification(string image, string config, OwnedCoreDependencyInputs? dependency)
        => new(image, 1, new string('0', 64), config, 1, new string('1', 64), dependency);
    private static async Task Until(Func<bool> condition)
    {
        long deadline = Environment.TickCount64 + 8000;
        while (!condition()) { if (Environment.TickCount64 >= deadline) throw new TimeoutException(); await Task.Delay(20); }
    }
    private static Task Ready(string file) => Until(() => File.Exists(file));
    private sealed record Settings(string Mode, string Ready, string Descendant);
    private sealed record ChildIdentity(int Pid, long Birth);
    private static async Task<int> Child(string[] args)
    {
        try
        {
            if (args.Length == 2 && args[0] == "--descendant")
            {
                using var self = Process.GetCurrentProcess();
                string temporary = args[1] + ".partial";
                File.WriteAllText(temporary, JsonSerializer.Serialize(new ChildIdentity(self.Id, self.StartTime.ToUniversalTime().Ticks)));
                File.Move(temporary, args[1]);
                await Task.Delay(20000); return 0;
            }
            if (args.Length != 7 || args[0] != "--config-file" || args[2] != "--disable-env-parsing"
                || args[3] != "--console-log-level" || args[4] != "off" || args[5] != "--file-log-level" || args[6] != "off") return 2;
            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllBytes(args[1]))!;
            if (settings.Mode == "spawn-and-exit")
            {
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add("--descendant"); start.ArgumentList.Add(settings.Descendant);
                using var child = Process.Start(start) ?? throw new InvalidOperationException();
                await Ready(settings.Descendant);
            }
            File.WriteAllText(settings.Ready, "ready");
            if (settings.Mode != "spawn-and-exit") await Task.Delay(20000);
            return 0;
        }
        catch { return 2; }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "ff-owned-dependency-" + Guid.NewGuid().ToString("N"));
        internal string Config => Path.Combine(Root, "configuration.json");
        internal string Ready => Path.Combine(Root, "ready");
        internal string Descendant => Path.Combine(Root, "descendant");
        internal string Image { get; }
        internal string Dependency => Path.Combine(Path.GetDirectoryName(Image)!, "wintun.dll");
        private readonly List<OwnedCoreProcess> owners = new();
        internal Fixture(string mode = "idle", bool isolated = false)
        {
            Directory.CreateDirectory(Root);
            Image = isolated ? Path.Combine(Root, "owned-child.exe") : Environment.ProcessPath!;
            if (isolated) { File.Copy(Environment.ProcessPath!, Image); File.WriteAllBytes(Dependency, [0]); }
            File.WriteAllText(Config, JsonSerializer.Serialize(new Settings(mode, Ready, Descendant)));
        }
        internal OwnedCoreLaunchInputs Inputs(bool withDependency = true) => new(Image, new FileInfo(Image).Length, DigestFile(Image),
            Config, new FileInfo(Config).Length, DigestFile(Config), withDependency ? OwnedCoreDependencyInputs.ForOfficialWintun0141Amd64(Path.GetDirectoryName(Image)!) : null);
        internal OwnedCoreProcess Create(bool withDependency = true)
        { var owner = OwnedCoreProcess.CreateSuspended(Inputs(withDependency)); owners.Add(owner); return owner; }
        public async ValueTask DisposeAsync()
        {
            foreach (var owner in owners) await owner.StopAsync();
            await DeleteOwnedDirectoryAsync(Root, () => owners.All(owner => !owner.IsAlive));
        }
    }
}
