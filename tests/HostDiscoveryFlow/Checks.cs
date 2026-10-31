using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SplinterCellCNLauncher.Services;

public static class HostDiscoveryFlowChecks
{
    private static int passed;
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(2);
    private static readonly LocalDiscoveredHost Host = new("127.0.0.1", 50051, "Fixture", "fixture-session");
    private static void Check(bool ok, string label)
    {
        if (!ok) throw new InvalidOperationException(label);
        Console.WriteLine("PASS " + label); passed++;
    }
    private sealed class Transport(Func<string, TimeSpan, CancellationToken, Task<LocalHostDiscoveryScan>> run) : ILocalHostDiscoveryTransport
    {
        public Task<LocalHostDiscoveryScan> SearchAsync(string bind, TimeSpan window, CancellationToken token) => run(bind, window, token);
    }
    private static TaskCompletionSource<T> Pending<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static LocalHostDiscoveryScan Done(params LocalDiscoveredHost[] hosts) => new(LocalHostDiscoveryStatus.Completed, hosts);
    private static byte[] Advert(string ip = "127.0.0.1", string port = "50051", string session = "fixture-session", string name = "Fixture") =>
        Encoding.UTF8.GetBytes($"SCBL_LAN_HOST\nVersion=1\nHostName={name}\nHostIP={ip}\nApiPort={port}\nSessionId={session}\nState=ServerReady\nStartedAt=1");

    // Own both socket endpoints. A real UDP query and response use the existing
    // protocol; this does not launch the WPF host, a game, or a public listener.
    private static async Task<LocalHostDiscoveryScan> UdpScan(params byte[][] replies)
    {
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = (IPEndPoint)peer.Client.LocalEndPoint!;
        var transport = new UdpLocalHostDiscoveryTransport(_ => [endpoint]);
        using var watchdog = new CancellationTokenSource(Limit);
        var scan = transport.SearchAsync("127.0.0.1", Window, watchdog.Token);
        var request = await peer.ReceiveAsync(watchdog.Token);
        string text = Encoding.UTF8.GetString(request.Buffer);
        Check(text.StartsWith("SCBL_LAN_HOST_QUERY\nVersion=1\nClientIP=127.0.0.1\nRequestedAt=", StringComparison.Ordinal)
            && request.RemoteEndPoint.Address.Equals(IPAddress.Loopback) && request.RemoteEndPoint.Port != LocalHostDiscoveryProtocol.Port,
            "real query uses retained protocol and selected IP's owned ephemeral source port");
        foreach (byte[] reply in replies) await peer.SendAsync(reply, request.RemoteEndPoint, watchdog.Token);
        return await scan.WaitAsync(Limit);
    }

