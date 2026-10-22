using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Scbl.Server.Host;

public sealed class ServerRuntime : IAsyncDisposable
{
    private readonly ServerPaths _paths;
    private readonly ServerLog _log;
    private readonly List<ManagedProcess> _processes = [];
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private PipeServer? _pipe;
    private bool _running;
    private ServerConfiguration? _config;
    private string _version = "0.0.0";

    public ServerRuntime(ServerPaths paths)
    {
        _paths = paths;
        _paths.EnsureLayout();
        _log = new ServerLog(paths);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_running) return;
            _config = ServerConfiguration.LoadOrCreate(_paths);
            ServerSecrets secrets = ServerSecrets.Load(_paths);
            string release = _paths.RequireCurrentRelease();
            ReleasePointer pointer = AtomicJson.Read<ReleasePointer>(_paths.CurrentReleasePointer)!;
            _version = pointer.Version;
            PrepareRuntimeFiles(release, _config, secrets);
            TryRefreshServerDescriptor(_config, secrets);
            Dictionary<string, string> dedicatedEnvironment = RuntimeEnvironment.BuildDedicated(_paths, _config, secrets);
            Dictionary<string, string> controlEnvironment = RuntimeEnvironment.BuildControl(_paths, _config, secrets, _version);
            Dictionary<string, string> updateEnvironment = RuntimeEnvironment.BuildUpdate();

            ExternalComponentManager external = new(_paths, _log);
            string easyTier = await external.EnsureEasyTierAsync(cancellationToken).ConfigureAwait(false);
            AddProcess(new ManagedProcessSpec("easytier", easyTier,
            [
                "--config-file", Path.Combine(_paths.DataRoot, "config", "easytier.toml"),
                "--rpc-portal", $"127.0.0.1:{_config.EasyTierRpcPort}",
                "--console-log-level", "warn", "--file-log-level", "off"
            ], _paths.EasyTier));
            await _processes[^1].StartAsync(cancellationToken).ConfigureAwait(false);
            await WaitForAddressAsync(_config.VirtualIp, TimeSpan.FromSeconds(40), cancellationToken).ConfigureAwait(false);
            await EnableForwardingAsync(_config.VirtualIp, cancellationToken).ConfigureAwait(false);

            string dedicatedRoot = Path.Combine(_paths.DataRoot, "data", "dedicated");
            AddProcess(new ManagedProcessSpec("dedicated", Path.Combine(release, "dedicated", "SCBL.Dedicated.exe"),
                ["--config", Path.Combine(_paths.DataRoot, "config", "dedicated.toml")], dedicatedRoot, dedicatedEnvironment));
            AddProcess(new ManagedProcessSpec("control", Path.Combine(release, "python", "python.exe"),
                [Path.Combine(release, "control", "scbl_control_plane.py")], Path.Combine(_paths.DataRoot, "data"), controlEnvironment));
            AddProcess(new ManagedProcessSpec("update", Path.Combine(release, "python", "python.exe"),
            [
                Path.Combine(release, "update", "scbl_update_server.py"),
                "--root", _paths.ClientUpdates,
                "--bundle-cache", _paths.ClientBundleCache,
                "--port", _config.UpdatePort.ToString(),
                "--ipv6", _config.EnableIpv6 ? "y" : "n"
            ], _paths.ClientRoot, updateEnvironment));

            foreach (ManagedProcess process in _processes.Skip(1)) await process.StartAsync(cancellationToken).ConfigureAwait(false);
            await WaitForTcpAsync(IPAddress.Parse(_config.VirtualIp), _config.ControlPort, TimeSpan.FromSeconds(25), cancellationToken).ConfigureAwait(false);
            await WaitForTcpAsync(IPAddress.Loopback, _config.UpdatePort, TimeSpan.FromSeconds(25), cancellationToken).ConfigureAwait(false);
            await WaitForCoreProcessStabilityAsync(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);

            if (_config.DdnsEnabled)
            {
                try
                {
                    string ddns = await external.EnsureDdnsGoAsync(cancellationToken).ConfigureAwait(false);
                    string[] arguments =
                    [
                        "-l", $"127.0.0.1:{_config.DdnsManagementPort}",
                        "-c", Path.Combine(_paths.DataRoot, "config", "ddns-go.yaml"),
                    ];
                    AddProcess(new ManagedProcessSpec("ddns-go", ddns, arguments, _paths.DdnsGo));
                    await _processes[^1].StartAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    _log.Write("ddns-go", "optional component unavailable: " + error.Message);
                }
            }

