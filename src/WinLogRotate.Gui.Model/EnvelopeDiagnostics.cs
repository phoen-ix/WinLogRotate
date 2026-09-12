using System.Text;
using System.Text.Json;

namespace WinLogRotate.Gui.Cli;

/// <summary>One diagnostic, as a dialog would show it.</summary>
public sealed record EnvelopeDiagnostic
{
    public required string Severity { get; init; }
    public required string Message { get; init; }
    public string Remedy { get; init; } = string.Empty;
    public string Where { get; init; } = string.Empty;

    /// <summary>True for anything above Info, which is what stops a page saying "all clear".</summary>
    public bool IsProblem =>
        Severity.Equals("Warning", StringComparison.OrdinalIgnoreCase)
        || Severity.Equals("Error", StringComparison.OrdinalIgnoreCase)
        || Severity.Equals("Critical", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Reads the diagnostics out of an envelope, for the pages that have to report them.
/// </summary>
/// <remarks>
/// Every verb's envelope carries this array, and until now no page read it. Each one invented a
/// sentence from the exit code instead - which is how "The configuration directory has been
/// secured." came to be printed for a repair that explicitly declined to change anything and
/// said so in a diagnostic sitting in the same response.
/// </remarks>
public static class EnvelopeDiagnostics
{
    /// <summary>
    /// The diagnostics under <paramref name="property"/>, or an empty list if there are none.
    /// </summary>
    /// <param name="property">
    /// <c>diagnostics</c> for the envelope's own array, or a dotted path into the result -
    /// <c>result.diagnostics</c> - for a verb that carries its own copy.
    /// </param>
    public static IReadOnlyList<EnvelopeDiagnostic> From(string json, string property)
    {
        List<EnvelopeDiagnostic> found = [];

        try
        {
            using var document = JsonDocument.Parse(json);

            var element = document.RootElement;
            foreach (var step in property.Split('.'))
            {
                if (!element.TryGetProperty(step, out element))
                {
                    return found;
                }
            }

            if (element.ValueKind != JsonValueKind.Array)
            {
                return found;
            }

            foreach (var d in element.EnumerateArray())
            {
                var file = Text(d, "file") is { Length: > 0 } f ? f : Text(d, "path");
                var line = d.TryGetProperty("line", out var n) && n.ValueKind == JsonValueKind.Number
                    ? n.GetInt32()
                    : 0;

                found.Add(new EnvelopeDiagnostic
                {
                    Severity = Text(d, "severity"),
                    Message = Text(d, "message"),
                    Remedy = Text(d, "remedy"),
                    Where = file.Length == 0 ? "" : line > 0 ? $"{file}:{line}" : file,
                });
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return found;
    }

    /// <summary>The diagnostics as an operator would read them, one per line.</summary>
    public static string Render(IReadOnlyList<EnvelopeDiagnostic> diagnostics)
    {
        var text = new StringBuilder();

        foreach (var d in diagnostics)
        {
            text.Append(d.Severity.ToLowerInvariant()).Append(": ");

            if (d.Where.Length > 0)
            {
                text.Append(d.Where).Append(": ");
            }

            text.AppendLine(d.Message);

            if (d.Remedy.Length > 0)
            {
                text.Append("    ").AppendLine(d.Remedy);
            }
        }

        return text.ToString();
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}

/// <summary>What the Settings page says after repairing permissions.</summary>
/// <remarks>
/// <para>
/// The page said "The configuration directory has been secured." whenever the verb exited 0 -
/// and on a per-user installation `host repair --acl` deliberately changes nothing, says so in a
/// Warning, and exits 0. The hardened descriptor grants SYSTEM and Administrators full control
/// and everyone else read, so applying it to a directory in the user's own profile would take
/// away their write access to their own jobs; refusing is correct, and claiming success for it
/// is not.
/// </para>
/// <para>
/// So the one person whose directory cannot be secured was the one person told it had been.
/// </para>
/// </remarks>
public static class RepairProjection
{
    public static ConfigCheckView From(CliResult result)
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

        var diagnostics = EnvelopeDiagnostics.From(result.StdOut, "diagnostics");
        var problems = diagnostics.Where(d => d.IsProblem).ToList();

        if (result.ExitCode != Core.ExitCode.Ok || problems.Any(d => !IsWarning(d)))
        {
            return new ConfigCheckView
            {
                Tone = CheckTone.Error,
                Message = problems.Count > 0 ? problems[0].Message : result.Describe(),
                Details = EnvelopeDiagnostics.Render(diagnostics),
            };
        }

        if (problems.Count > 0)
        {
            // Its own words, not ours. The verb already explains why it declined and what to do
            // instead; restating that here would be a second wording of one fact, and the one
            // most likely to drift.
            return new ConfigCheckView
            {
                Tone = CheckTone.Warning,
                Message = problems[0].Message,
                Details = EnvelopeDiagnostics.Render(diagnostics),
            };
        }

        return new ConfigCheckView
        {
            Tone = CheckTone.Clean,
            Message = "The configuration directory has been secured.",
            Details = EnvelopeDiagnostics.Render(diagnostics),
        };
    }

    private static bool IsWarning(EnvelopeDiagnostic d) =>
        d.Severity.Equals("Warning", StringComparison.OrdinalIgnoreCase);
}
