using Tomlyn.Syntax;

namespace WinLogRotate.Core.Configuration;

/// <summary>Why a value could not be written.</summary>
public enum TomlEditError
{
    None,

    /// <summary>No table with that header. Deliberately not created - see the remarks below.</summary>
    NoSuchTable,

    /// <summary>The key would need quoting to be written back, so it is refused instead.</summary>
    UnusableKey,
}

/// <summary>
/// Changes exactly one value in a TOML file, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes the config-format decision true rather than merely intended.
/// <see cref="TomlFile"/> has always said that <c>Load, set one value, Save</c> differs in exactly
/// one line - but until now there was no way to set a value, so the property was asserted by
/// nothing and the sysadmin's <c>#&#160;do NOT enable compress</c> comment was safe only by
/// never being touched. <c>SetsOneValueAndChangesNothingElse</c> is the test that claim needed.
/// </para>
/// <para>
/// <b>Deliberately tiny.</b> It sets an existing key, or appends one to a table that already
/// exists. It does not create tables, delete keys, reorder anything, or reformat. A missing table
/// is refused with the header to add, because deciding where a new <c>[notify.email.relay]</c>
/// belongs in somebody's file - which blank lines, which comments travel with it - is the point
/// at which a comment-preserving editor becomes a formatter, and formatters are how configuration
/// files get mangled.
/// </para>
/// <para>
/// Values are written through <see cref="StringValueSyntax"/>, which escapes them. Deliberately
/// not the <c>LogrotateImporter</c> pattern of interpolating into <c>$"key = \"{value}\""</c>: an
/// imported file is written disabled and reviewed by a human, whereas a value here comes from a
/// text box and may contain a quote or a backslash.
/// </para>
/// </remarks>
public static class TomlEditor
{
    /// <summary>Sets <paramref name="key"/> in <paramref name="tablePath"/> to a quoted string.</summary>
    /// <param name="tablePath">Dotted, as written in the header: <c>notify.email.relay</c>.</param>
    /// <returns>False, with <paramref name="error"/> set, if nothing was changed.</returns>
    public static bool TrySet(
        TomlFile file, string tablePath, string key, string value,
        out TomlEditError error, out string? detail) =>
        TrySet(file, tablePath.Split('.', StringSplitOptions.RemoveEmptyEntries),
            key, value, out error, out detail);

