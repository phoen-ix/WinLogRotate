using System.Globalization;
using Tomlyn.Syntax;

namespace WinLogRotate.Core.Configuration;

/// <summary>The four shapes a value in this product's configuration ever takes.</summary>
public enum TomlValueKind
{
    /// <summary>A quoted string: <c>compresstype = "zip"</c>.</summary>
    String,

    /// <summary>A bare number: <c>rotate = 7</c>.</summary>
    Integer,

    /// <summary>A bare <c>true</c> or <c>false</c>.</summary>
    Boolean,

    /// <summary>An array of quoted strings: <c>paths = ["a", "b"]</c>.</summary>
    StringList,
}

/// <summary>
/// A value on its way into a configuration file, carrying the type it will be read back as.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TomlEditor"/> could write only quoted strings, and every caller it had wanted one.
/// The moment anything wanted to write a job key that is not a string, that limit stopped being a
/// simplification and became an outage: <c>enabled = "false"</c> is a string where
/// <see cref="ConfigBinder"/> requires a bool, so the binder errors, the job is <b>not</b>
/// disabled, and - because that error is raised against the whole configuration rather than
/// against one file - <c>LoadedConfig.HasErrors</c> is true and a run exits 2 having rotated
/// nothing on the machine. Two wrong answers from one keystroke.
/// </para>
/// <para>
/// So the type travels with the value, and it comes from the schema rather than from the text.
/// Guessing it from the text is the other way to get this wrong: <c>rotate=30</c> guessed as a
/// string writes <c>"30"</c>, which the binder rejects, and <c>dateformat=20240101</c> guessed as
/// a number writes a bare integer for a key that must be a string.
/// </para>
/// <para>
/// <see cref="ToSyntax"/> is the only place in the product that constructs a Tomlyn value node.
/// That is deliberate: an array needs item nodes and comma tokens, and a caller that assembled one
/// from text would re-open the escaping hole <see cref="TomlEditor"/> exists to close.
/// </para>
/// </remarks>
public readonly record struct TomlValue
{
    private readonly string? _text;
    private readonly long _number;
    private readonly bool _flag;
    private readonly IReadOnlyList<string>? _items;

    private TomlValue(TomlValueKind kind, string? text, long number, bool flag, IReadOnlyList<string>? items)
    {
        Kind = kind;
        _text = text;
        _number = number;
        _flag = flag;
        _items = items;
    }

    /// <summary>Which of the four this is.</summary>
    public TomlValueKind Kind { get; }

    /// <summary>A quoted string. Escaping is Tomlyn's, not ours.</summary>
    public static TomlValue Of(string text) =>
        new(TomlValueKind.String, text, 0, false, null);

    /// <summary>A bare integer.</summary>
    public static TomlValue Of(long number) =>
        new(TomlValueKind.Integer, null, number, false, null);

    /// <summary>A bare <c>true</c> or <c>false</c>.</summary>
    public static TomlValue Of(bool flag) =>
        new(TomlValueKind.Boolean, null, 0, flag, null);

    /// <summary>An array of quoted strings.</summary>
    /// <remarks>
    /// An empty list is deliberately allowed to be constructed and deliberately never written by
    /// a verb: to <see cref="ConfigBinder"/> an empty array and an absent key are the same state,
    /// and two spellings of one state is a state nobody can reason about. Removing the key is how
    /// that is said.
    /// </remarks>
    public static TomlValue List(IReadOnlyList<string> items) =>
        new(TomlValueKind.StringList, null, 0, false, [.. items]);

    /// <summary>
    /// The TOML source text this would be written as.
    /// </summary>
    /// <remarks>
    /// For showing a caller what changed, never for parsing back: a round trip goes through the
    /// document, not through this string.
    /// </remarks>
    public override string ToString() => Kind switch
    {
        TomlValueKind.Integer => _number.ToString(CultureInfo.InvariantCulture),
        TomlValueKind.Boolean => _flag ? "true" : "false",
        TomlValueKind.StringList => "[" + string.Join(", ", (_items ?? []).Select(Quote)) + "]",
        _ => Quote(_text ?? string.Empty),
    };

    /// <summary>The node Tomlyn will render, with Tomlyn's own escaping.</summary>
    internal ValueSyntax ToSyntax() => Kind switch
    {
        TomlValueKind.Integer => new IntegerValueSyntax(_number),
        TomlValueKind.Boolean => new BooleanValueSyntax(_flag),
        TomlValueKind.StringList => Array(_items ?? []),
        _ => new StringValueSyntax(_text ?? string.Empty),
    };

    private static ArraySyntax Array(IReadOnlyList<string> items)
    {
        var array = new ArraySyntax
        {
            OpenBracket = new SyntaxToken(TokenKind.OpenBracket, "["),
            CloseBracket = new SyntaxToken(TokenKind.CloseBracket, "]"),
        };

        for (var i = 0; i < items.Count; i++)
        {
            var item = new ArrayItemSyntax { Value = new StringValueSyntax(items[i]) };

            // No trailing comma after the last item. TOML permits one, but a file this product
            // wrote should read the way a person would have written it.
            if (i < items.Count - 1)
            {
                item.Comma = new SyntaxToken(TokenKind.Comma, ",")
                {
                    TrailingTrivia = [new SyntaxTrivia(TokenKind.Whitespaces, " ")],
                };
            }

            array.Items.Add(item);
        }

        return array;
    }

    /// <summary>
    /// A string as TOML writes it, for <see cref="ToString"/> only.
    /// </summary>
    /// <remarks>
    /// Enough for a change report, and not a serializer: the two characters that would break the
    /// line are escaped and nothing else is. What actually reaches the file goes through
    /// <see cref="StringValueSyntax"/>, which handles the rest.
    /// </remarks>
    private static string Quote(string text) =>
        "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal)
                   .Replace("\"", "\\\"", StringComparison.Ordinal)
             + "\"";
}
