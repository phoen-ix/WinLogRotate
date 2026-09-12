using System.Text;
using System.Text.Json;

namespace WinLogRotate.Gui.Cli;

/// <summary>How loudly a configuration check should be reported.</summary>
public enum CheckTone
{
    /// <summary>Nothing was found.</summary>
    Clean,

    /// <summary>Something was found that does not stop anything running.</summary>
    Warning,

    /// <summary>Something was found that stops a job, or all of them.</summary>
    Error,
}

/// <summary>What the Jobs page says after a configuration check.</summary>
public sealed record ConfigCheckView
{
    public required CheckTone Tone { get; init; }

    /// <summary>The sentence in the dialog.</summary>
    public required string Message { get; init; }

    /// <summary>The expandable text under it.</summary>
    public required string Details { get; init; }
}

/// <summary>
/// Turns a <c>config check --json</c> response into what the operator is told.
/// </summary>
/// <remarks>
/// <para>
/// The page said "No problems found." whenever the verb exited 0, and `config check` exits 0
/// with warnings. It then handed the dialog `result.StdOut` as the detail - and the warnings are
/// on stderr, so the pane held the summary line and nothing else. An operator who opened the
/// details to see what the warning was found a count of it.
/// </para>
/// <para>
/// Decided from the envelope rather than from the text, because `result.warnings`,
/// `result.errors` and `result.diagnostics[]` have all been on the wire since the verb had a
/// shape - which makes this a contract the product already keeps rather than a new one invented
/// here for the GUI to parse.
/// </para>
/// </remarks>
public static class ConfigCheckProjection
{
    /// <param name="configInvalid">
    /// <c>ExitCode.ConfigInvalid</c>. Passed rather than referenced so this stays a pure
    /// function of the response, and so the distinction it draws is visible in the test.
    /// </param>
    public static ConfigCheckView From(CliResult result, int configInvalid)
    {
        if (result.Failure != CliFailure.None)
        {
            return new ConfigCheckView
            {
                Tone = CheckTone.Error,
                Message = result.Describe(),
                Details = result.Details,
            };
        }

        int errors;
        int warnings;
        string details;

        try
        {
            using var document = JsonDocument.Parse(result.StdOut);
            var payload = document.RootElement.GetProperty("result");

            errors = payload.GetProperty("errors").GetInt32();
            warnings = payload.GetProperty("warnings").GetInt32();
            details = Render(payload);
        }
        catch (Exception e) when (e is JsonException
                                      or KeyNotFoundException
                                      or InvalidOperationException)
        {
            return new ConfigCheckView
            {
                Tone = CheckTone.Error,
                Message = "Could not read the response from winlogrotate.exe.",
                Details = result.Details,
            };
        }

        if (errors > 0)
        {
            // Exit 2 means nothing was attempted; exit 1 means a job was skipped and the rest
            // rotate. This sentence was written when 2 was the only failure, and then told an
            // operator nothing would run when almost everything would. The distinction is the
            // whole point of having two codes.
            return new ConfigCheckView
            {
                Tone = CheckTone.Error,
                Message = result.ExitCode == configInvalid
                    ? "The configuration has problems. Nothing will run until they are fixed."
                    : "One or more jobs have problems and will be skipped. The rest will still run.",
                Details = details,
            };
        }

        if (warnings > 0)
        {
            return new ConfigCheckView
            {
                Tone = CheckTone.Warning,
                Message = $"{warnings} warning(s). Everything will still run.",
                Details = details,
            };
        }

        return new ConfigCheckView
        {
            Tone = CheckTone.Clean,
            Message = "No problems found.",
            Details = details,
        };
    }

    /// <summary>The diagnostics as an operator would read them, one per line.</summary>
    private static string Render(JsonElement payload)
    {
        if (!payload.TryGetProperty("diagnostics", out var diagnostics)
            || diagnostics.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var text = new StringBuilder();

        foreach (var d in diagnostics.EnumerateArray())
        {
            var severity = Text(d, "severity");
            var file = Text(d, "file");
            var line = d.TryGetProperty("line", out var n) && n.ValueKind == JsonValueKind.Number
                ? n.GetInt32()
                : 0;

            var where = file.Length == 0 ? "" : line > 0 ? $"{file}:{line}: " : $"{file}: ";

            text.Append(severity.ToLowerInvariant()).Append(": ").Append(where)
                .AppendLine(Text(d, "message"));

            if (Text(d, "remedy") is { Length: > 0 } remedy)
            {
                text.Append("    ").AppendLine(remedy);
            }
        }

        return text.ToString();
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
