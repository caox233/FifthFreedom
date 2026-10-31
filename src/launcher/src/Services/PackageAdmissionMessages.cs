namespace SplinterCellCNLauncher.Services;

/// <summary>Explain explicit server admission reasons without changing admission.</summary>
internal static class PackageAdmissionMessages
{
    public static string? ForDetail(string? detail)
    {
        // Match the exact reason or its existing "REASON: explanation" format.
        // A transport message merely containing these words is not admission evidence.
        bool Is(string reason) => detail == reason || detail?.StartsWith(reason + ": ", StringComparison.Ordinal) == true;
        string? reason = Is("RELEASE_MISMATCH") ? "发行版本不同（RELEASE_MISMATCH）。\nDifferent releases."
            : Is("PACKAGE_CATALOG_MISMATCH") ? "完整包目录校验不同（PACKAGE_CATALOG_MISMATCH），即使显示版本相同也不能混用。\nPackage catalogs differ, even if the displayed version is the same."
            : null;
        return reason is null ? null : reason + "\n房主和加入端请使用同一个完整 ZIP：房主启动包内配套服务端，加入端使用包内启动器。保留存档和账号数据，不要只替换 DLL。"
            + "\nHost and joining players must use the same complete ZIP: the host starts its bundled server, and joining players use its launcher. Preserve saves and account data; do not swap individual DLLs.";
    }
}
