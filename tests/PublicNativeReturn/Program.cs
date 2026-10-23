using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using SplinterCellCNLauncher.Services;

internal static partial class Program
{
    private const string Instance = "0123456789abcdef0123456789abcdef";
    private const string User = "synthetic-client";
    private static int passed, skipped;
    private static SyntheticPackage package = null!;
    private static PublicDeploymentProfile profile = null!;
    private static string token = "";
    private static void Check(bool result) { if (!result) throw new Exception("Assertion failed"); }
    private static async Task Case(string name, Func<Task> action) { await action(); passed++; Console.WriteLine("PASS " + name); }
    private static async Task Reject(Func<Task> action)
    {
        try { await action(); } catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException or JsonException or OperationCanceledException) { return; }
        throw new Exception("Invalid input admitted");
    }
    private static JsonDocument Frame(string kind, ulong generation, string? bearer = null) => JsonDocument.Parse(JsonSerializer.Serialize(
        bearer == null ? (object)new { schema = 1, kind, generation } : new { schema = 1, kind, generation, account_id = "synthetic-account", username = User, token = bearer }));
    private static void Apply(PublicReturnState state, string kind, ulong generation, string? bearer = null, bool first = false)
    { using var frame = Frame(kind, generation, bearer); state.Apply(frame.RootElement, first); }
    private static PublicNativeGameSession Create(string instance, string account, string username, string bearer) => PublicNativeGameSession.FromOwnedNativeResponse(profile,
        CredentialEnvironmentContext.ForPublic(profile).CreateIdentity("10.66.0.1:50051", User), "10.66.0.1:50051", package.Package,
        new("synthetic-account", User), "10.66.0.10", "scbl-lan/2/game/mod", instance, false, account, username, bearer);
    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string MakeToken() => "v1." + Encode(JsonSerializer.SerializeToUtf8Bytes(new {
        release = $"1|{package.Package.ReleaseId}|{package.Package.CompatId}|{package.Package.ManifestSha256}", user_id = 1000,
        username = User, source_ip = "10.66.0.10", issued_at = 1, expires_at = 2, auth_version = 1,
        game_run = new { product = "scbl-lan/2/game/mod", instance = Instance } })) + "." + Encode(new byte[32]);
    private static async Task Main(string[] args)
    {
        if (args is ["--config-child", var configPipe, var reply]) { await ConfigChild(configPipe, int.Parse(reply)); return; }
        if (args is ["--child", var pipe]) { await Child(pipe); return; }
        if (args is ["--idle"]) { Console.WriteLine("IDLE"); _ = Console.ReadLine(); return; }
        using var ownedPackage = new SyntheticPackage(); package = ownedPackage;
        profile = new("synthetic-return", "https://control.example.invalid", "10.66.0.0/24", "10.66.0.1", PublicTransport.Fast,
            "quic://relay.example.invalid:11010", new("synthetic", Enum.GetValues<PublicRuntimeRole>().Select(role => new PublicRuntimePin(role, 1, new string('a', 64)))));
        token = MakeToken();
        if (args is ["--native-interop", var nativeHelper])
        {
            try { await NativeInterop(nativeHelper); }
            catch (Exception) { Console.WriteLine("NATIVE_MANAGED_INTEROP_FAILED"); Environment.ExitCode = 1; }
            return;
        }
        await Case("bounded framing round trip preserves document after buffer clearing", async () => {
            using var memory = new MemoryStream(); await PublicReturnProtocol.WriteAsync(memory, new { schema = 1, kind = "invalidate", generation = 1 }, default);
            memory.Position = 0; using var doc = await PublicReturnProtocol.ReadAsync(memory, default); Check(doc.RootElement.GetProperty("kind").GetString() == "invalidate");
        });
        await Case("zero oversized truncated and malformed UTF8 frames fail closed", async () => {
            foreach (uint length in new uint[] { 0, 8193, uint.MaxValue }) {
                byte[] bytes = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, length);
                await Reject(async () => { using var doc = await PublicReturnProtocol.ReadAsync(new MemoryStream(bytes), default); });
            }
            foreach (byte[] bytes in new[] { new byte[] { 4,0,0,0, (byte)'{' }, new byte[] { 1,0,0,0, 0xff } })
                await Reject(async () => { using var doc = await PublicReturnProtocol.ReadAsync(new MemoryStream(bytes), default); });
        });
        await Case("wire shape rejects duplicate unknown null and overflowing generation", async () => {
            foreach (string json in new[] { "{\"schema\":1,\"kind\":\"invalidate\",\"generation\":1,\"generation\":2}",
                "{\"schema\":1,\"kind\":\"invalidate\",\"generation\":1,\"token\":\"unexpected\"}",
                "{\"schema\":1,\"kind\":\"invalidate\",\"generation\":18446744073709551616}",
                "{\"schema\":1,\"kind\":\"authenticated\",\"generation\":1,\"token\":null}" }) {
                using var state = new PublicReturnState(Create); state.Hello(Instance); using var doc = JsonDocument.Parse(json);
                await Reject(() => { state.Apply(doc.RootElement, true); return Task.CompletedTask; }); Check(state.Current == null);
            }
        });
        await Case("no state admitted before hello and run nonce remains pinned", async () => {
            using var state = new PublicReturnState(Create);
            await Reject(() => { Apply(state, "authenticated", 1, token); return Task.CompletedTask; });
            state.Hello(Instance); state.Hello(Instance);
            await Reject(() => { state.Hello(new string('a', 32)); return Task.CompletedTask; });
        });
        await Case("new auth generation cancels old context and stale success cannot replace it", async () => {
            using var state = new PublicReturnState(Create); state.Hello(Instance); Apply(state, "authenticated", 2, token);
            var old = state.Current!; Apply(state, "invalidate", 3); Check(old.Lifetime.IsCancellationRequested && state.Current == null);
            await Reject(() => { Apply(state, "authenticated", 2, token); return Task.CompletedTask; }); Check(state.Current == null);
            Apply(state, "authenticated", 4, token); Check(state.Current != null);
        });
        await Case("failed newer auth context burns old generation across reconnect", async () => {
            using var state = new PublicReturnState(Create); state.Hello(Instance); Apply(state, "authenticated", 1, token);
            var old = state.Current!;
            using var invalid = JsonDocument.Parse(JsonSerializer.Serialize(new { schema = 1, kind = "authenticated", generation = 2,
                account_id = "other-account", username = User, token }));
            await Reject(() => { state.Apply(invalid.RootElement, false); return Task.CompletedTask; });
            Check(old.Lifetime.IsCancellationRequested && state.Current == null); state.Disconnected();
            await Reject(() => { Apply(state, "authenticated", 1, token, first: true); return Task.CompletedTask; });
        });
        await Case("invalidation cannot become success at equal generation", async () => {
            using var state = new PublicReturnState(Create); state.Hello(Instance); Apply(state, "invalidate", 1);
            await Reject(() => { Apply(state, "authenticated", 1, token); return Task.CompletedTask; }); Check(state.Current == null);
        });
        await Case("EOF cancels context before identical reconnect replay creates a fresh lifetime", () => {
            using var state = new PublicReturnState(Create); state.Hello(Instance); Apply(state, "authenticated", 1, token);
            var old = state.Current!; state.Disconnected(); Check(old.Lifetime.IsCancellationRequested && state.Current == null);
            state.Hello(Instance); Apply(state, "authenticated", 1, token, first: true); Check(state.Current != null && !ReferenceEquals(old, state.Current)); return Task.CompletedTask;
        });
        await Case("different token at same generation is rejected even after EOF", async () => {
            using var state = new PublicReturnState(Create); state.Hello(Instance); Apply(state, "authenticated", 1, token); state.Disconnected();
            await Reject(() => { Apply(state, "authenticated", 1, token[..^1] + "E"); return Task.CompletedTask; }); Check(state.Current == null);
        });
        await Case("heartbeats require a prior current generation and cannot restore EOF state", async () => {
            using var state = new PublicReturnState(Create); state.Hello(Instance);
            await Reject(() => { Apply(state, "heartbeat", 1, first: true); return Task.CompletedTask; });
            Apply(state, "authenticated", 2, token); Apply(state, "heartbeat", 2);
            await Reject(() => { Apply(state, "heartbeat", 3); return Task.CompletedTask; });
            state.Disconnected(); await Reject(() => { Apply(state, "heartbeat", 2, first: true); return Task.CompletedTask; }); Check(state.Current == null);
        });
        await Case("disposal prevents late publication and cancels existing context", async () => {
            using var state = new PublicReturnState(Create); state.Hello(Instance); Apply(state, "authenticated", 1, token);
            var old = state.Current!; state.Dispose(); Check(old.Lifetime.IsCancellationRequested);
            await Reject(() => { Apply(state, "authenticated", 2, token); return Task.CompletedTask; });
        });
        await Case("owned response factory marks numeric claim provenance and rejects account substitution", async () => {
            using var context = Create(Instance, "synthetic-account", User, token); Check(!context.HasIndependentNumericUserId);
            using var strict = new PublicNativeGameSession(profile, CredentialEnvironmentContext.ForPublic(profile).CreateIdentity("10.66.0.1:50051", User),
                "10.66.0.1:50051", package.Package, new(1000, User, "10.66.0.10", "scbl-lan/2/game/mod", Instance, false), token);
            Check(strict.HasIndependentNumericUserId);
            foreach (var pair in new[] { ("different-account", User), ("synthetic-account", "different-user") })
                await Reject(() => { using var invalid = Create(Instance, pair.Item1, pair.Item2, token); return Task.CompletedTask; });
            foreach (string bad in new[] { "", new string('a', 65), "control\n", new string('é', 33), "\uD800" })
                await Reject(() => { _ = new PublicNativeAccountAnchor(bad, User); return Task.CompletedTask; });
        });
        await Case("actual return-state invalidation and EOF cancel pending real TLS session queries", async () => {
            using var certificates = new SyntheticCertificates();
            foreach (bool eof in new[] { false, true })
            {
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await using var endpoint = new LoopbackTlsServer(certificates, async (tls, _, ct) => {
                    await release.Task.WaitAsync(ct); await LoopbackTlsServer.Send(tls, ct, "{\"schema\":1,\"sessions\":[]}");
                });
                var local = new PublicDeploymentProfile("synthetic-return-tls", endpoint.Origin, "10.66.0.0/24", "10.66.0.1", PublicTransport.Fast,
                    "quic://relay.example.invalid:11010", new("synthetic", Enum.GetValues<PublicRuntimeRole>().Select(role => new PublicRuntimePin(role, 1, new string('a', 64)))));
                using var state = new PublicReturnState((instance, account, username, bearer) => PublicNativeGameSession.FromOwnedNativeResponse(local,
                    CredentialEnvironmentContext.ForPublic(local).CreateIdentity("10.66.0.1:50051", User), "10.66.0.1:50051", package.Package,
                    new("synthetic-account", User), "10.66.0.10", "scbl-lan/2/game/mod", instance, false, account, username, bearer));
                state.Hello(Instance); Apply(state, "authenticated", 1, token);
                using var client = new PublicSessionControlClient(local, certificates.Handler(), TimeSpan.FromSeconds(8));
                var pending = client.QueryAsync(state.Current!); await endpoint.RequestArrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (eof) state.Disconnected(); else Apply(state, "invalidate", 2);
                release.TrySetResult();
                try { await pending; throw new Exception("Cancelled return state published a late TLS snapshot"); }
                catch (OperationCanceledException) { }
            }
        });
        await Case("missing native handoff never falls back to a network password or token", async () => {
            await using var bridge = new PublicNativeReturn(profile, CredentialEnvironmentContext.ForPublic(profile).CreateIdentity("10.66.0.1:50051", User),
                "10.66.0.1:50051", package.Package, new("synthetic-account", User), "10.66.0.10", "scbl-lan/2/game/mod", false);
            try { await bridge.QueryCurrentAsync(); } catch (PublicControlException error) { Check(error.Message == "PUBLIC_NATIVE_GAME_SESSION_MISSING"); return; }
            throw new Exception("Missing handoff queried");
        });
        if (OperatingSystem.IsWindows()) await WindowsCases();
        else { skipped = 11; Console.WriteLine("SKIP 11 Windows-only actual named-pipe and owned-child process cases"); }
        Console.WriteLine($"RESULT {passed} passed, {skipped} skipped");
    }
    private static async Task Eventually(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(25, deadline.Token);
    }
    private static Process StartChild(params string[] arguments)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        start.Environment.Remove(PublicReturnProtocol.EnvironmentKey);
        return Process.Start(start) ?? throw new Exception("Synthetic child failed");
    }
    private static async Task Line(Process child, string expected)
    { Check(await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) == expected); }
    private static async Task End(Process child)
    {
        if (!child.HasExited)
        {
            try { await child.StandardInput.WriteLineAsync("exit"); } catch (IOException) { } catch (InvalidOperationException) { }
            try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (TimeoutException) { try { child.Kill(); } catch (InvalidOperationException) { } await child.WaitForExitAsync(); }
        }
    }
    private static async Task StartClient(Process child)
    { await child.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { token, instance = Instance })); await Line(child, "CONNECTED"); }
    private static async Task WindowsCases()
    {
        foreach (int reply in new[] { 1, 0, -1 }) await Case($"actual retained config pipe ACK={reply} grants only byte-one after validation", async () => {
            string root = Path.Combine(Path.GetTempPath(), "ff-config-child-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try
            {
                // This is a copy of our synthetic test apphost, never a game image.
                foreach (string file in Directory.EnumerateFiles(AppContext.BaseDirectory)) File.Copy(file, Path.Combine(root, Path.GetFileName(file)));
                string executable = Path.Combine(root, "Blacklist_DX11_game.exe");
                File.Copy(Path.Combine(AppContext.BaseDirectory, "PublicNativeReturn.exe"), executable);
                await using var config = new ScblLan.LaunchPipe("ConfigServer = \"127.0.0.1\"\n", executable);
                int validated = 0, acknowledged = 0;
                config.PeerValidated += _ => Interlocked.Increment(ref validated);
                config.ConfigurationAcknowledged += _ => Interlocked.Increment(ref acknowledged);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                Task delivery = config.DeliverAsync(deadline.Token);
                var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true };
                start.ArgumentList.Add("--config-child"); start.ArgumentList.Add(config.Name); start.ArgumentList.Add(reply.ToString());
                using var child = Process.Start(start)!;
                try
                {
                    await Line(child, "FRAME_RECEIVED"); Check(validated == 1 && acknowledged == 0 && !delivery.IsCompleted);
                    await child.StandardInput.WriteLineAsync("ack");
                    if (reply == 1) { await delivery; Check(acknowledged == 1); }
                    else { await Reject(() => delivery); Check(acknowledged == 0); }
                    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
                finally { await End(child); }
            }
            finally { Directory.Delete(root, recursive: true); }
        });
        await Case("wrong launcher catalog cannot create a launch descriptor", async () => {
            await using var bridge = new PublicNativeReturn(profile, CredentialEnvironmentContext.ForPublic(profile).CreateIdentity("10.66.0.1:50051", User),
                "10.66.0.1:50051", package.Package, new("synthetic-account", User), "10.66.0.10", "scbl-lan/2/game/mod", false);
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            start.Environment[PublicReturnProtocol.EnvironmentKey] = "ambient-untrusted";
            await Reject(() => { bridge.Prepare(start, Guid.NewGuid()); return Task.CompletedTask; });
            Check(!start.Environment.ContainsKey(PublicReturnProtocol.EnvironmentKey));
        });
        await Case("actual owned process handle pins creation time and rejects image substitution", async () => {
            using var child = StartChild("--idle"); try { await Line(child, "IDLE"); using var owned = PublicReturnProcess.Capture(child, Environment.ProcessPath!);
                Check(owned.Matches((uint)child.Id, owned.CreationFileTime)); Check(!owned.Matches((uint)child.Id, owned.CreationFileTime + 1));
                await Reject(() => { using var wrong = PublicReturnProcess.Capture(child, Path.Combine(Path.GetTempPath(), "wrong.exe")); return Task.CompletedTask; });
            } finally { await End(child); }
        });
        await Case("actual pipe publishes only after exact created process ACK and EOF cancels", async () => {
            await using var server = new PublicReturnPipe(Create); server.Start(Guid.NewGuid()); using var child = StartChild("--child", server.Name);
            try { server.CaptureCreated(child, Environment.ProcessPath!); await StartClient(child); Check(server.Current == null);
                await Task.Delay(100); Check(server.Current == null); server.ConfigurationAcknowledged(child); await Line(child, "AUTH_SENT");
                await Eventually(() => server.Current != null); var old = server.Current!;
                await child.StandardInput.WriteLineAsync("disconnect"); await Line(child, "DISCONNECTED");
                await Eventually(() => old.Lifetime.IsCancellationRequested && server.Current == null);
                await child.StandardInput.WriteLineAsync("reconnect"); await Line(child, "CONNECTED"); await Line(child, "AUTH_SENT");
                await Eventually(() => server.Current != null); Check(!ReferenceEquals(old, server.Current));
            } finally { await End(child); }
        });
        await Case("forged responding child PID and its ACK cannot obtain a challenge", async () => {
            await using var server = new PublicReturnPipe(Create); server.Start(Guid.NewGuid()); using var owner = StartChild("--idle"); using var impostor = StartChild("--child", server.Name);
            try { await Line(owner, "IDLE"); server.CaptureCreated(owner, Environment.ProcessPath!); await StartClient(impostor); server.ConfigurationAcknowledged(impostor);
                await Task.Delay(100); Check(server.Current == null); server.ConfigurationAcknowledged(owner); await Line(impostor, "REJECTED"); Check(server.Current == null);
            } finally { await End(impostor); await End(owner); }
        });
        await Case("ACK racing Process.Start completion still requires captured exact owner", async () => {
            await using var server = new PublicReturnPipe(Create); server.Start(Guid.NewGuid()); using var child = StartChild("--child", server.Name);
            try { await StartClient(child); server.ConfigurationAcknowledged(child); Check(server.Current == null); server.CaptureCreated(child, Environment.ProcessPath!);
                await Line(child, "AUTH_SENT"); await Eventually(() => server.Current != null);
            } finally { await End(child); }
        });
        await Case("unacknowledged pipe occupant is disconnected within startup deadline", async () => {
            await using var server = new PublicReturnPipe(Create); server.Start(Guid.NewGuid()); using var child = StartChild("--idle");
            try
            {
                await Line(child, "IDLE"); server.CaptureCreated(child, Environment.ProcessPath!);
                using var candidate = new NamedPipeClientStream(".", server.Name, PipeDirection.InOut, PipeOptions.Asynchronous);
                await candidate.ConnectAsync(10000); using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(13));
                byte[] one = new byte[1]; Check(await candidate.ReadAsync(one, limit.Token) == 0 && server.Current == null);
            }
            finally { await End(child); }
        });
        await Case("silent authenticated peer expires without heartbeats while process stays alive", async () => {
            await using var server = new PublicReturnPipe(Create); server.Start(Guid.NewGuid()); using var child = StartChild("--child", server.Name);
            try
            {
                server.CaptureCreated(child, Environment.ProcessPath!); server.ConfigurationAcknowledged(child); await StartClient(child); await Line(child, "AUTH_SENT");
                await Eventually(() => server.Current != null); var old = server.Current!;
                await Eventually(() => old.Lifetime.IsCancellationRequested && server.Current == null); Check(!child.HasExited);
            }
            finally { await End(child); }
        });
        await Case("owned child exit and server disposal cancel retained native session", async () => {
            await using var server = new PublicReturnPipe(Create); server.Start(Guid.NewGuid()); using var child = StartChild("--child", server.Name);
            try { server.CaptureCreated(child, Environment.ProcessPath!); server.ConfigurationAcknowledged(child); await StartClient(child); await Line(child, "AUTH_SENT");
                await Eventually(() => server.Current != null); var old = server.Current!; await End(child);
                await Eventually(() => old.Lifetime.IsCancellationRequested && server.Current == null); await server.DisposeAsync();
            } finally { await End(child); }
        });
    }
    private static async Task ConfigChild(string name, int reply)
    {
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(10000); byte[] prefix = new byte[4]; await pipe.ReadExactlyAsync(prefix);
        int count = BinaryPrimitives.ReadInt32LittleEndian(prefix); Check(count is > 0 and < 65536);
        byte[] body = new byte[count]; await pipe.ReadExactlyAsync(body);
        Check(Encoding.UTF8.GetString(body) == "ConfigServer = \"127.0.0.1\"\n"); Console.WriteLine("FRAME_RECEIVED");
        if (await Console.In.ReadLineAsync() == "ack" && reply >= 0) { await pipe.WriteAsync(new byte[] { (byte)reply }); await pipe.FlushAsync(); }
    }
    private static async Task Child(string name)
    {
        using var input = JsonDocument.Parse((await Console.In.ReadLineAsync())!); string bearer = input.RootElement.GetProperty("token").GetString()!;
        string instance = input.RootElement.GetProperty("instance").GetString()!;
        NamedPipeClientStream? pipe = null;
        try
        {
            while (true)
            {
                if (pipe == null)
                {
                    pipe = new(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
                    await pipe.ConnectAsync(10000); Console.WriteLine("CONNECTED");
                    try
                    {
                        using var challenge = await PublicReturnProtocol.ReadAsync(pipe, default); var frame = challenge.RootElement;
                        await PublicReturnProtocol.WriteAsync(pipe, new { schema = 1, kind = "hello", launch_id = frame.GetProperty("launch_id").GetString(), challenge = frame.GetProperty("challenge").GetString(), game_instance = instance }, default);
                        await PublicReturnProtocol.WriteAsync(pipe, new { schema = 1, kind = "authenticated", generation = 1, account_id = "synthetic-account", username = User, token = bearer }, default);
                        Console.WriteLine("AUTH_SENT");
                    }
                    catch (IOException) { Console.WriteLine("REJECTED"); return; }
                }
                string? command = await Console.In.ReadLineAsync();
                if (command is null or "exit") return;
                if (command == "disconnect") { pipe.Dispose(); pipe = null; Console.WriteLine("DISCONNECTED"); if (await Console.In.ReadLineAsync() != "reconnect") return; }
            }
        }
        finally { pipe?.Dispose(); }
    }
}
