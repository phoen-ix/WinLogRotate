using WinLogRotate.Cli.Output;
using WinLogRotate.Contracts;
using WinLogRotate.Core;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Engine;
using WinLogRotate.Core.Journaling;
using WinLogRotate.Core.Notify;
using WinLogRotate.Core.Notify.Delivery;
using WinLogRotate.Core.Secrets;
using WinLogRotate.Core.State;

namespace WinLogRotate.Cli.Commands;

/// <summary>
/// The notification phase of a run: decide, deliver, record.
/// </summary>
/// <remarks>
/// <para>
/// Called from <b>both</b> exits of <see cref="RunCommand.Run"/> - after a completed run, and
/// after a configuration that would not load. The second is the case that matters most: "nothing
/// rotated at all because the configuration is broken" is the single night an operator most
/// wants to hear about, and a phase placed only after a successful run is the one that stays
/// silent for it.
/// </para>
/// <para>
/// The order here is load-bearing. Diagnostics are snapshotted before anything is sent, so a
/// delivery failure can never become part of the next run's idea of what was wrong; the dry-run
/// guard sits above delivery rather than below it; and state is advanced only for the messages the
/// dispatcher reports as delivered - see <see cref="HookDispatcher"/> for what that means and why
/// "at least one channel" is not it.
/// </para>
/// </remarks>
internal static class NotifyPhase
{
    /// <param name="report">Null when the run never started.</param>
    /// <param name="started">
    /// When the invocation began, for the deadline clamp. The phase's own clock reading would be
    /// useless: what matters is how much of the host's limit the rotation already spent.
    /// </param>
    public static void Run(
        CommandContext ctx, InstallPaths paths, LoadedConfig config,
        RunReport? report, RunOptions options, DateTimeOffset started)
    {
        if (!options.Notify)
        {
            return;
        }

        var settings = config.Notify;
        if (!settings.Enabled || settings.On == NotifyOn.Never)
        {
            return;
        }

        var statePath = paths.NotifyStateFile;
        var state = NotifyStateStore.Load(statePath);

        if (state.Warning is { } warning)
        {
            ctx.Output.Diagnostic(new CliDiagnostic
            {
                Severity = Severity.Warning,
                Code = DiagnosticCode.NotifyStateUnreadable,
                Message = $"Notification history was reset: {warning}.",
                Path = statePath,
                Remedy = "Change detection starts over, so the next run may repeat a message you "
                       + "have already had. Nothing else is affected.",
            });
        }

        // The sink's list, not the report's. RotationRunner only ever appends job-scoped
        // failures; the config-load diagnostics - an insecure conf.d, an unreadable secret
        // store, a configuration that would not parse - exist only here, and they are precisely
        // the ones worth mailing. IOutputSink.Diagnostics says as much in its own doc comment.
        //
        // Snapshotted before the phase reports anything of its own, so a notification failure
        // cannot become part of the next run's idea of what was wrong.
        var seen = ctx.Output.Diagnostics.ToArray();

        var summary = new RunSummary
        {
            RunId = report?.RunId ?? string.Empty,
            Machine = Environment.MachineName,
            JobsRun = report?.JobsRun ?? 0,
            Completed = report?.Completed ?? 0,
            BytesFreed = report?.BytesFreed ?? 0,
            ObservedJobs = report is null ? [] : [.. report.Plans.Select(p => p.JobName)],

            // The journal's own upkeep is muted the same way any other job is, rather than
            // through a special case in the planner - so it obeys exactly the rules everything
            // else does, and turning it on is a config key rather than a code change.
            MutedJobs =
            [
                .. config.Jobs.Where(j => !j.Notify).Select(j => j.Name),
                .. config.Journal.Notify ? Array.Empty<string>() : [JournalMaintenance.JobName],
            ],
        };

        var now = TimeProvider.System.GetUtcNow();
        var plan = NotificationPlanner.PlanFor(summary, seen, settings, state, now);

        if (ctx.Output.Verbose)
        {
            foreach (var line in plan.Suppressed)
            {
                ctx.Output.Line($"notify: {line}");
            }
        }

        // Above delivery, not below it. A dry run must describe what it would send without
        // sending it, and without moving a breaker counter.
        if (options.DryRun)
        {
            foreach (var message in plan.Messages)
            {
                ctx.Output.Event(PlanEvent(summary, message));

                if (ctx.Output.Verbose)
                {
                    ctx.Output.Line($"notify: would send - {message.Subject}");
                }
            }

            return;
        }

        Deliver(ctx, paths, config, summary, plan, settings, state, options, started, now);

        // Job state to record even though nothing was sent - the first run, where outcomes are
        // noted so the next genuine change notifies and installing monitoring does not produce a
        // wall of alerts about problems that were already there.
        foreach (var (job, next) in plan.Baseline)
        {
            state.SetJob(job, next);
        }

        try
        {
            state.Prune(TimeProvider.System.GetUtcNow(), TimeSpan.FromDays(30));
            state.Save(TimeProvider.System);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ctx.Output.Diagnostic(new CliDiagnostic
            {
                Severity = Severity.Warning,
                Code = DiagnosticCode.NotifyStateUnreadable,
                Message = $"Notification history could not be saved: {e.Message}",
                Path = statePath,
            });
        }
    }

