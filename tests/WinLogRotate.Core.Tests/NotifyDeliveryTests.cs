using Microsoft.Extensions.Time.Testing;
using Shouldly;
using WinLogRotate.Contracts;
using WinLogRotate.Core.Notify;
using WinLogRotate.Core.Notify.Delivery;
using WinLogRotate.Core.State;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// What the dispatcher records as delivered, and what it refuses to.
/// </summary>
/// <remarks>
/// All of it runs on the Linux leg. The senders are fakes and the clock is fake, so retries,
/// budget exhaustion and breaker transitions are exercised without a socket, a thread or a real
/// second passing - which is the entire reason <c>RetrySchedule</c> neither sleeps nor reads a
/// clock and the dispatcher takes both as parameters.
/// </remarks>
public sealed class NotifyDeliveryTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("winlogrotate-delivery-");
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 9, 3, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); } catch (IOException) { }
    }

    // ---- scaffolding ------------------------------------------------------------------------

    /// <summary>A transport that answers however the test says, and spends however long it says.</summary>
    private sealed class FakeSender(FakeTimeProvider clock) : INotifySender
    {
        public Func<int, SendResult> Answer { get; set; } = _ => SendResult.Delivered();

        /// <summary>Wall clock one attempt burns, so budget arithmetic can be driven.</summary>
        public TimeSpan Cost { get; set; } = TimeSpan.Zero;

        public List<string> Subjects { get; } = [];

        public int Attempts { get; private set; }

        public SendResult Send(ResolvedChannel channel, NotifyMessage message, TimeSpan timeout)
        {
            Attempts++;
            Subjects.Add(message.Subject);

            if (Cost > TimeSpan.Zero)
            {
                clock.Advance(Cost);
            }

            return Answer(Attempts);
        }
    }

    private NotifyStateStore State() =>
        NotifyStateStore.Load(Path.Combine(_dir.FullName, $"notify-{Guid.NewGuid():N}.json"));

    /// <summary>
    /// A provider-backed channel, which is how every real one is built.
    /// </summary>
    /// <remarks>
    /// The scheme is explicit because it is the dispatcher's lookup key for the transport, and
    /// because <c>HookAction.Display</c> masks an http: target down to <c>***</c> - reconstructing
    /// a channel from its display name, which is what this helper first did, gave every channel
    /// the same identity and made three tests assert nothing.
    /// </remarks>
    private static ResolvedChannel Channel(string name, HookScheme scheme) => new()
    {
        Action = HookAction.Create(scheme, name, name, provider: name),
    };

    private static RunSummary Run() => new()
    {
        RunId = "r1",
        Machine = "TESTBOX",
        JobsRun = 1,
        Completed = 0,
        BytesFreed = 0,
        ObservedJobs = ["iis"],
    };

    private static NotificationPlan Plan(params string[] jobs) => new()
    {
        Messages = [.. jobs.Select(Message)],
        Suppressed = [],
        Baseline = [],
    };

    private static PlannedNotification Message(string job)
    {
        DigestLine[] lines =
        [
            new()
            {
                Severity = Severity.Error,
                Code = DiagnosticCode.FileLocked,
                Job = job,
                Text = "Access denied",
                Where = @"C:\logs",
                DirectoryKey = @"c:\logs",
                Count = 1,
            },
        ];

        return new PlannedNotification
        {
            Reason = NotifyReason.NewFailure,
            Job = job,
            Severity = Severity.Error,
            Subject = $"[WinLogRotate] FAILED on TESTBOX - {job}",
            Lines = lines,
            Context = lines,
            Fingerprint = "abc123",
            NextState = new JobNotifyState
            {
                Outcome = NotifyOutcome.Failing,
                Fingerprint = "abc123",
                NotifiedAt = new DateTimeOffset(2026, 9, 9, 3, 0, 0, TimeSpan.Zero),
            },
        };
    }

    private DeliveryReport Dispatch(
        NotificationPlan plan, NotifyStateStore state,
        IReadOnlyList<(ResolvedChannel Channel, INotifySender Sender)> channels,
        NotifySettings? settings = null, TimeSpan? budget = null)
    {
        var map = new Dictionary<HookScheme, INotifySender>();

        foreach (var (channel, sender) in channels)
        {
            map[channel.Action.Scheme] = sender;
        }

        return HookDispatcher.Dispatch(
            plan, Run(), [.. channels.Select(c => c.Channel)],
            settings ?? NotifySettings.Default, state, new SenderTable(map),
            new DispatchOptions
            {
                Clock = _clock,
                Wait = d => _clock.Advance(d),
                Budget = budget ?? TimeSpan.FromSeconds(30),

                // Fixed, so a backoff sequence is a number rather than a range.
                Jitter = () => 1.0,
            });
    }

    // ---- the rule ---------------------------------------------------------------------------

    /// <summary>
    /// The trap this whole type exists to avoid, in one test.
    /// </summary>
    /// <remarks>
    /// "Delivered to at least one channel" is what the doc comment on
    /// <c>PlannedNotification.NextState</c> used to say on its own, and taken literally it is the
    /// bug it was meant to prevent: eventlog: is a local write that essentially cannot fail, so it
    /// alone would satisfy the condition and a week-long mail outage would end with nobody ever
    /// emailed - not even when mail came back, because by then the incident is old news.
    /// </remarks>
    [Fact]
    public void AMessageIsNotMarkedDeliveredWhenAnAttemptedChannelFailed()
    {
        var good = new FakeSender(_clock);
        var bad = new FakeSender(_clock) { Answer = _ => SendResult.Failed(500, "relay down") };

        var report = Dispatch(Plan("iis"), State(),
            [(Channel("eventlog", HookScheme.EventLog), good), (Channel("email.relay", HookScheme.Smtp), bad)]);

        good.Attempts.ShouldBeGreaterThan(0, "the healthy channel must still be tried");
        report.Delivered.ShouldBeEmpty(
            "one channel accepting it does not mean the operator was told");
    }

    /// <summary>The other half of the rule, and what stops it deadlocking for ever.</summary>
    [Fact]
    public void AnOpenBreakerDoesNotBlockDelivery()
    {
        var good = new FakeSender(_clock);
        var bad = new FakeSender(_clock) { Answer = _ => SendResult.Failed(500, "relay down") };

        var state = State();

        // Exactly what BreakerPolicy.Record leaves behind after breaker_after failed runs.
        state.SetChannel("email.relay", new ChannelNotifyState
        {
            ConsecutiveFailures = 5,
            SkipRunsRemaining = 5,
            Cooldown = 5,
            OpenCount = 1,
            OpenedAt = _clock.GetUtcNow(),
            LastError = "relay down",
            LastAttempt = _clock.GetUtcNow(),
        });

        var report = Dispatch(Plan("iis"), state,
            [(Channel("eventlog", HookScheme.EventLog), good), (Channel("email.relay", HookScheme.Smtp), bad)]);

        bad.Attempts.ShouldBe(0, "an open breaker means the channel is not attempted at all");
        report.Delivered.ShouldHaveSingleItem().Job.ShouldBe("iis");
        report.Channels.ShouldContain(c => c.Skipped);
    }

    /// <summary>
    /// The cost of the rule, asserted rather than discovered, end to end through the planner.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A permanently broken channel blocks state from advancing, so the planner keeps deciding the
    /// same failure is still news and the healthy channels get it again every run. That is a real
    /// number of duplicate alerts, and it belongs in a test so that changing <c>breaker_after</c>
    /// is visibly a decision about how many an operator receives.
    /// </para>
    /// <para>
    /// It has to drive the real planner. Handing the dispatcher a fresh plan each run - which is
    /// what the first version of this test did - sends unconditionally and proves nothing at all,
    /// because the convergence being claimed happens in the planner, not here.
    /// </para>
    /// </remarks>
    [Fact]
    public void ABrokenChannelStopsDuplicatingOnceItsBreakerOpens()
    {
        var settings = NotifySettings.Default with
        {
            To = ["eventlog:", "email.relay"],
            Retries = 0,
            BreakerAfter = 5,

            // Otherwise the reminder fires partway through and the count measures two things.
            RemindAfter = TimeSpan.Zero,
        };

        CliDiagnostic[] failing =
        [
            new()
            {
                Severity = Severity.Error,
                Code = DiagnosticCode.FileLocked,
                Message = "Access denied",
                Job = "iis",
                Path = @"C:\logs\a.log",
            },
        ];

        var state = State();

        // One healthy run first, so what follows is a job that STARTS failing. Without it the
        // planner records a baseline and correctly says nothing ever again - installing
        // monitoring on an already-broken machine must not alert about what was already there,
        // and with remind_after off there is no second chance.
        foreach (var (job, baseline) in NotificationPlanner
                     .PlanFor(Run(), [], settings, state, _clock.GetUtcNow()).Baseline)
        {
            state.SetJob(job, baseline);
        }

        _clock.Advance(TimeSpan.FromHours(24));

        var delivered = 0;

        for (var run = 0; run < 12; run++)
        {
            var good = new FakeSender(_clock);
            var bad = new FakeSender(_clock) { Answer = _ => SendResult.Failed(500, "down") };

            var plan = NotificationPlanner.PlanFor(
                Run(), failing, settings, state, _clock.GetUtcNow());

            var report = Dispatch(plan, state,
                [(Channel("eventlog", HookScheme.EventLog), good), (Channel("email.relay", HookScheme.Smtp), bad)], settings);

            foreach (var message in report.Delivered)
            {
                state.SetJob(message.Job, message.NextState);
            }

            foreach (var (job, next) in plan.Baseline)
            {
                state.SetJob(job, next);
            }

            delivered += good.Attempts;
            _clock.Advance(TimeSpan.FromHours(24));
        }

        // Runs 1-5 send, and each is blocked by the failing channel, so state never advances and
        // the planner keeps calling it new. That is five consecutive failures, so the breaker
        // opens. Run 6 skips the broken channel entirely, the healthy one alone therefore counts
        // as complete, state advances - and runs 7-12 are silent. Six alerts for one incident,
        // not twelve, and not one.
        delivered.ShouldBe(6);
    }

    // ---- failure classes --------------------------------------------------------------------

    [Fact]
    public void AConnectionFailureAbandonsTheChannelNotJustTheMessage()
    {
        // A dead relay is dead. Retrying it once per message multiplies the spend by the message
        // count and reaches nobody either way.
        var dead = new FakeSender(_clock) { Answer = _ => SendResult.Unreachable("refused") };

        Dispatch(Plan("a", "b", "c"), State(), [(Channel("email.relay", HookScheme.Smtp), dead)],
            NotifySettings.Default with { Retries = 0 });

        dead.Attempts.ShouldBe(1, "three messages must not mean three connection attempts");
    }

    [Fact]
    public void AFourOhFourDoesNotAbandonTheChannel()
    {
        // A 4xx is about this one message. The next one still goes - a single oversized or
        // malformed message must not silence the rest of the run.
        var picky = new FakeSender(_clock) { Answer = _ => SendResult.Failed(404, "no such hook") };

        Dispatch(Plan("a", "b", "c"), State(), [(Channel("hook", HookScheme.Http), picky)],
            NotifySettings.Default with { Retries = 0 });

        picky.Attempts.ShouldBe(3);
    }

    [Fact]
    public void ARetryableStatusIsRetriedUpToTheConfiguredCount()
    {
        var flaky = new FakeSender(_clock)
        {
            Answer = n => n < 3 ? SendResult.Failed(503, "busy") : SendResult.Delivered(),
        };

        var report = Dispatch(Plan("iis"), State(), [(Channel("hook", HookScheme.Http), flaky)],
            NotifySettings.Default with { Retries = 2 });

        flaky.Attempts.ShouldBe(3, "the first attempt plus two retries");
        report.Delivered.ShouldHaveSingleItem();
    }

    [Fact]
    public void ANonRetryableStatusIsNeverRepeated()
    {
        // 401 is the one that matters: repeating a rejected credential against a rate-limited
        // endpoint is how a misconfiguration becomes a lockout.
        var rejected = new FakeSender(_clock) { Answer = _ => SendResult.Failed(401, "denied") };

        Dispatch(Plan("iis"), State(), [(Channel("hook", HookScheme.Http), rejected)],
            NotifySettings.Default with { Retries = 5 });

        rejected.Attempts.ShouldBe(1);
    }

    // ---- the budget -------------------------------------------------------------------------

    [Fact]
    public void ADeadChannelDoesNotEatTheWholeBudget()
    {
        // An even split rather than first-come. Without it the unreachable relay spends the whole
        // thirty seconds on connection timeouts and the working channel behind it is never tried -
        // one broken destination silencing the healthy ones, which is the failure this feature
        // exists to prevent.
        var slow = new FakeSender(_clock)
        {
            Answer = _ => SendResult.Unreachable("timed out"),
            Cost = TimeSpan.FromSeconds(14),
        };

        var quick = new FakeSender(_clock);

        var report = Dispatch(Plan("iis"), State(), [(Channel("email.relay", HookScheme.Smtp), slow), (Channel("hook", HookScheme.Http), quick)],
            NotifySettings.Default with { Retries = 0 }, budget: TimeSpan.FromSeconds(30));

        quick.Attempts.ShouldBe(1, "the healthy channel must still be reached");
        report.Channels.Count.ShouldBe(2);
    }

    [Fact]
    public void RetriesStopWhenTheBudgetIsSpent()
    {
        var slow = new FakeSender(_clock)
        {
            Answer = _ => SendResult.Failed(503, "busy"),
            Cost = TimeSpan.FromSeconds(4),
        };

        Dispatch(Plan("iis"), State(), [(Channel("hook", HookScheme.Http), slow)],
            NotifySettings.Default with { Retries = 5 }, budget: TimeSpan.FromSeconds(10));

        // Five retries were configured; the ten-second budget is what actually decides.
        slow.Attempts.ShouldBeLessThan(6);
        slow.Attempts.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void RunningOutOfBudgetDoesNotOpenABreakerOnAHealthyChannel()
    {
        // Not the channel's fault, so the breaker must learn nothing - otherwise a tight budget
        // suppresses a destination that was working perfectly well.
        var slow = new FakeSender(_clock) { Cost = TimeSpan.FromSeconds(20) };
        var never = new FakeSender(_clock);
        var state = State();

        Dispatch(Plan("iis"), state, [(Channel("hook", HookScheme.Http), slow), (Channel("email.relay", HookScheme.Smtp), never)],
            budget: TimeSpan.FromSeconds(10));

        never.Attempts.ShouldBe(0);
        state.ChannelOrDefault("email.relay").ConsecutiveFailures.ShouldBe(0);
    }

    [Fact]
    public void AMessageThatRanOutOfBudgetIsNotRecordedAsReported()
    {
        var slow = new FakeSender(_clock) { Cost = TimeSpan.FromSeconds(20) };
        var never = new FakeSender(_clock);

        var report = Dispatch(Plan("iis"), State(),
            [(Channel("hook", HookScheme.Http), slow), (Channel("email.relay", HookScheme.Smtp), never)],
            budget: TimeSpan.FromSeconds(10));

        report.Delivered.ShouldBeEmpty("a channel that was never reached has not been told");

        // And the operator is told. This used to assert DeliveryReport.BudgetExhausted, a flag
        // that was computed here and read by nothing in the product - so the only evidence that a
        // channel had been skipped lived in a field no code path consulted.
        report.Diagnostics.ShouldContain(d =>
            d.Code == DiagnosticCode.NotifyBudgetClamped && d.Message.Contains("never tried"));
    }

    /// <summary>
    /// A channel cut short mid-run reports what it dropped, rather than reporting success.
    /// </summary>
    /// <remarks>
    /// The defect this replaced the flag for. When a channel's share ran out partway through its
    /// messages, blocked[m] was set without incrementing failed - so ChannelOutcome carried
    /// Sent = 1, Failed = 0, Error = null, the LR5002 diagnostic was skipped (it is gated on
    /// failures), the CLI rendered OpResult.Ok, and --verbose printed "1 sent, 0 failed". The
    /// dropped messages never entered Delivered, so their state never advanced and the next run
    /// planned and dropped exactly the same ones - nightly, indefinitely, looking like success.
    /// </remarks>
    [Fact]
    public void AChannelCutShortMidRunSaysSoInsteadOfReportingSuccess()
    {
        // Two messages, one channel, and a send that overruns the whole share on its own - so
        // the first is delivered and the second is never attempted.
        var slow = new FakeSender(_clock) { Cost = TimeSpan.FromSeconds(12) };

        var report = Dispatch(Plan("iis", "app"), State(),
            [(Channel("hook", HookScheme.Http), slow)],
            budget: TimeSpan.FromSeconds(10));

        var outcome = report.Channels.ShouldHaveSingleItem();
        outcome.Sent.ShouldBe(1);
        outcome.Failed.ShouldBe(0);
        outcome.Unattempted.ShouldBe(1, "the second message was never tried, and that must be visible");

        report.Diagnostics.ShouldContain(d =>
            d.Code == DiagnosticCode.NotifyBudgetClamped
            && d.Message.Contains("never sent")
            && d.Message.Contains("budget"));
    }

    /// <summary>
    /// An abandoned channel still counts its dropped messages, and is still a failure.
    /// </summary>
    /// <remarks>
    /// A dead relay abandons the channel, so every message after the first is never attempted -
    /// which now shows up in Unattempted. It must not turn the channel into "skipped": that is the
    /// word for a breaker suppression, and an operator reading it would go looking for a cooldown
    /// instead of a broken destination.
    /// </remarks>
    [Fact]
    public void AnAbandonedChannelCountsWhatItDroppedAndIsStillAFailure()
    {
        // 0 is a connection failure, which abandons the channel for the rest of the run.
        var dead = new FakeSender(_clock) { Answer = _ => SendResult.Failed(0, "no route to host") };

        var report = Dispatch(Plan("iis", "app"), State(),
            [(Channel("hook", HookScheme.Http), dead)]);

        var outcome = report.Channels.ShouldHaveSingleItem();
        outcome.Failed.ShouldBe(1);
        outcome.Unattempted.ShouldBe(1, "the second message was never tried once the relay was abandoned");

        // And it is reported as unreachable, not as starved - a different problem with a
        // different fix.
        report.Diagnostics.ShouldContain(d => d.Code == DiagnosticCode.NotifyFailed);
        report.Diagnostics.ShouldNotContain(d => d.Code == DiagnosticCode.NotifyBudgetClamped);
    }

    /// <summary>The four outcomes a channel can report, and the order they are decided in.</summary>
    [Theory]
    [InlineData(2, 0, 0, false, OpResult.Ok)]
    [InlineData(0, 0, 0, true, OpResult.Skipped)]
    [InlineData(1, 0, 1, false, OpResult.Skipped)]
    [InlineData(0, 1, 0, false, OpResult.Failed)]

    // The one that matters: abandoned after a dead relay, so both are set. It is a failure.
    [InlineData(0, 1, 1, false, OpResult.Failed)]
    public void AChannelReportsTheOutcomeItActuallyHad(
        int sent, int failed, int unattempted, bool skipped, string expected)
    {
        new ChannelOutcome
        {
            Key = "hook",
            Display = "hook",
            Sent = sent,
            Failed = failed,
            Unattempted = unattempted,
            Skipped = skipped,
        }.Result.ShouldBe(expected);
    }

    // ---- state ------------------------------------------------------------------------------

    [Fact]
    public void AFullySuccessfulRunAdvancesTheJobState()
    {
        var report = Dispatch(Plan("iis"), State(),
            [(Channel("eventlog", HookScheme.EventLog), new FakeSender(_clock)), (Channel("hook", HookScheme.Http), new FakeSender(_clock))]);

        report.Delivered.ShouldHaveSingleItem().NextState.Outcome.ShouldBe(NotifyOutcome.Failing);
    }

    [Fact]
    public void ChannelsThatWereNeverConfiguredAreNotInvented()
    {
        var report = Dispatch(Plan("iis"), State(), []);

        report.Channels.ShouldBeEmpty();
        report.Delivered.ShouldBeEmpty();
    }

    [Fact]
    public void AFailedChannelRecordsTheErrorForTheNextRunToSee()
    {
        var state = State();
        var bad = new FakeSender(_clock) { Answer = _ => SendResult.Failed(500, "relay down") };

        Dispatch(Plan("iis"), state, [(Channel("email.relay", HookScheme.Smtp), bad)],
            NotifySettings.Default with { Retries = 0 });

        var stored = state.ChannelOrDefault("email.relay");
        stored.ConsecutiveFailures.ShouldBe(1);
        stored.LastError.ShouldBe("relay down");
    }
}
