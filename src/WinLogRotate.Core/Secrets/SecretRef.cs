namespace WinLogRotate.Core.Secrets;

/// <summary>Where a credential comes from.</summary>
public enum SecretSource
{
    /// <summary>Nothing configured.</summary>
    None,

    /// <summary>Written in the configuration file, in the clear.</summary>
    Literal,

    /// <summary><c>@secret:name</c> - the encrypted store.</summary>
    Store,

    /// <summary><c>@env:NAME</c> - the run host's environment, for containers and testing.</summary>
    Environment,

    /// <summary><c>@command:...</c> - fetched from a vault at send time.</summary>
    Command,
}

/// <summary>
/// A reference to a credential, resolved at send time rather than at parse time.
/// </summary>
/// <remarks>
/// The indirection is the point, and it matters more than the encryption behind it: a
/// configuration file saying <c>password = "@secret:ses-smtp"</c> is safe to paste into a support
/// ticket, commit to a deployment repository, or screenshot. Those are the two ways credentials
/// actually escape, and neither is addressed by encrypting a file that still has the password
/// written next to it.
/// </remarks>
public sealed record SecretRef
{
    public required SecretSource Source { get; init; }

    /// <summary>The name, variable or command line. Never a value.</summary>
    public string? Key { get; init; }

    /// <summary>Only for <see cref="SecretSource.Literal"/>.</summary>
    public SecretString Literal { get; init; }

    public int Line { get; init; }

    public int Column { get; init; }

    public static SecretRef None { get; } = new() { Source = SecretSource.None };

    public bool HasValue => Source != SecretSource.None;

    /// <summary>
    /// What to print. Safe by construction: there is no branch that returns a value.
    /// </summary>
    public string Describe() => Source switch
    {
        SecretSource.None => "none",
        SecretSource.Store => $"secret:{Key}",
        SecretSource.Environment => $"env:{Key}",
        SecretSource.Command => "command",
        _ => "literal (in the config file)",
    };

    /// <summary>
    /// Reads the reference grammar.
    /// </summary>
    /// <remarks>
    /// <c>@@</c> escapes a literal that genuinely starts with <c>@</c>, so a password of
    /// <c>@secret:x</c> remains expressible. Anything else with no prefix is a literal, which is
    /// supported deliberately: forbidding it drives people to worse workarounds, and warning at
    /// the exact line every single run does not.
    /// </remarks>
    public static SecretRef Parse(string? raw, int line = 0, int column = 0)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return None;
        }

        if (raw.StartsWith("@@", StringComparison.Ordinal))
        {
            return Literalise(raw[1..], line, column);
        }

        if (!raw.StartsWith('@'))
        {
            return Literalise(raw, line, column);
        }

        var colon = raw.IndexOf(':');
        if (colon < 0)
        {
            return Literalise(raw, line, column);
        }

        var kind = raw[1..colon].ToLowerInvariant();
        var rest = raw[(colon + 1)..];

        return kind switch
        {
            "secret" => new SecretRef { Source = SecretSource.Store, Key = rest, Line = line, Column = column },
            "env" => new SecretRef { Source = SecretSource.Environment, Key = rest, Line = line, Column = column },
            "command" => new SecretRef { Source = SecretSource.Command, Key = rest, Line = line, Column = column },
            _ => Literalise(raw, line, column),
        };
    }

    private static SecretRef Literalise(string raw, int line, int column) => new()
    {
        Source = SecretSource.Literal,
        Literal = SecretString.From(raw),
        Line = line,
        Column = column,
    };
}

/// <summary>Whether a named secret exists, from the point of view of whoever is asking.</summary>
public interface ISecretLookup
{
    /// <summary>
    /// True, false, or <b>null for "this caller cannot tell"</b>.
    /// </summary>
    /// <remarks>
    /// The third answer is the important one. The secret store grants Users nothing, so an
    /// unelevated caller - the read-only GUI running <c>config check</c>, most often - cannot
    /// read it. A two-valued answer would make that GUI report "no secret called ses-smtp" on a
    /// perfectly healthy machine: a false security alarm, shown to the person least equipped to
    /// evaluate it, about a file they are not allowed to look at.
    /// </remarks>
    bool? Exists(string name);
}

/// <summary>The lookup for a caller that cannot see the store at all.</summary>
public sealed class UnknownSecretLookup : ISecretLookup
{
    public bool? Exists(string name) => null;
}
