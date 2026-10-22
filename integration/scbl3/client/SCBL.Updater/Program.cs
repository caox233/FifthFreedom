using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        UpdaterApplication updater = CreateUpdater();
        if (args.Any(argument => argument.Equals(
                "--restart-helper",
                StringComparison.OrdinalIgnoreCase)))
        {
            return updater.Run(args);
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var window = new UpdaterProgressWindow();
        int exitCode = 1;
        window.Shown += async (_, _) =>
        {
            await Task.Yield();
            exitCode = await Task.Run(() => updater.Run(args));
            if (exitCode == 0)
                window.CompleteAndClose();
            else
                window.ShowFailure();
        };
        Application.Run(window);
        return exitCode;
    }

    private static UpdaterApplication CreateUpdater()
        => new UpdaterApplication(
            new TransactionalUpdateInstaller(),
            new WindowsUpdaterProcessBoundary(),
            new RestartBoundary());
}
