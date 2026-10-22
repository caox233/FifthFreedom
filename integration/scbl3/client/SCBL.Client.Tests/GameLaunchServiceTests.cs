using SplinterCellCNLauncher.Services;

namespace SCBL.Client.Tests;

public sealed class GameLaunchServiceTests
{
    [Fact]
    public void Disposing_unresumed_game_terminates_the_suspended_process()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var service = new GameLaunchService();
        GameLaunchService.SuspendedGameProcess suspended = service.StartGameSuspended(
            Environment.SystemDirectory,
            "cmd.exe");
        System.Diagnostics.Process process = suspended.Process;
        try
        {
            suspended.Dispose();

            Assert.True(process.WaitForExit(5000));
            Assert.True(process.HasExited);
        }
        finally
        {
            suspended.Dispose();
            process.Dispose();
        }
    }

    [Fact]
    public void Disposing_unresumed_game_uses_owned_handle_when_process_wrapper_was_disposed()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var service = new GameLaunchService();
        GameLaunchService.SuspendedGameProcess suspended = service.StartGameSuspended(
            Environment.SystemDirectory,
            "cmd.exe");
        int processId = suspended.Process.Id;
        suspended.Process.Dispose();

        Exception? error = Record.Exception(() => suspended.Dispose());

        Assert.Null(error);
        Assert.True(SpinWait.SpinUntil(() => !ProcessExists(processId), TimeSpan.FromSeconds(5)));
        Assert.Null(Record.Exception(() => suspended.Dispose()));
    }

    [Fact]
    public void Cleanup_does_not_replace_the_original_launch_failure()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var service = new GameLaunchService();
        var original = new InvalidOperationException("route guard failed");
        Action failAfterProcessWrapperIsDisposed = () =>
        {
            using GameLaunchService.SuspendedGameProcess suspended = service.StartGameSuspended(
                Environment.SystemDirectory,
                "cmd.exe");
            suspended.Process.Dispose();
            throw original;
        };
        Exception? observed = Record.Exception(failAfterProcessWrapperIsDisposed);

        Assert.Same(original, observed);
    }

    private static bool ProcessExists(int processId)
    {
        try
        {
            using System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
