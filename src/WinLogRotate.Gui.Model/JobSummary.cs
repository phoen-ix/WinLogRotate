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
    /// <param name="manage">The application starts new files itself.</param>
    /// <param name="schedule">The schedule the form shows, or null for the default.</param>
    /// <param name="maxSize">The early trigger, or null.</param>
    /// <param name="rotate">Old copies to keep, or null for the default.</param>
    /// <param name="maxAge">Days after which old copies go, or null.</param>
    /// <param name="compress">Whether old copies are compressed; null for the default.</param>
    /// <param name="firstPath">The first line of the files box, or null.</param>
    public static string Sentence(
        bool manage, string? schedule, string? maxSize, string? rotate, string? maxAge, bool? compress, string? firstPath)
    {
        var files = string.IsNullOrWhiteSpace(firstPath) ? "the matching logs" : firstPath.Trim();
        var keep = Count(rotate) ?? 7;
        var age = Count(maxAge);
        var compressed = compress ?? true;

        var parts = new List<string>();

        if (manage)
        {
            parts.Add($"Each run, leave the newest file of {files} to the application");
            parts.Add($"keep the {keep} newest of the rest");
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

            parts.Add($"{when}{early}, move {files} aside");
            parts.Add($"keep the {keep} newest {(keep == 1 ? "copy" : "copies")}");
        }

        parts.Add(compressed ? "compress the rest" : "leave the rest uncompressed");

        if (age is { } days)
        {
            parts.Add($"delete any older than {days} {(days == 1 ? "day" : "days")}");
        }

        return string.Join(", ", parts.Take(parts.Count - 1)) + ", and " + parts[^1] + ".";
    }

    private static bool SizeSchedule(string? schedule) =>
        string.Equals(schedule?.Trim(), "size", StringComparison.OrdinalIgnoreCase);

    private static int? Count(string? text) =>
        int.TryParse(text?.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : null;
}
