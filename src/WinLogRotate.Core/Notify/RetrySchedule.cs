namespace WinLogRotate.Core.Notify;

/// <summary>
/// Whether a failed send is worth trying again, and how long to wait.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not <see cref="Io.RetryPolicy"/>, which exists for a different problem and says
/// so: it <c>Thread.Sleep</c>s because it is waiting for another process to let go of a file
/// handle, which "no test clock can hurry along", and its notion of a transient failure is a
/// share violation. Network retries have to be cancellable, have to respect a server's own
/// instruction, and have to fit inside a budget shared with every other channel.
/// </para>
/// <para>
/// Pure, so all of it runs on the Linux leg.
/// </para>
/// </remarks>
public static class RetrySchedule
{
    /// <summary>The most attempts after the first, whatever the configuration says.</summary>
    public const int MaxRetries = 5;

    /// <summary>
    /// Whether a status is worth repeating the request for.
    /// </summary>
    /// <remarks>
    /// 0 means the request never completed - a connection failure, a timeout, a DNS answer that
    /// did not come. 408 and 429 are the server asking; 5xx is the server admitting.
    /// <para>
    /// 400, 401, 403 and 404 are never retried, and that is the important half. The request is
    /// wrong, repeating it is a slower way to be wrong, and against a rate-limited endpoint it
    /// is how a misconfiguration becomes a lockout.
    /// </para>
    /// </remarks>
    public static bool IsRetryable(int status) =>
        status is 0 or 408 or 429 || status >= 500;

    /// <summary>
    /// How long to wait before attempt <paramref name="attempt"/>, or null to give up.
    /// </summary>
    /// <param name="attempt">1 after the first failure, 2 after the second.</param>
    /// <param name="retryAfter">What the server asked for, if it asked.</param>
    /// <param name="remaining">What is left of the whole phase's budget.</param>
    /// <param name="jitter">0.0 to 1.0. Deterministic in tests, random in production.</param>
    /// <remarks>
    /// Returning null rather than a clamped wait is the point. A server that says
    /// <c>Retry-After: 3600</c> is not asking to be retried inside a thirty-second budget - it is
    /// telling us to go away - and waiting the remaining twenty seconds just to fail anyway
    /// spends the budget every other channel was going to share.
    /// </remarks>
    public static TimeSpan? Delay(int attempt, TimeSpan? retryAfter, TimeSpan remaining, double jitter)
    {
        if (attempt < 1 || remaining <= TimeSpan.Zero)
        {
            return null;
        }

        // 1s, 2s, 4s, 8s, capped. Computed by multiplication rather than by shifting: a shift
        // by a large attempt number overflows to a negative delay, and a negative delay compares
        // as "plenty of budget left" against every check below.
        var backoff = TimeSpan.FromSeconds(Math.Min(8, Math.Pow(2, Math.Min(attempt, 10) - 1)));

        // Half to full, so a fleet of machines that all failed at 03:00 does not all retry at
        // 03:00:01.
        var scaled = backoff * (0.5 + (Math.Clamp(jitter, 0, 1) * 0.5));

        var wait = retryAfter ?? scaled;

        // Enough left to wait AND to make the attempt. Waiting out the budget and then having no
        // time to send is the worst of both.
        var reserve = TimeSpan.FromSeconds(2);
        return wait + reserve >= remaining ? null : wait;
    }
}
