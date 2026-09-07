using System.Globalization;
using WinLogRotate.Cli.Output;
using WinLogRotate.Core;
using WinLogRotate.Core.Journaling;

namespace WinLogRotate.Cli.Commands;

internal static class JournalCommand
{
    public static int Run(CommandContext ctx, string? since, string? job, string? configDir)
    {
        var paths = InstallPaths.Resolve(configDir);
        var reader = new JournalReader(paths.JournalDirectory);

        DateTimeOffset? sinceTime = null;
        if (!string.IsNullOrWhiteSpace(since))
        {
            if (!DateTimeOffset.TryParse(since, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var parsed))
            {
                ctx.Output.Line($"winlogrotate: could not read '{since}' as a date or time.");
                return ctx.Output.Complete<JournalResult>("journal", ExitCode.ConfigInvalid, null);
            }

            sinceTime = parsed;
        }

        var entries = reader
            .Read(new JournalFilter { Since = sinceTime, Job = job })
            .ToArray();

        foreach (var e in entries)
        {
            var reason = e.Reason is null ? "" : $"  ({e.Reason})";
            ctx.Output.Line($"{e.Ts}  {e.Job,-16} {e.Operation,-14} {e.Src}{reason}");
        }

        if (entries.Length == 0)
        {
            ctx.Output.Line($"no journal entries in {paths.JournalDirectory}");
        }

        if (reader.SkippedLines > 0)
        {
            // Not an error: a run killed mid-write leaves a torn final line, and reporting the
            // count is more useful than either throwing or staying quiet about it.
            ctx.Output.Line($"({reader.SkippedLines} unreadable line(s) skipped - a previous run was probably terminated)");
        }

        var result = new JournalResult
        {
            Directory = paths.JournalDirectory,
            Count = entries.Length,
            SkippedLines = reader.SkippedLines,
            Entries = entries,
        };

        return ctx.Output.Complete("journal", ExitCode.Ok, result);
    }
}
