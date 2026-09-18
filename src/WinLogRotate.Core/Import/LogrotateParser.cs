namespace WinLogRotate.Core.Import;

/// <summary>One block from a logrotate configuration file.</summary>
public sealed record LogrotateStanza
{
    public required IReadOnlyList<string> Patterns { get; init; }

    /// <summary>Directive name to argument, in file order. A flag carries an empty argument.</summary>
    public required IReadOnlyList<(string Name, string Argument)> Directives { get; init; }

    /// <summary>Script bodies, keyed by their opening directive.</summary>
    public required IReadOnlyDictionary<string, string> Scripts { get; init; }
}

/// <summary>
/// Reads logrotate's own configuration syntax.
/// </summary>
/// <remarks>
/// A real lexer rather than regular expressions, for one specific reason: a script body between
/// <c>postrotate</c> and <c>endscript</c> may contain braces, and any regex-based approach
/// breaks on the first <c>awk '{print $1}'</c>. Since a mis-parsed script body would be silently
/// dropped from the imported job, that is not an acceptable failure.
/// </remarks>
public static class LogrotateParser
{
    private static readonly string[] ScriptOpeners =
        ["prerotate", "postrotate", "firstaction", "lastaction", "preremove"];

    /// <summary>
    /// Directives whose argument is a path, and which were therefore mistaken for a pattern list
    /// when they stood directly above a block.
    /// </summary>
    private static readonly string[] PathDirectives = ["include", "tabooext", "taboopat"];

    private static readonly char[] Whitespace = [' ', '\t'];

    public static IReadOnlyList<LogrotateStanza> Parse(string text)
    {
        var stanzas = new List<LogrotateStanza>();
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        var globals = new List<(string, string)>();
        var i = 0;

        while (i < lines.Length)
        {
            var line = Strip(lines[i]);
            if (line.Length == 0)
            {
                i++;
                continue;
            }

            // 'include /etc/logrotate.d' directly above '/var/log/wtmp {' contains a slash and
            // stands before a brace, so it read as two patterns: a job named 'include' whose
            // live path was "include", and the directory it named silently never imported.
            if (IsPathDirective(line))
            {
                globals.Add(SplitDirective(line));
                i++;
                continue;
            }

            if (line.Contains('{', StringComparison.Ordinal) || PeeksAtBrace(lines, i))
            {
                var patterns = new List<string>();

                // Patterns may span several lines before the brace.
                while (!Strip(lines[i]).Contains('{', StringComparison.Ordinal))
                {
                    patterns.AddRange(SplitPatterns(Strip(lines[i])));
                    i++;
                }

                var headerLine = Strip(lines[i]);
                var brace = headerLine.IndexOf('{', StringComparison.Ordinal);
                patterns.AddRange(SplitPatterns(headerLine[..brace]));

                // logrotate tolerates a directive on the brace line itself. Splitting rather
                // than assuming the brace ends the line means such a config does not silently
                // lose its first directive - which for something like "copytruncate" would
                // change what the imported job does.
                var trailing = headerLine[(brace + 1)..].Trim();
                i++;

                var (directives, scripts, next) = ReadBlock(lines, i);
                i = next;

                if (trailing.Length > 0 && !trailing.StartsWith('}'))
                {
                    directives.Insert(0, SplitDirective(trailing));
                }

                stanzas.Add(new LogrotateStanza
                {
                    Patterns = patterns.Where(p => p.Length > 0).ToArray(),
                    Directives = [.. globals, .. directives],
                    Scripts = scripts,
                });
                continue;
            }

            // Outside a block: a global default, which applies to every block parsed after it.
            // That ordering is upstream's - a directive after a block does not affect it.
            var (name, argument) = SplitDirective(line);
            globals.Add((name, argument));
            i++;
        }

        return stanzas;
    }

    private static (List<(string, string)> Directives, Dictionary<string, string> Scripts, int Next)
        ReadBlock(string[] lines, int i)
    {
        var directives = new List<(string, string)>();
        var scripts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        while (i < lines.Length)
        {
            var raw = lines[i];
            var line = Strip(raw);

            if (line.StartsWith('}'))
            {
                return (directives, scripts, i + 1);
            }

            if (line.Length == 0)
            {
                i++;
                continue;
            }

            var (name, argument) = SplitDirective(line);

            if (ScriptOpeners.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                // Everything up to endscript is taken verbatim, braces and all. No stripping of
                // comments either: a '#' inside a shell script is part of the script.
                var body = new List<string>();
                i++;
                while (i < lines.Length
                       && !lines[i].Trim().Equals("endscript", StringComparison.OrdinalIgnoreCase))
                {
                    body.Add(lines[i]);
                    i++;
                }

                i++;
                scripts[name] = string.Join(Environment.NewLine, body).Trim();
                continue;
            }

            directives.Add((name, argument));
            i++;
        }

        return (directives, scripts, i);
    }

    private static bool PeeksAtBrace(string[] lines, int i)
    {
        for (var j = i; j < lines.Length && j < i + 5; j++)
        {
            var line = Strip(lines[j]);
            if (line.EndsWith('{'))
            {
                return true;
            }

            if (line.Length > 0 && (IsPathDirective(line) || !LooksLikePath(line)))
            {
                return false;
            }
        }

        return false;
    }

    private static bool LooksLikePath(string line) =>
        line.Contains('/', StringComparison.Ordinal)
        || line.Contains('\\', StringComparison.Ordinal)
        || line.StartsWith('"')
        || line.Contains('*', StringComparison.Ordinal);

    private static IEnumerable<string> SplitPatterns(string header)
    {
        var current = new System.Text.StringBuilder();
        var quote = '\0';

        foreach (var c in header)
        {
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                else
                {
                    current.Append(c);
                }

                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }

                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    private static bool IsPathDirective(string line) =>
        PathDirectives.Contains(SplitDirective(line).Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>The directive name and whatever follows it, split at the first blank or tab.</summary>
    /// <remarks>
    /// A tab counts. The stock files are indented with tabs and some are aligned with them, and a
    /// split on the space character alone read <c>rotate\t4</c> as one word that nothing
    /// recognised.
    /// </remarks>
    private static (string Name, string Argument) SplitDirective(string line)
    {
        var space = line.IndexOfAny(Whitespace);
        return space < 0
            ? (line.Trim(), string.Empty)
            : (line[..space].Trim(), line[(space + 1)..].Trim());
    }

    /// <summary>Removes a comment and surrounding whitespace. Only applied outside script bodies.</summary>
    /// <remarks>
    /// logrotate's rule: a <c>#</c> opens a comment where a word would start, at the beginning of
    /// the line or after a blank. One inside a word is part of it - <c>/var/log/app#1.log</c> used
    /// to lose its tail here and match nothing.
    /// </remarks>
    private static string Strip(string line)
    {
        for (var hash = line.IndexOf('#', StringComparison.Ordinal);
             hash >= 0;
             hash = line.IndexOf('#', hash + 1))
        {
            if (hash == 0 || char.IsWhiteSpace(line[hash - 1]))
            {
                return line[..hash].Trim();
            }
        }

        return line.Trim();
    }
}
