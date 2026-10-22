using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using SplinterCellCNLauncher.Network;

// Explicit integration test: fresh loopback data only, no real accounts/saves.
if (args.Length != 2) throw new ArgumentException("Usage: ObserverServer <packaged-server.exe> <new-evidence-directory>");
string exe = Path.GetFullPath(args[0]), root = Path.GetFullPath(args[1]);
if (!File.Exists(exe) || Directory.Exists(root) || File.Exists(root)) throw new IOException("Require packaged server and fresh evidence path.");
foreach (var (port, udp) in new[] {(18080,false),(18000,false),(22126,true),(22127,true),(15051,false)}) {
    using var socket = new Socket(AddressFamily.InterNetwork, udp ? SocketType.Dgram : SocketType.Stream, udp ? ProtocolType.Udp : ProtocolType.Tcp);
    socket.ExclusiveAddressUse = true;
    socket.Bind(new IPEndPoint(IPAddress.Loopback, port)); // Never terminate another owner.
}
Directory.CreateDirectory(root);
var catalogBytes=await File.ReadAllBytesAsync(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(exe)!)!,"package","RELEASE-CATALOG.json"));
using var catalog=JsonDocument.Parse(catalogBytes);
var release=catalog.RootElement.GetProperty("release_id").GetString()!;
var compat=catalog.RootElement.GetProperty("compat_id").GetString()!;
ReleaseComponent Component()=>new() {ReleaseId=release,CompatId=compat};
var package=new PackageIdentity {Schema=1,ReleaseId=release,CompatId=compat,
    ManifestSha256=Convert.ToHexStringLower(SHA256.HashData(catalogBytes)),
    Launcher=Component(),MainDll=Component(),GameplayDll=Component(),Server=Component()};
