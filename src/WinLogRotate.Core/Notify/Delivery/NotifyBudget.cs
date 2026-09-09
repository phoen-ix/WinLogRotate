namespace WinLogRotate.Core.Notify.Delivery;

/// <summary>
/// How much wall clock the notification phase may spend, and how it is shared out.
/// </summary>
/// <remarks>
/// Pure arithmetic, so the whole of it runs on the Linux leg. The interesting part is not the
/// subtraction, it is <see cref="For"/>: without it a fifty-nine minute rotation followed by a
/// thirty-second notification phase reaches the scheduled task's one-hour ExecutionTimeLimit, and
/// Task Scheduler reports that as <c>0x41306</c> - which is indistinguishable from an operator
/// pressing Stop. The rotation succeeded and the only machine-readable evidence of it says
/// "terminated".
/// </remarks>
public static class NotifyBudget
{
    /// <summary>
    /// Left for the process to save state and exit after the last send.
    /// </summary>
    /// <remarks>
    /// Notification state is written after delivery, and losing that write is worse than losing a
    /// message: the next run would re-send everything it just sent.
    /// </remarks>
    public static readonly TimeSpan Reserve = TimeSpan.FromSeconds(5);

    /// <summary>What the phase may spend, and whether the host's deadline is what decided it.</summary>
    /// <param name="budget">The configured phase budget.</param>
    /// <param name="deadline">How long the whole invocation has, or null when nothing limits it.</param>
    /// <param name="started">When the invocation began.</param>
    /// <param name="now">When the phase began.</param>
    public static (TimeSpan Allowed, bool Clamped) For(
        TimeSpan budget, TimeSpan? deadline, DateTimeOffset started, DateTimeOffset now)
    {
        if (budget < TimeSpan.Zero)
        {
            budget = TimeSpan.Zero;
        }

        if (deadline is not { } limit)
        {
            // A hand-run rotation is never truncated. Nothing is going to kill it, so inventing a
            // deadline would only make the interactive case behave differently from the tests.
            return (budget, false);
        }

        var left = (started + limit) - now - Reserve;

        if (left <= TimeSpan.Zero)
        {
            return (TimeSpan.Zero, true);
        }

        return left < budget ? (left, true) : (budget, false);
    }

    /// <summary>
    /// One channel's share of what is left.
    /// </summary>
    /// <remarks>
    /// An even split rather than first-come. A single unreachable relay will spend everything it
    /// is given on connection timeouts, and giving it the whole remaining budget means the healthy
    /// channels behind it are never attempted - so the one broken destination silences the working
    /// ones, which is the failure this whole feature exists to prevent.
    /// </remarks>
    public static TimeSpan Share(TimeSpan remaining, int channelsLeft) =>
        channelsLeft <= 1 || remaining <= TimeSpan.Zero
            ? (remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero)
            : remaining / channelsLeft;
}
