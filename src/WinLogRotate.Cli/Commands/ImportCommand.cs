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
            ctx.Output.Line($"winlogrotate: '{source}' does not exist.");
            return ctx.Output.Complete<ImportResult>("import", ExitCode.ConfigInvalid, null);
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

        foreach (var job in jobs)
        {
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
        ctx.Output.Line($"{jobs.Count} job(s) written to {target}.");

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

        return ctx.Output.Complete("import", ExitCode.Ok, new ImportResult
        {
            Source = source,
            OutputDirectory = target,
            Jobs = jobs.Count,
            NeedingReview = needingReview,
            Files = written,
        });
    }
}
