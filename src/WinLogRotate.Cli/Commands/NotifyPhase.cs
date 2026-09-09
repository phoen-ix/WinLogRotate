using WinLogRotate.Cli.Output;
using WinLogRotate.Contracts;
using WinLogRotate.Core;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Engine;
using WinLogRotate.Core.Notify;
using WinLogRotate.Core.State;

namespace WinLogRotate.Cli.Commands;

/// <summary>
/// The notification phase of a run: decide, record, and (from milestone 10) deliver.
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
/// Nothing is sent in milestone 8. The planner's decisions are proven before there is any way to
/// page somebody with a bug in them.
/// </para>
/// </remarks>
internal static class NotifyPhase
{
    /// <param name="report">Null when the run never started.</param>
    public static void Run(
        CommandContext ctx, InstallPaths paths, LoadedConfig config,
        RunReport? report, RunOptions options)
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

        var statePath = Path.Combine(paths.Root, "notify.json");
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
        };

        var plan = NotificationPlanner.PlanFor(summary, seen, settings, state, TimeProvider.System.GetUtcNow());

        if (ctx.Output.Verbose)
        {
            foreach (var line in plan.Suppressed)
            {
                ctx.Output.Line($"notify: {line}");
            }
        }

        foreach (var message in plan.Messages)
        {
            // Milestone 8 has no senders. The plan is journalled and described, and the state is
            // deliberately NOT advanced - see below.
            ctx.Output.Event(new CliEvent
            {
                Ts = string.Empty,
                Run = summary.RunId,
                Operation = Op.Hook,
                Phase = Phase.Plan,
                Job = message.Job == NotifyStateDocument.RunScope ? null : message.Job,
                Src = "notify",
                Reason = message.Subject,
            });

            if (ctx.Output.Verbose)
            {
                ctx.Output.Line($"notify: would send - {message.Subject}");
            }
        }

        // Baselines are recorded; messages are not. The rule is that the notified side of state
        // advances only when something was actually delivered, and in this milestone nothing
        // ever is. Recording "we have seen this failure" without having told anybody would make
        // the next run treat a live incident as old news - silence compounding into permanent
        // silence, which is the worst thing this feature could do.
        foreach (var (job, next) in plan.Baseline)
        {
            state.SetJob(job, next);
        }

        if (options.DryRun)
        {
            return;
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
}
