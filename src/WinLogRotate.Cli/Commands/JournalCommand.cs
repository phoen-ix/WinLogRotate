using System.Globalization;
using WinLogRotate.Cli.Output;
using WinLogRotate.Contracts;
using WinLogRotate.Core;
using WinLogRotate.Core.Journaling;

namespace WinLogRotate.Cli.Commands;

/// <summary>
/// Reads the record back.
/// </summary>
/// <remarks>
/// <para>
/// One line per operation, not per record. The journal writes a plan half and an apply half for
/// every destructive step, and this verb printed both - neither showing the phase nor the result,
/// so they arrived as two lines identical but for the timestamp - and then reported their number
/// as the operation count. An operator asking what happened last night was told, precisely,
/// twice the truth.
/// </para>
/// <para>
/// Collapsing runs after the filter rather than before it. A <c>--since</c> boundary falling
/// between the two halves of one operation therefore reports it by whichever half survived,
/// which is a small inaccuracy at the edge of a window; collapsing first would be the larger one,
/// because it would return entries from outside the window the caller asked for.
/// </para>
/// </remarks>
internal static class JournalCommand
{
    public static int Run(
        CommandContext ctx, string? since, string? job, string? configDir, bool all = false)
    {
        var paths = InstallPaths.Resolve(configDir);
        var reader = new JournalReader(paths.JournalDirectory);

        DateTimeOffset? sinceTime = null;
        if (!string.IsNullOrWhiteSpace(since))
        {
            if (!DateTimeOffset.TryParse(since, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var parsed))
            {
                return Refusals.CannotUse<JournalResult>(
                    ctx, "journal", since, "a date or time",
                    "Use an ISO timestamp such as 2026-09-01, or 2026-09-01T18:00:00Z.");
            }

            sinceTime = parsed;
        }

        var records = reader
            .Read(new JournalFilter { Since = sinceTime, Job = job })
            .ToArray();

        var entries = all ? records : CliEventLastWord.Collapse(records);

        foreach (var e in entries)
        {
            // The product's own sentence, not a fourth spelling of it. This verb hand-rolled its
            // own format string for data CliEventText already renders for the console and the
            // GUI - which is the defect the previous milestone existed to remove, surviving in
            // the one verb it did not touch. Describe indents by two spaces, and that indent is
            // the gap after the timestamp; a test pins it so it cannot quietly stop.
            ctx.Output.Line($"{e.Ts}{CliEventText.Describe(e)}");
        }

        if (entries.Count == 0)
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
            Count = entries.Count,
            SkippedLines = reader.SkippedLines,
            Entries = entries,
        };

        return ctx.Output.Complete("journal", ExitCode.Ok, result);
    }
}
