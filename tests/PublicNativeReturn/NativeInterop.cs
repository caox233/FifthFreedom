using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ScblLan;
using SplinterCellCNLauncher.Services;

internal static partial class Program
{
    // Explicit post-native-build probe. This mode must fail, not skip, when its
    // required Windows platform or exact same-run Rust test executable is absent.
    private static async Task NativeInterop(string nativeHelper)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("NATIVE_INTEROP_WINDOWS_REQUIRED");
        nativeHelper = Path.GetFullPath(nativeHelper);
        if (!File.Exists(nativeHelper) || !Regex.IsMatch(Path.GetFileName(nativeHelper), @"^hooks-[0-9a-f]+\.exe$", RegexOptions.CultureInvariant)
            || (File.GetAttributes(nativeHelper) & FileAttributes.ReparsePoint) != 0) throw new IOException("NATIVE_INTEROP_HELPER_INVALID");
        await Case("actual Rust native publisher and managed owner exchange ACK auth invalidation and EOF", () => RunNativePeer(nativeHelper, "valid"));
        await Case("actual native helper rejects incorrect pinned launcher creation time", () => RunNativePeer(nativeHelper, "wrong-creation"));
        await Case("actual native helper rejects incorrect independently pinned fixture image bytes", () => RunNativePeer(nativeHelper, "wrong-image"));
        Console.WriteLine("RESULT 3 native-managed IPC interoperability checks passed; synthetic owned children only; package/DLL admission and game not exercised.");
    }
    private static async Task RunNativePeer(string nativeHelper, string scenario)
    {
        string directory = Path.Combine(Path.GetTempPath(), "ff-native-managed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Process? child = null;
        Task? stderr = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        try
        {
            using var certificates = new SyntheticCertificates();
            var releaseResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var endpoint = new LoopbackTlsServer(certificates, async (tls, _, ct) => {
                await releaseResponse.Task.WaitAsync(ct); await LoopbackTlsServer.Send(tls, ct, "{\"schema\":1,\"sessions\":[]}");
            });
            var local = new PublicDeploymentProfile("synthetic-native-return", endpoint.Origin, "10.66.0.0/24", "10.66.0.1", PublicTransport.Fast,
                "quic://relay.example.invalid:11010", new("synthetic", Enum.GetValues<PublicRuntimeRole>().Select(role => new PublicRuntimePin(role, 1, new string('a', 64)))));
            string executable = Path.Combine(directory, "Blacklist_DX11_game.exe");
            File.Copy(nativeHelper, executable, overwrite: false);
            // This is a copied Rust test executable with an explicit exact test
            // selection. No game image, hooks injection or driver is launched.
            using var fixture = typeof(Program).Assembly.GetManifestResourceStream("FifthFreedom.NativeInterop.toml")
                ?? throw new IOException("NATIVE_INTEROP_CONFIG_MISSING");
            using var fixtureReader = new StreamReader(fixture, new UTF8Encoding(false, true));
            string config = await fixtureReader.ReadToEndAsync(deadline.Token);
            await using var configuration = new LaunchPipe(config, executable);
            await using var server = new PublicReturnPipe((instance, account, username, bearer) => PublicNativeGameSession.FromOwnedNativeResponse(local,
                CredentialEnvironmentContext.ForPublic(local).CreateIdentity("10.66.0.1:50051", User), "10.66.0.1:50051", package.Package,
                new("synthetic-account", User), "10.66.0.10", "scbl-lan/2/game/mod", instance, false, account, username, bearer));
            Guid launch = Guid.NewGuid(); server.Start(launch);
            configuration.ConfigurationAcknowledged += server.ConfigurationAcknowledged;
            int acknowledgements = 0;
            configuration.ConfigurationAcknowledged += _ => Interlocked.Increment(ref acknowledgements);
            File.WriteAllText(Path.Combine(directory, "scbl-lan.toml"), $"LaunchPipe = \"{configuration.Name}\"\n", new UTF8Encoding(false));
            using var self = Process.GetCurrentProcess();
            using var launcher = PublicReturnProcess.Capture(self, Environment.ProcessPath!);
            string imageHash;
            using (var image = File.OpenRead(launcher.ImagePath)) imageHash = Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant();
            if (scenario == "wrong-image") imageHash = new string('a', 64);
            var start = new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = false,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("--exact"); start.ArgumentList.Add("api::public_return::tests::owned_pipe_server"); start.ArgumentList.Add("--nocapture");
            start.Environment.Remove("SCBL_HOOKS_CONFIG");
            start.Environment.Remove("FF_SYNTHETIC_RETURN_PIPE_TEST");
            start.Environment["RUST_BACKTRACE"] = "0";
            start.Environment[PublicReturnProtocol.EnvironmentKey] = JsonSerializer.Serialize(new {
                schema = 1, pipe = server.Name, launch_id = launch.ToString("N"), launcher_pid = launcher.Pid,
                launcher_creation_filetime = launcher.CreationFileTime + (scenario == "wrong-creation" ? 1UL : 0),
                launcher_path = launcher.ImagePath, launcher_sha256 = imageHash });
            // Independent test fixture anchor only. Production prepare's actual
            // four-role package and loaded-DLL admission are deliberately bypassed
            // by the Rust test entry; this probe never calls that bypass production.
            start.Environment["FF_SYNTHETIC_RETURN_INTEROP_V1"] = JsonSerializer.Serialize(new {
                launcher_path = launcher.ImagePath, launcher_sha256 = imageHash,
                release_id = package.Package.ReleaseId, compat_id = package.Package.CompatId, manifest_sha256 = package.Package.ManifestSha256 });
            Task delivery = configuration.DeliverAsync(deadline.Token);
            child = Process.Start(start) ?? throw new IOException("NATIVE_INTEROP_CHILD_FAILED");
            server.CaptureCreated(child, executable);
            stderr = DrainBounded(child.StandardError, deadline.Token);
            await delivery.WaitAsync(deadline.Token);
            Check(acknowledgements == 1);
            if (scenario != "valid")
            {
                await NativeMarker(child, "NATIVE_INTEROP_REJECTED", deadline.Token);
                await child.WaitForExitAsync(deadline.Token); await stderr;
                Check(child.ExitCode != 0 && server.Current == null);
                return;
            }
            await NativeMarker(child, "READY", deadline.Token); Check(server.Current == null);
            await child.StandardInput.WriteLineAsync("AUTH"); await NativeMarker(child, "AUTHENTICATED", deadline.Token);
            await Eventually(() => server.Current != null); var first = server.Current!;
            Check(first.Expected.Username == User && !first.HasIndependentNumericUserId && PublicReturnProtocol.Hex(first.Expected.Instance, 32));
            using var client = new PublicSessionControlClient(local, certificates.Handler(), TimeSpan.FromSeconds(8));
            var pending = client.QueryAsync(first);
            await endpoint.RequestArrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await child.StandardInput.WriteLineAsync("INVALIDATE"); await NativeMarker(child, "INVALIDATED", deadline.Token);
            await Eventually(() => first.Lifetime.IsCancellationRequested && server.Current == null);
            releaseResponse.TrySetResult();
            try { await pending; throw new Exception("Native invalidation admitted a late TLS result"); }
            catch (OperationCanceledException) { }
            await child.StandardInput.WriteLineAsync("AUTH"); await NativeMarker(child, "AUTHENTICATED", deadline.Token);
            await Eventually(() => server.Current != null); var second = server.Current!;
            Check(!ReferenceEquals(first, second) && first.Expected.Instance == second.Expected.Instance);
            await child.StandardInput.WriteLineAsync("EXIT"); await NativeMarker(child, "STOPPED", deadline.Token);
            await child.WaitForExitAsync(deadline.Token); await stderr;
            await Eventually(() => second.Lifetime.IsCancellationRequested && server.Current == null);
            Check(child.ExitCode == 0);
        }
        finally
        {
            // Never enumerate/kill by name: only this exact synthetic child is
            // terminated if its own graceful shutdown exceeds the bounded wait.
            try
            {
                if (child != null)
                {
                    try
                    {
                        if (!child.HasExited)
                        {
                            try { await child.StandardInput.WriteLineAsync("EXIT"); child.StandardInput.Close(); } catch (IOException) { }
                            try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8)); }
                            catch (TimeoutException) { try { child.Kill(); } catch (InvalidOperationException) { } await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
                        }
                    }
                    finally { child.Dispose(); }
                }
            }
            finally
            {
                deadline.Cancel();
                try { if (stderr != null) await stderr.WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (OperationCanceledException) { }
                finally { Directory.Delete(directory, recursive: true); }
            }
        }
    }
    private static async Task NativeMarker(Process child, string expected, CancellationToken token)
    {
        // No unbounded ReadLine allocation and no raw native output forwarding.
        char[] one = new char[1]; var line = new StringBuilder(); int total = 0;
        while (total++ < 16384)
        {
            int count = await child.StandardOutput.ReadAsync(one.AsMemory(), token);
            if (count == 0) throw new IOException("NATIVE_INTEROP_MARKER_MISSING");
            char value = one[0];
            if (value == '\n')
            {
                string text = line.ToString().TrimEnd('\r'); line.Clear();
                if (text == expected) return;
                if (text == "NATIVE_INTEROP_REJECTED") throw new IOException("NATIVE_INTEROP_REJECTED");
            }
            else { if (line.Length >= 512) throw new IOException("NATIVE_INTEROP_OUTPUT_BOUND"); line.Append(value); }
        }
        throw new IOException("NATIVE_INTEROP_OUTPUT_BOUND");
    }
    private static async Task DrainBounded(StreamReader reader, CancellationToken token)
    {
        char[] buffer = new char[512]; int total = 0;
        while (true)
        {
            int count = await reader.ReadAsync(buffer.AsMemory(), token); if (count == 0) return;
            if ((total += count) > 16384) throw new IOException("NATIVE_INTEROP_OUTPUT_BOUND");
        }
    }
}
