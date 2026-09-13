using System.Text.Json;

namespace WinLogRotate.Gui.Cli;

/// <summary>What the editor, or the Jobs page, says after a <c>job</c> verb has answered.</summary>
public sealed record JobEditView
{
    public required CheckTone Tone { get; init; }

    /// <summary>The sentence for the status line, or the dialog.</summary>
    public required string Message { get; init; }

    /// <summary>The diagnostics as an operator would read them, or empty.</summary>
    public required string Details { get; init; }

    /// <summary>Whether the file was changed. False for a dry run, a refusal, and nothing to do.</summary>
    public required bool Written { get; init; }

    /// <summary>The keys that changed, or would have.</summary>
    public required int Changes { get; init; }
}

/// <summary>
/// Turns the answer of <c>job add</c>, <c>set</c>, <c>enable</c>, <c>disable</c> or
/// <c>remove</c> into what the operator is told.
/// </summary>
/// <remarks>
/// <para>
/// Every refused edit used to read "Could not read the response from winlogrotate.exe." The
/// pages fed these verbs' answers to <c>ConfigCheckProjection</c>, which requires
/// <c>result.errors</c> and <c>result.warnings</c>; a <c>JobEditResult</c> carries neither, and
/// a refusal carries no <c>result</c> at all. The reason was in the envelope's own diagnostics
/// the whole time, and reached the operator only under "Show details".
/// </para>
/// <para>
/// Read from the envelope's <c>diagnostics</c> rather than the payload's. The verb forwards its
/// verdict to both, and the envelope also carries what the payload cannot: the refusal of a name
/// that is not a job, of a value the key cannot hold, and of a write without administrator
/// rights - each of which completes with no payload.
/// </para>
/// </remarks>
public static class JobEditProjection
{
    public static JobEditView From(CliResult result)
    {
        if (result.Failure != CliFailure.None)
        {
            return Failed(result.Describe(), result.Details);
        }

        if (EnvelopeReader.Last(result.StdOut) is not { } envelope)
        {
            // A text answer, or none. What there is to show is on standard error, which is where
            // a verb without --json puts its diagnostics.
            return Failed("Could not read the response from winlogrotate.exe.", result.Details);
        }

        var diagnostics = EnvelopeDiagnostics.From(envelope, "diagnostics");
        var details = EnvelopeDiagnostics.Render(diagnostics);

        string verb;
        string job;
        string file;
        bool written;
        int changes;

        try
        {
            using var document = JsonDocument.Parse(envelope);

            if (!document.RootElement.TryGetProperty("result", out var payload)
                || payload.ValueKind != JsonValueKind.Object)
            {
                // A refusal: the verb completed with no payload and said why in a diagnostic.
                return Failed(Reason(diagnostics) ?? result.Describe(), details);
            }

            verb = Text(payload, "verb");
            job = Text(payload, "job");
            file = FileNameOf(Text(payload, "path"));
            written = payload.TryGetProperty("written", out var w) && w.ValueKind == JsonValueKind.True;

            changes = payload.TryGetProperty("changes", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Count(c => !Text(c, "kind").Equals("Unchanged", StringComparison.OrdinalIgnoreCase))
                : 0;
        }
        catch (Exception e) when (e is JsonException
                                      or KeyNotFoundException
                                      or InvalidOperationException)
        {
            return Failed("Could not read the response from winlogrotate.exe.", result.Details);
        }

        if (!result.Ok)
        {
            // The payload is present but the verb refused: a validator verdict, or a write that
            // failed. The first error is the sentence; the rest are the details.
            return Failed(Reason(diagnostics) ?? result.Describe(), details);
        }

        var warnings = diagnostics.Count(d => d.Severity.Equals("Warning", StringComparison.OrdinalIgnoreCase));
        var message = Sentence(verb, job, file, written, changes);

        return new JobEditView
        {
            Tone = warnings > 0 ? CheckTone.Warning : CheckTone.Clean,
            Message = warnings > 0 ? $"{message} {warnings} warning(s)." : message,
            Details = details,
            Written = written,
            Changes = changes,
        };
    }

    /// <summary>
    /// What was done, in the CLI's own words where it has them.
    /// </summary>
    /// <remarks>
    /// Removal and switching are worded as themselves rather than as a count of keys: "5 changes
    /// written" is a strange way to say a file was deleted, and "1 change" a strange way to say
    /// a job was switched off.
    /// </remarks>
    private static string Sentence(string verb, string job, string file, bool written, int changes)
    {
        if (verb is "enable" or "disable")
        {
            return written
                ? $"{job} {verb}d."
                : $"{job} is already {verb}d. Nothing to change.";
        }

        if (verb == "remove")
        {
            return written
                ? $"{job} removed. {file} is gone."
                : $"{job} would be removed, and {file} deleted. Nothing has been.";
        }

        if (written)
        {
            return $"{job}: {Count(changes)} written to {file}.";
        }

        return changes > 0
            ? $"{job} would be accepted: {Count(changes)}. Nothing has been written."
            : $"{job} is already what you asked for. Nothing to change.";
    }

    private static string Count(int changes) => changes == 1 ? "1 change" : $"{changes} changes";

    /// <summary>The first error, which is the reason a verb refused.</summary>
    private static string? Reason(IReadOnlyList<EnvelopeDiagnostic> diagnostics) =>
        diagnostics.FirstOrDefault(d => d.IsProblem
                && !d.Severity.Equals("Warning", StringComparison.OrdinalIgnoreCase))
            ?.Message;

    private static JobEditView Failed(string message, string details) => new()
    {
        Tone = CheckTone.Error,
        Message = message,
        Details = details,
        Written = false,
        Changes = 0,
    };

    /// <summary>
    /// The last segment of a path, whichever way its separators lean.
    /// </summary>
    /// <remarks>
    /// Not <c>Path.GetFileName</c>: that reads the current platform's separators, and the path in
    /// the envelope is always a Windows one while the tests that read it run on Linux.
    /// </remarks>
    private static string FileNameOf(string path)
    {
        var cut = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
        return cut < 0 ? path : path[(cut + 1)..];
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
