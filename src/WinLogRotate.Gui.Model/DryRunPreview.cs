using System.Text.Json;
using WinLogRotate.Contracts;

namespace WinLogRotate.Gui.Cli;

/// <summary>What a rotation of one job would do, as a dialog says it.</summary>
public sealed record DryRunPreview(CheckTone Tone, string Message, string Details);

/// <summary>
/// Reads a dry run of one job into the sentence and the lines "What would happen…" shows.
/// </summary>
/// <remarks>
/// <para>
/// The command line is <c>run --dry-run --force --catchup --no-notify --job NAME --json-stream</c>:
/// a dry run writes no state, journal or notification and runs no hook; <c>--force</c> makes the
/// log due whatever the calendar says, a first sighting included, so a job saved a minute ago
/// shows its moves rather than "first time this log has been seen" (<c>--catchup</c> asked for
/// that before <c>--force</c> covered it, and stays because it is harmless and the tests pin the
/// line); and only the stream carries the per-file events, because the envelope alone is counts.
/// The dialog says all of that in one line: as if it were due tonight, and nothing has been changed.
/// </para>
/// <para>
/// Only a saved job can be previewed: the verb reads the job from the configuration directory,
/// and nothing unelevated can put an unsaved one there.
/// </para>
/// </remarks>
public static class DryRunPreviewProjection
{
    public static string[] Arguments(string? configDir, string job) =>
        CliArgs.For(configDir, "run", "--dry-run", "--force", "--catchup", "--no-notify", "--job", job, "--json-stream");

    public static DryRunPreview From(CliResult result, string job)
    {
        if (result.Failure != CliFailure.None || result.IsDefect)
        {
            return new DryRunPreview(CheckTone.Error, result.Describe(), result.Details);
        }

        if (result.ExitCode == Core.ExitCode.LockHeld)
        {
            return new DryRunPreview(CheckTone.Warning,
                "Another rotation is running right now; try again in a moment.", result.Details);
        }

        var events = new List<CliEvent>();

        foreach (var raw in result.StdOut.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || !line.StartsWith('{'))
            {
                continue;
            }

            try
            {
                // The envelope lacks an event's required members and is refused by the
                // deserializer, which is how the two are told apart.
                if (JsonSerializer.Deserialize(line, CliEventJson.Default.CliEvent) is { } e
                    && e.Operation is not (Op.RunStart or Op.RunEnd or Op.JobStart or Op.JobEnd))
                {
                    events.Add(e);
                }
            }
            catch (JsonException)
            {
                // Not an event.
            }
        }

        var envelope = EnvelopeReader.Last(result.StdOut);
        var diagnostics = envelope is null ? [] : EnvelopeDiagnostics.From(envelope, "diagnostics");
        var problem = diagnostics.FirstOrDefault(d => d.IsProblem);
        var details = string.Join(Environment.NewLine, events.Select(CliEventText.Describe));

        if (events.Count == 0)
        {
            if (problem is not null)
            {
                return new DryRunPreview(
                    problem.Severity.Equals("Warning", StringComparison.OrdinalIgnoreCase) ? CheckTone.Warning : CheckTone.Error,
                    problem.Message,
                    EnvelopeDiagnostics.Render(diagnostics));
            }

            return new DryRunPreview(CheckTone.Warning,
                $"Nothing would happen to {job}: no file matched, or every file is held back by a rule. Nothing has been changed.",
                EnvelopeDiagnostics.Render(diagnostics));
        }

        var counts = new List<string>();
        Count(counts, events.Count(e => e.Operation == Op.Rename), "rename", "renames");
        Count(counts, events.Count(e => e.Operation is Op.CopyTruncate or Op.Copy), "copy out", "copies out");
        Count(counts, events.Count(e => e.Operation == Op.Compress), "compression", "compressions");
        Count(counts, events.Count(e => e.Operation == Op.Delete), "deletion", "deletions");
        Count(counts, events.Count(e => e.Operation == Op.Hook), "command to run", "commands to run");
        Count(counts, events.Count(e => e.Operation == Op.Plan && e.Result == OpResult.Skipped), "file left alone", "files left alone");

        var refused = events.Count(e => e.Operation == Op.GuardRefuse);
        Count(counts, refused, "path refused", "paths refused");

        var tone = refused > 0 || problem is not null ? CheckTone.Warning : CheckTone.Clean;
        var summary = counts.Count == 0 ? "nothing" : string.Join(", ", counts);

        return new DryRunPreview(tone,
            $"As if {job} were due tonight: {summary}. Nothing has been changed.",
            problem is null ? details : details + Environment.NewLine + Environment.NewLine + EnvelopeDiagnostics.Render(diagnostics));
    }

    private static void Count(List<string> counts, int n, string one, string many)
    {
        if (n > 0)
        {
            counts.Add($"{n} {(n == 1 ? one : many)}");
        }
    }
}
