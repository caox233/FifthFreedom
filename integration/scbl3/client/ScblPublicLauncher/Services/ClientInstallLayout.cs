using System;
using System.IO;

namespace SplinterCellCNLauncher.Services;

/// <summary>
/// Canonical SCBL 3.0 portable-client layout. Runtime code must resolve packaged
/// components through this contract instead of scattering relative paths.
/// </summary>
internal static class ClientInstallLayout
{
    internal const string UpdaterRelativePath = "SCBL.Updater.exe";
    internal const string HooksRelativePath = "components/hooks/uplay_r1_loader.dll";
    internal const string HooksSidecarRelativePath = "components/hooks/uplay_r1_loader.dll.sha256";
    internal const string HooksModDirectoryRelativePath = "components/hooks/mod";
    internal const string EasyTierCoreRelativePath = "components/network/easytier-core.exe";
    internal const string EasyTierCliRelativePath = "components/network/easytier-cli.exe";
    internal const string PacketDllRelativePath = "components/network/Packet.dll";
    internal const string WintunDllRelativePath = "components/network/wintun.dll";
    internal const string RouteGuardExecutableRelativePath = "components/route-guard/scbl-process-router.exe";
    internal const string WinDivertDllRelativePath = "components/route-guard/WinDivert.dll";
    internal const string WinDivertPayloadRelativePath = "components/route-guard/WinDivert64.payload.sys";

    internal static string Resolve(string root, string relativePath)
    {
        string normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string target = Path.GetFullPath(Path.Combine(
            normalizedRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Client component path escaped the install root.");
        return target;
    }
}
