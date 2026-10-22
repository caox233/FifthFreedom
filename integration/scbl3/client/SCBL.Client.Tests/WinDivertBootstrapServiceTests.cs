using SplinterCellCNLauncher.Services;

namespace SCBL.Client.Tests;

public sealed class WinDivertBootstrapServiceTests
{
    [Theory]
    [InlineData(@"\??\C:\ProgramData\SCBL\RouteGuard\WinDivert64.sys", @"C:\ProgramData\SCBL\RouteGuard\WinDivert64.sys")]
    [InlineData(@"\\?\C:\ProgramData\SCBL\RouteGuard\WinDivert64.sys", @"C:\ProgramData\SCBL\RouteGuard\WinDivert64.sys")]
    [InlineData(@"\SystemRoot\System32\drivers\WinDivert64.sys", @"C:\Windows\System32\drivers\WinDivert64.sys")]
    [InlineData("\"C:\\Program Files\\SCBL\\WinDivert64.sys\"", @"C:\Program Files\SCBL\WinDivert64.sys")]
    public void Registered_driver_paths_are_resolved_without_changing_the_target(
        string registeredPath,
        string expected)
    {
        string? resolved = WinDivertBootstrapService.ResolveRegisteredDriverPath(
            registeredPath,
            @"C:\Windows");

        Assert.Equal(expected, resolved, ignoreCase: true);
    }

    [Theory]
    [InlineData("")]
    [InlineData("WinDivert64.sys")]
    [InlineData("\"unterminated")]
    public void Ambiguous_or_relative_service_paths_fail_closed(string registeredPath)
    {
        Assert.Null(WinDivertBootstrapService.ResolveRegisteredDriverPath(
            registeredPath,
            @"C:\Windows"));
    }

    [Fact]
    public void Stopped_registration_with_missing_driver_is_the_only_deleted_state()
    {
        const string registeredPath = @"\??\C:\Old SCBL\WinDivert64.sys";

        Assert.Equal(
            WinDivertBootstrapService.ServiceRegistrationAction.DeleteStale,
            WinDivertBootstrapService.DecideServiceRegistrationAction(
                registeredPath,
                @"C:\Windows",
                isStopped: true,
                _ => false));
        Assert.Equal(
            WinDivertBootstrapService.ServiceRegistrationAction.Keep,
            WinDivertBootstrapService.DecideServiceRegistrationAction(
                registeredPath,
                @"C:\Windows",
                isStopped: true,
                _ => true));
        Assert.Equal(
            WinDivertBootstrapService.ServiceRegistrationAction.Keep,
            WinDivertBootstrapService.DecideServiceRegistrationAction(
                registeredPath,
                @"C:\Windows",
                isStopped: false,
                _ => false));
        Assert.Equal(
            WinDivertBootstrapService.ServiceRegistrationAction.FailClosed,
            WinDivertBootstrapService.DecideServiceRegistrationAction(
                "WinDivert64.sys",
                @"C:\Windows",
                isStopped: true,
                _ => false));
    }
}
