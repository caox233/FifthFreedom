using System;

namespace SplinterCellCNLauncher.Models;

public enum ClientNetworkMode
{
    Fast,
    Compatible
}

public static class ClientNetworkModeNames
{
    public const string Fast = "Fast";
    public const string Compatible = "Compatible";

    public static ClientNetworkMode ParseOrFast(string? value)
        => string.Equals(value?.Trim(), Compatible, StringComparison.OrdinalIgnoreCase)
            ? ClientNetworkMode.Compatible
            : ClientNetworkMode.Fast;

    public static string ToName(ClientNetworkMode mode)
        => mode == ClientNetworkMode.Compatible ? Compatible : Fast;
}
