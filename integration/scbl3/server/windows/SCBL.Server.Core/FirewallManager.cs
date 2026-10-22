using System.Runtime.InteropServices;

namespace Scbl.Server;

public static class FirewallManager
{
    private const string Prefix = "SCBL Server";
    private const int Inbound = 1;
    private const int Allow = 1;
    private const int Tcp = 6;
    private const int Udp = 17;
    private const int AllProfiles = int.MaxValue;
    private static readonly string[] RuleNames =
    [
        $"{Prefix} - Game WSS",
        $"{Prefix} - Game QUIC",
        $"{Prefix} - Client updates",
        $"{Prefix} - Control overlay",
    ];

    public static Task ApplyAsync(ServerConfiguration config, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WithRules(rules =>
        {
            RemoveRules(rules, cancellationToken);
            try
            {
                AddRule(rules, RuleNames[0], Tcp, config.GamePort, null);
                AddRule(rules, RuleNames[1], Udp, config.GamePort, null);
                AddRule(rules, RuleNames[2], Tcp, config.UpdatePort, null);
                AddRule(rules, RuleNames[3], Tcp, config.ControlPort, config.VirtualNetwork);
            }
            catch
            {
                try { RemoveRules(rules, CancellationToken.None); }
                catch (Exception cleanup) { throw new AggregateException("添加防火墙规则失败且新增规则未能完整清理。", cleanup); }
                throw;
            }
        });
        return Task.CompletedTask;
    }

    public static Task RemoveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WithRules(rules => RemoveRules(rules, cancellationToken));
        return Task.CompletedTask;
    }

    private static void WithRules(Action<dynamic> action)
    {
        Type policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!;
        object policy = Activator.CreateInstance(policyType) ?? throw new InvalidOperationException("无法打开 Windows 防火墙策略。");
        object? rulesObject = null;
        try
        {
            dynamic policyApi = policy;
            rulesObject = policyApi.Rules;
            action(rulesObject);
        }
        catch (COMException error)
        {
            throw new InvalidOperationException($"Windows 防火墙操作失败，HRESULT=0x{unchecked((uint)error.HResult):X8}。", error);
        }
        finally
        {
            if (rulesObject is not null && Marshal.IsComObject(rulesObject)) Marshal.FinalReleaseComObject(rulesObject);
            if (Marshal.IsComObject(policy)) Marshal.FinalReleaseComObject(policy);
        }
    }

    private static void RemoveRules(dynamic rules, CancellationToken cancellationToken)
    {
        foreach (string name in RuleNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { rules.Remove(name); }
            catch (COMException error) when (unchecked((uint)error.HResult) == 0x80070002u)
            {
                // 删除不存在的规则本来就是成功；HRESULT 不受系统显示语言影响。
            }
        }
    }

    private static void AddRule(dynamic rules, string name, int protocol, int port, string? remoteAddresses)
    {
        Type ruleType = Type.GetTypeFromProgID("HNetCfg.FWRule", throwOnError: true)!;
        object ruleObject = Activator.CreateInstance(ruleType) ?? throw new InvalidOperationException("无法创建 Windows 防火墙规则。");
        try
        {
            dynamic rule = ruleObject;
            rule.Name = name;
            rule.Description = "由 SCBL Windows Server 管理";
            rule.Grouping = Prefix;
            rule.Protocol = protocol;
            rule.LocalPorts = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            rule.Direction = Inbound;
            rule.Enabled = true;
            rule.Profiles = AllProfiles;
            rule.Action = Allow;
            if (!string.IsNullOrWhiteSpace(remoteAddresses)) rule.RemoteAddresses = remoteAddresses;
            try { rules.Add(rule); }
            catch (COMException error)
            {
                throw new InvalidOperationException($"添加防火墙规则失败：{name}，HRESULT=0x{unchecked((uint)error.HResult):X8}。", error);
            }
        }
        finally
        {
            if (Marshal.IsComObject(ruleObject)) Marshal.FinalReleaseComObject(ruleObject);
        }
    }
}
