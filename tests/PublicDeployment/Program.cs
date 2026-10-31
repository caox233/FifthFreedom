using System.Security.Cryptography;
using System.Text;
using SplinterCellCNLauncher.Services;

static class Program
{
    private static int passed;
    private static readonly byte[] SyntheticRuntimeBytes = Encoding.UTF8.GetBytes("synthetic pinned input; never executable");
    private static readonly string SyntheticDigest = Convert.ToHexString(SHA256.HashData(SyntheticRuntimeBytes)).ToLowerInvariant();
    private static PublicRuntimePin[] PinArray() => Enum.GetValues<PublicRuntimeRole>().Select(role => new PublicRuntimePin(role, SyntheticRuntimeBytes.Length, SyntheticDigest)).ToArray();
    private static PublicDeploymentRuntimePins Pins() => new("synthetic-test-only", PinArray());
    private static PublicDeploymentProfile Profile(string id = "synthetic-a", string origin = "https://control.example.invalid",
        string cidr = "10.66.0.0/24", string server = "10.66.0.1", PublicTransport transport = PublicTransport.Fast,
        string peer = "quic://relay.example.invalid:11010", PublicDeploymentRuntimePins? pins = null)
        => new(id, origin, cidr, server, transport, peer, pins ?? Pins());
    private static void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); }
    private static void Case(string label, Action test) { test(); passed++; Console.WriteLine("PASS " + label); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (ArgumentException e) { Check(!e.Message.Contains("example.invalid") && !e.Message.Contains("synthetic-secret"), "input leaked in bounded error"); return; }
        throw new Exception("invalid input admitted");
    }
    private static string FileFor(string root, CredentialIdentity identity)
        => Path.Combine(root, "credentials", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ScopeValue))) + ".bin");
    private static void Main()
    {
        Case("selected unconfigured Public never loads a LAN credential identity", () =>
        {
            var context = CredentialEnvironmentContext.UnconfiguredPublic;
            Check(context.Environment == NetworkEnvironmentKind.Public && !context.Equals(CredentialEnvironmentContext.Lan), "unconfigured Public aliased LAN");
            Reject(() => context.CreateIdentity("192.0.2.1:50051", "fixture"));
        });
        Case("LAN scope bytes remain compatible with existing filenames and DPAPI entropy", () =>
        {
            const string expected = "SCBL-LAN-DPAPI-v1\nexample.test:50051\nfixture";
            Check(CredentialStore.Scope("http://EXAMPLE.test", " fixture ") == expected, "legacy bytes changed");
            Check(CredentialEnvironmentContext.Lan.CreateIdentity("example.test:50051", "fixture").ScopeValue == expected, "typed LAN diverged");
            Check(CredentialStore.Scope("192.0.2.10:50052", "fixture") == "SCBL-LAN-DPAPI-v1\n192.0.2.10:50052\nfixture", "explicit LAN port changed");
        });
        Case("Public credential storage is separated from LAN and other deployment IDs", () =>
        {
            var a = CredentialEnvironmentContext.ForPublic(Profile());
            var b = CredentialEnvironmentContext.ForPublic(Profile("synthetic-b"));
            var lan = CredentialEnvironmentContext.Lan.CreateIdentity("10.66.0.1:50051", "fixture");
            Check(!a.CreateIdentity("10.66.0.1:50051", "fixture").Equals(b.CreateIdentity("10.66.0.1:50051", "fixture")), "deployment namespace collision");
            Check(!lan.Equals(a.CreateIdentity("10.66.0.1:50051", "fixture")), "Public downgraded to LAN");
        });
        Case("HTTPS authority and overlay server changes isolate contexts and credentials", () =>
        {
            var a = CredentialEnvironmentContext.ForPublic(Profile());
            var origin = CredentialEnvironmentContext.ForPublic(Profile(origin: "https://other.example.invalid:443/"));
            var server = CredentialEnvironmentContext.ForPublic(Profile(server: "10.66.0.2"));
            Check(!a.Equals(origin) && !a.Equals(server), "context change would skip input clearing");
            Check(!a.CreateIdentity("10.66.0.1:50051", "fixture").Equals(origin.CreateIdentity("10.66.0.1:50051", "fixture")), "changed HTTPS authority reused credential");
        });
        Case("canonical origin aliases and selected transport preserve one explicit deployment", () =>
        {
            var a = CredentialEnvironmentContext.ForPublic(Profile(origin: "https://CONTROL.example.invalid:443/"));
            var b = CredentialEnvironmentContext.ForPublic(Profile(transport: PublicTransport.Compatible, peer: "wss://relay.example.invalid:11010"));
            Check(a.Equals(b), "transport became a different deployment or origin was not canonical");
            Check(a.CreateIdentity("10.66.0.1:50051", " fixture ").Equals(b.CreateIdentity("10.66.0.1:50051", "fixture")), "canonical identities differ");
        });
        Case("missing Public identity never guesses LAN or a default deployment", () =>
        {
            Reject(() => CredentialEnvironmentContext.ForPublic(null!));
            foreach (string value in new[] { "", " ", "a\nb", "../profile", "synthetic-secret@host" }) Reject(() => Profile(value));
            Reject(() => new PublicDeploymentProfile("valid", "https://example.invalid", "10.66.0.0/24", "10.66.0.1", PublicTransport.Fast, "quic://relay.example.invalid:11010", null!));
        });
        Case("origin and peer URI reject HTTP credentials paths queries fragments and IPv4 aliases", () =>
        {
            foreach (string origin in new[] { "", "http://control.example.invalid", "https://user:synthetic-secret@control.example.invalid", "https://control.example.invalid/path",
                "https://control.example.invalid/a/../", "https://control.example.invalid?x", "https://control.example.invalid#x", "https://control.example.invalid?", "https://control.example.invalid#",
                "https://control.example.invalid\\x", " https://control.example.invalid", "https://control.example.invalid\n", "https://127.1", "https://2130706433", "https://0x7f000001", "https://127.000.000.001" })
                Reject(() => Profile(origin: origin));
            foreach (string peer in new[] { "", "wss://relay.example.invalid:11010", "quic://relay.example.invalid", "quic://user:synthetic-secret@relay.example.invalid:11010",
                "quic://relay.example.invalid:11010/path", "quic://relay.example.invalid:11010?x", "quic://relay.example.invalid:11010#x", "quic://127.1:11010" })
                Reject(() => Profile(peer: peer));
            Reject(() => Profile(transport: PublicTransport.Compatible, peer: "quic://relay.example.invalid:11010"));
            Reject(() => Profile(transport: (PublicTransport)999));
        });
        Case("overlay requires canonical usable IPv4 network and a usable in-network server", () =>
        {
            foreach (var pair in new[] { ("10.66.0.1/24", "10.66.0.2"), ("10.66.0.0/24", "10.66.0.0"), ("10.66.0.0/24", "10.66.0.255"),
                ("10.66.0.0/24", "10.67.0.1"), ("0.0.0.0/0", "10.66.0.1"), ("10.66.0.0/31", "10.66.0.1"), ("10.66.0.0/024", "10.66.0.1"),
                ("10.066.0.0/24", "10.66.0.1"), ("127.0.0.0/8", "127.0.0.1"), ("169.254.0.0/16", "169.254.0.1"), ("224.0.0.0/8", "224.0.0.1"),
                ("0.0.0.0/8", "0.0.0.1"), ("10.66.0.0/24", "10.66.1"), ("10.66.0.0/24", "10.66.0.01"), ("::/8", "::1") })
                Reject(() => Profile(cidr: pair.Item1, server: pair.Item2));
        });
        Case("Public native endpoint cannot fall back to implicit port unrelated hosts or ambiguous input", () =>
        {
            var context = CredentialEnvironmentContext.ForPublic(Profile());
            foreach (string endpoint in new[] { "", "10.66.0.1", "10.66.0.2:50051", "10.66.0.1:050051", "10.66.0.1:0", "10.66.0.1:65536", "10.66.0.01:50051",
                "http://10.66.0.1:50051", "https://10.66.0.1:50051", "user:pass@10.66.0.1:50051", "10.66.0.1:50051/path", "10.66.0.1:50051#x" })
                Reject(() => context.CreateIdentity(endpoint, "fixture"));
            foreach (string user in new[] { "", " ", "a\nb", "a\0b", "a\rb", "a\tb", "\uD800", "two words" }) Reject(() => context.CreateIdentity("10.66.0.1:50051", user));
        });
        Case("configuration identity representations do not print endpoint user or runtime pins", () =>
        {
            var profile = Profile(); var context = CredentialEnvironmentContext.ForPublic(profile); var identity = context.CreateIdentity("10.66.0.1:50051", "private-fixture-user");
            string text = string.Join(" ", profile, context, identity, profile.RuntimePins, profile.RuntimePins.Pins[0]);
            foreach (string value in new[] { "example.invalid", "10.66", "private-fixture-user", SyntheticDigest }) Check(!text.Contains(value), "configuration value printed");
        });
        Case("runtime pins are complete and immutable after caller collection changes", () =>
        {
            var original = PinArray(); var pins = new PublicDeploymentRuntimePins("synthetic", original);
            original[0] = new PublicRuntimePin(PublicRuntimeRole.Core, 1, new string('1', 64));
            Check(pins.Pins[0].Bytes == SyntheticRuntimeBytes.Length, "mutable caller array changed pinned input");
            Reject(() => new PublicDeploymentRuntimePins("synthetic", PinArray().Take(3)));
            Reject(() => new PublicDeploymentRuntimePins("synthetic", new[] { original[0], original[0], original[0], original[0] }));
            Reject(() => new PublicRuntimePin((PublicRuntimeRole)99, 1, SyntheticDigest));
            Reject(() => new PublicRuntimePin(PublicRuntimeRole.Core, 0, SyntheticDigest));
            Reject(() => new PublicRuntimePin(PublicRuntimeRole.Core, 1, "bad"));
        });
        string root = Path.Combine(Path.GetTempPath(), "ff-public-deployment-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var a = CredentialEnvironmentContext.ForPublic(Profile()).CreateIdentity("10.66.0.1:50051", "fixture");
            var b = CredentialEnvironmentContext.ForPublic(Profile("synthetic-b")).CreateIdentity("10.66.0.1:50051", "fixture");
            var lan = CredentialEnvironmentContext.Lan.CreateIdentity("10.66.0.1:50051", "fixture");
            var store = new CredentialStore(root);
            Case("production TryLoad and Delete never fall back to another deployment or legacy LAN file", () =>
            {
                Directory.CreateDirectory(Path.Combine(root, "credentials"));
                File.WriteAllText(FileFor(root, lan), "synthetic-invalid-ciphertext");
                File.WriteAllText(FileFor(root, a), "synthetic-invalid-ciphertext");
                Check(!store.TryLoad(b, out var absent) && absent is null, "Public missing file fell back");
                store.Delete(b);
                Check(File.Exists(FileFor(root, lan)) && File.Exists(FileFor(root, a)), "delete crossed namespace");
                store.Delete(a);
                Check(!File.Exists(FileFor(root, a)) && File.Exists(FileFor(root, lan)), "delete affected LAN");
                store.Remove("10.66.0.1:50051", "fixture");
            });
            Case("runtime verification hashes actual owned synthetic files and rejects changed or missing inputs", () =>
            {
                string runtime = Path.Combine(root, "runtime"); Directory.CreateDirectory(runtime);
                foreach (string name in new[] { "easytier-core.exe", "easytier-cli.exe", "Packet.dll", "wintun.dll" }) File.WriteAllBytes(Path.Combine(runtime, name), SyntheticRuntimeBytes);
                var pins = Pins(); pins.VerifyFiles(runtime);
                string target = Path.Combine(runtime, "Packet.dll"); File.WriteAllBytes(target, new byte[SyntheticRuntimeBytes.Length]);
                try { pins.VerifyFiles(runtime); throw new Exception("changed bytes admitted"); } catch (IOException e) { Check(e.Message == "PUBLIC_RUNTIME_INPUTS_INVALID", "path leaked"); }
                File.Delete(target);
                try { pins.VerifyFiles(runtime); throw new Exception("missing bytes admitted"); } catch (IOException e) { Check(e.Message == "PUBLIC_RUNTIME_INPUTS_INVALID", "path leaked"); }
                if (!OperatingSystem.IsWindows())
                {
                    File.CreateSymbolicLink(target, Path.Combine(runtime, "easytier-core.exe"));
                    try { pins.VerifyFiles(runtime); throw new Exception("symlink admitted"); } catch (IOException e) { Check(e.Message == "PUBLIC_RUNTIME_INPUTS_INVALID", "path leaked"); }
                }
            });
            if (OperatingSystem.IsWindows())
            {
                Case("real Windows DPAPI Save TryLoad and Delete isolate LAN and both Public deployments", () =>
                {
                    store.Save("10.66.0.1:50051", "fixture", "synthetic-lan-password");
                    store.Save(a, "synthetic-public-a-password"); store.Save(b, "synthetic-public-b-password");
                    Check(store.Load(lan) == "synthetic-lan-password" && store.TryLoad(a, out var password) && password == "synthetic-public-a-password"
                        && store.Load(b) == "synthetic-public-b-password", "DPAPI identity isolation failed");
                    foreach (string path in Directory.GetFiles(Path.Combine(root, "credentials"))) Check(!Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains("synthetic-"), "plaintext persisted");
                    File.Copy(FileFor(root, a), FileFor(root, b), overwrite: true);
                    try { store.Load(b); throw new Exception("ciphertext copied between deployments decrypted"); } catch (CryptographicException) { }
                    store.Delete(a); Check(store.Load(a) is null && store.Load(lan) == "synthetic-lan-password", "Public deletion affected LAN");
                    store.Clear(); Check(Directory.GetFiles(Path.Combine(root, "credentials"), "*.bin").Length == 0, "clear incomplete");
                });
            }
            else Console.WriteLine("SKIP real Windows DPAPI checks: Windows crypt32 runtime required.");
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine($"RESULT {passed} public deployment/credential cases passed; no network, game or driver started.");
    }
}
