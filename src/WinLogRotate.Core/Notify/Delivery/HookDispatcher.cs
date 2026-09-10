using WinLogRotate.Contracts;
using WinLogRotate.Core.State;

namespace WinLogRotate.Core.Notify.Delivery;

/// <summary>What one channel did over a whole run.</summary>
public sealed record ChannelOutcome
{
    public required string Key { get; init; }
    public required string Display { get; init; }
    public int Sent { get; init; }
    public int Failed { get; init; }

    /// <summary>The breaker was open, so nothing was attempted. Not a failure.</summary>
    public bool Skipped { get; init; }

    /// <summary>
    /// Messages this channel never tried at all - its share of the budget ran out, or a dead
    /// relay had already abandoned it.
    /// </summary>
    /// <remarks>
    /// Counted separately from <see cref="Failed"/> because nothing failed: no send was made. It
    /// is nonetheless the number that matters most, and it used to be invisible. A channel that
    /// sent one message and silently dropped five reported <c>Sent = 1, Failed = 0, Error =
    /// null</c>, which the diagnostic below skipped (it is gated on failures), which the CLI
    /// rendered as <c>OpResult.Ok</c>, and which printed "1 sent, 0 failed" under --verbose. The
    /// five undelivered messages then never advanced their state, so the next run planned them
    /// again and dropped them again - for ever, with a green line beside them every night.
    /// </remarks>
    public int Unattempted { get; init; }

    /// <summary>Already redacted.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// This channel's outcome in the <see cref="OpResult"/> vocabulary.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Here rather than at the call site so it can be tested without driving the CLI. The order is
    /// the whole content: <see cref="Failed"/> is asked before <see cref="Unattempted"/>, because a
    /// channel abandoned after a dead-relay response carries both and it is a failure. Testing
    /// Unattempted first turns every abandoned channel into "skipped" - which is the word for a
    /// breaker suppression, and sends an operator looking for a cooldown instead of a broken
    /// destination.
    /// </para>
    /// <para>
    /// And a channel that dropped messages is never "ok". Reporting it as ok is what kept the
    /// budget defect invisible: the event said ok, --verbose said "1 sent, 0 failed", and the
    /// messages that were never tried went unreported every night.
    /// </para>
    /// </remarks>
    public string Result =>
        Skipped ? OpResult.Skipped
        : Failed > 0 ? OpResult.Failed
        : Unattempted > 0 ? OpResult.Skipped
        : OpResult.Ok;
}

/// <summary>What the phase delivered, and what it must now record.</summary>
public sealed record DeliveryReport
{
    /// <summary>Messages every attempted channel accepted. Only these advance state.</summary>
    public required IReadOnlyList<PlannedNotification> Delivered { get; init; }

    public required IReadOnlyList<ChannelOutcome> Channels { get; init; }

    public required IReadOnlyList<CliDiagnostic> Diagnostics { get; init; }
}

/// <summary>Everything the dispatcher needs that is not a decision.</summary>
public sealed record DispatchOptions
{
    public required TimeProvider Clock { get; init; }

    /// <summary>How to wait between retries. <c>Thread.Sleep</c> in production.</summary>
    /// <remarks>
    /// Injected so a test can advance a <c>FakeTimeProvider</c> instead of sleeping, which is what
    /// puts retry, budget and breaker behaviour on the Linux leg with no threads and no real time.
    /// </remarks>
    public required Action<TimeSpan> Wait { get; init; }

    public required TimeSpan Budget { get; init; }

    /// <summary>0.0 to 1.0. Fixed in tests so a backoff sequence is assertable.</summary>
    public Func<double> Jitter { get; init; } = Random.Shared.NextDouble;
}

