using System.Buffers.Binary;
using System.Net;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SplinterCellCNLauncher.Network;

// Fresh owned loopback HTTP/2 gRPC server. Port 80 is needed by the actual retained
// configuration rule; inability to bind is a real failure, never a successful skip.
internal sealed class LoopbackApi : IAsyncDisposable
{
    private readonly WebApplication app;
    internal string Address { get; private set; } = "";
    internal string ReturnedAccount { get; set; } = "synthetic-account";
    internal string ReturnedUsername { get; set; } = "initial-returned";
    internal int Logins { get; private set; }
    internal string? LastLoginLocalAddress { get; private set; }
    internal int LastLoginLocalPort { get; private set; }
    internal string? LastLoginPeerAddress { get; private set; }
    internal int LastLoginPeerPort { get; private set; }
    internal LoginRequest? LastLoginRequest { get; private set; }
    private LoopbackApi(WebApplication app) => this.app = app;
    internal static async Task<LoopbackApi> Start()
    {
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => {
            options.Listen(IPAddress.Loopback, 0, listener => listener.Protocols = HttpProtocols.Http2);
            options.Listen(IPAddress.Loopback, 80, listener => listener.Protocols = HttpProtocols.Http1);
        });
        var app = builder.Build(); var server = new LoopbackApi(app);
        app.Run(server.Handle);
        try
        {
            await app.StartAsync();
            server.Address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
                .Single(address => new Uri(address).Port != 80)).Authority;
            return server;
        }
        catch { await app.DisposeAsync(); throw; }
    }
    private async Task Handle(HttpContext context)
    {
        if (context.Request.Method == "GET") { context.Response.StatusCode = 200; return; }
        context.Response.ContentType = "application/grpc"; context.Response.DeclareTrailer("grpc-status");
        byte[] prefix = new byte[5]; await context.Request.Body.ReadExactlyAsync(prefix, context.RequestAborted);
        int size = BinaryPrimitives.ReadInt32BigEndian(prefix.AsSpan(1));
        if (prefix[0] != 0 || size is < 0 or > 16384) throw new IOException("Invalid synthetic request framing");
        byte[] input = new byte[size]; await context.Request.Body.ReadExactlyAsync(input, context.RequestAborted);
        IMessage response;
        if (context.Request.Path.Value!.EndsWith("/GetCapabilities", StringComparison.Ordinal))
            response = new CapabilitiesResponse { Product = "SCBL LAN", ProtocolVersion = 1, Ready = true, ConfigPort = 80,
                ContentPort = 80, AuthPort = 26000, SecurePort = 26001, OfficialOnly = false };
        else if (context.Request.Path.Value.EndsWith("/Login", StringComparison.Ordinal))
        {
            LastLoginRequest = LoginRequest.Parser.ParseFrom(input); Logins++;
            LastLoginLocalAddress = context.Connection.LocalIpAddress!.ToString(); LastLoginLocalPort = context.Connection.LocalPort;
            LastLoginPeerAddress = context.Connection.RemoteIpAddress!.ToString(); LastLoginPeerPort = context.Connection.RemotePort;
            response = new LoginResponse { Token = "synthetic-account-token-not-observed", User = new User { Id = ReturnedAccount, Username = ReturnedUsername } };
        }
        else throw new IOException("Unexpected synthetic method");
        byte[] output = response.ToByteArray(); prefix[0] = 0; BinaryPrimitives.WriteInt32BigEndian(prefix.AsSpan(1), output.Length);
        await context.Response.Body.WriteAsync(prefix, context.RequestAborted); await context.Response.Body.WriteAsync(output, context.RequestAborted);
        context.Response.AppendTrailer("grpc-status", "0");
    }
    public async ValueTask DisposeAsync()
    { using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await app.StopAsync(stop.Token); await app.DisposeAsync(); }
}
