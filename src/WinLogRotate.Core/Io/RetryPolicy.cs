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
    /// <summary>
    /// True for errors worth trying again: a transient share or lock violation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>UnauthorizedAccessException</c> used to be listed here, unconditionally, in pointed
    /// contrast to the <c>IOException</c> arm beside it - which is carefully narrowed to two
    /// codes. Permissions are not momentary. The justification this class gives for retrying at
    /// all is antivirus and the Search Indexer, and what those produce is
    /// <c>ERROR_SHARING_VIOLATION</c>, which the other arm already covers.
    /// </para>
    /// <para>
    /// It went unnoticed while every caller was a file operation on a directory the service had
    /// just written to. Milestone 16 added a planned <c>CreateDirectory</c> for <c>olddir</c>,
    /// where an ACL denial is the <i>expected</i> failure rather than an exotic one - and at
    /// default settings that cost five attempts and 1500 ms of real sleeping per directory, one
    /// per distinct olddir. An estate with a relative olddir across two hundred log directories
    /// on a share the service account cannot write burned five minutes of pure backoff, which is
    /// enough to clamp the notification phase that would have reported it.
    /// </para>
    /// <para>
    /// If an antivirus is ever observed returning access denied rather than a sharing violation,
    /// the fix is to narrow to that case - not to restore the blanket retry.
    /// </para>
    /// </remarks>
    public static bool IsTransient(Exception e) => e switch
    {
        IOException io => Win32Error.TryFromHResult(io.HResult, out var code) && IsTransientCode(code),
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
