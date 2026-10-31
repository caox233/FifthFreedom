using System.Diagnostics;
using System.Runtime.ExceptionServices;

internal static partial class Program
{
    // Only called for this test's freshly created directories. This does not
    // change attributes/ACLs or retry production pipe/process admission errors.
    private static async Task<int> DeleteOwnedDirectoryAsync(string root, Func<bool> handlesReleased, TimeSpan? timeout = null)
    {
        TimeSpan budget = timeout ?? TimeSpan.FromSeconds(2);
        if (budget <= TimeSpan.Zero || budget > TimeSpan.FromSeconds(2)) throw new ArgumentOutOfRangeException(nameof(timeout));
        var elapsed = Stopwatch.StartNew();
        ExceptionDispatchInfo? firstFailure = null;
        int attempts = 0;
        while (true)
        {
            if (firstFailure != null && (!handlesReleased() || elapsed.Elapsed >= budget)) firstFailure.Throw();
            attempts++;
            try { Directory.Delete(root, recursive: true); return attempts; }
            catch (Exception error)
            {
                firstFailure ??= ExceptionDispatchInfo.Capture(error);
                if (!OperatingSystem.IsWindows() || !RetryableDirectoryFailure(error)
                    || !handlesReleased() || elapsed.Elapsed >= budget) firstFailure.Throw();
                TimeSpan remaining = budget - elapsed.Elapsed;
                if (remaining <= TimeSpan.Zero) firstFailure.Throw();
                await Task.Delay(remaining < TimeSpan.FromMilliseconds(100) ? remaining : TimeSpan.FromMilliseconds(100));
            }
        }
    }

    private static bool RetryableDirectoryFailure(Exception error) =>
        error is UnauthorizedAccessException && error.HResult == unchecked((int)0x80070005)
        || error is IOException && error.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021);
}
