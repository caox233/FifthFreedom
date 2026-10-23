using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using SplinterCellCNLauncher.Services;

// Only the filesystem default is substituted. Actual credential identity and
// package validation code are linked; no credential crypto or HTTP stub is used.
namespace SplinterCellCNLauncher.Services
{
    internal static class LogService
    {
        public static string AppDataDir => throw new InvalidOperationException("No implicit credential root in this test.");
    }
}

internal sealed class SyntheticPackage : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ff-public-session-" + Guid.NewGuid().ToString("N"));
    internal ReleasePackage Package { get; }
    internal SyntheticPackage()
    {
        Directory.CreateDirectory(root);
        var rows = new List<object>();
        foreach (var item in new[] { ("launcher", "FifthFreedom.exe"), ("main", "runtime/uplay_r1_loader.dll"),
                                    ("gameplay", "runtime/scbl_gameplay.dll"), ("server", "runtime/FifthFreedom.Server.exe") })
        {
            string path = Path.Combine(root, item.Item2); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            byte[] bytes = Encoding.ASCII.GetBytes($"Synthetic nonexecuted package fixture\0SCBL_RELEASE_V1|{item.Item1}|{ReleaseBuildIdentity.ReleaseId}|{ReleaseBuildIdentity.CompatId}|END_SCBl\0");
            File.WriteAllBytes(path, bytes);
            rows.Add(new { role = item.Item1, path = item.Item2, release_id = ReleaseBuildIdentity.ReleaseId,
                compat_id = ReleaseBuildIdentity.CompatId, bytes = bytes.Length, sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() });
        }
        Directory.CreateDirectory(Path.Combine(root, "package"));
        File.WriteAllBytes(Path.Combine(root, "package/RELEASE-CATALOG.json"), JsonSerializer.SerializeToUtf8Bytes(new {
            schema = 1, release_id = ReleaseBuildIdentity.ReleaseId, compat_id = ReleaseBuildIdentity.CompatId, components = rows }));
        Package = ReleasePackage.Verify(Path.Combine(root, "FifthFreedom.exe"));
    }
    public void Dispose() => Directory.Delete(root, recursive: true);
}

internal sealed class SyntheticCertificates : IDisposable
{
    private readonly RSA authorityKey = RSA.Create(2048), serverKey = RSA.Create(2048);
    internal X509Certificate2 Authority { get; }
    internal X509Certificate2 Server { get; }
    internal SyntheticCertificates()
    {
        var root = new CertificateRequest("CN=FifthFreedom temporary test CA", authorityKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        root.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        root.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        Authority = root.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        var leaf = new CertificateRequest("CN=temporary-loopback-test", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        leaf.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        leaf.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        leaf.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        var san = new SubjectAlternativeNameBuilder(); san.AddIpAddress(IPAddress.Loopback);
        leaf.CertificateExtensions.Add(san.Build());
        using var issued = leaf.Create(Authority, DateTimeOffset.UtcNow.AddMinutes(-2), DateTimeOffset.UtcNow.AddHours(1), RandomNumberGenerator.GetBytes(16));
        Server = issued.CopyWithPrivateKey(serverKey);
    }
    internal SocketsHttpHandler Handler()
    {
        var policy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust,
            RevocationMode = X509RevocationMode.NoCheck, VerificationFlags = X509VerificationFlags.NoFlag };
        policy.CustomTrustStore.Add(Authority);
        return new SocketsHttpHandler { SslOptions = new SslClientAuthenticationOptions { CertificateChainPolicy = policy } };
    }
    public void Dispose() { Server.Dispose(); Authority.Dispose(); serverKey.Dispose(); authorityKey.Dispose(); }
}

internal sealed class CapturedRequest
{
    internal string RequestLine { get; }
    internal Dictionary<string, string> Headers { get; }
    internal byte[] Body { get; }
    internal CapturedRequest(string line, Dictionary<string, string> headers, byte[] body) => (RequestLine, Headers, Body) = (line, headers, body);
}

internal sealed class LoopbackTlsServer : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly Task worker;
    internal readonly ConcurrentQueue<CapturedRequest> Requests = new();
    internal readonly TaskCompletionSource<bool> RequestArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal string Origin => $"https://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
    internal LoopbackTlsServer(SyntheticCertificates certificates, Func<SslStream, CapturedRequest, CancellationToken, Task> respond)
    {
        listener.Start();
        worker = Task.Run(async () => {
            try
            {
                using var socket = await listener.AcceptTcpClientAsync(stop.Token);
                using var tls = new SslStream(socket.GetStream(), leaveInnerStreamOpen: false);
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificates.Server,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, stop.Token);
                var request = await ReadRequest(tls, stop.Token);
                Requests.Enqueue(request); RequestArrived.TrySetResult(true);
                await respond(tls, request, stop.Token);
            }
            catch (Exception error) when (error is OperationCanceledException or IOException or AuthenticationException or SocketException
                || error is InvalidOperationException && stop.IsCancellationRequested) { }
        });
    }
    private static async Task<CapturedRequest> ReadRequest(SslStream stream, CancellationToken token)
    {
        var buffer = new List<byte>(); byte[] one = new byte[1];
        while (buffer.Count < 16384)
        {
            await stream.ReadExactlyAsync(one, token); buffer.Add(one[0]);
            if (buffer.Count >= 4 && buffer.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
        }
        if (buffer.Count >= 16384) throw new IOException("Synthetic request too large");
        string[] lines = Encoding.ASCII.GetString(buffer.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1)) { int split = line.IndexOf(':'); headers.Add(line[..split], line[(split + 1)..].Trim()); }
        int size = int.Parse(headers["Content-Length"], System.Globalization.CultureInfo.InvariantCulture);
        if (size is < 1 or > 16384) throw new IOException("Synthetic request body too large");
        byte[] body = new byte[size]; await stream.ReadExactlyAsync(body, token);
        return new CapturedRequest(lines[0], headers, body);
    }
    internal static Task Send(SslStream stream, CancellationToken token, string body, int status = 200, string headers = "")
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        return SendBytes(stream, token, bytes, $"HTTP/1.1 {status} Synthetic\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n{headers}\r\n");
    }
    internal static async Task SendBytes(SslStream stream, CancellationToken token, byte[] body, string header)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), token);
        await stream.WriteAsync(body, token);
        await stream.FlushAsync(token);
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); listener.Stop();
        await worker.WaitAsync(TimeSpan.FromSeconds(4)); stop.Dispose();
    }
}
