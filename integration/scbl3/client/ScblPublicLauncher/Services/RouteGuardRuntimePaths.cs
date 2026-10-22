using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace SplinterCellCNLauncher.Services;

/// <summary>
/// Resolves the privileged Route Guard runtime independently from the portable
/// client tree. ProgramData is used because WinDivert is machine-wide and the
/// binaries must not be loaded from a user-editable or network-shared install root.
/// </summary>
internal static class RouteGuardRuntimePaths
{
    internal const string ReceiptPrefix = "@route-guard/";

    internal static string RootDirectory
    {
        get
        {
            string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (string.IsNullOrWhiteSpace(common))
                throw new InvalidOperationException("The system ProgramData directory is unavailable.");
            return Path.Combine(Path.GetFullPath(common), "SCBL", "RouteGuard");
        }
    }

    internal static string RouterExecutablePath => ResolveFile("scbl-process-router.exe");
    internal static string WinDivertDllPath => ResolveFile("WinDivert.dll");
    internal static string WinDivertDriverPath => ResolveFile("WinDivert64.sys");

    internal static string ResolveFile(string fileName, string? rootOverride = null)
    {
        if (string.IsNullOrWhiteSpace(fileName)
            || Path.GetFileName(fileName) != fileName
            || fileName is "." or "..")
        {
            throw new InvalidDataException("Invalid Route Guard runtime file name.");
        }

        string root = Path.GetFullPath(rootOverride ?? RootDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string target = Path.GetFullPath(Path.Combine(root, fileName));
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Route Guard runtime path escaped its controlled root.");
        return target;
    }

    internal static void EnsureControlledDirectory(string? rootOverride = null, bool configureAcl = true)
    {
        string root = Path.GetFullPath(rootOverride ?? RootDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (rootOverride == null)
        {
            string programData = Path.GetFullPath(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string scblRoot = Path.Combine(programData, "SCBL");
            Directory.CreateDirectory(scblRoot);
            RejectReparsePoint(programData);
            RejectReparsePoint(scblRoot);
        }
        Directory.CreateDirectory(root);
        RejectReparsePoint(root);

        if (!configureAcl || !OperatingSystem.IsWindows())
            return;

        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        const InheritanceFlags inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(administrators);
        security.AddAccessRule(new FileSystemAccessRule(
            administrators,
            FileSystemRights.FullControl,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            system,
            FileSystemRights.FullControl,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            users,
            FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(root).SetAccessControl(security);
    }

    internal static void RejectReparsePoint(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Route Guard controlled path is a reparse point.");
    }

    internal static bool IsUnderControlledRoot(string path, string? rootOverride = null)
    {
        string root = Path.GetFullPath(rootOverride ?? RootDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string full = Path.GetFullPath(path);
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
