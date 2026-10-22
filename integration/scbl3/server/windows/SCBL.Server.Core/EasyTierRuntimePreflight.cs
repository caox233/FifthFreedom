using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;

namespace Scbl.Server;

public static class EasyTierRuntimePreflight
{
    public static async Task RunAsync(
        ServerPaths paths,
        ServerConfiguration config,
        ServerSecrets secrets,
        string executable,
        CancellationToken cancellationToken = default)
    {
        if (!WindowsServiceInstaller.IsAdministrator()) throw new UnauthorizedAccessException("EasyTier 虚拟网卡预检需要管理员权限。");
        string configPath = Path.Combine(paths.DataRoot, "config", "easytier.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        AtomicFile.WriteAllText(configPath, RuntimeTemplates.EasyTier(config, secrets));

        ProcessStartInfo start = new()
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in new[]
        {
            "--config-file", configPath,
            "--rpc-portal", $"127.0.0.1:{config.EasyTierRpcPort}",
            "--console-log-level", "warn",
            "--file-log-level", "off",
        }) start.ArgumentList.Add(argument);

        List<string> output = [];
        using Process process = new() { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, args) => AddBounded(output, args.Data);
        process.ErrorDataReceived += (_, args) => AddBounded(output, args.Data);
        if (!process.Start()) throw new InvalidOperationException("无法启动 EasyTier 虚拟网卡预检。");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            IPAddress expected = IPAddress.Parse(config.VirtualIp);
            DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(25);
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (HasAddress(expected)) return;
                if (process.HasExited)
                {
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                    throw new InvalidOperationException($"EasyTier 虚拟网卡预检失败（exit={process.ExitCode}）。{Tail(output)}");
                }
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
            throw new TimeoutException($"EasyTier 进程仍在运行，但 25 秒内没有建立 {config.VirtualIp}。{Tail(output)}");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            await WaitForAddressRemovalAsync(IPAddress.Parse(config.VirtualIp), CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static bool HasAddress(IPAddress expected) =>
        NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Any(item => item.Address.Equals(expected));

    private static async Task WaitForAddressRemovalAsync(IPAddress address, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTimeOffset.UtcNow < deadline && HasAddress(address))
        {
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }
        if (HasAddress(address)) throw new IOException($"EasyTier 预检结束后虚拟地址 {address} 未及时释放，请稍后重试。");
    }

    private static void AddBounded(List<string> output, string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (output)
        {
            output.Add(line.Trim());
            if (output.Count > 30) output.RemoveAt(0);
        }
    }

    private static string Tail(List<string> output)
    {
        lock (output)
        {
            string result = string.Join(" | ", output.TakeLast(6));
            return result.Length == 0 ? "未返回错误日志。" : result.Length <= 1200 ? result : result[^1200..];
        }
    }
}
