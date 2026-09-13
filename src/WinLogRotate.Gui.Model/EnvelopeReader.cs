using System.Text.Json;

namespace WinLogRotate.Gui.Cli;

/// <summary>
/// Finds the envelope in whatever a verb wrote.
/// </summary>
/// <remarks>
/// <para>
/// Two shapes arrive here. An unelevated <c>--json</c> verb writes exactly one line to stdout,
/// and it is the envelope. An elevated child writes an NDJSON file the GUI hands over whole:
/// zero or more event lines, then the envelope last. A reader that parsed the whole text as one
/// document would read the first shape and fail on the second - which is how an elevated save
/// would come to report "could not read the response" the day a job verb started streaming an
/// event.
/// </para>
/// <para>
/// The rule is the one <c>JsonStreamTests</c> already uses: the last <c>{</c>-prefixed line with
/// a numeric <c>schema</c>. Events have no <c>schema</c>; envelopes always do.
/// </para>
/// </remarks>
public static class EnvelopeReader
{
    /// <summary>The last envelope line in the output, or null if there is none.</summary>
    public static string? Last(string output)
    {
        string? found = null;

        foreach (var line in output.Split('\n'))
        {
            var text = line.TrimEnd('\r');
            if (text.Length == 0 || !text.StartsWith('{'))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(text);
                var root = document.RootElement;

                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("schema", out var schema)
                    && schema.ValueKind == JsonValueKind.Number)
                {
                    found = text;
                }
            }
            catch (JsonException)
            {
                // Not an envelope. A torn line, or a verb that printed something shaped like one.
            }
        }

        return found;
    }
}
