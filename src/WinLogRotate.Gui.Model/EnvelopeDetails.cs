using System.Text.Json;
using WinLogRotate.Contracts;

namespace WinLogRotate.Gui.Cli;

/// <summary>
/// What the CLI said went wrong, taken out of the envelope it said it in.
/// </summary>
/// <remarks>
/// <para>
/// An elevated "runas" child has no stderr its parent can read - the console it would write to
/// is hidden and destroyed with the process - so the GUI's failure dialogs were built over an
/// empty string. The child had said exactly what was wrong; it said it in the envelope, in the
/// file it was told to write, and nothing on this side opened it.
/// </para>
/// <para>
/// Only the diagnostics are taken. The envelope's result is generic over types registered in
/// the CLI's own internal context, so it cannot be deserialized here whole - and it is not what
/// an operator staring at a failed dialog needs.
/// </para>
/// </remarks>
public static class EnvelopeDetails
{
    /// <summary>
    /// The warnings and errors from the envelope in this stream, as the CLI would have printed
    /// them, or empty if there are none.
    /// </summary>
    /// <summary>
    /// What to show a person, from whichever channel the child used.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The envelope first, then standard error, and never both - because the two are mutually
    /// exclusive by construction. A <c>--json</c> verb writes everything to stdout and nothing to
    /// stderr; a text verb writes its diagnostics to stderr and never puts a <c>{</c>-prefixed
    /// line on stdout; and the last-resort reporter, which runs when the guard itself was not
    /// reached, writes to stderr with no envelope at all. Concatenating would buy nothing and
    /// cost a rendering decision on a path that cannot happen.
    /// </para>
    /// <para>
    /// Whitespace-only stderr is nothing. Five call sites test <c>Details.Length &gt; 0</c> to
    /// decide whether there is anything to show, and a stray newline is not something to show -
    /// which is the judgement <c>LrDialog.Absent</c> has always made about the same string.
    /// </para>
    /// </remarks>
    public static string From(string stdOut, string stdErr)
    {
        var envelope = From(stdOut);

        return envelope.Length > 0 ? envelope
            : string.IsNullOrWhiteSpace(stdErr) ? string.Empty
            : stdErr;
    }

    public static string From(string ndjson)
    {
        var lines = new List<string>();

        foreach (var line in ndjson.Split('\n'))
        {
            var text = line.TrimEnd('\r');
            if (text.Length == 0 || !text.StartsWith('{'))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(text);

                if (!document.RootElement.TryGetProperty("diagnostics", out var diagnostics)
                    || diagnostics.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var element in diagnostics.EnumerateArray())
                {
                    if (JsonSerializer.Deserialize(
                            element.GetRawText(), CliDiagnosticJson.Default.CliDiagnostic) is { } d)
                    {
                        lines.Add(CliDiagnosticText.Describe(d));
                    }
                }
            }
            catch (JsonException)
            {
                // A line torn by the sweep, or one this version does not understand. A failure
                // dialog that throws while explaining a failure helps nobody.
            }
        }

        return string.Join(Environment.NewLine, lines);
    }
}
