namespace Scbl.Server;

public static class RuntimeEnvironment
{
    public static Dictionary<string, string> BuildDedicated(ServerPaths paths, ServerConfiguration config, ServerSecrets secrets)
    {
        return Base(config)
            .With("SCBL_AUTH_TOKEN_KEY", secrets.AuthTokenKeyHex)
            .With("SCBL_GAMEPLAY_MODE_STATE_PATH", paths.GameplayModeState);
    }

    public static Dictionary<string, string> BuildControl(ServerPaths paths, ServerConfiguration config, ServerSecrets secrets, string version)
    {
        return Base(config)
            .With("SCBL_ROOT", Path.Combine(paths.DataRoot, "data"))
            .With("SCBL_CONTROL_PORT", config.ControlPort.ToString())
            .With("SCBL_SECRET", secrets.NetworkSecret)
            .With("SCBL_AUTH_TOKEN_KEY", secrets.AuthTokenKeyHex)
            .With("SCBL_CONTROL_AUTH_POLICY", "required")
            .With("SCBL_DB_PATH", paths.Database)
            .With("SCBL_GAMEPLAY_MODE_STATE_PATH", paths.GameplayModeState)
            .With("SCBL_SERVER_TOOL_VERSION", version)
            .With("SCBL_SERVER_MODE", config.Mode)
            .With("SCBL_HEARTBEAT_TTL", config.HeartbeatTtlSeconds.ToString())
            .With("SCBL_GAMEPLAY_MODE_ASSERTION_TTL", config.ModeAssertionTtlSeconds.ToString());
    }

    public static Dictionary<string, string> BuildUpdate() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["PYTHONUTF8"] = "1",
        ["PYTHONUNBUFFERED"] = "1",
    };

    private static Dictionary<string, string> Base(ServerConfiguration config)
    {
        return new(StringComparer.OrdinalIgnoreCase)
        {
            ["SCBL_SERVER_IP"] = config.VirtualIp,
            ["SCBL_VIRTUAL_NET"] = config.VirtualNetwork,
            ["SCBL_MTU"] = config.Mtu.ToString(),
            ["SCBL_WSS_PORT"] = config.GamePort.ToString(),
            ["SCBL_QUIC_PORT"] = config.GamePort.ToString(),
            ["SCBL_ENABLE_IPV6"] = config.EnableIpv6 ? "y" : "n",
            ["EASYTIER_NETWORK_NAME"] = config.EasyTierNetworkName,
            ["PYTHONUTF8"] = "1",
            ["PYTHONUNBUFFERED"] = "1",
        };
    }

    private static Dictionary<string, string> With(this Dictionary<string, string> values, string key, string value)
    {
        values[key] = value;
        return values;
    }
}
