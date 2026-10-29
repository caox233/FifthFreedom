using System.Security.Cryptography;
using System.Text.Json;
using SplinterCellCNLauncher.Models;
using SplinterCellCNLauncher.Services;

int passed=0;
void Check(bool value,string name) { if(!value)throw new InvalidOperationException(name);Console.WriteLine("PASS "+name);passed++; }
foreach(string json in new[]{"{}","{\"NetworkEnvironment\":null}","{\"NetworkEnvironment\":\"invalid\"}"})
    Check(LauncherSettingsCodec.Deserialize(json).NetworkEnvironment=="Public","missing or invalid deployment preference defaults Public");
var settings=LauncherSettingsCodec.Deserialize("{\"NetworkEnvironment\":\"Lan\",\"NetworkMode\":\"Compatible\",\"ServerAddress\":\"192.0.2.1:50051\",\"PublicEndpoint\":\"example.com:11010\",\"PublicUpdatePort\":18080,\"TestPublicEndpoint\":\"test.example.com:11011\",\"TestPublicUpdatePort\":18081}");
settings.RememberPassword=false;settings.Password="fixture-never-persist";
var serialized=LauncherSettingsCodec.Serialize(settings);var roundtrip=LauncherSettingsCodec.Deserialize(serialized);
Check(roundtrip.NetworkEnvironment=="Lan"&&roundtrip.NetworkMode=="Compatible"&&roundtrip.ServerAddress==settings.ServerAddress&&roundtrip.PublicEndpoint==settings.PublicEndpoint&&roundtrip.TestPublicUpdatePort==18081,"explicit deployment transport and both server settings survive persistence independently");
Check(!serialized.Contains("fixture-never-persist")&&!roundtrip.RememberPassword,"password omitted and explicit remember choice retained");
foreach(string old in new[]{"Blacklist_game.exe","Blacklist_DX9_game.exe","DX9"})
 Check(LauncherSettingsCodec.Deserialize(JsonSerializer.Serialize(new{GameExecutable=old})).GameExecutable=="Blacklist_DX11_game.exe","legacy renderer migrates before any launch read: "+old);
settings.FovScale=double.NaN;Check(LauncherSettingsCodec.Deserialize(LauncherSettingsCodec.Serialize(settings)).FovScale==1.2&&double.IsNaN(settings.FovScale),"normalization does not mutate caller before persistence success");
Check(PublicServerConfiguration.TryValidate("wss://Example.com:11010","18080",out var server,out _)&&server!.PublicEndpoint=="example.com:11010"&&server.UpdatePort==18080,"donor endpoint and independent update port normalize without connecting");
Check(PublicServerConfiguration.TryValidate("[2001:db8::1]:11010","18080",out _,out _),"explicit bracketed IPv6 server endpoint accepted");
foreach(string bad in new[]{"", "https://user:secret@example.com:11010", "example.com:11010/path", "example.com:11010?token=fixture", "example.com:11010\\path", "example.com"})
 Check(!PublicServerConfiguration.TryValidate(bad,"18080",out _,out _),"invalid or implicit server authority rejected");
