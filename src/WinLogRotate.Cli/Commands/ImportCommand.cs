using WinLogRotate.Cli.Output;
using WinLogRotate.Contracts;
using WinLogRotate.Core;
using WinLogRotate.Core.Import;

namespace WinLogRotate.Cli.Commands;

internal static class ImportCommand
{
    public static int Run(CommandContext ctx, string source, string? outDirectory, string? configDir)
    {
        if (!File.Exists(source))
        {
            return Refusals.CannotUse<ImportResult>(
                ctx, "import", source, "a file that exists",
                "Point it at a logrotate configuration, such as /etc/logrotate.conf "
                + "copied off the machine being migrated.");
        }

        var paths = InstallPaths.Resolve(configDir);
        var target = outDirectory ?? paths.ConfigDirectory;

        var jobs = LogrotateImporter.Import(File.ReadAllText(source), source);

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

        Directory.CreateDirectory(target);
        var written = new List<string>();
        var needingReview = 0;
        var refused = 0;

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

            File.WriteAllText(path, job.Toml);
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
}
