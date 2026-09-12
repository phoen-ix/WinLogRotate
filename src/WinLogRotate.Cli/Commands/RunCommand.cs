using WinLogRotate.Cli.Output;
using WinLogRotate.Contracts;
using WinLogRotate.Core;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Engine;
using WinLogRotate.Core.Journaling;
using WinLogRotate.Core.Safety;
using WinLogRotate.Core.Secrets;
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
            // Said as a diagnostic and not only as a line, because ok is false whenever the exit
            // code is not zero and an empty diagnostics array beside it leaves a caller with a
            // bare 3. Info, not an error: this is the expected outcome of an overlapping manual
            // run, which is why the registered task passes --lock-held-exit 0.
            ctx.Output.Diagnostic(new CliDiagnostic
            {
                Severity = Severity.Info,
                Code = DiagnosticCode.AlreadyRunning,
                Message = "Another rotation is already running; nothing was done.",
                Remedy = "This is normal when a manual run overlaps the scheduled one.",
            });

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

        // The machine that will need the credential tonight, and usually the one identity - SYSTEM -
        // whose "no such secret" is worth trusting.
        var config = ConfigLoader.Load(
            paths, guard, new StoreSecretLookup(Senders.Platform(), paths.SecretsFile));

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
                Code = DiagnosticCode.StateUnreadable,
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
                    Code = DiagnosticCode.JournalUnavailable,
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

        // Not a "using" of its own: the tee below takes ownership and disposes it. Two using
        // declarations over one writer double-dispose it, and JournalWriter.Dispose flushes
        // rather than checking - so every real run ended on an ObjectDisposedException from the
        // last line of the verb, after all the work was done and before the exit code was
        // returned. The test that runs the verb for real found it on the first attempt.
        IJournal durable = new NullJournal();

        if (!options.DryRun && journalSettings.Enabled)
        {
            try
            {
                durable = JournalWriter.Open(
                    paths.JournalDirectory, TimeProvider.System, maxSize: journalSettings.MaxSize);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The rotation goes ahead without it. docs/diagnostics.md names three channels,
                // each for a different reader, and this one is "whoever is asking what happened
                // to a specific file" - losing it costs that reader and nobody else. Unguarded,
                // a journal directory that had become a file, or a disk with nothing left on it,
                // took the whole run down before a single log was touched and reported LR1006:
                // "a defect in the product, not a problem with the machine".
                //
                // JournalMaintenance, ten lines above, has always reported its own failures this
                // way. Opening the file was the half nobody had guarded.
                ctx.Output.Diagnostic(new CliDiagnostic
                {
                    Severity = Severity.Warning,
                    Code = DiagnosticCode.JournalUnavailable,
                    Message = $"The journal could not be opened: {e.Message}",
                    Path = paths.JournalDirectory,
                    Job = JournalMaintenance.JobName,
                    Remedy = "The rotation went ahead. 'winlogrotate journal' will not show this "
                           + "run; check free space and the permissions on that directory.",
                });
            }
        }

        // Wrapped unconditionally, NullJournal included. A dry run is the one that most needs to
        // say what it would do, and it is the run that journals nothing - so making the tee
        // conditional on a real journal would leave --dry-run silent on exactly the channel the
        // GUI reads. The seam is two members wide, which is why the whole engine can be given a
        // voice from the Cli project without Core learning what a sink is.
        using IJournal journal = new TeeJournal(durable, ctx.Output, TimeProvider.System, options.DryRun);

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

        // Read before Complete, while the writer is still alive. A disk that filled mid-rotation
        // used to throw out of Write - between two halves of a rotation - and reach
        // CommandContext.Guarded as LR1006, reporting a run that was half done as a defect in the
        // product. It is latched now, so this is the one place it is said.
        if (durable is JournalWriter { Fault: { } fault })
        {
            ctx.Output.Diagnostic(new CliDiagnostic
            {
                Severity = Severity.Warning,
                Code = DiagnosticCode.JournalUnavailable,
                Message = $"The journal stopped accepting entries partway through: {fault.Message}",
                Path = paths.JournalDirectory,
                Job = JournalMaintenance.JobName,
                Remedy = "The rotation went ahead. This run's record is incomplete; "
                       + "check free space on that directory.",
            });
        }

        // The per-operation report used to be hand-printed here, from report.Plans, in a format
        // of its own - a second renderer for facts the sinks already had, reaching only the
        // channel --json throws away. The sinks render it now, from the events the tee carries,
        // so text and NDJSON describe the same run instead of two versions of it.

        // Forwarded as the engine classified them, not re-labelled. Reporting every failure as
        // an Error with DiagnosticCode.RotationFailed used to describe a refused dangerous path
        // - a security decision the guard made deliberately - as a rotation that went wrong,
        // and dropped the job name and the Win32 code on the way.
        foreach (var d in report.Diagnostics)
        {
            ctx.Output.Diagnostic(d);
        }

        var refused = config.SkippedJobs.Count > 0
            ? $" {config.SkippedJobs.Count} job(s) skipped: "
              + $"{string.Join(", ", config.SkippedJobs.Select(j => j.Name))}."
            : string.Empty;

        ctx.Output.Line((options.DryRun
            ? $"dry run: {report.JobsRun} job(s), {report.Plans.Sum(p => p.Destructive.Count())} operation(s) planned. Nothing was changed."
            : $"{report.JobsRun} job(s), {report.Completed} operation(s), {GlobCommand.Humanize(report.BytesFreed)} freed, {report.Failed} failure(s)."
              + (report.Retries > 0 ? $" {report.Retries} retry(s) - something else was holding a file." : string.Empty))
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
            SkippedJobs = [.. config.SkippedJobs.Select(j => j.Name)],
        };

        // A job refused during validation never reaches the runner, so the runner cannot know it
        // happened and reports a clean run. Exit 1, not 2: work was attempted and most of it
        // succeeded, which is exactly the distinction ExitCode.ConfigInvalid's own doc comment
        // draws when it says nothing on disk was touched.
        //
        // A file that would not parse counts the same way, and it is the half that used to be
        // missing: before it was scoped to its own file it made HasErrors true and this method
        // returned above, having attempted nothing on the machine. Carrying on without counting it
        // would be the opposite mistake - forty jobs rotated, one file silently out of the
        // configuration, and exit 0.
        var exit = report.ExitCode == ExitCode.Ok
            && (config.SkippedJobs.Count > 0 || config.Unloadable.Count > 0)
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
