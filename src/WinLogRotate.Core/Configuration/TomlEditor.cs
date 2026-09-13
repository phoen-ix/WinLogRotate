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

    /// <summary>The table is there and the key is not, which only removing and reading mind.</summary>
    NoSuchKey,
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
/// Values are written through <see cref="TomlValue"/>, which carries the type the binder will
/// read back and renders through Tomlyn's own nodes, so they are escaped. Deliberately not the
/// <c>LogrotateImporter</c> pattern of interpolating into <c>$"key = \"{value}\""</c>: an imported
/// file is written disabled and reviewed by a human, whereas a value here comes from a text box
/// and may contain a quote or a backslash.
/// </para>
/// <para>
/// It writes a string, an integer, a bool or an array of strings, because those are the four
/// shapes this product's configuration has. It wrote only the first until a caller needed to set
/// <c>enabled = false</c> and got <c>enabled = "false"</c> - see <see cref="TomlValue"/> for what
/// that costs.
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
        TrySet(file, Split(tablePath), key, TomlValue.Of(value), out error, out detail);

    /// <summary>Sets <paramref name="key"/> to a value of the type the binder expects.</summary>
    public static bool TrySet(
        TomlFile file, string tablePath, string key, TomlValue value,
        out TomlEditError error, out string? detail) =>
        TrySet(file, Split(tablePath), key, value, out error, out detail);

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
        out TomlEditError error, out string? detail) =>
        TrySet(file, tableParts, key, TomlValue.Of(value), out error, out detail);

    /// <summary>The same, typed, which is the one every other overload ends up in.</summary>
    public static bool TrySet(
        TomlFile file, string[] tableParts, string key, TomlValue value,
        out TomlEditError error, out string? detail)
    {
        if (!Locate(file, tableParts, key, out var table, out var existing, out error, out detail))
        {
            return false;
        }

        if (existing is not null)
        {
            // The whole point: only the value node is replaced, so the key's spacing, any inline
            // comment after it, and every other line in the file are untouched.
            var replacement = value.ToSyntax();

            // An inline comment after the value is trailing trivia on the value's own token, so
            // replacing the node alone deletes it.
            if (LastTokenOf(existing.Value) is { TrailingTrivia: { Count: > 0 } after }
                && LastTokenOf(replacement) is { } into)
            {
                into.TrailingTrivia = [.. after];
            }

            existing.Value = replacement;
            return true;
        }

        Append(file, table!, key, value);
        return true;
    }

    /// <summary>
    /// Removes <paramref name="key"/> from a table, leaving its neighbours and their spacing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is how "inherit again" is said. A form cannot express it by writing: <c>""</c>, <c>0</c>
    /// and <c>false</c> are all <i>set</i> to <see cref="SettingsMerge"/>, which asks
    /// <c>job ?? defaults ?? built-in</c> - so clearing a text box and writing the empty string
    /// severs that key from <c>[defaults]</c> for ever while looking like nothing happened.
    /// </para>
    /// <para>
    /// A full-line comment above the removed key stays, and that is deliberate: deleting a line
    /// somebody wrote, because of a different line you removed, is worse than leaving a comment
    /// with nothing under it.
    /// </para>
    /// </remarks>
    public static bool TryRemove(
        TomlFile file, string[] tableParts, string key,
        out TomlEditError error, out string? detail)
    {
        if (!Locate(file, tableParts, key, out var table, out var existing, out error, out detail))
        {
            return false;
        }

        if (existing is null)
        {
            error = TomlEditError.NoSuchKey;
            detail = $"There is no '{key}' in [{string.Join('.', tableParts)}] to remove.";
            return false;
        }

        // The blank line separating this table from the next is trailing trivia on the LAST key's
        // end-of-line token. Removing that key takes the blank line with it and the two tables
        // run together - the mirror image of what Append is careful about, and the reason this
        // cannot be a bare Remove.
        var keys = table!.Items.OfType<KeyValueSyntax>().ToList();

        if (keys.Count > 1
            && ReferenceEquals(keys[^1], existing)
            && existing.EndOfLineToken is { TrailingTrivia: { Count: > 0 } trivia }
            && keys[^2].EndOfLineToken is { } previous)
        {
            previous.TrailingTrivia = [.. trivia];
        }

        for (var i = 0; i < table.Items.ChildrenCount; i++)
        {
            if (ReferenceEquals(table.Items.GetChild(i), existing))
            {
                table.Items.RemoveChildAt(i);
                break;
            }
        }

        return true;
    }

    /// <summary>
    /// The value's source text and 1-based line, exactly as the file writes them.
    /// </summary>
    /// <remarks>
    /// The source text and not a re-rendering, so <c>"100M"</c> comes back as <c>"100M"</c> and
    /// <c>1_000</c> as <c>1_000</c>. An editor showing an operator what their file says must show
    /// what it says.
    /// </remarks>
    public static bool TryRead(
        TomlFile file, string[] tableParts, string key, out string? source, out int line)
    {
        source = null;
        line = 0;

        if (!Locate(file, tableParts, key, out _, out var existing, out _, out _)
            || existing?.Value is null)
        {
            return false;
        }

        source = Rendered(existing.Value);
        line = existing.Span.Start.Line + 1;
        return true;
    }

    /// <summary>
    /// A value's own text, without whatever follows it on the line.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A node renders with its trailing trivia attached, so <c>rotate = 7   # two weeks</c> reads
    /// back as <c>7   # two weeks</c> rather than as <c>7</c>. That is wrong twice over: an
    /// editor comparing "what it says now" with "what was asked for" would never find them equal,
    /// so setting a key to the value it already has rewrote the file - identical bytes, a new
    /// timestamp, a new descriptor, and a change reported to whatever is watching the directory.
    /// And <c>job show</c> would publish somebody's comment as part of the value, which is then
    /// fed back in as one.
    /// </para>
    /// <para>
    /// Stripped by length rather than by cutting at the first <c>#</c>, because a string value
    /// may contain one and <c>dateformat = "%Y#%m"</c> is not a comment.
    /// </para>
    /// </remarks>
    private static string Rendered(ValueSyntax value)
    {
        var text = value.ToString()!;

        if (LastTokenOf(value) is { TrailingTrivia: { Count: > 0 } trivia })
        {
            // Text, not ToString(): a SyntaxTrivia renders as its type name, so concatenating
            // ToString() produced "Tomlyn.Syntax.SyntaxTrivia..." - which is never a suffix of
            // the value, so the strip below silently did nothing and every read carried the
            // comment. Nothing about that is visible in a build.
            var following = string.Concat(trivia.Select(t => t.Text));

            if (following.Length > 0 && text.EndsWith(following, StringComparison.Ordinal))
            {
                text = text[..^following.Length];
            }
        }

        return text.Trim();
    }

    private static string[] Split(string tablePath) =>
        tablePath.Split('.', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Finds the table, and the key inside it if it is there.
    /// </summary>
    /// <remarks>
    /// Shared by every operation, so setting, removing and reading cannot come to disagree about
    /// which table they are looking at or which key they found.
    /// </remarks>
    private static bool Locate(
        TomlFile file, string[] tableParts, string key,
        out TableSyntaxBase? table, out KeyValueSyntax? existing,
        out TomlEditError error, out string? detail)
    {
        error = TomlEditError.None;
        detail = null;
        table = null;
        existing = null;

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
    private static void Append(TomlFile file, TableSyntaxBase table, string key, TomlValue value)
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

        var added = new KeyValueSyntax(key, value.ToSyntax())
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
