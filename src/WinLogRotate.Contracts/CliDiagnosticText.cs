namespace WinLogRotate.Contracts;

/// <summary>
/// Turns a diagnostic into the line an operator reads. The only place that does.
/// </summary>
/// <remarks>
/// Beside <see cref="CliEventText"/> and for the same reason. The CLI's text sink had this and
/// the GUI had nothing, so an elevated operation that failed showed its dialog with an empty
/// details box - the CLI had said exactly what went wrong, in the envelope, and the only reader
/// on that side of the wire could not turn it into a sentence.
/// </remarks>
public static class CliDiagnosticText
{
    /// <summary>
    /// The diagnostic as one or two lines: what happened, and what to do about it.
    /// </summary>
    /// <param name="label">
    /// The severity word, so a terminal can hand in a coloured one. Plain by default, because
    /// every other caller is writing to somewhere that has no colours.
    /// </param>
    public static string Describe(CliDiagnostic d, string? label = null)
    {
        var where = d.Path is null ? ""
            : d.Line is null ? $"{d.Path}: "
            : $"{d.Path}({d.Line},{d.Column ?? 1}): ";

        var head = label is null
            ? $"{Label(d.Severity)}: {where}{d.Message} [{d.Code}]"
            : $"{label} {where}{d.Message} [{d.Code}]";

        return d.Remedy is null ? head : $"{head}{Environment.NewLine}        {d.Remedy}";
    }

    /// <summary>The severity as the word the CLI has always printed.</summary>
    public static string Label(Severity severity) => severity switch
    {
        Severity.Critical => "critical",
        Severity.Error => "error",
        Severity.Warning => "warning",
        _ => "info",
    };
}
