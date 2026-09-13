using WinLogRotate.Cli.Output;
using WinLogRotate.Contracts;
using WinLogRotate.Core;
using WinLogRotate.Core.Import;

namespace WinLogRotate.Cli.Commands;

internal static class ImportCommand
{
    /// <summary>What decides whether this process is elevated. A seam for the tests.</summary>
    private static readonly Func<bool> Elevated = Privilege.IsElevated;

    public static int Run(CommandContext ctx, string source, string? outDirectory, string? configDir) =>
        Run(ctx, source, outDirectory, InstallPaths.Resolve(configDir), elevated: null);

    /// <summary>The same, for a caller that already knows where the installation lives.</summary>
    internal static int Run(
        CommandContext ctx, string source, string? outDirectory, InstallPaths paths, Func<bool>? elevated)
    {
        if (!File.Exists(source))
        {
            return Refusals.CannotUse<ImportResult>(
                ctx, "import", source, "a file that exists",
                "Point it at a logrotate configuration, such as /etc/logrotate.conf "
                + "copied off the machine being migrated.");
        }

        var target = outDirectory ?? paths.ConfigDirectory;

        // The shape `job add` uses, and the gap its own remarks named: an unelevated write into
        // a per-machine conf.d escaped as an UnauthorizedAccessException that the guard reported
        // as LR1006, "a defect in the product", about a machine refusing correctly. Only the
        // product's own conf.d is guarded this way; an --out somewhere else is the caller's.
        if (outDirectory is null && paths.Scope == InstallScope.PerMachine && !(elevated ?? Elevated)())
        {
            ctx.Output.Diagnostic(new CliDiagnostic
            {
                Severity = Severity.Error,
                Code = DiagnosticCode.NeedsAdministrator,
                Message = "Writing imported jobs into the configuration directory needs administrator rights.",
                Remedy = "Run this from an elevated prompt, or pass --out to write the files somewhere "
                       + "else and review them first.",
            });

            return ctx.Output.Complete<ImportResult>("import", ExitCode.Errors, null);
        }

        string text;
        try
        {
            text = File.ReadAllText(source);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Refusals.CannotUse<ImportResult>(
                ctx, "import", source, "a file this account can read",
                $"{e.Message} Copy it somewhere readable, or run from an account that can open it.");
        }

        var jobs = LogrotateImporter.Import(text, source);

        if (jobs.Count == 0)
        {
            ctx.Output.Line($"No log blocks found in {source}.");
            return ctx.Output.Complete("import", ExitCode.Ok, new ImportResult
            {
                Source = source,
                OutputDirectory = target,
                Jobs = 0,
                NeedingReview = 0,
                Files = [],
            });
        }

        var written = new List<string>();
        var needingReview = 0;
        var refused = 0;

        try
        {
            Directory.CreateDirectory(target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Unwritable(ctx, source, target, target, e, written, needingReview);
        }

        foreach (var job in jobs)
        {
            // Read back before it is written. This verb used to hand its output to the live
            // conf.d and report ExitCode.Ok without ever parsing it, and three separate faults
            // made that a file the loader refused - each of them the ordinary outcome for a real
            // logrotate configuration. Writing a broken file is worse than writing none: until
            // recently one of them stopped every rotation on the machine, and the operator had
            // been told the migration succeeded.
            if (LogrotateImporter.Check(job) is { Count: > 0 } problems)
            {
                refused++;

                ctx.Output.Diagnostic(new CliDiagnostic
                {
                    Severity = Severity.Error,
                    Code = DiagnosticCode.ConfigInvalid,
                    Message = $"'{job.SuggestedFileName}' was not written: {problems[0].Message}",
                    Path = Path.Combine(target, job.SuggestedFileName),
                    Remedy = "This is a defect in the importer, not in your logrotate file. "
                           + "Please report it; the block it came from can be translated by hand "
                           + "in the meantime.",
                });

                continue;
            }

            // Never overwrite: an import run twice must not silently replace edits someone made
            // in between.
            var path = Path.Combine(target, job.SuggestedFileName);
            var suffix = 2;
            while (File.Exists(path))
            {
                path = Path.Combine(target,
                    Path.GetFileNameWithoutExtension(job.SuggestedFileName) + "-" + suffix + ".toml");
                suffix++;
            }

            try
            {
                ConfigWrites.Job(path, job.Toml);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // What was written before this one stays written and is reported as such; an
                // import is a set of independent files and half of them is worth having.
                return Unwritable(ctx, source, target, path, e, written, needingReview);
            }

            written.Add(path);

            ctx.Output.Line($"  {Path.GetFileName(path)}");
            foreach (var warning in job.Warnings)
            {
                ctx.Output.Line($"      {warning}");
            }

            if (job.NeedsReview)
            {
                needingReview++;
            }
        }

        ctx.Output.Line("");
        ctx.Output.Line($"{written.Count} job(s) written to {target}.");

        if (refused > 0)
        {
            ctx.Output.Line($"{refused} job(s) could not be written; see the errors above.");
        }

        // Every imported job is written disabled. An import that quietly began deleting files
        // under rules nobody had read would be an unforgivable default.
        ctx.Output.Line("All of them are disabled. Review each file, then set enabled = true.");

        if (needingReview > 0)
        {
            ctx.Output.Diagnostic(new CliDiagnostic
            {
                Severity = Severity.Warning,
                Code = DiagnosticCode.ConfigInvalid,
                Message = $"{needingReview} job(s) contain directives that could not be translated.",
                Remedy = "Each is written into the file as a TODO comment rather than guessed at.",
            });
        }

        return ctx.Output.Complete(
            "import",
            refused > 0 ? ExitCode.Errors : ExitCode.Ok,
            new ImportResult
            {
                Source = source,
                OutputDirectory = target,
                Jobs = written.Count,
                NeedingReview = needingReview,
                Files = written,
            });
    }

    /// <summary>A file could not be written; what was written before it is reported as written.</summary>
    private static int Unwritable(
        CommandContext ctx, string source, string target, string path, Exception e,
        List<string> written, int needingReview)
    {
        ctx.Output.Diagnostic(new CliDiagnostic
        {
            Severity = Severity.Error,
            Code = DiagnosticCode.ConfigUnwritable,
            Message = $"'{path}' could not be written: {e.GetType().Name}: {e.Message}",
            Path = path,
            Remedy = "Check free space and the permissions on the output directory. Files written "
                   + "before this one are listed in the result and were left in place.",
        });

        return ctx.Output.Complete("import", ExitCode.Errors, new ImportResult
        {
            Source = source,
            OutputDirectory = target,
            Jobs = written.Count,
            NeedingReview = needingReview,
            Files = written,
        });
    }
}