    private static void Deliver(
        CommandContext ctx, InstallPaths paths, LoadedConfig config, RunSummary summary,
        NotificationPlan plan, NotifySettings settings, NotifyStateStore state,
        RunOptions options, DateTimeOffset started, DateTimeOffset now)
    {
        if (plan.Messages.Count == 0)
        {
            return;
        }

        var (allowed, clamped) = NotifyBudget.For(settings.Budget, options.RunDeadline, started, now);

        if (clamped)
        {
            ctx.Output.Diagnostic(new CliDiagnostic
            {
                Severity = Severity.Warning,
                Code = DiagnosticCode.NotifyBudgetClamped,
                Message = allowed <= TimeSpan.Zero
                    ? "The rotation used the whole time the scheduled task allows, so nothing was notified."
                    : $"The notification phase was cut to {allowed.TotalSeconds:0}s to stay inside the "
                    + "scheduled task's time limit.",
                Remedy = "Nothing was recorded as reported, so the next run says it again. Raise the "
                       + "task's ExecutionTimeLimit, or find out why the rotation itself is slow.",
            });
        }

        if (allowed <= TimeSpan.Zero)
        {
            // Deliberately not advancing anything: nobody was told, so nothing is old news.
            return;
        }

        using var senders = Senders.Build(settings);

        var resolved = ChannelResolver.Resolve(
            settings, config.NotifyProviders,
            new SecretResolver(Senders.Platform(), paths.SecretsFile),
            senders.Table);

        foreach (var diagnostic in resolved.Diagnostics)
        {
            ctx.Output.Diagnostic(diagnostic);
        }

        if (resolved.Channels.Count == 0)
        {
            return;
        }

        var delivery = HookDispatcher.Dispatch(
            plan, summary, resolved.Channels, settings, state, senders.Table,
            new DispatchOptions
            {
                Clock = TimeProvider.System,
                Wait = Thread.Sleep,
                Budget = allowed,
            });

        foreach (var diagnostic in delivery.Diagnostics)
        {
            ctx.Output.Diagnostic(diagnostic);
        }

        foreach (var channel in delivery.Channels)
        {
            ctx.Output.Event(new CliEvent
            {
                Ts = string.Empty,
                Run = summary.RunId,
                Operation = Op.Hook,
                Phase = Phase.Apply,
                Src = "notify",
                Dst = channel.Display,
                Result = channel.Skipped ? OpResult.Skipped
                    : channel.Failed > 0 ? OpResult.Failed : OpResult.Ok,
                Reason = channel.Error,
            });

            if (ctx.Output.Verbose)
            {
                ctx.Output.Line(channel.Skipped
                    ? $"notify: {channel.Display} skipped - suppressed after repeated failures"
                    : $"notify: {channel.Display} - {channel.Sent} sent, {channel.Failed} failed");
            }
        }

        // The rule this whole feature turns on: only messages every attempted channel accepted.
        // Recording the rest as reported would make the next run treat a live incident as old
        // news, which is silence compounding into permanent silence.
        foreach (var message in delivery.Delivered)
        {
            state.SetJob(message.Job, message.NextState);
        }
    }

    private static CliEvent PlanEvent(RunSummary summary, PlannedNotification message) => new()
    {
        Ts = string.Empty,
        Run = summary.RunId,
        Operation = Op.Hook,
        Phase = Phase.Plan,
        Job = message.Job == NotifyStateDocument.RunScope ? null : message.Job,
        Src = "notify",
        Reason = message.Subject,
    };
}
