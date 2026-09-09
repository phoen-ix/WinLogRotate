using Shouldly;
using WinLogRotate.Core.Notify;
using WinLogRotate.Core.State;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>Whether a failed send is worth trying again, and how long to wait.</summary>
public sealed class RetryScheduleTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(0)]    // never completed - connection refused, timeout, DNS
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public void TransientStatusesAreRetried(int status) => RetrySchedule.IsRetryable(status).ShouldBeTrue();

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    public void AWrongRequestIsNeverRetried(int status)
    {
        // Repeating it is a slower way to be wrong, and against a rate-limited endpoint it is
        // how a misconfiguration becomes a lockout.
        RetrySchedule.IsRetryable(status).ShouldBeFalse();
    }

    [Fact]
    public void TheDelayGrowsAndIsCapped()
    {
        var first = RetrySchedule.Delay(1, null, TimeSpan.FromMinutes(10), jitter: 1).ShouldNotBeNull();
        var second = RetrySchedule.Delay(2, null, TimeSpan.FromMinutes(10), jitter: 1).ShouldNotBeNull();
        var far = RetrySchedule.Delay(9, null, TimeSpan.FromMinutes(10), jitter: 1).ShouldNotBeNull();

        second.ShouldBeGreaterThan(first);
        far.ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(8));
    }

    [Fact]
    public void ALargeAttemptNumberDoesNotOverflowIntoANegativeDelay()
    {
        // Computed by multiplication rather than by shifting. A shift by a large attempt count
        // wraps negative, and a negative delay compares as "plenty of budget left" against every
        // check that follows.
        RetrySchedule.Delay(64, null, TimeSpan.FromMinutes(10), jitter: 1)
            .ShouldNotBeNull().ShouldBeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public void JitterSpreadsTheRetryWithoutChangingItsOrder()
    {
        // So a fleet that all failed at 03:00 does not all retry at 03:00:01.
        var low = RetrySchedule.Delay(3, null, TimeSpan.FromMinutes(10), jitter: 0).ShouldNotBeNull();
        var high = RetrySchedule.Delay(3, null, TimeSpan.FromMinutes(10), jitter: 1).ShouldNotBeNull();

        low.ShouldBeLessThan(high);
        low.ShouldBeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public void AnHoursRetryAfterInsideAThirtySecondBudgetMeansGiveUp()
    {
        // The server is not asking to be retried inside our budget, it is telling us to go away.
        // Waiting out the remaining twenty seconds only to fail spends what every other channel
        // was going to share.
        RetrySchedule.Delay(1, TimeSpan.FromHours(1), Budget, jitter: 0.5).ShouldBeNull();
    }

    [Fact]
    public void AShortRetryAfterIsHonouredExactly()
    {
        RetrySchedule.Delay(1, TimeSpan.FromSeconds(3), Budget, jitter: 0)
            .ShouldBe(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void NothingIsAttemptedWithNoBudgetLeft()
    {
        RetrySchedule.Delay(1, null, TimeSpan.Zero, jitter: 0.5).ShouldBeNull();
        RetrySchedule.Delay(1, null, TimeSpan.FromSeconds(-5), jitter: 0.5).ShouldBeNull();
    }

    [Fact]
    public void ItLeavesTimeToActuallySendAfterWaiting()
    {
        // Waiting out the budget and then having no time left to make the request is the worst
        // of both outcomes.
        RetrySchedule.Delay(1, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5), jitter: 0).ShouldBeNull();
    }
}

/// <summary>Whether a channel is worth attempting at all.</summary>
public sealed class BreakerPolicyTests
{
    private readonly DateTimeOffset _now = new(2026, 9, 9, 3, 0, 0, TimeSpan.Zero);

    private static NotifySettings Settings(int after = 5, int cooldown = 5) =>
        new() { BreakerAfter = after, BreakerCooldown = cooldown };

    private ChannelNotifyState Fail(ChannelNotifyState state, int times, NotifySettings settings)
    {
        for (var i = 0; i < times; i++)
        {
            state = BreakerPolicy.Record(state, succeeded: false, "boom", settings, _now);
        }

        return state;
    }

    [Fact]
    public void AHealthyChannelIsClosed()
    {
        BreakerPolicy.Verdict(new ChannelNotifyState(), Settings()).ShouldBe(BreakerVerdict.Closed);
    }

    [Fact]
    public void ItStaysClosedUntilTheConfiguredCount()
    {
        var state = Fail(new ChannelNotifyState(), 4, Settings());

        BreakerPolicy.Verdict(state, Settings()).ShouldBe(BreakerVerdict.Closed);
    }

    [Fact]
    public void ItOpensAtTheConfiguredCount()
    {
        var state = Fail(new ChannelNotifyState(), 5, Settings());

        BreakerPolicy.Verdict(state, Settings()).ShouldBe(BreakerVerdict.Open);
        state.SkipRunsRemaining.ShouldBe(5);
    }

    [Fact]
    public void ItProbesOnceTheCooldownIsSpent()
    {
        var settings = Settings();
        var state = Fail(new ChannelNotifyState(), 5, settings);

        for (var i = 0; i < 5; i++)
        {
            state = BreakerPolicy.Skip(state);
        }

        // Half-open, not closed: one probe, and the dispatcher disables retries for it, because
        // three retries against a dead endpoint is three timeouts - the cost this exists to
        // avoid in the first place.
        BreakerPolicy.Verdict(state, settings).ShouldBe(BreakerVerdict.HalfOpen);
    }

    [Fact]
    public void ASuccessfulProbeClosesItCompletely()
    {
        var settings = Settings();
        var state = Fail(new ChannelNotifyState(), 5, settings);

        state = BreakerPolicy.Record(state, succeeded: true, null, settings, _now);

        BreakerPolicy.Verdict(state, settings).ShouldBe(BreakerVerdict.Closed);
        state.ConsecutiveFailures.ShouldBe(0);

        // Including the growth. A channel that works is a channel that works; carrying a grudge
        // from last month only makes the next outage slower to report.
        state.OpenCount.ShouldBe(0);
    }

    [Fact]
    public void RepeatedOutagesBackOffFurtherEachTime()
    {
        var settings = Settings();
        var state = Fail(new ChannelNotifyState(), 5, settings);
        var first = state.SkipRunsRemaining;

        state = Fail(state, 1, settings);

        state.SkipRunsRemaining.ShouldBeGreaterThan(first);
    }

    [Fact]
    public void TheCooldownNeverOverflowsAndAlwaysCountsDown()
    {
        // Doubling by shifting overflows to a negative cooldown after about thirty outages, and
        // a negative cooldown never counts down - the breaker latches open and the channel goes
        // silent for ever. That failure is invisible until the day somebody needs the alert.
        var settings = Settings();
        var state = Fail(new ChannelNotifyState(), 200, settings);

        state.SkipRunsRemaining.ShouldBeGreaterThan(0);
        state.SkipRunsRemaining.ShouldBeLessThanOrEqualTo(BreakerPolicy.MaxCooldownRuns);
        state.Cooldown.ShouldBeLessThanOrEqualTo(BreakerPolicy.MaxCooldownRuns);
    }

    [Fact]
    public void EvenAHopelessChannelIsTriedAgainEventually()
    {
        var settings = Settings();
        var state = Fail(new ChannelNotifyState(), 200, settings);

        for (var i = 0; i < BreakerPolicy.MaxCooldownRuns; i++)
        {
            state = BreakerPolicy.Skip(state);
        }

        BreakerPolicy.Verdict(state, settings).ShouldBe(BreakerVerdict.HalfOpen);
    }

    [Fact]
    public void ResetClosesItAtOnce()
    {
        // What notify reset does, and why the verb has to exist: the operator has just fixed the
        // firewall and should not wait five more runs to find out.
        BreakerPolicy.Verdict(BreakerPolicy.Reset(), Settings()).ShouldBe(BreakerVerdict.Closed);
    }

    [Fact]
    public void TheLastErrorIsKeptForNotifyStatus()
    {
        var state = BreakerPolicy.Record(new ChannelNotifyState(), false, "connection refused", Settings(), _now);

        state.LastError.ShouldBe("connection refused");
        state.LastAttempt.ShouldBe(_now);
    }
}