    /// <summary>
    /// The same, for a caller that already knows the header's parts.
    /// </summary>
    /// <remarks>
    /// Needed because a part may itself contain a dot: <c>[notify.email."my.relay"]</c> is three
    /// parts, not four, and a caller that joins them into a string for this to split again names
    /// a table that does not exist.
    /// </remarks>
    public static bool TrySet(
        TomlFile file, string[] tableParts, string key, string value,
        out TomlEditError error, out string? detail)
    {
        error = TomlEditError.None;
        detail = null;

        if (!IsBareKey(key))
        {
            error = TomlEditError.UnusableKey;
            detail = $"'{key}' would have to be quoted to be written back.";
            return false;
        }

        var wanted = tableParts;

        // Matched on the header's parsed key nodes rather than on its text, because
        // [notify . email . relay] and ["notify".email.relay] are both legal spellings of the
        // same table and neither survives a string comparison. ConfigBinder.KeyParts is the
        // binder's own reader, used here so the writer cannot disagree with it about which
        // table it is looking at.
        TableSyntaxBase? table = null;
        foreach (var candidate in file.Document.Tables)
        {
            // [[notify.email.relay]] is an array of tables, not a table. ConfigBinder refuses
            // those outright, so writing into the first element would be editing something the
            // product never reads.
            if (candidate is TableArraySyntax)
            {
                continue;
            }

            var parts = ConfigBinder.KeyParts(candidate);
            if (parts.Length == wanted.Length && parts.Zip(wanted).All(
                    p => string.Equals(p.First, p.Second, StringComparison.OrdinalIgnoreCase)))
            {
                table = candidate;
                break;
            }
        }

        if (table is null)
        {
            error = TomlEditError.NoSuchTable;
            detail = $"There is no [{string.Join('.', wanted)}] table in {file.Path}.";
            return false;
        }

        // Compared unquoted, because "password" and password are the same key. Comparing the
        // rendered text appends a SECOND password instead - TOML then rejects the duplicate or
        // the binder takes the first, and the plaintext credential this exists to remove is still
        // sitting in the file.
        KeyValueSyntax? existing = null;

        foreach (var candidate in table.Items.OfType<KeyValueSyntax>())
        {
            var parts = NameParts(candidate);

            if (parts.Length == 0
                || !string.Equals(parts[0], key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (parts.Length > 1)
            {
                // b.c = 1 already defines b as a table. Appending b = "x" after it is a
                // redefinition, and the file stops parsing.
                error = TomlEditError.UnusableKey;
                detail = $"'{key}' is already a table here, defined by '{string.Join('.', parts)}'.";
                return false;
            }

            existing = candidate;
            break;
        }

        if (existing is not null)
        {
            // The whole point: only the value node is replaced, so the key's spacing, any inline
            // comment after it, and every other line in the file are untouched.
            var replacement = new StringValueSyntax(value);

            // An inline comment after the value is trailing trivia on the value's own token, so
            // replacing the node alone deletes it.
            if (LastTokenOf(existing.Value) is { TrailingTrivia: { Count: > 0 } after })
            {
                replacement.Token!.TrailingTrivia = [.. after];
            }

            existing.Value = replacement;
            return true;
        }

        Append(file, table, key, value);
        return true;
    }

    /// <summary>The last token a value is made of, which carries any trivia written after it.</summary>
    private static SyntaxToken? LastTokenOf(SyntaxNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is SyntaxToken token)
        {
            return token;
        }

        for (var i = node.ChildrenCount - 1; i >= 0; i--)
        {
            if (LastTokenOf(node.GetChild(i)) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Adds a key at the end of a table's own keys.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The explicit end-of-line token is not decoration. Tomlyn emits a constructed key-value
    /// with a bare <c>\n</c>, so without one a CRLF file silently acquires a mixed line ending -
    /// which is invisible in an editor and shows up as a whole-file diff in git.
    /// </para>
    /// </remarks>
    private static void Append(TomlFile file, TableSyntaxBase table, string key, string value)
    {
        var last = table.Items.OfType<KeyValueSyntax>().LastOrDefault();

        // A file whose last line has no newline - which plenty of editors produce - leaves its
        // last key with no end-of-line token, and appending after that writes straight onto the
        // end of the line: retain = 30notify = "yes".
        if (last is not null && last.EndOfLineToken is null)
        {
            last.EndOfLineToken = new SyntaxToken(TokenKind.NewLine, NewlineOf(file));
        }

        // Taken from the file, never from the machine. Environment.NewLine is "\n" on the Linux
        // test leg and on any Linux CI runner, so using it would quietly put a lone LF into a
        // CRLF configuration file and show every following line as changed in the operator's
        // next diff.
        var eol = last?.EndOfLineToken?.Text ?? NewlineOf(file);

        var added = new KeyValueSyntax(key, new StringValueSyntax(value))
        {
            EndOfLineToken = new SyntaxToken(TokenKind.NewLine, eol),
        };

        // The blank line separating this table from the next is trailing trivia on the last key,
        // so it moves onto the new one. Otherwise the new key lands after the blank line, the two
        // tables run together, and a one-line addition reads as a two-line diff in review.
        if (last?.EndOfLineToken is { TrailingTrivia: { Count: > 0 } trivia })
        {
            added.EndOfLineToken.TrailingTrivia = [.. trivia];
            trivia.Clear();
        }

        table.Items.Add(added);
    }

    /// <summary>
    /// The line ending this file already uses, for a table that has no keys to copy one from.
    /// </summary>
    /// <remarks>
    /// Read from the document rather than from <see cref="Environment.NewLine"/>, which is
    /// <c>\n</c> on the Linux test leg and on any Linux CI runner - so the machine doing the
    /// editing would decide the line endings of a Windows operator's file.
    /// </remarks>
    private static string NewlineOf(TomlFile file) =>
        file.ToString().Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    /// <summary>
    /// A key's parts, unquoted, the way <see cref="ConfigBinder"/> reads a table header's.
    /// </summary>
    /// <remarks>
    /// One element for an ordinary key, more for a dotted one such as <c>b.c</c> - which defines
    /// a table rather than a value, and therefore cannot be replaced by one.
    /// </remarks>
    private static string[] NameParts(KeyValueSyntax kv)
    {
        if (kv.Key is not { } name)
        {
            return [];
        }

        var parts = new List<string> { ConfigBinder.Unquote(name.Key?.ToString()?.Trim()) };

        foreach (var dotted in name.DotKeys)
        {
            parts.Add(ConfigBinder.Unquote(dotted.Key?.ToString()?.Trim()));
        }

        return [.. parts];
    }

    /// <summary>Whether a key can be written unquoted, which is the only form this writes.</summary>
    private static bool IsBareKey(string key) =>
        key.Length > 0 && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
}
