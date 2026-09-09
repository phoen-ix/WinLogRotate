using WinLogRotate.Core.Secrets;

namespace WinLogRotate.Cli.Output;

/// <summary>
/// The rules every way of reading a secret has to obey, in one place.
/// </summary>
/// <remarks>
/// There are two <see cref="IInputSource"/> implementations now - a console and a pipe - and the
/// interesting parts of "read a secret" are not the plumbing, they are these three rules. Left in
/// each implementation they would drift, and the drift would be invisible: a pipe that trimmed
/// whitespace where the console did not would store a different password from the one the
/// operator typed, and nothing would say so until a relay rejected it months later.
/// </remarks>
internal static class SecretInput
{
    /// <summary>
    /// Long enough for any credential anybody actually has, short enough that piping a file in
    /// by mistake is refused rather than stored.
    /// </summary>
    public const int MaxChars = 4096;

    /// <summary>
    /// Strips at most one trailing newline, and nothing else.
    /// </summary>
    /// <remarks>
    /// One newline, because that is what a shell or a text box appends. Not <c>Trim()</c>: a
    /// password may legitimately end in a space, and helpfully removing it stores something other
    /// than what was given - the worst failure available here, because it surfaces as an
    /// authentication error somewhere else entirely. <c>secret test</c> reports the length for
    /// exactly this reason.
    /// </remarks>
    public static string StripOneNewline(string text)
    {
        if (text.EndsWith("\r\n", StringComparison.Ordinal))
        {
            return text[..^2];
        }

        return text.EndsWith('\n') ? text[..^1] : text;
    }

    /// <summary>Turns raw text into a secret, or explains why it is not one.</summary>
    public static bool Validate(string? text, out SecretString value, out string? error)
    {
        value = SecretString.None;

        if (text is null)
        {
            error = "No value was given.";
            return false;
        }

        if (text.Length == 0)
        {
            error = "An empty secret is not stored. Use 'secret remove' to delete one.";
            return false;
        }

        if (text.Length > MaxChars)
        {
            error = $"That is {text.Length:N0} characters, above the {MaxChars:N0} limit. "
                  + "If you meant to store a file, store its contents deliberately with --from-file.";
            return false;
        }

        error = null;
        value = SecretString.From(text);
        return true;
    }
}
