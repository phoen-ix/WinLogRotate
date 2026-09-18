using System.Text.Json;

namespace WinLogRotate.Gui.Cli;

/// <summary>What one line of the files box matches, as the sentence under it.</summary>
public sealed record GlobPreview(CheckTone Tone, string Sentence, string Remedy, int Count, long TotalBytes);

/// <summary>
/// Reads <c>glob --json</c> for the files box, one line at a time.
/// </summary>
/// <remarks>
/// <para>
/// The consequence of a line, shown before Save: "Matches 12 files, 340.5 MB." A refusal comes
/// back with no payload and the guard's own sentence on the envelope, which is the one to show -
/// it names the folder and says what to do. The preview never gates anything; a line the CLI
/// cannot answer for is one sentence in Muted, not a blocked Save.
/// </para>
/// <para>
/// <see cref="Arguments"/> is the only correct way to build the call: the verb declares no
/// <c>--config-dir</c>, so a line sent through <c>CliArgs.For</c> would be refused as a parse
/// error - the same trap the identity probe documents for <c>--version</c>.
/// </para>
/// </remarks>
public static class GlobPreviewProjection
{
    /// <summary>The command line, without a configuration directory: the verb takes none.</summary>
    public static string[] Arguments(string line) => ["glob", line.Trim(), "--json"];

    public static GlobPreview From(CliResult result)
    {
        if (result.Failure != CliFailure.None || result.IsDefect)
        {
            return new GlobPreview(CheckTone.Warning, $"Could not preview: {result.Describe()}", string.Empty, 0, 0);
        }

        if (EnvelopeReader.Last(result.StdOut) is not { } envelope)
        {
            return new GlobPreview(CheckTone.Error, "Could not read the response from winlogrotate.exe.", string.Empty, 0, 0);
        }

        var diagnostics = EnvelopeDiagnostics.From(envelope, "diagnostics");
        var problem = diagnostics.FirstOrDefault(d => d.IsProblem);

        try
        {
            using var document = JsonDocument.Parse(envelope);

            if (!document.RootElement.TryGetProperty("result", out var payload)
                || payload.ValueKind != JsonValueKind.Object)
            {
                // Refused: the guard said why, in words that name the folder.
                return new GlobPreview(
                    CheckTone.Error,
                    problem?.Message ?? result.Describe(),
                    problem?.Remedy ?? string.Empty,
                    0,
                    0);
            }

            var count = payload.GetProperty("count").GetInt32();
            var bytes = payload.GetProperty("totalBytes").GetInt64();
            var resolved = payload.TryGetProperty("resolvedAnchor", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString()
                : null;

            if (count == 0)
            {
                return new GlobPreview(
                    CheckTone.Warning,
                    "Matches no files. Check the folder and the name; if the files only appear later, "
                    + "tick 'Allow no matches' under Advanced settings.",
                    string.Empty,
                    0,
                    0);
            }

            var matched = $"Matches {count} {(count == 1 ? "file" : "files")}, {Humanize(bytes)}.";

            if (!result.Ok && problem is not null)
            {
                // A folder was skipped on the way: the count is real, and so is the gap.
                return new GlobPreview(CheckTone.Warning, $"{matched[..^1]}, but a folder was skipped: {problem.Message}",
                    problem.Remedy, count, bytes);
            }

            return new GlobPreview(
                CheckTone.Clean,
                resolved is null ? matched : $"{matched} The folder is a link to {resolved}.",
                string.Empty,
                count,
                bytes);
        }
        catch (Exception e) when (e is JsonException
                                      or KeyNotFoundException
                                      or InvalidOperationException)
        {
            return new GlobPreview(CheckTone.Error, "Could not read the response from winlogrotate.exe.", string.Empty, 0, 0);
        }
    }

    /// <summary>One sentence for several lines: the worst first, or the total when every line is clean.</summary>
    public static GlobPreview Combine(IReadOnlyList<GlobPreview> lines)
    {
        if (lines.Count == 0)
        {
            return new GlobPreview(CheckTone.Clean, string.Empty, string.Empty, 0, 0);
        }

        if (lines.Count == 1)
        {
            return lines[0];
        }

        var worst = lines.Select((p, i) => (Preview: p, Line: i + 1))
            .Where(x => x.Preview.Tone != CheckTone.Clean)
            .OrderByDescending(x => x.Preview.Tone)
            .FirstOrDefault();

        if (worst.Preview is not null)
        {
            return worst.Preview with { Sentence = $"Line {worst.Line}: {worst.Preview.Sentence}" };
        }

        var count = lines.Sum(p => p.Count);
        var bytes = lines.Sum(p => p.TotalBytes);

        return new GlobPreview(
            CheckTone.Clean,
            $"{lines.Count} lines match {count} {(count == 1 ? "file" : "files")}, {Humanize(bytes)}.",
            string.Empty,
            count,
            bytes);
    }

    /// <summary>The same thresholds the verb prints with.</summary>
    public static string Humanize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):N1} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):N1} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):N1} KB",
        _ => $"{bytes} B",
    };
}