const string Ack="x-scbl-observer-run";
const string FixtureUser="ObserverFixture", FixturePassword="offline-fixture-password-93";
GrpcChannel Channel()=>GrpcChannel.ForAddress("http://127.0.0.1:15051",new GrpcChannelOptions {HttpHandler=new SocketsHttpHandler {UseProxy=false}});
async Task RegisterFixture() {
    using var channel=Channel();var api=new Users.UsersClient(channel);
    var result=await api.RegisterAsync(new RegisterRequest {PackageIdentity=package,ClientProduct="scbl-lan/1/official",
        Username=FixtureUser,Password=FixturePassword,UbiId="0123456789abcdef0123456789abcdef"},deadline:DateTime.UtcNow.AddSeconds(10));
    if(result.Error.Length!=0 || result.RecoveryCode.Length==0)throw new IOException("Fixture registration failed");
    // Never print or persist the generated token/recovery code.
}
async Task Login(string label,bool requested,string? instance,string product,StatusCode? denied=null,string? password=null) {
    using var channel=Channel();var api=new Users.UsersClient(channel);var headers=new Metadata();
    if(instance is not null)headers.Add("x-scbl-game-instance",instance);
    if(requested)headers.Add("x-scbl-observer","1");
    using var call=api.LoginAsync(new LoginRequest {PackageIdentity=package,ClientProduct=product,Username=FixtureUser,
        Password=password??FixturePassword},headers,DateTime.UtcNow.AddSeconds(10));
    try {
        var response=await call.ResponseAsync;var responseHeaders=await call.ResponseHeadersAsync;
        if(denied is not null)throw new IOException($"{label}: expected denial {denied}");
        var ack=responseHeaders.GetValue(Ack);
        if(response.Token.Length==0 || response.User is null || response.Error.Length!=0
            || (requested ? ack!=instance : ack is not null))throw new IOException($"{label}: bad admission response");
        Console.WriteLine($"PASS {label}: authenticated=true observer_ack={(ack is null?"absent":"exact-run")}");
    } catch(RpcException e) when(denied==e.StatusCode) {
        var responseHeaders=await call.ResponseHeadersAsync;
        if(responseHeaders.GetValue(Ack) is not null || e.Trailers.GetValue(Ack) is not null)
            throw new IOException($"{label}: denied request received OB acknowledgment");
        Console.WriteLine($"PASS {label}: denied={e.StatusCode} observer_ack=absent");
    }
}
ProcessStartInfo Info(bool control) {
    var psi = new ProcessStartInfo(exe) {WorkingDirectory=root, UseShellExecute=false, CreateNoWindow=true, WindowStyle=ProcessWindowStyle.Hidden,
        RedirectStandardInput=true, RedirectStandardOutput=true, RedirectStandardError=true};
    psi.ArgumentList.Add("--data-dir"); psi.ArgumentList.Add(Path.Combine(root,"data"));
    psi.Environment.Remove("SCBL_LAUNCHER_CONTROL");
    if (control) psi.Environment["SCBL_LAUNCHER_CONTROL"]="1";
    return psi;
}
async Task Init() {
    var psi=Info(false); psi.ArgumentList.Add("--init-lan"); psi.ArgumentList.Add("127.0.0.1"); psi.ArgumentList.Add("--smoke-ports");
    using var p=Process.Start(psi) ?? throw new IOException("Init failed to start");
    var so=p.StandardOutput.ReadToEndAsync(); var se=p.StandardError.ReadToEndAsync();
    try {await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));}
    finally {if(!p.HasExited) {p.Kill(false);await p.WaitForExitAsync();}}
    await File.WriteAllTextAsync(Path.Combine(root,"init.stdout.txt"),await so);
    await File.WriteAllTextAsync(Path.Combine(root,"init.stderr.txt"),await se);
    if(p.ExitCode!=0)throw new IOException($"Initializer exit {p.ExitCode}");
}
async Task Expect(bool allowed,string label,Process p) {
    var watch=Stopwatch.StartNew(); Exception? last=null;
    // Package verification runs before listener creation. A reused failed channel
    // also backs off across server restarts; use a fresh connection for readiness.
    while(watch.Elapsed<TimeSpan.FromSeconds(35) && !p.HasExited) {
        try {
            using var channel=GrpcChannel.ForAddress("http://127.0.0.1:15051",new GrpcChannelOptions {HttpHandler=new SocketsHttpHandler {UseProxy=false}});
            var api=new Users.UsersClient(channel);
            var c=await api.GetCapabilitiesAsync(new(),deadline:DateTime.UtcNow.AddSeconds(1));
            if(c.Product=="SCBL LAN" && c.Ready && c.ObserverAllowed==allowed) {Console.WriteLine($"PASS {label}: ready=true observer_allowed={allowed.ToString().ToLowerInvariant()}"); return;}
        } catch(Exception e) {last=e;}
        await Task.Delay(200);
    }
    throw new IOException($"{label}: expected {allowed}; process_exited={p.HasExited}",last);
}
async Task Run(string label,bool control,Func<Process,Task> test) {
    using var p=Process.Start(Info(control)) ?? throw new IOException("Server failed to start");
    var so=p.StandardOutput.ReadToEndAsync();var se=p.StandardError.ReadToEndAsync();
    try { await Expect(false,label+" default",p); await test(p); }
    finally {
        if(!p.HasExited) {p.Kill(false);await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));}
        await File.WriteAllTextAsync(Path.Combine(root,label+".stdout.txt"),await so);
        await File.WriteAllTextAsync(Path.Combine(root,label+".stderr.txt"),await se);
        await File.WriteAllTextAsync(Path.Combine(root,label+".exit.txt"),p.ExitCode+" (owned test process stopped)\n");
    }
}
async Task Send(Process p,string command) { await p.StandardInput.WriteLineAsync(command); await p.StandardInput.FlushAsync(); }
await Init();
await Run("private",true,async p=>{
    await RegisterFixture();
    await Login("normal player with OB closed",false,"11111111111111111111111111111111","scbl-lan/2/game/mod");
    await Login("OB denied while host closed",true,"22222222222222222222222222222222","scbl-lan/2/game/mod",StatusCode.PermissionDenied);
    await Send(p,"OB_ALLOW=1");await Expect(true,"host enabled",p);
    await Login("account UI cannot become OB",true,null,"scbl-lan/1/official",StatusCode.InvalidArgument);
    await Login("bad password cannot obtain OB",true,"22222222222222222222222222222222","scbl-lan/2/game/mod",StatusCode.Unauthenticated,"incorrect-fixture-password");
    await Login("OB requires a native instance",true,null,"scbl-lan/2/game/mod",StatusCode.InvalidArgument);
    await Login("MOD OB gets exact run acknowledgment",true,"22222222222222222222222222222222","scbl-lan/2/game/mod");
    await Login("official OB gets exact run acknowledgment",true,"33333333333333333333333333333333","scbl-lan/2/game/official");
    await Login("ordinary login does not inherit OB",false,"44444444444444444444444444444444","scbl-lan/2/game/mod");
    await Send(p,"OB_ALLOW=0");await Expect(false,"host disabled",p);
    await Login("new OB admission denied after host closes",true,"55555555555555555555555555555555","scbl-lan/2/game/mod",StatusCode.PermissionDenied);
    await Send(p,"OB_ALLOW=1");await Expect(true,"host reenabled",p);
    p.StandardInput.Close();await Expect(false,"controller EOF revokes permission",p);
});
await Run("invalid",true,async p=>{
    await Send(p,"OB_ALLOW=1");await Expect(true,"host enabled before invalid frame",p);
    await Send(p,new string('X',1000));await Expect(false,"oversized frame revokes permission",p);
});
await Run("standalone",false,async p=>{
    await Send(p,"OB_ALLOW=1");await Task.Delay(150);await Expect(false,"standalone ignores control frames",p);
});
Console.WriteLine("PASS production server loopback lifecycle; no game/client character validation claimed");
