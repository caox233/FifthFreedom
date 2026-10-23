using System.Diagnostics;
using System.Reflection;
using System.Text;
using ScblLan;
using SplinterCellCNLauncher.Models;
using SplinterCellCNLauncher.Services;

internal static class Program
{
    private static int passed;
    private static bool failureReported;
    private static string phase = "SETUP_PACKAGE";
    private static void ReportFailure(string which, Exception error)
    {
        if (failureReported) return;
        failureReported = true;
        string type = error.GetType().Name;
        string[] allowed = ["AuthenticationException", "CryptographicException", "IOException", "EndOfStreamException",
            "SocketException", "HttpRequestException", "OperationCanceledException", "TaskCanceledException", "TimeoutException",
            "ArgumentException", "InvalidOperationException", "ObjectDisposedException", "PlatformNotSupportedException", "PublicControlException",
            "Win32Exception", "FileNotFoundException", "DirectoryNotFoundException", "UnauthorizedAccessException", "InvalidDataException",
            "MissingMethodException", "JsonException", "Exception", "OtherException"];
        if (!allowed.Contains(type, StringComparer.Ordinal)) type = "OtherException";
        Console.WriteLine($"CI_MANAGED_FAILURE suite=FinalLaunchPreparation case={which} exception={type}");
    }
    private const string Username = "requested-user", Password = "synthetic-password", Account = "synthetic-account";
    private static void Check(bool result) { if (!result) throw new Exception("Synthetic preparation assertion failed"); }
    private static async Task Case(string name, Func<Task> action)
    {
        try { await action(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception error) { ReportFailure((passed + 1).ToString("D2", System.Globalization.CultureInfo.InvariantCulture), error); throw; }
    }
    private static byte[] RawFrame(LaunchPipe pipe) => ((byte[])typeof(LaunchPipe).GetField("config", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pipe)!).ToArray();
    private static async Task<bool> Disposed(LaunchPipe pipe)
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await pipe.DeliverAsync(cancelled.Token); } catch (ObjectDisposedException) { return true; } catch (OperationCanceledException) { return false; }
        throw new Exception("Unexpected pipe delivery without peer");
    }
    private static async Task Main()
    {
        try { await Run(); }
        catch (Exception error) { ReportFailure(phase, error); Environment.ExitCode = 1; }
    }
    private static async Task Run()
    {
        using var package = new SyntheticPackage(); phase = "SETUP_LISTENER"; await using var api = await LoopbackApi.Start(); phase = "UNKNOWN_PHASE";
        string executable = Path.Combine(package.Package.PackageRoot, "Blacklist_DX11_game.exe");
        var configuration = new HookConfigService(_ => package.Package);
        Task<PreparedGameConfiguration> Prepare() => configuration.PrepareConfigurationAsync(executable, Username, Password, Account, api.Address, "127.0.0.1");
        await Case("actual final login replaces initial response observation without changing caller config fields", async () => {
            var initial = await new AuthService(() => package.Package).LoginAsync(api.Address, Username, Password, "127.0.0.1");
            Check(initial.Status == LoginStatus.Success && initial.AccountId == Account && api.Logins == 1);
            api.ReturnedUsername = "final-returned-user";
            await using var prepared = await Prepare();
            Check(api.Logins == 2 && prepared.FinalLogin.AccountId == Account && prepared.FinalLogin.ReturnedUsername == "final-returned-user");
            Check(ReferenceEquals(prepared.FinalLogin.Package, package.Package));
            Check(api.LastLoginRequest!.PackageIdentity.ManifestSha256 == package.Package.ManifestSha256);
            string frame = Encoding.UTF8.GetString(RawFrame(prepared.Pipe));
            Check(frame.Contains("Username = \"requested-user\"") && !frame.Contains("final-returned-user") && frame.Contains("Password = \"synthetic-password\""));
            string representation = prepared + " " + prepared.FinalLogin + " " + prepared.FinalLogin.Transport;
            Check(!representation.Contains(Account) && !representation.Contains(Username) && !representation.Contains(Password) && !representation.Contains("synthetic-account-token"));
        });
        await Case("transport observation matches endpoints of actual final HTTP2 login socket", async () => {
            await using var prepared = await Prepare(); var observed = prepared.FinalLogin.Transport;
            Check(observed != null && observed.ConnectionObservationId != Guid.Empty);
            Check(observed!.LocalAddress == api.LastLoginPeerAddress && observed.LocalPort == api.LastLoginPeerPort);
            Check(observed.RemoteAddress == api.LastLoginLocalAddress && observed.RemotePort == api.LastLoginLocalPort);
            Check(observed.ObservedUtc <= DateTimeOffset.UtcNow && observed.LocalPort != 0);
            // The connection has already been disposed; these facts deliberately
            // remain historical and expose no live/ready capability property.
            await prepared.DisposeAsync(); Check(observed.LocalPort == api.LastLoginPeerPort);
        });
        await Case("legacy preparation wrapper returns byte-identical frame with independent ownership", async () => {
            await using var prepared = await Prepare(); byte[] expected = RawFrame(prepared.Pipe);
            await using var legacy = await configuration.PrepareAsync(executable, Username, Password, Account, api.Address, "127.0.0.1");
            Check(RawFrame(legacy).SequenceEqual(expected)); Check(!await Disposed(legacy));
        });
        await Case("final response account mismatch still rejects before preparing a pipe", async () => {
            api.ReturnedAccount = "different-account";
            try { await using var invalid = await Prepare(); throw new Exception("Mismatched final account admitted"); }
            catch (IOException) { }
            finally { api.ReturnedAccount = Account; }
        });
        await Case("empty actual returned username remains visible without introducing a LAN acceptance change", async () => {
            api.ReturnedUsername = "";
            try { await using var prepared = await Prepare(); Check(prepared.FinalLogin.ReturnedUsername == ""); Check(Encoding.UTF8.GetString(RawFrame(prepared.Pipe)).Contains("Username = \"requested-user\"")); }
            finally { api.ReturnedUsername = "final-returned-user"; }
        });
        var request = new GameLaunchRequest(package.Package.PackageRoot, "Blacklist_DX11_game.exe", "Official", package.Package.PackageRoot,
            Username, Password, Account, api.Address, "127.0.0.1", 1.2) { ObserverRequested = true };
        await Case("beforeStart failure disposes only its prepared pipe without starting", async () => {
            var operations = new Operations(Prepare); var workflow = new GameLaunchPreparation(operations);
            try { using var unused = await workflow.StartAsync(request, () => throw new IOException("Synthetic beforeStart failure")); throw new Exception("Failure ignored"); }
            catch (IOException) { }
            Check(operations.StartCalls == 0 && await Disposed(operations.LastPipe!));
            await using var independent = await Prepare(); Check(!await Disposed(independent.Pipe));
        });
        await Case("start rejection disposes pending ownership and preserves exact start arguments", async () => {
            var operations = new Operations(Prepare) { RejectStart = true }; var workflow = new GameLaunchPreparation(operations);
            try { using var unused = await workflow.StartAsync(request, () => Task.CompletedTask); throw new Exception("Failure ignored"); }
            catch (IOException) { }
            Check(operations.StartCalls == 1 && ReferenceEquals(operations.LastRequest, request) && operations.LastPlugin == "synthetic-plugin");
            Check(await Disposed(operations.LastPipe!));
        });
        await Case("successful preparation transfers its actual pipe once and metadata stays with the same attempt", async () => {
            var operations = new Operations(Prepare); var stages = new List<LaunchPreparationStage>();
            using var processHandle = await new GameLaunchPreparation(operations).StartAsync(request, () => Task.CompletedTask, stages.Add);
            Check(stages.SequenceEqual(new[] { LaunchPreparationStage.Deploying, LaunchPreparationStage.PreparingConfiguration, LaunchPreparationStage.Starting }));
            Check(ReferenceEquals(operations.LastRequest, request) && ReferenceEquals(operations.StartObservation, operations.LastPrepared!.FinalLogin));
            await operations.LastPrepared!.DisposeAsync(); Check(!await Disposed(operations.LastPipe!));
            try { _ = operations.LastPrepared.TransferOwnership(); throw new Exception("Ownership transferred twice"); } catch (ObjectDisposedException) { }
            await operations.LastPipe!.DisposeAsync(); Check(await Disposed(operations.LastPipe));
        });
        await Case("dispose and failed repeated disposal clear only the current prepared frame", async () => {
            var first = await Prepare(); await using var second = await Prepare(); var firstPipe = first.Pipe; var secondPipe = second.Pipe;
            await first.DisposeAsync(); await first.DisposeAsync(); Check(await Disposed(firstPipe) && !await Disposed(secondPipe));
            Check(RawFrame(firstPipe).All(value => value == 0));
        });
        phase = "CLEANUP"; Console.WriteLine($"RESULT {passed} final-login preparation checks passed; owned loopback HTTP2 only; no game, Public runtime or live-route admission.");
    }
    private sealed class Operations(Func<Task<PreparedGameConfiguration>> prepare) : ILaunchPreparationOperations
    {
        internal PreparedGameConfiguration? LastPrepared;
        internal LaunchPipe? LastPipe;
        internal GameLaunchRequest? LastRequest;
        internal FinalLoginObservation? StartObservation;
        internal string? LastPlugin;
        internal int StartCalls;
        internal bool RejectStart;
        public string? ValidateMode(GameLaunchRequest request) => "synthetic-plugin";
        public void Deploy(GameLaunchRequest request) { /* No executable deployment in these ownership tests. */ }
        public async Task<PreparedGameConfiguration> PrepareAsync(GameLaunchRequest request)
        { LastPrepared = await prepare(); LastPipe = LastPrepared.Pipe; return LastPrepared; }
        public Task<Process> StartAsync(GameLaunchRequest request, PreparedGameConfiguration configuration, string? pluginDirectory)
        {
            StartCalls++; LastRequest = request; LastPlugin = pluginDirectory; StartObservation = configuration.FinalLogin;
            if (RejectStart) throw new IOException("Synthetic rejection before launch adoption");
            // A harmless handle represents successful adoption at the process-start
            // boundary; real pipe ownership/lifetime remains under test.
            return Task.FromResult(Process.GetCurrentProcess());
        }
    }
}
