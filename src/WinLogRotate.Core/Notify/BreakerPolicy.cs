using WinLogRotate.Core.State;

namespace WinLogRotate.Core.Notify;

/// <summary>Whether a channel is worth attempting this run.</summary>
public enum BreakerVerdict
{
    /// <summary>Attempt it normally, with retries.</summary>
    Closed,

    /// <summary>Suppressed. Do not attempt, do not spend budget.</summary>
    Open,

    /// <summary>One probe, with retries disabled.</summary>
    HalfOpen,
}

/// <summary>
/// Stops a channel that no longer exists from costing every run its budget.
/// </summary>
/// <remarks>
/// <para>
/// Counted in <b>runs</b>, not wall clock. The cost being bounded is time stolen from a rotation,
/// which is incurred once per run - so the allowance for it is naturally denominated in runs. A
/// wall-clock cooldown does not bound it at all: on a machine that rotates hourly,
/// "suppress for thirty minutes" skips no runs whatever and the full timeout is paid every hour,
/// for ever.
/// </para>
/// <para>
/// The asymmetry is real and is handled by disclosure rather than by a second unit: five runs is
/// five hours if you rotate hourly and five days if you rotate daily, <c>notify status</c> says
/// so, and <c>notify reset</c> closes a breaker the moment somebody has fixed the firewall.
/// </para>
/// </remarks>
public static class BreakerPolicy
{
    /// <summary>However many times a channel has failed, it is retried at least this often.</summary>
    public const int MaxCooldownRuns = 100;

    /// <summary>What to do with a channel at the start of a run.</summary>
    public static BreakerVerdict Verdict(ChannelNotifyState state, NotifySettings settings)
    {
        if (state.ConsecutiveFailures < settings.BreakerAfter)
        {
            return BreakerVerdict.Closed;
        }

        return state.SkipRunsRemaining > 0 ? BreakerVerdict.Open : BreakerVerdict.HalfOpen;
    }

    /// <summary>Consumes one run of an open channel's cooldown.</summary>
    public static ChannelNotifyState Skip(ChannelNotifyState state) =>
        state with { SkipRunsRemaining = Math.Max(0, state.SkipRunsRemaining - 1) };

    /// <summary>
    /// Folds one run's outcome for one channel.
    /// </summary>
    /// <remarks>
    /// Called once per channel per run with the run's <em>final</em> outcome. Retries inside a
    /// run are invisible here on purpose: counting attempts would open the circuit on the second
    /// run rather than the fifth with the default settings, so <c>breaker_after = 5</c> would
    /// mean a third of what it says.
    /// </remarks>
    public static ChannelNotifyState Record(
        ChannelNotifyState state, bool succeeded, string? error,
        NotifySettings settings, DateTimeOffset now)
    {
        if (succeeded)
        {
            // Fully reset, including the growth. A channel that works is a channel that works;
            // carrying a grudge from last month only makes the next outage slower to report.
            return new ChannelNotifyState { LastAttempt = now };
        }

        var failures = state.ConsecutiveFailures + 1;
        if (failures < settings.BreakerAfter)
        {
            return state with
            {
                ConsecutiveFailures = failures,
                LastError = error,
                LastAttempt = now,
            };
        }

        var opens = state.OpenCount + 1;

        // Doubling, computed by multiplication and clamped at every step. Shifting by the open
        // count overflows to a negative cooldown after about thirty outages, and a negative
        // cooldown never counts down - the breaker would latch open and the channel would go
        // silent for ever.
        var cooldown = settings.BreakerCooldown;
        for (var i = 1; i < opens && cooldown < MaxCooldownRuns; i++)
        {
            cooldown = Math.Min(cooldown * 2, MaxCooldownRuns);
        }

        return state with
        {
            ConsecutiveFailures = failures,
            SkipRunsRemaining = cooldown,
            Cooldown = cooldown,
            OpenCount = opens,
            OpenedAt = state.OpenedAt ?? now,
            LastError = error,
            LastAttempt = now,
        };
    }

    /// <summary>What <c>notify reset</c> does: closes it, keeping nothing.</summary>
    public static ChannelNotifyState Reset() => new();
}