    public static async Task Run()
    {
        var empty = await UdpScan();
        Check(empty.Status == LocalHostDiscoveryStatus.Completed && empty.Hosts.Count == 0,
            "normal observation window completes with zero responses, not a network timeout");
        var one = await UdpScan(Advert());
        Check(one.Status == LocalHostDiscoveryStatus.Completed && one.Hosts.SequenceEqual([Host]), "one valid host is returned without selecting or joining it");
        var many = await UdpScan(Advert(), Advert(port: "50052", session: "two", name: "Two"), Advert(session: "replacement"));
        Check(many.Status == LocalHostDiscoveryStatus.Completed && many.Hosts.Count == 2
            && many.Hosts.Single(h => h.ApiPort == 50051).SessionId == "replacement", "multiple endpoints retained and repeated endpoint replaced within this scan only");
        var invalidOnly = await UdpScan(Advert(ip: "192.0.2.1"), Advert(port: "65536"), [0xff]);
        Check(invalidOnly.Status == LocalHostDiscoveryStatus.Completed && invalidOnly.Hosts.Count == 0, "untrusted or malformed responses never become selectable hosts");

        var remote = new IPEndPoint(IPAddress.Loopback, 40000);
        foreach (var (label, data) in new (string, byte[])[]
        {
            ("source-address mismatch", Advert(ip: "192.0.2.10")),
            ("multicast", Advert(ip: "224.0.0.1")), ("unspecified", Advert(ip: "0.0.0.0")),
            ("broadcast", Advert(ip: "255.255.255.255")), ("IPv6", Advert(ip: "::1")),
            ("short IPv4 alias", Advert(ip: "127.1")), ("zero API port", Advert(port: "0")),
            ("oversize API port", Advert(port: "65536")), ("invalid API port", Advert(port: "+50051")),
            ("oversize name", Advert(name: new string('x', 65))), ("control name", Advert(name: "bad\0name")),
            ("bidirectional control", Advert(name: "bad\u202ename")),
            ("duplicate address field", Advert().Concat(Encoding.UTF8.GetBytes("\nHostIP=127.0.0.1")).ToArray()),
            ("unsupported version", Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Advert()).Replace("Version=1", "Version=2"))),
            ("unready state", Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Advert()).Replace("ServerReady", "Starting"))),
            ("oversize packet", new byte[LocalHostDiscoveryProtocol.MaxPacketBytes + 1]), ("invalid UTF8", [0xff])
        }) Check(LocalHostDiscoveryProtocol.Parse(data, remote) == null, "reject " + label);
        Check(LocalHostDiscoveryProtocol.Parse(Advert(name: "中文房主"), remote)?.HostName == "中文房主", "valid UTF8 host label retained");

        foreach (var (socketError, expected) in new[] { (SocketError.NetworkDown, LocalHostDiscoveryStatus.NetworkError), (SocketError.TimedOut, LocalHostDiscoveryStatus.TimedOut) })
        {
            var broken = new UdpLocalHostDiscoveryTransport(_ => throw new SocketException((int)socketError));
            var result = await broken.SearchAsync("127.0.0.1", Window, default);
            Check(result.Status == expected && result.Hosts.Count == 0, socketError + " cannot claim a successful empty scan");
        }
        var noTargets = await new UdpLocalHostDiscoveryTransport(_ => []).SearchAsync("127.0.0.1", Window, default);
        Check(noTargets.Status == LocalHostDiscoveryStatus.NetworkError, "no send targets is an error, not no hosts");

        int calls = 0;
        using (var flow = new LocalHostDiscoveryFlow(new Transport((_, _, _) => { calls++; return Task.FromResult(Done()); }), Window, Limit))
        {
            foreach (string bind in new[] { "", "example.invalid", "0.0.0.0", "224.0.0.1", "::1", "127.1", "127.0.0.1:50051" })
                Check((await flow.SearchAsync(bind)).Status == LocalHostDiscoveryStatus.InvalidEndpoint, "invalid bind rejected before transport: " + bind);
            Check(calls == 0, "invalid endpoints perform no transport work");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            var result = await flow.SearchAsync("127.0.0.1", cancelled.Token);
            Check(result.Status == LocalHostDiscoveryStatus.Cancelled && !flow.IsCurrent(result) && calls == 0, "pre-cancelled search performs no transport work or selection");
        }

        var started = Pending<CancellationToken>();
        var held = Pending<LocalHostDiscoveryScan>();
        using (var flow = new LocalHostDiscoveryFlow(new Transport((_, _, token) => { started.SetResult(token); return held.Task; }), Window, TimeSpan.FromMilliseconds(300)))
        {
            var timer = Stopwatch.StartNew();
            var work = flow.SearchAsync("127.0.0.1");
            CancellationToken owner = await started.Task.WaitAsync(Limit);
            var result = await work.WaitAsync(Limit);
            Check(result.Status == LocalHostDiscoveryStatus.TimedOut && result.Hosts.Count == 0 && timer.Elapsed < Limit && owner.IsCancellationRequested,
                "hard deadline returns timeout and cancels unresponsive owned transport");
            held.SetResult(Done(Host));
            Check(result.Hosts.Count == 0, "late timeout result cannot add selectable hosts");
        }

        started = Pending<CancellationToken>(); held = Pending<LocalHostDiscoveryScan>();
        using (var flow = new LocalHostDiscoveryFlow(new Transport((_, _, token) => { started.SetResult(token); return held.Task; }), Window, Limit))
        {
            using var caller = new CancellationTokenSource();
            var work = flow.SearchAsync("127.0.0.1", caller.Token);
            await started.Task.WaitAsync(Limit); caller.Cancel();
            var result = await work.WaitAsync(Limit);
            Check(result.Status == LocalHostDiscoveryStatus.Cancelled && result.Hosts.Count == 0 && !flow.IsCurrent(result), "caller cancellation cannot appear as no hosts");
            held.SetResult(Done(Host));
        }

        started = Pending<CancellationToken>(); held = Pending<LocalHostDiscoveryScan>(); calls = 0;
        using (var flow = new LocalHostDiscoveryFlow(new Transport((_, _, token) =>
        {
            if (++calls == 1) { started.SetResult(token); return held.Task; }
            return Task.FromResult(Done(Host));
        }), Window, Limit))
        {
            var previous = flow.SearchAsync("127.0.0.1");
            CancellationToken oldOwner = await started.Task.WaitAsync(Limit);
            var latest = await flow.SearchAsync("127.0.0.1");
            var stale = await previous.WaitAsync(Limit);
            held.SetResult(Done(Host with { ApiPort = 50052 }));
            Check(stale.Status == LocalHostDiscoveryStatus.Superseded && stale.Hosts.Count == 0 && oldOwner.IsCancellationRequested
                && latest.Generation > stale.Generation && flow.IsCurrent(latest) && !flow.IsCurrent(stale), "retry generation cancels and rejects late predecessor results");
            flow.Cancel();
            Check(!flow.IsCurrent(latest), "mode/bind/close cancellation invalidates already completed selection");
        }

        calls = 0;
        using (var flow = new LocalHostDiscoveryFlow(new Transport((_, _, _) =>
            ++calls == 1 ? Task.FromException<LocalHostDiscoveryScan>(new IOException("owned fixture")) : Task.FromResult(Done())), Window, Limit))
        {
            var failed = await flow.SearchAsync("127.0.0.1");
            var retry = await flow.SearchAsync("127.0.0.1");
            Check(failed.Status == LocalHostDiscoveryStatus.NetworkError && retry.Status == LocalHostDiscoveryStatus.Completed
                && retry.Hosts.Count == 0 && !flow.IsCurrent(failed) && flow.IsCurrent(retry), "explicit retry recovers after network error without cached hosts");
        }
        using (var flow = new LocalHostDiscoveryFlow(new Transport((_, _, _) => Task.FromResult(Done(Host))), Window, Limit))
        {
            using var caller = new CancellationTokenSource();
            var result = await flow.SearchAsync("127.0.0.1", caller.Token);
            caller.Cancel();
            Check(!flow.IsCurrent(result), "caller cancellation after completion invalidates a pending user choice");
            flow.Dispose();
            bool rejected = false;
            try { await flow.SearchAsync("127.0.0.1"); } catch (ObjectDisposedException) { rejected = true; }
            Check(rejected, "disposed flow cannot start another search");
        }
        Console.WriteLine($"RESULT {passed} host discovery checks passed; real owned loopback UDP and controlled flow faults; no game, autojoin or live LAN acceptance.");
    }
}
