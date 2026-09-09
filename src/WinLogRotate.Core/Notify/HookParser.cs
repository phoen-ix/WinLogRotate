namespace WinLogRotate.Core.Notify;

/// <summary>Why a target could not be parsed.</summary>
public enum HookParseError
{
    None,

    /// <summary>Blank, or only whitespace.</summary>
    Empty,

    /// <summary>Something before a colon that looks like a scheme but is not one of ours.</summary>
    UnknownScheme,
}

/// <summary>The outcome of parsing one target.</summary>
public readonly record struct HookParseResult
{
    public HookAction? Action { get; init; }
    public HookParseError Error { get; init; }

    /// <summary>The scheme that was written, when it was not recognised.</summary>
    public string? Scheme { get; init; }

    public bool IsOk => Error == HookParseError.None && Action is not null;
}

/// <summary>
/// Turns a configured string into a <see cref="HookAction"/>.
/// </summary>
/// <remarks>
/// One parser for hooks and for notification targets alike, so <c>postrotate</c> and
/// <c>[notify]</c> cannot drift apart in what they accept.
/// </remarks>
public static class HookParser
{
    private const int MinSchemeLength = 2;

    public static HookParseResult Parse(string raw, string? file = null, int line = 0, int column = 0)
    {
        var text = raw.Trim();

        if (text.Length == 0)
        {
            return new HookParseResult { Error = HookParseError.Empty };
        }

        if (!TryFindScheme(text, out var colon))
        {
            // No scheme is a command line. That is logrotate's own form, and it is what somebody
            // pastes out of a ticket - so it has to keep working rather than being an error.
            return Ok(HookAction.Create(HookScheme.Command, text, text, file: file, line: line, column: column));
        }

        var name = text[..colon].ToLowerInvariant();
        var rest = text[(colon + 1)..];

        return name switch
        {
            // The scheme is part of the address, so Target is the whole URL rather than the
            // remainder. https maps to the same member: they differ in transport, not in kind.
            "http" or "https" =>
                Ok(HookAction.Create(HookScheme.Http, text, text, file: file, line: line, column: column)),

            "command" =>
                Ok(HookAction.Create(HookScheme.Command, text, rest, file: file, line: line, column: column)),

            "eventlog" =>
                Ok(HookAction.Create(HookScheme.EventLog, text, rest, file: file, line: line, column: column)),

            "event" =>
                Ok(HookAction.Create(HookScheme.Event, text, rest, file: file, line: line, column: column)),

            "smtp" =>
                Ok(HookAction.Create(HookScheme.Smtp, text, rest, file: file, line: line, column: column)),

            "pushover" =>
                Ok(HookAction.Create(HookScheme.Pushover, text, rest, file: file, line: line, column: column)),

            "service" => ParseService(text, rest, file, line, column),

            // Never a silent fall-through to Command. A typed "htp://..." must become a
            // diagnostic, not an attempt to execute a program called htp://...
            _ => new HookParseResult { Error = HookParseError.UnknownScheme, Scheme = name },
        };
    }

    private static HookParseResult ParseService(string raw, string rest, string? file, int line, int column)
    {
        var split = rest.IndexOf(':');

        // service:NAME carries no verb, which means the default control code. Accepted because
        // it is the obvious thing to write, and because rejecting it would only teach people
        // that the three-part form is a magic incantation.
        return split < 0
            ? Ok(HookAction.Create(HookScheme.Service, raw, rest, file: file, line: line, column: column))
            : Ok(HookAction.Create(
                HookScheme.Service, raw, rest[(split + 1)..], verb: rest[..split].ToLowerInvariant(),
                file: file, line: line, column: column));
    }

    private static HookParseResult Ok(HookAction action) => new() { Action = action };

    /// <summary>
    /// Finds a scheme, if the string has one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The case that decides the whole grammar is <c>command:C:\tools\x.exe</c>. The rule: a
    /// scheme is <b>two or more scheme characters, terminated by the first colon</b>. A drive
    /// letter is one character and a scheme never is, so <c>C:\tools\x.exe</c> on its own finds
    /// no scheme and is a command line.
    /// </para>
    /// <para>
    /// Restricting the prefix to <c>[A-Za-z0-9+.-]</c> also subsumes "no path separator before
    /// the colon", and does it more thoroughly than checking for separators would: a space is
    /// not a scheme character either, so <c>cmd /c copy C:\a C:\b</c> and
    /// <c>"C:\Program Files\x.exe" -q</c> both fall through to a command line rather than
    /// finding a scheme of "cmd /c copy C".
    /// </para>
    /// </remarks>
    internal static bool TryFindScheme(string text, out int colon)
    {
        colon = text.IndexOf(':');

        if (colon < MinSchemeLength)
        {
            return false;
        }

        foreach (var c in text.AsSpan(0, colon))
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('+' or '.' or '-'))
            {
                return false;
            }
        }

        // A scheme starts with a letter; "8080:something" is not one.
        return char.IsAsciiLetter(text[0]);
    }
}
