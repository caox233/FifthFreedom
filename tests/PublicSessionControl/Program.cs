using System.Net.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SplinterCellCNLauncher.Services;

internal static class Program
{
    private static int passed;
    private static bool failureReported;
    private static void ReportFailure(string phase, Exception error)
    {
        if (failureReported) return;
        failureReported = true;
        // Fixed allowlisted tokens only. Never print message/inner/stack or any
        // request, token, endpoint, account or filesystem path into CI annotations.
        string type = error.GetType().Name;
        string[] allowed = ["AuthenticationException", "CryptographicException", "IOException", "EndOfStreamException",
            "SocketException", "HttpRequestException", "OperationCanceledException", "TaskCanceledException", "TimeoutException",
            "ArgumentException", "InvalidOperationException", "ObjectDisposedException", "PlatformNotSupportedException", "PublicControlException", "Exception"];
        if (!allowed.Contains(type, StringComparer.Ordinal)) type = "OtherException";
        Console.WriteLine($"CI_MANAGED_FAILURE suite=PublicSessionControl case={phase} exception={type}");
    }
    private const string User = "synthetic-client";
    private const string Nonce = "0123456789abcdef0123456789abcdef";
    private static PublicNativeRunExpectation Expect(bool observer = false, string source = "10.66.0.10", string instance = Nonce)
        => new(1000, User, source, "scbl-lan/2/game/mod", instance, observer);
    private static PublicDeploymentProfile Profile(string origin, string id = "synthetic-deployment")
    {
        var pins = new PublicDeploymentRuntimePins("synthetic-test-only", Enum.GetValues<PublicRuntimeRole>()
            .Select(role => new PublicRuntimePin(role, 1, new string('a', 64))));
        return new(id, origin, "10.66.0.0/24", "10.66.0.1", PublicTransport.Fast, "quic://relay.example.invalid:11010", pins);
    }
    private static string Release(ReleasePackage package) => $"1|{package.ReleaseId}|{package.CompatId}|{package.ManifestSha256}";
    private static Dictionary<string, object?> Claims(ReleasePackage package, bool observer = false)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var claims = new Dictionary<string, object?> { ["release"] = Release(package), ["user_id"] = 1000,
            ["username"] = User, ["source_ip"] = "10.66.0.10", ["issued_at"] = now, ["expires_at"] = now + 86400,
            ["auth_version"] = 1, ["game_run"] = new { product = "scbl-lan/2/game/mod", instance = Nonce } };
        if (observer) claims.Add("observer_admitted_run", Nonce);
        return claims;
    }
    private static string Base64(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string Token(Dictionary<string, object?> claims)
    {
        string content = "v1." + Base64(JsonSerializer.SerializeToUtf8Bytes(claims));
        // Test-only random issuer. The synthetic TLS response fixture does not
        // authenticate this signature; server admission has separate real tests.
        return content + "." + Base64(HMACSHA256.HashData(RandomNumberGenerator.GetBytes(32), Encoding.ASCII.GetBytes(content)));
    }
    private static PublicNativeGameSession Context(PublicDeploymentProfile profile, ReleasePackage package,
        PublicNativeRunExpectation? expected = null, string? token = null)
        => new(profile, CredentialEnvironmentContext.ForPublic(profile).CreateIdentity("10.66.0.1:50051", User),
            "10.66.0.1:50051", package, expected ?? Expect(), token ?? Token(Claims(package)));
    private static string Snapshot() => JsonSerializer.Serialize(new { schema = 1, sessions = new[] { new {
        session_id = 10, session_type = 1, host_user_id = 1000, host_username = User, gameplay_mode = 2,
        members = new[] { new { user_id = 1000, source_ip = "10.66.0.10" }, new { user_id = 1001, source_ip = "10.66.0.11" } }
    } } });
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (ArgumentException error)
        {
            Check(!error.Message.Contains(User) && !error.Message.Contains("10.66") && !error.Message.Contains("v1."), "Input leaked in rejection");
            return;
        }
        throw new Exception("Invalid context admitted");
    }
    private static async Task RejectQuery(Func<Task> query, string code)
    {
        try { await query(); }
        catch (PublicControlException error) { Check(error.Message == code && error.InnerException is null, "Unsanitized or incorrect transport error"); return; }
        throw new Exception("Invalid response admitted");
    }
    private static async Task Cancelled(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { return; }
        throw new Exception("Cancelled run published a snapshot");
    }
    private static async Task Case(string name, Func<Task> operation)
    {
        try { await operation(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception error) { ReportFailure((passed + 1).ToString("D2", System.Globalization.CultureInfo.InvariantCulture), error); throw; }
    }
    private static async Task<int> Main()
    {
        SyntheticPackage? package = null; SyntheticCertificates? certificates = null;
        string phase = "SETUP_PACKAGE"; int result = 0;
        try
        {
            package = new SyntheticPackage(); phase = "SETUP_CERTIFICATES";
            certificates = new SyntheticCertificates(); phase = "UNKNOWN_PHASE";
            await RunCases(package, certificates);
        }
        catch (Exception error) { ReportFailure(phase, error); result = 1; }
        finally
        {
            try { certificates?.Dispose(); } catch (Exception error) { ReportFailure("CLEANUP", error); result = 1; }
            try { package?.Dispose(); } catch (Exception error) { ReportFailure("CLEANUP", error); result = 1; }
        }
        return result;
    }
    private static async Task RunCases(SyntheticPackage package, SyntheticCertificates certificates)
    {
        await Case("verified package and typed deployment context reject account legacy and foreign credentials", () => {
            var profile = Profile("https://control.example.invalid");
            var identity = CredentialEnvironmentContext.ForPublic(profile).CreateIdentity("10.66.0.1:50051", User);
            var foreign = CredentialEnvironmentContext.ForPublic(Profile("https://control.example.invalid", "other")).CreateIdentity("10.66.0.1:50051", User);
            foreach (var wrong in new[] { foreign, CredentialEnvironmentContext.Lan.CreateIdentity("10.66.0.1:50051", User) })
                Reject(() => new PublicNativeGameSession(profile, wrong, "10.66.0.1:50051", package.Package, Expect(), Token(Claims(package.Package))));
            foreach (string token in new[] { "", "synthetic-account-token", "v1.payload.signature", "Bearer synthetic", new string('x', 4097) })
                Reject(() => Context(profile, package.Package, token: token));
            var account = Claims(package.Package); account.Remove("game_run");
            Reject(() => Context(profile, package.Package, token: Token(account)));
            Reject(() => new PublicNativeGameSession(profile, identity, "10.66.0.1:50051", null!, Expect(), Token(Claims(package.Package))));
            Reject(() => Context(profile, package.Package, expected: Expect(source: "10.67.0.10")));
            return Task.CompletedTask;
        });
        await Case("independent run user source package and observer expectations prevent token context substitution", () => {
            var profile = Profile("https://control.example.invalid");
            foreach (var update in new[] { ("user_id", (object?)1001), ("username", "other"), ("source_ip", "10.66.0.11"),
                ("release", "1|wrong|wrong|" + new string('a', 64)), ("game_run", new { product = "scbl-lan/1/official", instance = Nonce }),
                ("game_run", new { product = "scbl-lan/2/game/mod", instance = new string('a', 32) }), ("observer_admitted_run", new string('b', 32)) })
            {
                var claims = Claims(package.Package); claims[update.Item1] = update.Item2;
                Reject(() => Context(profile, package.Package, token: Token(claims)));
            }
            Reject(() => Context(profile, package.Package, expected: Expect(observer: true)));
            using var granted = Context(profile, package.Package, Expect(observer: true), Token(Claims(package.Package, observer: true)));
            string representation = granted + " " + Expect();
            Check(!representation.Contains(User) && !representation.Contains(Nonce) && !representation.Contains("10.66"), "Context representation leaked identity");
            return Task.CompletedTask;
        });
        await Case("duplicate claims and noncanonical token encodings cannot change token purpose", () => {
            var profile = Profile("https://control.example.invalid");
            string token = Token(Claims(package.Package));
            string[] parts = token.Split('.');
            Reject(() => Context(profile, package.Package, token: parts[0] + "." + parts[1] + "=." + parts[2]));
            Reject(() => Context(profile, package.Package, token: token + "="));
            string json = JsonSerializer.Serialize(Claims(package.Package));
            string duplicate = json[..^1] + ",\"user_id\":1000}";
            Reject(() => Context(profile, package.Package, token: "v1." + Base64(Encoding.UTF8.GetBytes(duplicate)) + "." + parts[2]));
            var claims = Claims(package.Package); claims["game_run"] = null;
            Reject(() => Context(profile, package.Package, token: Token(claims)));
            return Task.CompletedTask;
        });
        await Case("actual immutable native producer game and observer fixtures match the semantic token contract", () => {
            foreach (string name in new[] { "native_game_token_v1.json", "native_observer_token_v1.json" })
            {
                using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, name)));
                var value = fixture.RootElement; var claims = value.GetProperty("claims");
                var run = claims.GetProperty("game_run");
                bool observer = name.Contains("observer", StringComparison.Ordinal);
                // Expected fixture metadata is a separate native producer output;
                // the production API requires expectations from an owned handoff.
                var expected = new PublicNativeRunExpectation(claims.GetProperty("user_id").GetUInt32(), claims.GetProperty("username").GetString()!,
                    claims.GetProperty("source_ip").GetString()!, run.GetProperty("product").GetString()!, run.GetProperty("instance").GetString()!, observer);
                NativeGameTokenContract.Validate(value.GetProperty("token").GetString()!, expected, claims.GetProperty("release").GetString()!);
            }
            return Task.CompletedTask;
        });
        await Case("real TLS request matches exact native package protocol and exposes only caller bearer", async () => {
            await using var server = new LoopbackTlsServer(certificates, (tls, _, ct) => LoopbackTlsServer.Send(tls, ct, Snapshot()));
            var profile = Profile(server.Origin); string token = Token(Claims(package.Package));
            using var context = Context(profile, package.Package, token: token);
            using var client = new PublicSessionControlClient(profile, certificates.Handler(), TimeSpan.FromSeconds(8));
            System.Collections.ObjectModel.ReadOnlyCollection<PublicSessionSnapshot> snapshot;
            try { snapshot = await client.QueryAsync(context); }
            catch (Exception error)
            {
                await server.ObserveFailureAsync();
                string code = error is PublicControlException ? error.Message switch {
                    "PUBLIC_CONTROL_REQUEST_REJECTED" => "REQUEST_REJECTED",
                    "PUBLIC_CONTROL_RESPONSE_INVALID" => "RESPONSE_INVALID",
                    "PUBLIC_CONTROL_TRANSPORT_FAILED" => "TRANSPORT_FAILED",
                    "PUBLIC_CONTROL_TIMEOUT" => "TIMEOUT", _ => "OTHER"
                } : "OTHER";
                Console.WriteLine($"CI_TLS_FAILURE suite=PublicSessionControl case=05 client={code} server_stage={server.DiagnosticStage} server_exception={server.DiagnosticException} native={server.DiagnosticNative}");
                throw;
            }
            Check(snapshot.Count == 1 && snapshot[0].Members.Count == 2 && snapshot[0].SessionId == 10, "Valid session missing");
            var request = server.Requests.Single();
            Check(request.RequestLine == "POST /v1/sessions HTTP/1.1" && request.Headers["Authorization"] == "Bearer " + token, "Wrong request purpose or bearer");
            Check(request.Headers["Content-Type"] == "application/json" && !request.Headers.ContainsKey("Transfer-Encoding"), "Wrong framing");
            Check(!request.Headers.Keys.Any(name => name.Contains("Forward", StringComparison.OrdinalIgnoreCase) || name.Contains("Source", StringComparison.OrdinalIgnoreCase)), "Client asserted source header");
            using var body = JsonDocument.Parse(request.Body); var root = body.RootElement;
            Check(root.EnumerateObject().Select(x => x.Name).Order().SequenceEqual(new[] { "observer_requested", "package", "protocol_version" }), "Unexpected request fields");
            var declared = root.GetProperty("package");
            Check(root.GetProperty("protocol_version").GetInt32() == 1 && !root.GetProperty("observer_requested").GetBoolean()
                && declared.GetProperty("manifest_sha256").GetString() == package.Package.ManifestSha256, "Wrong package or protocol");
            foreach (string role in new[] { "launcher", "main_dll", "gameplay_dll", "server" })
                Check(declared.GetProperty(role).GetProperty("release_id").GetString() == package.Package.ReleaseId, "Mixed role identity");
            Check(!Encoding.UTF8.GetString(request.Body).Contains("password", StringComparison.OrdinalIgnoreCase), "Password entered session query");
        });
        await Case("production TLS rejects untrusted CA and isolated trust still enforces certificate hostname", async () => {
            await using (var server = new LoopbackTlsServer(certificates, (tls, _, ct) => LoopbackTlsServer.Send(tls, ct, Snapshot())))
            {
                var profile = Profile(server.Origin); using var context = Context(profile, package.Package);
                using var client = new PublicSessionControlClient(profile);
                await RejectQuery(async () => await client.QueryAsync(context), "PUBLIC_CONTROL_TRANSPORT_FAILED");
                Check(server.Requests.IsEmpty, "Bearer sent through an untrusted TLS peer");
            }
            await using (var server = new LoopbackTlsServer(certificates, (tls, _, ct) => LoopbackTlsServer.Send(tls, ct, Snapshot())))
            {
                var profile = Profile(server.Origin.Replace("127.0.0.1", "localhost", StringComparison.Ordinal));
                using var context = Context(profile, package.Package);
                using var client = new PublicSessionControlClient(profile, certificates.Handler(), TimeSpan.FromSeconds(8));
                await RejectQuery(async () => await client.QueryAsync(context), "PUBLIC_CONTROL_TRANSPORT_FAILED");
                Check(server.Requests.IsEmpty, "Bearer sent despite wrong certificate hostname");
            }
        });
        await Case("TLS redirects never forward bearer or retarget another origin", async () => {
            await using var target = new LoopbackTlsServer(certificates, (tls, _, ct) => LoopbackTlsServer.Send(tls, ct, Snapshot()));
            await using var origin = new LoopbackTlsServer(certificates, (tls, _, ct) => LoopbackTlsServer.Send(tls, ct, "{}", 302, "Location: " + target.Origin + "/v1/sessions\r\n"));
            var profile = Profile(origin.Origin); using var context = Context(profile, package.Package);
            using var client = new PublicSessionControlClient(profile, certificates.Handler(), TimeSpan.FromSeconds(8));
            await RejectQuery(async () => await client.QueryAsync(context), "PUBLIC_CONTROL_REQUEST_REJECTED");
            Check(target.Requests.IsEmpty, "Redirect destination received bearer");
        });
        await Case("non-200 responses reject without exposing server error content", async () => {
            foreach (int status in new[] { 201, 401, 403, 429, 503 })
            {
                await using var server = new LoopbackTlsServer(certificates, (tls, _, ct) => LoopbackTlsServer.Send(tls, ct, "{\"error\":\"synthetic-private-error\"}", status));
                var profile = Profile(server.Origin); using var context = Context(profile, package.Package);
                using var client = new PublicSessionControlClient(profile, certificates.Handler(), TimeSpan.FromSeconds(8));
                await RejectQuery(async () => await client.QueryAsync(context), "PUBLIC_CONTROL_REQUEST_REJECTED");
            }
        });
        await Case("strict response rejects schema ambiguity foreign sessions and membership collisions", async () => {
            foreach (string invalid in new[] { "null", "{\"schema\":1,\"schema\":1,\"sessions\":[]}", "{\"schema\":1,\"sessions\":null}",
                Snapshot().Replace("\"gameplay_mode\":2", "\"gameplay_mode\":1", StringComparison.Ordinal),
                Snapshot().Replace("\"host_user_id\":1000", "\"host_user_id\":999", StringComparison.Ordinal),
                Snapshot().Replace("\"user_id\":1000", "\"user_id\":999", StringComparison.Ordinal),
                Snapshot().Replace("10.66.0.11", "10.66.0.10", StringComparison.Ordinal),
                Snapshot().Replace("10.66.0.11", "10.67.0.11", StringComparison.Ordinal),
                Snapshot().Replace("10.66.0.10", "10.66.0.12", StringComparison.Ordinal),
                Snapshot().Replace("\"session_id\":10", "\"session_id\":true", StringComparison.Ordinal) })
            {
                await using var server = new LoopbackTlsServer(certificates, (tls, _, ct) => LoopbackTlsServer.Send(tls, ct, invalid));
                var profile = Profile(server.Origin); using var context = Context(profile, package.Package);
                using var client = new PublicSessionControlClient(profile, certificates.Handler(), TimeSpan.FromSeconds(8));
                await RejectQuery(async () => await client.QueryAsync(context), "PUBLIC_CONTROL_RESPONSE_INVALID");
            }
        });
        await Case("exact response media framing and no-store contract rejects alternate encodings", async () => {
            byte[] body = Encoding.UTF8.GetBytes(Snapshot());
            string valid = $"HTTP/1.1 200 Synthetic\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n";
            foreach (string header in new[] {
                valid.Replace("application/json", "text/plain", StringComparison.Ordinal),
                valid.Replace("application/json", "application/json; charset=utf-8", StringComparison.Ordinal),
                valid.Replace("Cache-Control: no-store\r\n", "", StringComparison.Ordinal),
                valid.Replace($"Content-Length: {body.Length}\r\n", "", StringComparison.Ordinal),
                valid + "Content-Encoding: gzip\r\n" })
            {
                await using var server = new LoopbackTlsServer(certificates, (tls, _, ct) => LoopbackTlsServer.SendBytes(tls, ct, body, header + "\r\n"));
                var profile = Profile(server.Origin); using var context = Context(profile, package.Package);
                using var client = new PublicSessionControlClient(profile, certificates.Handler(), TimeSpan.FromSeconds(8));
                await RejectQuery(async () => await client.QueryAsync(context), "PUBLIC_CONTROL_RESPONSE_INVALID");
            }
        });
        await Case("observer request requires matching native grant and admits an empty session projection", async () => {
            await using var server = new LoopbackTlsServer(certificates, (tls, _, ct) => LoopbackTlsServer.Send(tls, ct, "{\"schema\":1,\"sessions\":[]}"));
            var profile = Profile(server.Origin);
            using var context = Context(profile, package.Package, Expect(observer: true), Token(Claims(package.Package, observer: true)));
            using var client = new PublicSessionControlClient(profile, certificates.Handler(), TimeSpan.FromSeconds(8));
            Check((await client.QueryAsync(context)).Count == 0, "Empty admitted projection rejected");
            using var request = JsonDocument.Parse(server.Requests.Single().Body);
            Check(request.RootElement.GetProperty("observer_requested").GetBoolean(), "Observer flag did not follow caller and native grant");
        });
        await Case("response byte ceiling rejects large advertised bodies before buffering", async () => {
            await using var server = new LoopbackTlsServer(certificates, (tls, _, ct) => LoopbackTlsServer.SendBytes(tls, ct, [],
                $"HTTP/1.1 200 Synthetic\r\nContent-Type: application/json\r\nContent-Length: {PublicSessionControlClient.MaximumResponseBytes + 1}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n"));
            var profile = Profile(server.Origin); using var context = Context(profile, package.Package);
            using var client = new PublicSessionControlClient(profile, certificates.Handler(), TimeSpan.FromSeconds(8));
            await RejectQuery(async () => await client.QueryAsync(context), "PUBLIC_CONTROL_RESPONSE_INVALID");
        });
        await Case("request deadline applies while TLS response body stalls", async () => {
            await using var server = new LoopbackTlsServer(certificates, async (tls, _, ct) => {
                await LoopbackTlsServer.SendBytes(tls, ct, [], "HTTP/1.1 200 Synthetic\r\nContent-Type: application/json\r\nContent-Length: 50\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
                await Task.Delay(Timeout.Infinite, ct);
            });
            var profile = Profile(server.Origin); using var context = Context(profile, package.Package);
            using var client = new PublicSessionControlClient(profile, certificates.Handler(), TimeSpan.FromMilliseconds(750));
            await RejectQuery(async () => await client.QueryAsync(context), "PUBLIC_CONTROL_TIMEOUT");
            Check(server.RequestArrived.Task.IsCompletedSuccessfully, "Deadline test did not reach the real TLS request");
        });
        await Case("caller cancellation and native run disposal prevent late snapshot publication", async () => {
            foreach (bool dispose in new[] { false, true })
            {
                var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                await using var server = new LoopbackTlsServer(certificates, async (tls, _, ct) => {
                    await release.Task.WaitAsync(ct); await LoopbackTlsServer.Send(tls, ct, Snapshot());
                });
                var profile = Profile(server.Origin); using var context = Context(profile, package.Package);
                using var client = new PublicSessionControlClient(profile, certificates.Handler(), TimeSpan.FromSeconds(8));
                using var cancel = new CancellationTokenSource();
                var pending = client.QueryAsync(context, cancel.Token);
                await server.RequestArrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (dispose) context.Dispose(); else cancel.Cancel();
                release.TrySetResult(true);
                await Cancelled(pending);
            }
        });
        await Case("mismatched deployment and disposed context are rejected before HTTP", async () => {
            await using var server = new LoopbackTlsServer(certificates, (tls, _, ct) => LoopbackTlsServer.Send(tls, ct, Snapshot()));
            var profile = Profile(server.Origin); using var context = Context(profile, package.Package);
            using var foreign = new PublicSessionControlClient(Profile(server.Origin, "other"), certificates.Handler(), TimeSpan.FromSeconds(8));
            try { await foreign.QueryAsync(context); throw new Exception("Foreign deployment admitted"); } catch (ArgumentException) { }
            context.Dispose();
            using var client = new PublicSessionControlClient(profile, certificates.Handler(), TimeSpan.FromSeconds(8));
            await Cancelled(client.QueryAsync(context));
            Check(server.Requests.IsEmpty, "Rejected context performed network I/O");
        });
        Console.WriteLine($"RESULT {passed} Public session control checks passed; synthetic loopback TLS only; no native handoff, game or Public readiness.");
    }
}
