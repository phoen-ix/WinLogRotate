using System.Globalization;

namespace WinLogRotate.Gui.Cli;

/// <summary>
/// One sentence that says what a job will do, from what the Basics view shows.
/// </summary>
/// <remarks>
/// The explanation that never goes stale, because it is derived: if the sentence reads wrong the
/// settings are wrong, and a reader who has never heard of rotation learns what the product does
/// from the sentence rather than from a manual. Pure, so a test can hold every phrase.
/// </remarks>
public static class JobSummary
{
    /// <param name="how">How the log is taken away; null when Advanced settings decide.</param>
    /// <param name="schedule">The schedule the form shows, or null for the default.</param>
    /// <param name="maxSize">The early trigger, or null.</param>
    /// <param name="rotate">Old copies to keep, or null for the default.</param>
    /// <param name="maxAge">Days after which old copies go, or null.</param>
    /// <param name="dated">Old copies named by date rather than by number.</param>
    /// <param name="compression">Whether and how old copies are compressed.</param>
    /// <param name="oldDir">Where old copies go, or blank for beside the log.</param>
    /// <param name="firstPath">The first line of the files box, or null.</param>
    public static string Sentence(
        HowRotated? how, string? schedule, string? maxSize, string? rotate, string? maxAge,
        bool dated, ArchiveCompression compression, string? oldDir, string? firstPath)
    {
        var files = string.IsNullOrWhiteSpace(firstPath) ? "the matching logs" : firstPath.Trim();
        var keep = Count(rotate) ?? 7;
        var age = Count(maxAge);
        var folder = string.IsNullOrWhiteSpace(oldDir) ? "beside the log" : $"in {oldDir.Trim()}";

        var compressed = compression switch
        {
            ArchiveCompression.Gzip => "gzipped",
            ArchiveCompression.None => "uncompressed",
            _ => "zipped",
        };

        var parts = new List<string>();

        if (how == HowRotated.Manage)
        {
            parts.Add($"Each run, leave the newest file of {files} to the application");
            parts.Add($"keep the {keep} newest of the rest {compressed}");
        }
        else
        {
            var when = schedule?.Trim().ToLowerInvariant() switch
            {
                "hourly" => "Every hour",
                "weekly" => "Every week",
                "monthly" => "Every month",
                "yearly" => "Every year",
                "size" => "Once it reaches its size limit",
                _ => "Every day",
            };

            var early = string.IsNullOrWhiteSpace(maxSize) || SizeSchedule(schedule)
                ? string.Empty
                : $", or earlier once it exceeds {maxSize.Trim()}";

            var taken = how switch
            {
                HowRotated.Auto => $"take {files} aside (renamed when the program allows it, copied out otherwise)",
                HowRotated.CopyTruncate => $"copy the contents of {files} out and empty it in place",
                HowRotated.Rename => $"rename {files} and start a fresh one",
                HowRotated.Copy => $"copy {files} aside and leave it as it is",
                _ => $"take {files} aside as its settings say",
            };

            var name = FileName(firstPath);
            var named = dated ? $"dated like {name}-20260918" : $"numbered {name}.1, {name}.2 \u2026";

            parts.Add($"{when}{early}, {taken}");
            parts.Add($"keep the {keep} newest {(keep == 1 ? "copy" : "copies")} {named}, {compressed}, {folder}");
        }

        if (age is { } days)
        {
            parts.Add($"delete any older than {days} {(days == 1 ? "day" : "days")}");
        }

        return parts.Count == 1
            ? parts[0] + "."
            : string.Join(", ", parts.Take(parts.Count - 1)) + ", and " + parts[^1] + ".";
    }

    private static string FileName(string? firstPath)
    {
        if (string.IsNullOrWhiteSpace(firstPath))
        {
            return "app.log";
        }

        var line = firstPath.Trim();
        var cut = Math.Max(line.LastIndexOf('/'), line.LastIndexOf('\\'));
        var name = cut < 0 ? line : line[(cut + 1)..];

        return name.IndexOfAny(['*', '?', '[']) < 0 && name.Length > 0 ? name : "app.log";
    }

    private static bool SizeSchedule(string? schedule) =>
        string.Equals(schedule?.Trim(), "size", StringComparison.OrdinalIgnoreCase);

    private static int? Count(string? text) =>
        int.TryParse(text?.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : null;
}