/// <summary>
/// Sends what the planner decided, and reports what actually arrived.
/// </summary>
/// <remarks>
/// <para>
/// <b>The rule this type exists to enforce:</b> a message advances the notified side of state only
/// if every channel that was <i>attempted</i> accepted it, and at least one did. The doc comment on
/// <see cref="PlannedNotification.NextState"/> used to say "at least one channel" on its own, and
/// that is a trap: <c>eventlog:</c> is a local write that essentially cannot fail, so with
/// <c>to = ["eventlog:", "email.relay"]</c> it alone would satisfy the condition, state would
/// advance every run, and a week-long mail outage would end with nobody ever emailed - not even
/// once mail came back, because by then the incident counts as old news.
/// </para>
/// <para>
/// A channel whose breaker is open is <i>skipped</i>, not attempted, so it stops blocking. That is
/// what stops one permanently broken webhook from re-sending to the healthy channels for ever: it
/// fails <c>breaker_after</c> times, opens, and everything converges. Those first few duplicates
/// are the accepted cost, and <c>ABrokenChannelStopsDuplicatingAfterBreakerAfterRuns</c> pins it.
/// </para>
/// <para>
/// Channels are attempted one at a time, in configuration order. Serial is not an oversight: the
/// budget becomes one subtraction instead of a race, and SMTP certificate pinning goes through a
/// process-global callback that is only safe because nothing else is sending.
/// </para>
/// </remarks>
public static class HookDispatcher
{
    public static DeliveryReport Dispatch(
        NotificationPlan plan,
        RunSummary run,
        IReadOnlyList<ResolvedChannel> channels,
        NotifySettings settings,
        NotifyStateStore state,
        SenderTable senders,
        DispatchOptions options)
    {
        var diagnostics = new List<CliDiagnostic>();
        var outcomes = new List<ChannelOutcome>();

        if (plan.Messages.Count == 0 || channels.Count == 0)
        {
            return new DeliveryReport { Delivered = [], Channels = [], Diagnostics = diagnostics };
        }

        var messages = plan.Messages;

        // Per message: how many channels took it, and whether any channel that was tried did not.
        var accepted = new int[messages.Count];
        var blocked = new bool[messages.Count];

        var deadline = options.Clock.GetUtcNow() + options.Budget;
        var retries = Math.Clamp(settings.Retries, 0, RetrySchedule.MaxRetries);

        for (var c = 0; c < channels.Count; c++)
        {
            var channel = channels[c];
            var remaining = deadline - options.Clock.GetUtcNow();

            if (remaining <= TimeSpan.Zero)
            {
                // Everything from here on was not reached, so nothing from here on may be
                // recorded as told.
                for (var m = 0; m < messages.Count; m++)
                {
                    blocked[m] = true;
                }

                // Said here rather than left to the caller. The comment that used to sit on this
                // line claimed "LR5005 is raised once, by the caller" - it is not: NotifyPhase
                // raises LR5005 only for NotifyBudget.For's clamp, which is a different condition
                // decided before any channel is tried. These channels were simply never reached,
                // and until now the only evidence of that was a DeliveryReport flag that nothing
                // in the product ever read.
                diagnostics.Add(Starved(
                    $"{channels.Count - c} notification channel(s) were never tried: "
                    + $"{string.Join(", ", channels.Skip(c).Select(x => x.Display))}."));

                break;
            }

            var stored = state.ChannelOrDefault(channel.Key);

            if (BreakerPolicy.Verdict(stored, settings) == BreakerVerdict.Open)
            {
                state.SetChannel(channel.Key, BreakerPolicy.Skip(stored));
                outcomes.Add(new ChannelOutcome
                {
                    Key = channel.Key,
                    Display = channel.Display,
                    Skipped = true,
                    Error = stored.LastError,
                });

                diagnostics.Add(new CliDiagnostic
                {
                    Severity = Severity.Warning,
                    Code = DiagnosticCode.NotifyCircuitOpen,
                    Message = $"{channel.Display} is suppressed after {stored.ConsecutiveFailures} "
                            + $"failed run(s); {stored.SkipRunsRemaining} more run(s) to go.",
                    Remedy = "Fix it and run 'winlogrotate notify test', or clear the counter with "
                           + $"'winlogrotate notify reset {channel.Key}'.",
                });

                continue;
            }

            // A half-open channel gets exactly one probe. Retrying a channel that has already
            // failed five runs in a row is how a cooldown becomes decoration.
            var halfOpen = BreakerPolicy.Verdict(stored, settings) == BreakerVerdict.HalfOpen;
            var allowed = halfOpen ? 0 : retries;

            var share = NotifyBudget.Share(remaining, channels.Count - c);
            var until = options.Clock.GetUtcNow() + share;

            var sender = senders.For(channel);
            var sent = 0;
            var failed = 0;
            var unattempted = 0;
            var starved = 0;
            string? lastError = null;
            var abandoned = false;

            for (var m = 0; m < messages.Count; m++)
            {
                if (abandoned)
                {
                    blocked[m] = true;
                    unattempted++;
                    continue;
                }

                var left = until - options.Clock.GetUtcNow();
                if (left <= TimeSpan.Zero)
                {
                    // Out of this channel's share. Not the channel's fault, so it is not a
                    // breaker failure - but the message did not get there, so it cannot count
                    // as told, and it must not pass in silence either.
                    blocked[m] = true;
                    unattempted++;
                    starved++;
                    continue;
                }

                var result = Attempt(sender, channel, messages[m], run, settings, options, allowed, until);

                if (result.Ok)
                {
                    accepted[m]++;
                    sent++;
                    continue;
                }

                failed++;
                blocked[m] = true;
                lastError = result.Error;

                // A dead relay is dead. Retrying it once per message multiplies the spend by the
                // message count and reaches nobody either way; a 4xx is about this one message,
                // so the next one still goes.
                if (result.Status == 0 || result.Status >= 500)
                {
                    abandoned = true;
                }
            }

            outcomes.Add(new ChannelOutcome
            {
                Key = channel.Key,
                Display = channel.Display,
                Sent = sent,
                Failed = failed,
                Unattempted = unattempted,
                Error = lastError,
            });

            if (starved > 0)
            {
                // Reported whether or not anything also failed, because it is a different
                // problem with a different fix: a failure means the destination is wrong or
                // unreachable, this means the phase was not given enough time to finish. A
                // channel that sent one message and dropped five used to report Sent = 1,
                // Failed = 0 and say nothing at all.
                diagnostics.Add(Starved(
                    $"{channel.Display}: {starved} message(s) were never sent - this channel's "
                    + "share of the notification budget ran out."));
            }

            if (sent + failed == 0)
            {
                // Never attempted, so the breaker learns nothing. Recording a failure here would
                // let a tight budget open a breaker on a perfectly healthy channel.
                continue;
            }

            if (failed > 0)
            {
                diagnostics.Add(new CliDiagnostic
                {
                    Severity = Severity.Warning,
                    Code = DiagnosticCode.NotifyFailed,
                    Message = $"{channel.Display} could not be reached: {lastError}",
                    Remedy = abandoned
                        ? "The rotation itself is unaffected. Run 'winlogrotate notify test' once it is reachable."
                        : "The rotation itself is unaffected. Check the target and the message size.",
                });
            }

            // Once per channel per run, with the run's final outcome. Recording per retry would
            // make breaker_after = 5 mean five thirds of a run.
            state.SetChannel(channel.Key, BreakerPolicy.Record(
                stored,
                succeeded: sent > 0,
                error: lastError,
                settings,
                options.Clock.GetUtcNow()));
        }

        var delivered = new List<PlannedNotification>();
        for (var m = 0; m < messages.Count; m++)
        {
            if (accepted[m] > 0 && !blocked[m])
            {
                delivered.Add(messages[m]);
            }
        }

        return new DeliveryReport
        {
            Delivered = delivered,
            Channels = outcomes,
            Diagnostics = diagnostics,
        };
    }