Check(!PublicServerConfiguration.TryValidate("example.com:11010","65536",out _,out var error)&&error==PublicServerSettingsError.UpdatePortInvalid,"invalid update port rejected independently");
var pubSettings=new LauncherSettings();var adapter=new PublicNetworkEnvironmentAdapter(()=>pubSettings);
Check(adapter.Assess().Stage==PublicReadinessStage.ServerConfigurationRequired&&!adapter.CurrentReadiness.IsReady,"empty Public settings report a specific configuration prerequisite");
pubSettings.PublicEndpoint="public.example.invalid:11010";Check(adapter.Assess().Stage==PublicReadinessStage.ServerConfigurationRequired,"inert example is not a configured deployment");
pubSettings.PublicEndpoint="example.com:11010";Check(adapter.Assess().Stage==PublicReadinessStage.DeploymentConfigurationRequired,"server address alone is not deployment/runtime admission");
var lan=new OwnedLanAdapter();bool gameActive=false;
await using(var controller=new NetworkEnvironmentController(lan,()=>new(gameActive,false),adapter))
{
 await controller.ChangeAsync(NetworkEnvironmentSelection.ForLan(LanRole.Join));
 var result=await controller.ChangeAsync(NetworkEnvironmentSelection.ForPublic(PublicTransport.Fast));
 Check(result.Outcome==NetworkEnvironmentChangeOutcome.Applied&&result.Snapshot.Selection?.Environment==NetworkEnvironmentKind.Public&&!result.Snapshot.IsReady&&lan.Last!.Disposed,"Public selection drains prior LAN lease while keeping not-ready state");
 gameActive=true;
 Check((await controller.ChangeAsync(NetworkEnvironmentSelection.ForLan(LanRole.Join))).Outcome==NetworkEnvironmentChangeOutcome.RejectedGameActive&&controller.Snapshot.Selection!.Environment==NetworkEnvironmentKind.Public,"active game rejects mode change without publishing another selection");
 gameActive=false;
 await controller.ChangeAsync(NetworkEnvironmentSelection.ForLan(LanRole.Join));
 Check(controller.Snapshot.Selection!.Environment==NetworkEnvironmentKind.Lan&&controller.Snapshot.IsReady,"explicit return to LAN uses new LAN capabilities");
}
string temp=Path.Combine(Path.GetTempPath(),"ff-public-config-fixture-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
try
{
 var pins=new List<object>();
 foreach(var (role,name) in new[]{("Core","easytier-core.exe"),("Cli","easytier-cli.exe"),("Packet","Packet.dll"),("Wintun","wintun.dll")})
 {
  byte[] bytes=System.Text.Encoding.ASCII.GetBytes("owned non-executable fixture "+role);File.WriteAllBytes(Path.Combine(temp,name),bytes);
  pins.Add(new {role,bytes=bytes.Length,sha256=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()});
 }
 var declaration=new {schema=1,deployment_id="fixture",control_origin="https://example.com",overlay_cidr="10.66.0.0/24",server_overlay_ip="10.66.0.1",transport="Fast",peer_uri="quic://example.com:11010",runtime=new {version="fixture",directory=temp,pins}};
 string config=Path.Combine(temp,"profile.json");File.WriteAllText(config,JsonSerializer.Serialize(declaration));pubSettings.PublicDeploymentConfigurationPath=config;
 var read=adapter.Assess();
 Check(read.DeploymentConfigured&&read.RuntimeFilesVerified&&read.Stage==PublicReadinessStage.OwnedNetworkAdmissionRequired&&!read.IsReady,"real profile and all four file hashes verify but never grant Windows owned-network/native-session readiness");
 pubSettings.NetworkMode="Compatible";Check(adapter.Assess().Stage==PublicReadinessStage.DeploymentConfigurationInvalid,"changed donor transport cannot reuse incompatible deployment profile");pubSettings.NetworkMode="Fast";
 File.AppendAllText(Path.Combine(temp,"easytier-core.exe"),"changed");Check(adapter.Assess().Stage==PublicReadinessStage.RuntimeInputsInvalid,"changed runtime file fails actual SHA/size verification");
 var declarationJson=JsonSerializer.Serialize(declaration);File.WriteAllText(config,declarationJson.Replace("\"schema\":1","\"schema\":1,\"password\":\"not-allowed\""));
 Check(adapter.Assess().Stage==PublicReadinessStage.DeploymentConfigurationInvalid,"unknown secret-like config field rejected before component access");
 File.WriteAllText(config,declarationJson.Replace("\"schema\":1","\"schema\":1,\"schema\":1"));Check(adapter.Assess().Stage==PublicReadinessStage.DeploymentConfigurationInvalid,"duplicate deployment fields rejected");
}
finally {Directory.Delete(temp,true);}
Console.WriteLine($"RESULT {passed} network preference/configuration checks passed; no network, executable, driver, firewall or game started.");
sealed class OwnedLanAdapter:INetworkEnvironmentAdapter
{
 public NetworkEnvironmentKind Environment=>NetworkEnvironmentKind.Lan;public bool IsAvailable=>true;public Lease? Last;
 public Task<INetworkEnvironmentLease> ConnectAsync(NetworkEnvironmentSelection selection,CancellationToken token){token.ThrowIfCancellationRequested();Last=new();return Task.FromResult<INetworkEnvironmentLease>(Last);}
 public sealed class Lease:INetworkEnvironmentLease {public bool Disposed;public bool IsReady=>!Disposed;public long? LatencyMilliseconds=>null;public ValueTask DisposeAsync(){Disposed=true;return ValueTask.CompletedTask;}}
}
