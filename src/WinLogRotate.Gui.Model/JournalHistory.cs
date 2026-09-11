using System.Text.Json;
using WinLogRotate.Contracts;

namespace WinLogRotate.Gui.Cli;

/// <summary>One thing that happened to one file.</summary>
public sealed record HistoryRow
{
    public required string When { get; init; }
    public required string Job { get; init; }
    public required string What { get; init; }
    public required string File { get; init; }
    public required string Why { get; init; }
}

/// <summary>Everything the History page needs, and nothing that needs a window.</summary>
public sealed record HistoryView
{
    public required IReadOnlyList<HistoryRow> Rows { get; init; }

    /// <summary>Torn lines the CLI could not parse - normally a run that was terminated.</summary>
    public required int SkippedLines { get; init; }

    /// <summary>The response made no sense, so the rows say nothing rather than nothing much.</summary>
    public required bool Unreadable { get; init; }
}

/// <summary>
/// Turns a <c>journal --json</c> response into rows.
/// </summary>
/// <remarks>
/// <para>
/// The page hand-walked this with <c>GetProperty</c>, filtered on the operation name alone, and
/// added a row per record - so every operation that was actually carried out appeared twice, once
/// as its plan half and once as its apply half, differing only in the timestamp, and the count
/// underneath said the same doubled number. A file deliberately left alone showed the word
/// "plan" in the column headed What.
/// </para>
/// <para>
/// Here rather than on the page because a project that references the GUI carries a
/// Microsoft.WindowsDesktop.App framework reference and cannot run on the Linux leg at all, so
/// every one of those defects was unreachable by a test while the logic lived beside a
/// <c>DataGridView</c>.
/// </para>
/// <para>
/// The collapse is applied here even though the CLI now applies it too. The GUI ships separately
/// and may be driven by an older winlogrotate.exe, <c>Collapse</c> is idempotent so applying it
/// twice costs a pass and changes nothing, and doing it on this side is what makes the page's fix
/// provable by a test that runs where the tests run.
/// </para>
/// </remarks>
public static class JournalHistory
{
    /// <summary>
    /// Bookkeeping, not operations.
    /// </summary>
    /// <remarks>
    /// The run and job brackets are scaffolding. Guard verdicts and NUL-fill findings are
    /// decisions rather than things done to a file, and storing a credential did not happen to a
    /// log at all. A hook is kept: it ran as part of a rotation, and "did the postrotate script
    /// fire?" is a question people come to a history for.
    /// </remarks>
    private static bool IsBookkeeping(string operation) =>
        operation.StartsWith("run.", StringComparison.Ordinal)
        || operation.StartsWith("job.", StringComparison.Ordinal)
        || operation.StartsWith("guard.", StringComparison.Ordinal)
        || operation == Op.NulFill
        || operation == Op.Secret;

    /// <summary>
    /// Reads the response, or reports that it could not be read.
    /// </summary>
    /// <remarks>
    /// Only the parse can throw. Everything after it goes through <c>TryGetProperty</c> and a
    /// <c>ValueKind</c> check, because <c>GetProperty</c> throws <c>KeyNotFoundException</c> and
    /// <c>GetInt32</c> throws <c>InvalidOperationException</c> - neither of which a
    /// <c>catch (JsonException)</c> catches, and both of which reached an <c>async void</c>
    /// handler in a process that installs no unhandled-exception handler at all.
    /// </remarks>
    public static HistoryView From(string json)
    {
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return Nothing(unreadable: true);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("result", out var result)
                || result.ValueKind != JsonValueKind.Object
                || !result.TryGetProperty("entries", out var entries)
                || entries.ValueKind != JsonValueKind.Array)
            {
                return Nothing(unreadable: true);
            }

            var events = new List<CliEvent>();

            foreach (var element in entries.EnumerateArray())
            {
                CliEvent? entry;

                try
                {
                    entry = JsonSerializer.Deserialize(
                        element.GetRawText(), CliEventJson.Default.CliEvent);
                }
                catch (JsonException)
                {
                    // One entry this build does not understand is not a reason to show none of
                    // the others. The CLI's own reader takes the same view of a torn line.
                    continue;
                }

                if (entry is not null)
                {
                    events.Add(entry);
                }
            }

            var skipped = result.TryGetProperty("skippedLines", out var s)
                && s.ValueKind == JsonValueKind.Number
                && s.TryGetInt32(out var count)
                    ? count
                    : 0;

            return new HistoryView
            {
                Rows =
                [
                    .. CliEventLastWord.Collapse(events)
                        .Where(e => !IsBookkeeping(e.Operation))
                        .Select(Row),
                ],
                SkippedLines = skipped,
                Unreadable = false,
            };
        }
    }

    private static HistoryRow Row(CliEvent e) => new()
    {
        When = e.Ts,
        Job = e.Job ?? string.Empty,

        // The product's own words for what was done, so the grid and the console cannot end up
        // describing the same record differently.
        What = CliEventText.Action(e),
        File = CliEventText.Subject(e),

        // The error where there was one: a row that says an operation failed and leaves Why empty
        // sends the reader to the journal for the sentence the GUI was already holding.
        Why = e.Error ?? e.Reason ?? string.Empty,
    };

    private static HistoryView Nothing(bool unreadable) =>
        new() { Rows = [], SkippedLines = 0, Unreadable = unreadable };
}
