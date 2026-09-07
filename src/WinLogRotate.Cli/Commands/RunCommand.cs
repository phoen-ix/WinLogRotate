using WinLogRotate.Cli.Output;
using WinLogRotate.Contracts;
using WinLogRotate.Core;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Engine;
using WinLogRotate.Core.Journaling;
using WinLogRotate.Core.Safety;
using WinLogRotate.Core.State;

namespace WinLogRotate.Cli.Commands;

internal static class RunCommand
{
    public static int Run(CommandContext ctx, RunOptions options, string? configDir, string? stateOverride)
    {
        var paths = InstallPaths.Resolve(configDir);
        var guard = new PathGuard(new GuardOptions { Elevated = Privilege.IsElevated() });

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
            });
        }

        // Nothing was attempted, so this is exit 2 rather than 1 - the distinction a scheduled
        // task needs in order to treat 1 as "go read the logs" without ambiguity.
        if (config.HasErrors)
        {
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
        var journalSettings = LoadJournalSettings(paths);

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

        var runner = new RotationRunner(journal, guard, state, TimeProvider.System);
        var report = runner.Run(config, options);

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
                    _ => $"  {verb} {op.Action}  {op.Source}  ({op.Reason})",
                };
                ctx.Output.Line(line);
            }
        }

        foreach (var error in report.Errors)
        {
            ctx.Output.Diagnostic(new CliDiagnostic
            {
                Severity = Severity.Error,
                Code = DiagnosticCode.RotationFailed,
                Message = error,
            });
        }

        ctx.Output.Line(options.DryRun
            ? $"dry run: {report.JobsRun} job(s), {report.Plans.Sum(p => p.Destructive.Count())} operation(s) planned. Nothing was changed."
            : $"{report.JobsRun} job(s), {report.Completed} operation(s), {GlobCommand.Humanize(report.BytesFreed)} freed, {report.Failed} failure(s).");

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

        return ctx.Output.Complete("run", report.ExitCode, result);
    }

    /// <summary>
    /// Reads <c>[journal]</c> from config.toml. A malformed config.toml has already been
    /// reported by the loader above, so failures here fall back to the defaults rather than
    /// reporting the same problem twice.
    /// </summary>
    private static JournalSettings LoadJournalSettings(InstallPaths paths)
    {
        if (!File.Exists(paths.ConfigFile))
        {
            return JournalSettings.Default;
        }

        try
        {
            var file = TomlFile.Load(paths.ConfigFile);
            return file.HasErrors
                ? JournalSettings.Default
                : ConfigBinder.BindJournal(file, new DiagnosticBag());
        }
        catch (IOException)
        {
            return JournalSettings.Default;
        }
    }
}
