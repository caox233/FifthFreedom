// Controlled in-memory transport only. The production AuthService and result
// models execute unchanged; no DNS, socket, server, credentials or game is used.
using Grpc.Core;
namespace ScblLan
{
    public sealed class LoginRequest { public string Username="",Password="",ClientProduct=""; public object? PackageIdentity; }
    public sealed class RegisterRequest { public string Username="",Password="",UbiId="",ClientProduct=""; public object? PackageIdentity; }
    public sealed class User { public string Id="fixture-account"; }
    public sealed class LoginResponse { public string Error=""; public User? User=new(); }
    public sealed class RegisterResponse { public string Error="",RecoveryCode="fixture-recovery"; }
    public sealed class CapabilityResponse { public bool Ready=true; }
    public sealed class ApiFixture
    {
        public Task<LoginResponse> LoginAsync(LoginRequest request,DateTime deadline) => Fixture.Error is not null
            ? Task.FromException<LoginResponse>(Fixture.Error) : Task.FromResult(new LoginResponse {Error=Fixture.ResponseError});
        public Task<RegisterResponse> RegisterAsync(RegisterRequest request,DateTime deadline) => Fixture.Error is not null
            ? Task.FromException<RegisterResponse>(Fixture.Error) : Task.FromResult(new RegisterResponse {Error=Fixture.ResponseError});
    }
    public static class Fixture
    {
        public static Exception? Error,ConnectionError;
        public static string ResponseError="";
        public static int Connections;
        public static void Reset() { Error=ConnectionError=null;ResponseError="";Connections=0; }
    }
    public sealed class LanConnection:IDisposable
    {
        public const string ProductContract="scbl-lan/1/official";
        public CapabilityResponse Capabilities {get;}=new();
        public ApiFixture Api {get;}=new();
        public static Task<LanConnection> ConnectAsync(string address,string bind,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();Fixture.Connections++;
            return Fixture.ConnectionError is null?Task.FromResult(new LanConnection()):Task.FromException<LanConnection>(Fixture.ConnectionError);
        }
        public void Dispose() { }
    }
}
namespace SplinterCellCNLauncher.Services
{
    public sealed class ReleasePackage
    {
        public static ReleasePackage Current()=>new();
        public object Declaration()=>new object();
    }
    public static class LogService { public static void Error(string message) { } }
}
namespace SplinterCellCNLauncher.Network
{
    public sealed record ServerAddress(string Host,int Port)
    {
        public static ServerAddress Parse(string value)=>new("fixture.example.invalid",50051);
    }
}
