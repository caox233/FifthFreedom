using System.ServiceProcess;

namespace Scbl.Server.Host;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (Environment.UserInteractive || args.Contains("--console", StringComparer.OrdinalIgnoreCase))
        {
            using CancellationTokenSource cancellation = new();
            Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
            await using ServerRuntime runtime = new(new ServerPaths());
            try
            {
                await runtime.StartAsync(cancellation.Token).ConfigureAwait(false);
                Console.WriteLine("SCBL Server Host 正在运行。按 Ctrl+C 停止。");
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            finally { await runtime.StopAsync(CancellationToken.None).ConfigureAwait(false); }
            return 0;
        }

        ServiceBase.Run(new ServerWindowsService());
        return 0;
    }
}
