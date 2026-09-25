using System.Text.Json;
using WinLogRotate.Contracts;

namespace WinLogRotate.Gui.Cli;

/// <summary>What the Updates panel shows after a check.</summary>
public sealed record UpdateStatus
{
    public required CheckTone Tone { get; init; }

    public required string Sentence { get; init; }

    /// <summary>The newer version, when there is one.</summary>
    public string? Latest { get; init; }

    public bool Available => Latest is not null;

    /// <summary><c>PerMachine</c>, <c>PerUser</c>, or null for a portable copy.</summary>
    public string? Scope { get; init; }

    /// <summary>Pressing Update will raise a UAC prompt.</summary>
    public bool NeedsElevation => string.Equals(Scope, "PerMachine", StringComparison.OrdinalIgnoreCase);

    /// <summary>Everything the verb said, for a dialog's details when it did not go well.</summary>
    public string? Details { get; init; }
}

/// <summary>
/// Reads <c>update check --json</c> for the Updates panel and the start-up banner.
/// </summary>
/// <remarks>
/// The verb reports an unreachable feed as a warning at exit 0 and a policy block as exit 0
/// with a detail, so the exit code alone says nothing; the payload does. Every walk is guarded,
/// because a field a newer or older winlogrotate.exe does not write must read as "could not be
/// read" and never end the window.
/// </remarks>
public static class UpdateStatusProjection
{
    public static UpdateStatus From(CliResult result, string current)
    {
        if (result.Failure != CliFailure.None || result.ExitCode != Core.ExitCode.Ok)
        {
            return new UpdateStatus
            {
                Tone = CheckTone.Error,
                Sentence = result.Describe(),
                Details = result.Details,
            };
        }

        try
        {
            using var document = JsonDocument.Parse(result.StdOut);
            var payload = document.RootElement.GetProperty("result");

            var scope = Text(payload, "scope");
            var detail = Text(payload, "detail");
            var latest = Text(payload, "latest");

            if (string.Equals(detail, "disabled by policy", StringComparison.Ordinal))
            {
                return new UpdateStatus { Tone = CheckTone.Clean, Sentence = UpdateText.DisabledByPolicy, Scope = scope };
            }

            if (latest is null)
            {
                return new UpdateStatus
                {
                    Tone = CheckTone.Warning,
                    Sentence = UpdateText.CouldNotCheck,
                    Scope = scope,
                    Details = result.Details,
                };
            }

            var available = payload.TryGetProperty("updateAvailable", out var a) && a.ValueKind == JsonValueKind.True;

            return available
                ? new UpdateStatus { Tone = CheckTone.Clean, Sentence = UpdateText.Available(latest, current), Latest = latest, Scope = scope }
                : new UpdateStatus { Tone = CheckTone.Clean, Sentence = UpdateText.Newest(current), Scope = scope };
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new UpdateStatus
            {
                Tone = CheckTone.Error,
                Sentence = "The answer from winlogrotate.exe could not be read.",
                Details = result.Details,
            };
        }
    }

    private static string? Text(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
}

public enum UpdateOutcomeKind
{
    /// <summary>The installer has the file and this window is about to be closed by it.</summary>
    Installing,

    /// <summary>The user answered No to the elevation prompt. Nothing was changed.</summary>
    Cancelled,

    /// <summary>The verb declined, and said why in a diagnostic.</summary>
    Refused,

    /// <summary>The verb could not be run, or its answer could not be read.</summary>
    Failed,
}

public sealed record UpdateOutcome
{
    public required UpdateOutcomeKind Kind { get; init; }

    public required string Message { get; init; }

    public string? Details { get; init; }
}

/// <summary>
/// Reads what <c>update apply --json-stream</c> wrote: the progress as it goes, and the
/// envelope at the end.
/// </summary>
public static class UpdateApplyProjection
{
    /// <summary>
    /// One line of the stream as a sentence for the status line, or null for a line that is
    /// not download progress.
    /// </summary>
    public static string? ProgressLine(string line)
    {
        if (line.Length == 0 || line[0] != '{')
        {
            return null;
        }

        try
        {
            if (JsonSerializer.Deserialize(line, CliEventJson.Default.CliEvent) is { } e
                && e.Operation == Op.Update
                && e.Result is null)
            {
                return $"{UpdateText.Downloading} {e.Reason}".TrimEnd();
            }
        }
        catch (JsonException)
        {
            // The envelope, or a line that is not ours. Nothing to show for it.
        }

        return null;
    }

    public static UpdateOutcome From(CliResult result)
    {
        if (result.Failure == CliFailure.UacDeclined)
        {
            return new UpdateOutcome { Kind = UpdateOutcomeKind.Cancelled, Message = result.Describe() };
        }

        if (result.Failure != CliFailure.None)
        {
            return new UpdateOutcome { Kind = UpdateOutcomeKind.Failed, Message = result.Describe(), Details = result.Details };
        }

        var envelope = EnvelopeReader.Last(result.StdOut);

        if (envelope is null)
        {
            return new UpdateOutcome
            {
                Kind = UpdateOutcomeKind.Failed,
                Message = "winlogrotate.exe ran, but did not say how it ended.",
                Details = result.Details,
            };
        }

        try
        {
            using var document = JsonDocument.Parse(envelope);
            var root = document.RootElement;

            if (root.TryGetProperty("result", out var payload)
                && payload.ValueKind == JsonValueKind.Object
                && payload.TryGetProperty("installing", out var installing)
                && installing.ValueKind == JsonValueKind.True)
            {
                return new UpdateOutcome { Kind = UpdateOutcomeKind.Installing, Message = UpdateText.Installing };
            }

            // Its own words: the verb already explains why it declined and what to do instead.
            var diagnostics = EnvelopeDiagnostics.From(envelope, "diagnostics");
            var problem = diagnostics.FirstOrDefault(d => d.IsProblem);

            return new UpdateOutcome
            {
                Kind = UpdateOutcomeKind.Refused,
                Message = problem?.Message ?? result.Describe(),
                Details = diagnostics.Count > 0 ? EnvelopeDiagnostics.Render(diagnostics) : result.Details,
            };
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new UpdateOutcome
            {
                Kind = UpdateOutcomeKind.Failed,
                Message = "The answer from winlogrotate.exe could not be read.",
                Details = result.Details,
            };
        }
    }

    /// <summary>The local clock as the status line spells it.</summary>
    public static string Stamp(DateTimeOffset utc) =>
        UpdateText.LastChecked(utc.ToLocalTime());

    /// <summary>For the tests: a stamp in a known zone.</summary>
    public static string Stamp(DateTimeOffset utc, TimeZoneInfo zone) =>
        UpdateText.LastChecked(TimeZoneInfo.ConvertTime(utc, zone));
}
