using System.Security.AccessControl;
using System.Security.Principal;

namespace Scbl.Server;

public static class WindowsSecurity
{
    private static readonly InheritanceFlags TreeInheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    public static void SecureRoots(ServerPaths paths)
    {
        if (!OperatingSystem.IsWindows()) return;
        RestrictDirectory(paths.ProgramRoot);
        RestrictDirectory(paths.DataRoot);
        foreach (string file in new[]
        {
            paths.Config,
            paths.Secrets,
            paths.CurrentReleasePointer,
            paths.PreviousReleasePointer,
            paths.Database,
            paths.GameplayModeState,
        })
        {
            if (File.Exists(file)) RestrictFile(file);
        }
    }

    public static void RestrictDirectory(string path)
    {
        if (!OperatingSystem.IsWindows() || !Directory.Exists(path)) return;
        DirectorySecurity security = new();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (SecurityIdentifier identity in AllowedIdentities())
        {
            security.AddAccessRule(new FileSystemAccessRule(
                identity,
                FileSystemRights.FullControl,
                TreeInheritance,
                PropagationFlags.None,
                AccessControlType.Allow));
        }
        new DirectoryInfo(path).SetAccessControl(security);
    }

    public static void RestrictFile(string path)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path)) return;
        FileSecurity security = new();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (SecurityIdentifier identity in AllowedIdentities())
        {
            security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl, AccessControlType.Allow));
        }
        new FileInfo(path).SetAccessControl(security);
    }

    private static IEnumerable<SecurityIdentifier> AllowedIdentities()
    {
        yield return new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        yield return new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        using WindowsIdentity current = WindowsIdentity.GetCurrent();
        if (current.User is not null) yield return current.User;
        SecurityIdentifier? service = TryResolveServiceSid();
        if (service is not null) yield return service;
    }

    private static SecurityIdentifier? TryResolveServiceSid()
    {
        try
        {
            return (SecurityIdentifier)new NTAccount($"NT SERVICE\\{WindowsServiceInstaller.ServiceName}").Translate(typeof(SecurityIdentifier));
        }
        catch (IdentityNotMappedException) { return null; }
    }
}
