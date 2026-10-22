using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using SplinterCellCNLauncher.ApplicationLayer;

namespace SplinterCellCNLauncher.Services;

public sealed class WinDivertBootstrapService
{
    internal enum ServiceRegistrationAction
    {
        Keep,
        DeleteStale,
        FailClosed
    }

    private const string ServiceName = "WinDivert";
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint DeleteAccess = 0x00010000;
    private const uint ServiceStopped = 0x00000001;
    private const int ErrorServiceDoesNotExist = 1060;
    private const int ErrorServiceMarkedForDelete = 1072;

    private readonly LauncherRuntimeContext _runtimeContext;

    public WinDivertBootstrapService(LauncherRuntimeContext runtimeContext)
    {
        _runtimeContext = runtimeContext ?? throw new ArgumentNullException(nameof(runtimeContext));
    }

    public const string PayloadRelativePath = ClientInstallLayout.WinDivertPayloadRelativePath;

    public async Task<bool> EnsureCurrentDriverAsync()
    {
        try
        {
            await Task.Run(() => StagedComponentBootstrapService.ApplyRouteGuard(
                _runtimeContext.UpdateChannelName)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            LogService.Error("Staged Route Guard component application failed: " + ex.Message);
        }

        if (!ControlledRuntimeIsComplete())
        {
            LogService.Error("Route Guard controlled runtime is incomplete; reinstall the current component.");
            return false;
        }
        return EnsureServiceRegistrationReady();
    }

    private static bool ControlledRuntimeIsComplete()
        => File.Exists(RouteGuardRuntimePaths.RouterExecutablePath)
            && File.Exists(RouteGuardRuntimePaths.WinDivertDllPath)
            && File.Exists(RouteGuardRuntimePaths.WinDivertDriverPath);

    internal static bool EnsureServiceRegistrationReady()
    {
        if (!OperatingSystem.IsWindows())
            return true;

        IntPtr manager = IntPtr.Zero;
        IntPtr service = IntPtr.Zero;
        try
        {
            manager = OpenSCManager(null, null, ScManagerConnect);
            if (manager == IntPtr.Zero)
                throw new InvalidOperationException(
                    $"Unable to open the Windows service manager. error={Marshal.GetLastWin32Error()}");

            service = OpenService(manager, ServiceName, ServiceQueryStatus | DeleteAccess);
            if (service == IntPtr.Zero)
            {
                int openError = Marshal.GetLastWin32Error();
                if (openError == ErrorServiceDoesNotExist)
                    return true;
                if (openError == ErrorServiceMarkedForDelete)
                    return WaitForServiceDeletion(manager);
                throw new InvalidOperationException(
                    $"Unable to inspect the WinDivert service registration. error={openError}");
            }

            if (!QueryServiceStatus(service, out ServiceStatus status))
                throw new InvalidOperationException(
                    $"Unable to query the WinDivert service state. error={Marshal.GetLastWin32Error()}");
            string? registeredPath = ReadRegisteredDriverPath();
            ServiceRegistrationAction action = DecideServiceRegistrationAction(
                registeredPath,
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                status.CurrentState == ServiceStopped,
                File.Exists);
            if (action == ServiceRegistrationAction.Keep)
                return true;
            if (action == ServiceRegistrationAction.FailClosed)
            {
                LogService.Warning(
                    "The stopped WinDivert service registration has an unreadable driver path; it was left unchanged.");
                return false;
            }

            if (!DeleteService(service))
            {
                int deleteError = Marshal.GetLastWin32Error();
                if (deleteError != ErrorServiceMarkedForDelete)
                    throw new InvalidOperationException(
                        $"Unable to remove the stale WinDivert service registration. error={deleteError}");
            }

            CloseServiceHandle(service);
            service = IntPtr.Zero;
            if (!WaitForServiceDeletion(manager))
            {
                LogService.Warning(
                    "The stale WinDivert service registration is still held open; Windows must be restarted before Route Guard can start.");
                return false;
            }

            LogService.Info(
                "Removed a stopped WinDivert service registration whose driver path no longer exists; the controlled Route Guard runtime will register the verified driver on demand.");
            return true;
        }
        catch (Exception ex)
        {
            LogService.Error("WinDivert service registration preflight failed: " + ex.Message);
            return false;
        }
        finally
        {
            if (service != IntPtr.Zero)
                CloseServiceHandle(service);
            if (manager != IntPtr.Zero)
                CloseServiceHandle(manager);
        }
    }

    internal static string? ResolveRegisteredDriverPath(string? rawPath, string windowsDirectory)
    {
        if (string.IsNullOrWhiteSpace(rawPath) || string.IsNullOrWhiteSpace(windowsDirectory))
            return null;

        string path = Environment.ExpandEnvironmentVariables(rawPath.Trim());
        if (path.Length >= 2 && path[0] == '"')
        {
            int closingQuote = path.IndexOf('"', 1);
            if (closingQuote <= 1)
                return null;
            path = path[1..closingQuote];
        }
        if (path.StartsWith(@"\??\", StringComparison.Ordinal))
            path = path[4..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            path = path[4..];
        else if (path.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            path = Path.Combine(windowsDirectory, path[12..]);

        if (!Path.IsPathFullyQualified(path))
            return null;
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception) when (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            return null;
        }
    }

    internal static ServiceRegistrationAction DecideServiceRegistrationAction(
        string? registeredPath,
        string windowsDirectory,
        bool isStopped,
        Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        if (!isStopped)
            return ServiceRegistrationAction.Keep;

        string? resolvedPath = ResolveRegisteredDriverPath(registeredPath, windowsDirectory);
        if (resolvedPath == null)
            return ServiceRegistrationAction.FailClosed;
        return fileExists(resolvedPath)
            ? ServiceRegistrationAction.Keep
            : ServiceRegistrationAction.DeleteStale;
    }

    private static string? ReadRegisteredDriverPath()
    {
        using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using RegistryKey? key = baseKey.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + ServiceName);
        return key?.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    private static bool WaitForServiceDeletion(IntPtr manager)
    {
        for (int attempt = 0; attempt < 40; attempt++)
        {
            IntPtr current = OpenService(manager, ServiceName, ServiceQueryStatus);
            if (current == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                if (error == ErrorServiceDoesNotExist)
                    return true;
            }
            else
            {
                CloseServiceHandle(current);
            }
            Thread.Sleep(50);
        }
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        internal uint ServiceType;
        internal uint CurrentState;
        internal uint ControlsAccepted;
        internal uint Win32ExitCode;
        internal uint ServiceSpecificExitCode;
        internal uint CheckPoint;
        internal uint WaitHint;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr serviceManager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteService(IntPtr service);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr serviceHandle);
}
