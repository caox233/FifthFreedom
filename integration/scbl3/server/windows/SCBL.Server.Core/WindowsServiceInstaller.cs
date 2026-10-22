using System.Security.Principal;

namespace Scbl.Server;

public static class WindowsServiceInstaller
{
    public const string ServiceName = "SCBLServer";
    public const string DisplayName = "SCBL Server Host";

    public static bool IsAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static async Task<bool> ExistsAsync(CancellationToken cancellationToken = default)
    {
        CommandResult result = await CommandRunner.RunAsync("sc.exe", ["query", ServiceName], TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        if (result.ExitCode == 0) return true;
        if (IsMissingService(result)) return false;
        result.RequireSuccess("查询 SCBL Windows 服务");
        return false;
    }

    public static async Task InstallAsync(string hostPath, bool startAutomatically, CancellationToken cancellationToken = default)
    {
        RequireAdministrator();
        if (!File.Exists(hostPath)) throw new FileNotFoundException("Host 程序不存在。", hostPath);
        string quotedHost = $"\"{hostPath}\"";
        if (await ExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            CommandResult configure = await CommandRunner.RunAsync("sc.exe",
                ["config", ServiceName, "binPath=", quotedHost, "start=", StartMode(startAutomatically), "DisplayName=", DisplayName],
                TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
            configure.RequireSuccess("更新 SCBL Windows 服务");
        }
        else
        {
            CommandResult create = await CommandRunner.RunAsync("sc.exe",
                ["create", ServiceName, "binPath=", quotedHost, "start=", StartMode(startAutomatically), "DisplayName=", DisplayName],
                TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
            create.RequireSuccess("注册 SCBL Windows 服务");
        }
        (await CommandRunner.RunAsync("sc.exe", ["description", ServiceName, "SCBL 原生 Windows 游戏、控制面与更新服务"], TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false)).RequireSuccess("设置服务说明");
        (await CommandRunner.RunAsync("sc.exe", ["failure", ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/10000/restart/30000"], TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false)).RequireSuccess("设置服务恢复策略");
        (await CommandRunner.RunAsync("sc.exe", ["failureflag", ServiceName, "1"], TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false)).RequireSuccess("启用服务非崩溃恢复");
        (await CommandRunner.RunAsync("sc.exe", ["sidtype", ServiceName, "unrestricted"], TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false)).RequireSuccess("启用 SCBL 服务 SID");
    }

    public static async Task SetStartModeAsync(bool startAutomatically, CancellationToken cancellationToken = default)
    {
        RequireAdministrator();
        if (!await ExistsAsync(cancellationToken).ConfigureAwait(false)) return;
        CommandResult result = await CommandRunner.RunAsync(
            "sc.exe",
            ["config", ServiceName, "start=", StartMode(startAutomatically)],
            TimeSpan.FromSeconds(20),
            cancellationToken).ConfigureAwait(false);
        result.RequireSuccess("设置 SCBL 服务开机启动");
    }

    public static async Task StartAsync(CancellationToken cancellationToken = default)
    {
        RequireAdministrator();
        ServiceState state = await QueryStateAsync(cancellationToken).ConfigureAwait(false);
        if (state == ServiceState.Running) return;
        if (state == ServiceState.StartPending)
        {
            await WaitForStateAsync(ServiceState.Running, TimeSpan.FromSeconds(75), cancellationToken).ConfigureAwait(false);
            return;
        }
        CommandResult result = await CommandRunner.RunAsync("sc.exe", ["start", ServiceName], TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 && !result.Output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase) && !result.Output.Contains("1056", StringComparison.Ordinal)) result.RequireSuccess("启动 SCBL 服务");
        await WaitForStateAsync(ServiceState.Running, TimeSpan.FromSeconds(75), cancellationToken).ConfigureAwait(false);
    }

    public static async Task StopAsync(CancellationToken cancellationToken = default)
    {
        RequireAdministrator();
        ServiceState state = await QueryStateAsync(cancellationToken).ConfigureAwait(false);
        if (state is ServiceState.Missing or ServiceState.Stopped) return;
        if (state == ServiceState.StopPending)
        {
            await WaitForStateAsync(ServiceState.Stopped, TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false);
            return;
        }
        CommandResult result = await CommandRunner.RunAsync("sc.exe", ["stop", ServiceName], TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 && !result.Output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase) && !result.Output.Contains("1062", StringComparison.Ordinal)) result.RequireSuccess("停止 SCBL 服务");
        await WaitForStateAsync(ServiceState.Stopped, TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false);
    }

    public static async Task UninstallAsync(CancellationToken cancellationToken = default)
    {
        RequireAdministrator();
        if (!await ExistsAsync(cancellationToken).ConfigureAwait(false)) return;
        await StopAsync(cancellationToken).ConfigureAwait(false);
        (await CommandRunner.RunAsync("sc.exe", ["delete", ServiceName], TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false)).RequireSuccess("删除 SCBL 服务");
        await WaitForStateAsync(ServiceState.Missing, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
    }

    public static async Task<ServiceState> QueryStateAsync(CancellationToken cancellationToken = default)
    {
        CommandResult result = await CommandRunner.RunAsync("sc.exe", ["query", ServiceName], TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            if (IsMissingService(result)) return ServiceState.Missing;
            result.RequireSuccess("查询 SCBL Windows 服务状态");
        }
        return ParseStateOutput(result.Output);
    }

    private static bool IsMissingService(CommandResult result) =>
        result.ExitCode == 1060 ||
        result.Output.Contains("1060", StringComparison.Ordinal) ||
        result.Error.Contains("1060", StringComparison.Ordinal) ||
        result.Output.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ||
        result.Error.Contains("does not exist", StringComparison.OrdinalIgnoreCase);

    public static ServiceState ParseStateOutput(string output)
    {
        foreach (string line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = line.IndexOf(':');
            if (colon < 0) continue;
            string value = line[(colon + 1)..].TrimStart();
            string numeric = new(value.TakeWhile(char.IsDigit).ToArray());
            if (!int.TryParse(numeric, out int code) || code is < 1 or > 7) continue;
            return code switch
            {
                1 => ServiceState.Stopped,
                2 => ServiceState.StartPending,
                3 => ServiceState.StopPending,
                4 => ServiceState.Running,
                _ => ServiceState.Other,
            };
        }
        throw new InvalidDataException("无法解析 SCBL Windows 服务状态。");
    }

    private static async Task WaitForStateAsync(ServiceState expected, TimeSpan timeout, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        ServiceState last = ServiceState.Other;
        while (DateTimeOffset.UtcNow < deadline)
        {
            last = await QueryStateAsync(cancellationToken).ConfigureAwait(false);
            if (last == expected) return;
            if (expected == ServiceState.Running && last is ServiceState.Stopped or ServiceState.Missing)
            {
                throw new InvalidOperationException($"SCBL 服务启动失败，当前状态：{last}。");
            }
            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException($"SCBL 服务未在规定时间内进入 {expected}，最后状态：{last}。");
    }

    private static void RequireAdministrator()
    {
        if (!IsAdministrator()) throw new UnauthorizedAccessException("此操作需要以管理员身份运行 SCBL.Server.exe。");
    }

    private static string StartMode(bool startAutomatically) => startAutomatically ? "auto" : "demand";
}

public enum ServiceState
{
    Missing,
    Stopped,
    StartPending,
    StopPending,
    Running,
    Other,
}
