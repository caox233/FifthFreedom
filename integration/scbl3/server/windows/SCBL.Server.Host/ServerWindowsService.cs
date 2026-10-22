using System.ServiceProcess;

namespace Scbl.Server.Host;

public sealed class ServerWindowsService : ServiceBase
{
    private readonly CancellationTokenSource _lifetime = new();
    private ServerRuntime? _runtime;

    public ServerWindowsService()
    {
        ServiceName = WindowsServiceInstaller.ServiceName;
        CanStop = true;
        CanShutdown = true;
        AutoLog = true;
    }

    protected override void OnStart(string[] args)
    {
        RequestAdditionalTime(180_000);
        _runtime = new ServerRuntime(new ServerPaths());
        _runtime.StartAsync(_lifetime.Token).GetAwaiter().GetResult();
    }

    protected override void OnStop()
    {
        RequestAdditionalTime(30_000);
        _lifetime.Cancel();
        if (_runtime is not null)
        {
            _runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _runtime = null;
        }
    }

    protected override void OnShutdown() => OnStop();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _lifetime.Dispose();
        base.Dispose(disposing);
    }
}
