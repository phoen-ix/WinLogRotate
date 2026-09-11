using WinLogRotate.Cli.Output;
using WinLogRotate.Contracts;
using WinLogRotate.Core;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Engine;
using WinLogRotate.Core.Journaling;
using WinLogRotate.Core.Safety;
using WinLogRotate.Core.State;
using WinLogRotate.Hosting;

namespace WinLogRotate.Cli.Commands;

internal static class RunCommand
{
    public static int Run(
        CommandContext ctx, RunOptions options, string? configDir, string? stateOverride,
        RunLockOptions? locking = null)
    {
        // Stamped before anything else, including the pause check. The notification phase reserves
        // time against the host's limit, and what it needs to know is how much of that limit the
        // rotation has already spent - so the clock has to start where the process did.
        var started = TimeProvider.System.GetUtcNow();

        var paths = InstallPaths.Resolve(configDir);

        // Taken here, before the configuration is read, and not with the hook gate 130 lines
        // below. See OverrideSupport: this qualifies the read, that one qualifies the execution.
        var guard = new PathGuard(new GuardOptions
        {
            Overrides = OverrideSupport.ForThisMachine(paths),
        });

        // Honour a pause before doing anything. Exit 0, not an error: pausing is a deliberate
        // operator action, and a scheduled task logging a daily failure because somebody opened
        // a maintenance window would train people to ignore its failures.
        if (PauseCommand.PausedUntil(paths) is { } until)
        {
            ctx.Output.Diagnostic(new CliDiagnostic
            {
                Severity = Severity.Info,
                Code = DiagnosticCode.JobSkipped,
                Message = $"Rotations are paused until {until:u}.",
                Remedy = "Run 'winlogrotate host pause' with no duration to resume early.",
            });

            ctx.Output.Line($"Paused until {until:u}; nothing was rotated.");
            return ctx.Output.Complete<RunResult>("run", ExitCode.Ok, null);
        }

        // The machine-wide gate, taken before anything is read and held for the whole run.
        //
        // Two rotations over the same files is the worst bug this product could have, and until
        // now nothing ever took this: the option was declared on the verb, the scheduled task
        // passed --lock-held-exit 0, and RunCommand never received either.
        var locks = locking ?? new RunLockOptions();
        var gate = EnterGate(ctx, locks);
        using var held = gate.Handle;

        if (!gate.Entered)
        {
            ctx.Output.Line("Another rotation is already running; nothing was done.");
            return ctx.Output.Complete<RunResult>("run", locks.HeldExitCode, null);
        }

        if (gate.Outcome == GateOutcome.AcquiredAfterAbandon)
        {
            // Worth saying rather than swallowing: a previous run was killed mid-rotation, so
            // there may be a half-renamed file or an uncompressed archive about. The planners
            // are written to cope, but the operator should know it happened.
            ctx.Output.Diagnostic(new CliDiagnostic
            {
                Severity = Severity.Warning,
                Code = DiagnosticCode.PreviousRunAbandoned,
                Message = "A previous run ended without releasing the rotation lock.",
                Remedy = "It was probably killed - by a reboot, or by the task's ExecutionTimeLimit. "
                       + "This run continues; check the journal for operations that were planned "
                       + "but never applied.",
            });
        }

        var config = ConfigLoader.Load(paths, guard);

        foreach (var d in config.Diagnostics)
        {
            ctx.Output.Diagnostic(new CliDiagnostic
            {
                Severity = d.Severity,
                Code = d.Code,
                Message = d.Message,
                Path = d.File,
                Line = d.Line == 0 ? null : d.Line,
                Column = d.Column == 0 ? null : d.Column,
                Remedy = d.Remedy,

                // Carried, not dropped. It is what tells an operator which of forty jobs is the
                // one that will not run tonight, and what lets a notification be muted per job.
                Job = d.Job,
            });
        }

        // Nothing was attempted, so this is exit 2 rather than 1 - the distinction a scheduled
        // task needs in order to treat 1 as "go read the logs" without ambiguity.
        if (config.HasErrors)
        {
            // Notified on the way out, and this is the case that matters most. "The
            // configuration is so broken that nothing rotated" is the single night an operator
            // most wants to hear about, and returning here without a notification would make it
            // the one night that says nothing at all.
            NotifyPhase.Run(ctx, paths, config, report: null, options, started);

            ctx.Output.Line("winlogrotate: the configuration has errors; nothing was attempted.");
            return ctx.Output.Complete<RunResult>("run", ExitCode.ConfigInvalid, null);
        }

        var state = StateStore.Load(stateOverride ?? paths.StateFile, out var corrupt);
        if (corrupt is not null)
        {
            ctx.Output.Diagnostic(new CliDiagnostic
            {
                Severity = Severity.Warning,
                Code = DiagnosticCode.ConfigUnreadable,
                Message = $"The state file could not be read ({corrupt}); starting from a fresh baseline.",
                Path = paths.StateFile,
                Remedy = "Every log will wait one full interval before its next rotation.",
            });
        }

        // The journal looks after itself before it is opened, so the pass never touches a file
        // it is holding. It runs through ManageJobPlanner - the same code that tidies IIS logs -
        // which is deliberate: a log rotator that leaks its own logs would be embarrassing, and
        // if manage mode ever regresses this is where it shows up first.
        var journalSettings = config.Journal;

        if (!options.DryRun && journalSettings.Enabled)
        {
            var tidied = JournalMaintenance.Run(
                paths.JournalDirectory, journalSettings, TimeProvider.System);

            foreach (var error in tidied.Errors)
            {
                ctx.Output.Diagnostic(new CliDiagnostic
                {
                    Severity = Severity.Warning,
                    Code = DiagnosticCode.RotationFailed,
                    Message = $"Journal maintenance: {error}",

                    // Attributed, so it can be muted like any other job and so it stops merging
                    // into the run scope's aggregation - where a full journal directory made
                    // "the configuration is broken" look like a different problem each time.
                    Job = JournalMaintenance.JobName,
                });
            }

            if (tidied.DidAnything && ctx.Output.Verbose)
            {
                ctx.Output.Line(
                    $"journal: {tidied.Compressed} compressed, {tidied.Deleted} removed, "
                    + $"{GlobCommand.Humanize(tidied.BytesFreed)} freed");
            }
        }

        using IJournal journal = options.DryRun || !journalSettings.Enabled
            ? new NullJournal()
            : JournalWriter.Open(
                paths.JournalDirectory, TimeProvider.System, maxSize: journalSettings.MaxSize);

        // Taken here, immediately before rotation, and not at configuration load. A run reads its
        // configuration and then works for an hour; a directory's permissions can be changed in
        // between, by exactly the person the check exists to stop.
        var hooks = HookSupport.ForThisMachine(paths);

        // Once per run, and only where it costs somebody something. A machine with no hooks
        // configured has nothing refused, and telling it nightly that its conf.d could be tighter
        // would be a warning nobody can act on about a feature nobody uses.
        if (hooks.Finding is { } insecure
            && config.Jobs.Any(j => j.Enabled && (j.PreRotate.Count > 0 || j.PostRotate.Count > 0)))
        {
            ctx.Output.Diagnostic(insecure);
        }

        var runner = new RotationRunner(
            journal, guard, state, TimeProvider.System,
            archiveSource: null, hooks.Host, hooks.Gate);

        var report = runner.Run(config, options with { Started = started });

        foreach (var plan in report.Plans)
        {
            ctx.Output.Line($"[{plan.JobName}] {plan.MatchedFiles} file(s) matched");
            foreach (var op in plan.Operations)
            {
                var verb = options.DryRun ? "would" : "did";
                var line = op.Action switch
                {
                    PlannedAction.Skip => $"  skip      {op.Source}  ({op.Reason})",
                    PlannedAction.Compress => $"  {verb} zip  {op.Source}  ({op.Reason})",
                    PlannedAction.Delete => $"  {verb} del  {op.Source}  ({op.Reason})",
                    PlannedAction.CreateDirectory => $"  {verb} mkdir {op.Source}  ({op.Reason})",
                    _ => $"  {verb} {op.Action}  {op.Source}  ({op.Reason})",
                };
                ctx.Output.Line(line);
            }
        }

        // Forwarded as the engine classified them, not re-labelled. Reporting every failure as
        // an Error with DiagnosticCode.RotationFailed used to describe a refused dangerous path
        // - a security decision the guard made deliberately - as a rotation that went wrong,
        // and dropped the job name and the Win32 code on the way.
        foreach (var d in report.Diagnostics)
        {
            ctx.Output.Diagnostic(d);
        }

        var refused = config.SkippedJobs.Count > 0
            ? $" {config.SkippedJobs.Count} job(s) skipped: {string.Join(", ", config.SkippedJobs)}."
            : string.Empty;

        ctx.Output.Line((options.DryRun
            ? $"dry run: {report.JobsRun} job(s), {report.Plans.Sum(p => p.Destructive.Count())} operation(s) planned. Nothing was changed."
            : $"{report.JobsRun} job(s), {report.Completed} operation(s), {GlobCommand.Humanize(report.BytesFreed)} freed, {report.Failed} failure(s).")
            + refused);

        // After the journal is closed and after state.Save, so a notification can never delay or
        // fail the thing it is reporting on.
        NotifyPhase.Run(ctx, paths, config, report, options, started);

        var result = new RunResult
        {
            RunId = report.RunId,
            DryRun = options.DryRun,
            JobsConsidered = report.JobsConsidered,
            JobsRun = report.JobsRun,
            Completed = report.Completed,
            Failed = report.Failed,
            BytesFreed = report.BytesFreed,
            Errors = report.Errors,
        };

        // A job refused during validation never reaches the runner, so the runner cannot know it
        // happened and reports a clean run. Exit 1, not 2: work was attempted and most of it
        // succeeded, which is exactly the distinction ExitCode.ConfigInvalid's own doc comment
        // draws when it says nothing on disk was touched.
        var exit = report.ExitCode == ExitCode.Ok && config.SkippedJobs.Count > 0
            ? ExitCode.Errors
            : report.ExitCode;

        return ctx.Output.Complete("run", exit, result);
    }
    /// <summary>
    /// What taking the gate concluded, in terms the platform-neutral caller can read.
    /// </summary>
    /// <remarks>
    /// RotationGate is [SupportedOSPlatform("windows")], so its members cannot be touched from
    /// here. Returning the handle as an IDisposable alongside a plain enum keeps the analyzer
    /// satisfied without an annotation spreading up into every caller of the run verb.
    /// </remarks>
    private readonly record struct GateResult(IDisposable? Handle, bool Entered, GateOutcome Outcome);

    /// <summary>
    /// Takes the machine-wide rotation gate.
    /// </summary>
    /// <remarks>
    /// Two cases carry on without one, and neither is a failure: the operator passed
    /// --skip-state-lock, and this is not Windows, where the gate is a named kernel mutex.
    /// </remarks>
    private static GateResult EnterGate(CommandContext ctx, RunLockOptions locking)
    {
        if (locking.Skip)
        {
            ctx.Output.Diagnostic(new CliDiagnostic
            {
                Severity = Severity.Warning,
                Code = DiagnosticCode.JobSkipped,
                Message = "The rotation lock was skipped, so nothing prevents two runs overlapping.",
                Remedy = "Only use --skip-state-lock where a named kernel mutex is unavailable.",
            });
            return new GateResult(null, Entered: true, GateOutcome.Acquired);
        }

        return OperatingSystem.IsWindows()
            ? EnterOnWindows(locking)
            : new GateResult(null, Entered: true, GateOutcome.Acquired);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static GateResult EnterOnWindows(RunLockOptions locking)
    {
        var gate = RotationGate.Enter(locking.Wait ? locking.WaitFor : TimeSpan.Zero);
        return new GateResult(gate, gate.Entered, gate.Outcome);
    }

}
