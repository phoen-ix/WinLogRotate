using System.Runtime.InteropServices;

namespace WinLogRotate.Core.Io;

/// <summary>
/// Retries a file operation that failed for a reason that is usually momentary.
/// </summary>
/// <remarks>
/// On Windows this is not optional. Antivirus and the Search Indexer routinely open files for
/// a few hundred milliseconds, and a rotation that gives up the first time it sees
/// ERROR_SHARING_VIOLATION will fail unpredictably on perfectly healthy machines. Every
/// existing Windows port of logrotate either lacks this or bolted it on after users complained.
/// </remarks>
public static class RetryPolicy
{
    /// <summary>True for errors worth trying again: a transient share or lock violation.</summary>
    public static bool IsTransient(Exception e) => e switch
    {
        IOException io => Win32Error.TryFromHResult(io.HResult, out var code) && IsTransientCode(code),
        UnauthorizedAccessException => true,
        _ => false,
    };

    private static bool IsTransientCode(int code) =>
        code is Win32Error.SharingViolation or Win32Error.LockViolation;

    /// <summary>
    /// Runs <paramref name="action"/>, retrying transient failures with a doubling backoff.
    /// </summary>
    /// <param name="onRetry">Called before each retry, so the journal records that a
    /// contended file needed more than one attempt - useful evidence when someone asks why a
    /// rotation was slow.</param>
    public static T Execute<T>(
        Func<T> action,
        int attempts,
        int intervalMs,
        Action<int, Exception>? onRetry = null,
        TimeProvider? clock = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);

        var delay = intervalMs;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (Exception e) when (attempt < attempts && IsTransient(e))
            {
                onRetry?.Invoke(attempt, e);

                // Real sleeping, not the TimeProvider's: we are waiting for another process to
                // let go of a handle, which no test clock can hurry along.
                Thread.Sleep(delay);
                delay = Math.Min(delay * 2, 5000);
            }
        }
    }

    public static void Execute(Action action, int attempts, int intervalMs, Action<int, Exception>? onRetry = null) =>
        Execute<object?>(() => { action(); return null; }, attempts, intervalMs, onRetry);

    /// <summary>The Win32 error code behind an exception, or 0 when it did not come from Win32.</summary>
    public static int ErrorCode(Exception e) => e switch
    {
        IOException io => Win32Error.TryFromHResult(io.HResult, out var code) ? code : 0,
        UnauthorizedAccessException => Win32Error.AccessDenied,
        ExternalException ex => ex.ErrorCode & 0xFFFF,
        _ => 0,
    };
}