    /// <summary>
    /// A message that was never sent because the phase ran out of time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A warning rather than an error, and it says the rotation is unaffected, because it is: the
    /// files moved, and what failed is the reporting of it. But it is never silent. The undelivered
    /// messages do not advance their state - <see cref="DeliveryReport.Delivered"/> excludes them
    /// deliberately - so the next run plans exactly the same messages and, on an unchanged budget,
    /// drops them again. Without this line that repeats nightly and looks like success.
    /// </para>
    /// <para>
    /// The breaker is deliberately untouched by this. Starvation is not a channel fault, and
    /// opening a breaker on a slow-but-working destination would stop delivery altogether - it
    /// would turn "some messages were late" into "this channel is suppressed", which is the
    /// opposite of the remedy.
    /// </para>
    /// </remarks>
    private static CliDiagnostic Starved(string message) => new()
    {
        Severity = Severity.Warning,
        Code = DiagnosticCode.NotifyBudgetClamped,
        Message = message,
        Remedy = "Nothing was recorded as reported, so the next run says it again - and will drop "
               + "it again unless something changes. Raise [notify] budget, reduce the number of "
               + "targets, or find out which one is slow with 'winlogrotate notify test'.",
    };

    /// <summary>One message on one channel, with retries, inside what is left.</summary>
    private static SendResult Attempt(
        INotifySender sender, ResolvedChannel channel, PlannedNotification message, RunSummary run,
        NotifySettings settings, DispatchOptions options, int retries, DateTimeOffset until)
    {
        // Redacted here rather than in the planner. The subject is composed from the machine and
        // job names, which are local facts, and stays unmasked in the journal and on stdout; this
        // is the boundary where it leaves the machine.
        var composed = new NotifyMessage
        {
            Subject = Redaction.MaskText(message.Subject, settings.Redact),
            Body = MessageComposer.Render(
                message, run, channel.Limit, options.Clock.GetUtcNow(), settings.Redact),
            Plan = message,
            Run = run,
        };

        SendResult result = default;

        for (var attempt = 0; ; attempt++)
        {
            var left = until - options.Clock.GetUtcNow();
            if (left <= TimeSpan.Zero)
            {
                return result.Error is null ? SendResult.Unreachable("the notification budget ran out") : result;
            }

            result = sender.Send(channel, composed, left);

            if (result.Ok || attempt >= retries || !RetrySchedule.IsRetryable(result.Status))
            {
                return result;
            }

            var delay = RetrySchedule.Delay(
                attempt + 1, result.RetryAfter, until - options.Clock.GetUtcNow(), options.Jitter());

            if (delay is not { } wait)
            {
                return result;
            }

            options.Wait(wait);
        }
    }
}
