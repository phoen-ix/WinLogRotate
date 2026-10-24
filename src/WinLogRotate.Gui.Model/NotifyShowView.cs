using System.Globalization;
using System.Text.Json;
using WinLogRotate.Core.Notify;

namespace WinLogRotate.Gui.Cli;

/// <summary>
/// The sentences above the Notifications page's grid, from a <c>notify show --json</c> result.
/// </summary>
/// <remarks>
/// The page appended the wire values to its sentences as they came: "Reporting is on, change."
/// and "reminder after 7.00:00:00." - an enum name after a comma, and a TimeSpan in its
/// round-trip format. Both are right for the contract and unreadable in a window.
/// </remarks>
public static class NotifyShowView
{
    public static string Summary(JsonElement result)
    {
        var on = Enum.Parse<NotifyOn>(Text(result, "on"), ignoreCase: true);

        var lines = new List<string>();

        if (!result.GetProperty("wouldSend").GetBoolean())
        {
            // WouldSend is exactly these three, in this order, so the reason is never a guess.
            lines.Add("Nothing would be sent: " + (
                !result.GetProperty("enabled").GetBoolean() ? "[notify] has enabled = false."
                : on == NotifyOn.Never ? "[notify] has on = \"never\"."
                : "no targets are configured."));
        }
        else
        {
            lines.Add(on == NotifyOn.Every
                ? "Reporting is on: every run reports the jobs that failed."
                : "Reporting is on: a job is reported when its outcome changes.");
        }

        if (on != NotifyOn.Never)
        {
            var threshold = $"Threshold: {Text(result, "threshold").ToLowerInvariant()} and above.";

            // Only "change" holds a continuing failure back, so only "change" ever reminds.
            if (on == NotifyOn.Change)
            {
                var remind = TimeSpan.Parse(Text(result, "remindAfter"), CultureInfo.InvariantCulture);
                threshold += remind > TimeSpan.Zero
                    ? $" A failure that continues is reported again after {Duration(remind)}."
                    : " A failure that continues is not reported again.";
            }

            lines.Add(threshold);
        }

        lines.Add("Proxy: " + Text(result, "proxy"));
        lines.Add(result.TryGetProperty("certificatePin", out var pin) && pin.ValueKind == JsonValueKind.String
            ? $"TLS pinned to {pin.GetString()}"
            : "TLS: the machine's certificate store decides");

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>"7 days", "1 day 12 hours", "30 minutes".</summary>
    public static string Duration(TimeSpan value)
    {
        var parts = new List<string>();

        void Add(int count, string unit)
        {
            if (count > 0)
            {
                parts.Add(count == 1 ? $"1 {unit}" : $"{count} {unit}s");
            }
        }

        Add(value.Days, "day");
        Add(value.Hours, "hour");
        Add(value.Minutes, "minute");
        Add(value.Seconds, "second");

        return parts.Count > 0 ? string.Join(' ', parts) : "less than a second";
    }

    private static string Text(JsonElement element, string property) =>
        element.GetProperty(property).GetString()
        ?? throw new InvalidOperationException($"'{property}' is null.");
}