            _pipe = new PipeServer(this, _log);
            _pipe.Start();
            _running = true;
            _log.Write("host", $"SCBL Windows Server {_version} started mode={_config.Mode}");
        }
        catch (Exception original)
        {
            try { await StopProcessesAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception cleanup) { throw new AggregateException("服务启动失败且子进程未能完整停止。", original, cleanup); }
            throw;
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_pipe is not null) { await _pipe.DisposeAsync().ConfigureAwait(false); _pipe = null; }
            await StopProcessesAsync(cancellationToken).ConfigureAwait(false);
            _running = false;
            _log.Write("host", "SCBL Windows Server stopped");
        }
        finally { _lifecycle.Release(); }
    }

    public ServerStatus Status()
    {
        ServerConfiguration config = _config ?? ServerConfiguration.LoadOrCreate(_paths);
        bool healthy = _running && _processes.Where(process => process.Name is "easytier" or "dedicated" or "control" or "update").All(process => process.IsRunning);
        return new ServerStatus(healthy, _version, config.Mode, config.ServerId, config.PublicHost, config.GamePort, config.UpdatePort, config.ControlPort,
            _processes.Select(process => new ComponentStatus(process.Name, process.IsRunning, process.ProcessId, process.RestartCount, process.StartedAtUtc)).ToArray(), DateTimeOffset.UtcNow);
    }

    public IReadOnlyList<string> RecentLogs(int count) => _log.Recent(count);

    private void PrepareRuntimeFiles(string release, ServerConfiguration config, ServerSecrets secrets)
    {
        Directory.CreateDirectory(Path.Combine(_paths.DataRoot, "config"));
        string easyTierConfig = Path.Combine(_paths.DataRoot, "config", "easytier.toml");
        string dedicatedConfig = Path.Combine(_paths.DataRoot, "config", "dedicated.toml");
        AtomicFile.WriteAllText(easyTierConfig, RuntimeTemplates.EasyTier(config, secrets));
        AtomicFile.WriteAllText(dedicatedConfig, RuntimeTemplates.Dedicated(config, Convert.FromHexString(secrets.TicketKeyHex)));
        string sourceAsset = Path.Combine(release, "assets", "mp_balancing.ini");
        string targetAsset = Path.Combine(_paths.DataRoot, "data", "dedicated", "data", "mp_balancing.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(targetAsset)!);
        if (!File.Exists(sourceAsset)) throw new FileNotFoundException("当前 Runtime 缺少 mp_balancing.ini。", sourceAsset);
        if (!File.Exists(targetAsset) || !Hashing.FixedEqualsHex(Hashing.Sha256(sourceAsset), Hashing.Sha256(targetAsset))) AtomicFile.Copy(sourceAsset, targetAsset);
    }

    private void AddProcess(ManagedProcessSpec spec) => _processes.Add(new ManagedProcess(spec, _log));

    private void TryRefreshServerDescriptor(ServerConfiguration config, ServerSecrets secrets)
    {
        try
        {
            string path = new ServerDescriptorPublisher(_paths).Publish(config, secrets);
            _log.Write("descriptor", "published " + path);
        }
        catch (FileNotFoundException error)
        {
            _log.Write("descriptor", "client baseline is not installed; descriptor not published: " + error.Message);
        }
        catch (Exception error)
        {
            _log.Write("descriptor", "refresh rejected; previous descriptor retained: " + error.Message);
        }
    }

    private async Task StopProcessesAsync(CancellationToken cancellationToken)
    {
        List<Exception> errors = [];
        foreach (ManagedProcess process in _processes.AsEnumerable().Reverse())
        {
            try { await process.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { _log.Write(process.Name, "stop failed: " + error.Message); errors.Add(error); }
        }
        _processes.Clear();
        if (errors.Count != 0) throw new AggregateException("一个或多个服务端子进程未能停止。", errors);
    }

    private static async Task WaitForAddressAsync(string address, TimeSpan timeout, CancellationToken cancellationToken)
    {
        IPAddress expected = IPAddress.Parse(address);
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            bool found = NetworkInterface.GetAllNetworkInterfaces().SelectMany(item => item.GetIPProperties().UnicastAddresses).Any(item => item.Address.Equals(expected));
            if (found) return;
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException($"EasyTier 未在规定时间内建立 {address}。");
    }

    private static async Task EnableForwardingAsync(string address, CancellationToken cancellationToken)
    {
        IPAddress expected = IPAddress.Parse(address);
        NetworkInterface adapter = NetworkInterface.GetAllNetworkInterfaces()
            .Single(item => item.GetIPProperties().UnicastAddresses.Any(value => value.Address.Equals(expected)));
        CommandResult result = await CommandRunner.RunAsync(
            "netsh.exe",
            ["interface", "ipv4", "set", "interface", $"name={adapter.Name}", "forwarding=enabled"],
            TimeSpan.FromSeconds(15),
            cancellationToken).ConfigureAwait(false);
        result.RequireSuccess("启用 EasyTier IPv4 转发");
    }

    private static async Task WaitForTcpAsync(IPAddress address, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            using TcpClient client = new(address.AddressFamily);
            try { await client.ConnectAsync(address, port, cancellationToken).ConfigureAwait(false); return; }
            catch (SocketException) { await Task.Delay(400, cancellationToken).ConfigureAwait(false); }
        }
        throw new TimeoutException($"端口 {address}:{port} 未在规定时间内就绪。");
    }

    private async Task WaitForCoreProcessStabilityAsync(TimeSpan stableFor, TimeSpan timeout, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        DateTimeOffset? stableSince = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            bool healthy = _processes.Where(process => process.Name is "easytier" or "dedicated" or "control" or "update").All(process => process.IsRunning);
            if (healthy)
            {
                stableSince ??= DateTimeOffset.UtcNow;
                if (DateTimeOffset.UtcNow - stableSince >= stableFor) return;
            }
            else stableSince = null;
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
        throw new InvalidOperationException("核心服务未能保持稳定运行。");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _lifecycle.Dispose();
    }
}
