namespace WinLogRotate.Core.Globbing;

/// <summary>
/// POSIX-style glob matching, implemented here rather than delegated to Windows.
/// <para>
/// This is not reinvention for its own sake. Win32 wildcard matching differs from glob in
/// two ways that matter to a tool whose job is deleting files:
/// </para>
/// <list type="number">
/// <item><description>
/// <b>It matches 8.3 short names.</b> <c>FindFirstFile("*.log")</c> also returns
/// <c>something.logfile</c>, because that file's short name is <c>SOMETH~1.LOG</c>. Silent
/// over-matching is merely surprising in a search tool; in a rotator it deletes files the
/// operator never named.
/// </description></item>
/// <item><description>
/// <b>It has no character classes.</b> <c>[0-9]</c> is a literal under Win32, and dateext
/// retention needs classes to find its own archives.
/// </description></item>
/// </list>
/// <para>
/// So the enumerator asks the OS for a bare <c>*</c> and every name is matched here. There is
/// a test that asserts our matcher rejects <c>something.logfile</c> for <c>*.log</c> and that
/// the platform's own matcher accepts it, so if that platform behaviour ever changes we find
/// out from a red test rather than from a support ticket.
/// </para>
/// <para>
/// Matching is <see cref="StringComparison.OrdinalIgnoreCase"/>: NTFS is case-insensitive, and
/// Ordinal rather than InvariantCulture because the Turkish dotless-i turns a culture-aware
/// comparison of "LOGFILE" into a wrong answer on a Turkish machine.
/// </para>
/// </summary>
public static class Glob
{
    /// <summary>Matches a whole path against a pattern. Both may use either separator.</summary>
    /// <remarks>
    /// <c>*</c> and <c>?</c> never cross a directory separator. <c>**</c> as a whole segment
    /// matches zero or more segments, which the per-site IIS log layout effectively requires.
    /// </remarks>
    public static bool IsMatch(string path, string pattern)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(pattern);

        var pathSegments = Split(path);
        var patternSegments = Split(pattern);
        return MatchSegments(pathSegments, 0, patternSegments, 0);
    }

    /// <summary>Matches a single file or directory name - no separators, no <c>**</c>.</summary>
    public static bool IsNameMatch(string name, string pattern) =>
        MatchSegment(name.AsSpan(), pattern.AsSpan());

    /// <summary>True when the pattern contains any wildcard metacharacter.</summary>
    public static bool HasWildcard(string pattern) =>
        pattern.AsSpan().IndexOfAny('*', '?', '[') >= 0;

    /// <summary>
    /// Whether any file <b>strictly below</b> <paramref name="directory"/> could match
    /// <paramref name="pattern"/> - the question an enumerator answers before it descends.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what was missing, and its absence made every documented wildcard outside the
    /// final path segment match nothing at all, for ever, in silence. The enumerator anchored at
    /// <see cref="LiteralPrefix"/>, which cuts at the <i>first</i> wildcard, and then descended
    /// only when the pattern contained <c>**</c>. So
    /// <c>C:/inetpub/logs/LogFiles/W3SVC[0-9]/*.log</c> - the natural IIS spelling, and IIS
    /// filling disks is this product's headline case - anchored at <c>LogFiles</c>, never looked
    /// inside <c>W3SVC1</c>, and rotated nothing. With <c>missingok</c> the run was silent and
    /// exited 0, and <c>winlogrotate glob</c> printed "no files match", which reads as "nothing
    /// is there".
    /// </para>
    /// <para>
    /// Strictly below, because the files in the directory itself have already been listed by the
    /// time this is asked. So a directory is worth descending into when its segments match the
    /// pattern's leading segments and at least two pattern segments remain - one for a directory
    /// level and one for a filename. Past a <c>**</c> the answer is always yes: it matches zero
    /// or more segments and nothing below it can rule that out.
    /// </para>
    /// <para>
    /// Answering "maybe" too often costs a directory listing. Answering "no" wrongly is what
    /// this is replacing, and it costs the operator every log under that pattern.
    /// </para>
    /// </remarks>
    public static bool WorthDescending(string directory, string pattern)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(pattern);

        return CouldDescend(Split(directory), 0, Split(pattern), 0);
    }

    private static bool CouldDescend(string[] path, int pi, string[] pattern, int qi)
    {
        while (true)
        {
            if (qi == pattern.Length)
            {
                // The pattern ran out before the directory did. Everything it names is above
                // here, so nothing below can match.
                return false;
            }

            if (pattern[qi] == "**")
            {
                // Zero or more segments, so every directory from here down is a candidate and
                // no amount of looking further can rule one out.
                return true;
            }

            if (pi == path.Length)
            {
                // The directory ran out first. Something strictly below it can only match if the
                // pattern still has a directory level left as well as a filename - with one
                // segment left, every match is a file in this directory, and those have been
                // listed already.
                return pattern.Length - qi >= 2;
            }

            if (!MatchSegment(path[pi].AsSpan(), pattern[qi].AsSpan()))
            {
                return false;
            }

            pi++;
            qi++;
        }
    }

    /// <summary>
    /// The longest leading run of segments containing no wildcard - the directory an
    /// enumeration can start from instead of walking the whole volume.
    /// </summary>
    public static string LiteralPrefix(string pattern)
    {
        // Slice the original string rather than rejoining split segments: splitting discards
        // the leading "\\" of a UNC path, and an anchor that is no longer absolute silently
        // stops being recognised as a volume root - which is one of the refusals that matters.
        var normalized = pattern.Replace('/', '\\');

        // The '?' in a \\?\ prefix is not a wildcard, and reading it as one was a defect with
        // two halves. The scan found it at index 2, the separator before it is at index 1, and
        // the anchor came back as a single backslash - so PathGuard measured its protected-root
        // and volume-root rules against the wrong directory entirely, and FileEnumerator would
        // have walked from the root of the current drive. The prefix itself is kept: it is what
        // makes a path longer than MAX_PATH work, and this result is handed to the OS.
        var scanFrom = normalized.StartsWith(@"\\?\", StringComparison.Ordinal) ? 4 : 0;

        var found = normalized.AsSpan(scanFrom).IndexOfAny('*', '?', '[');
        var firstWildcard = found < 0 ? -1 : found + scanFrom;

        // No wildcard at all: the last segment is the filename, so the prefix is its directory.
        var searchFrom = firstWildcard < 0 ? normalized.Length - 1 : firstWildcard;
        if (searchFrom < 0)
        {
            return string.Empty;
        }

        var separator = normalized.LastIndexOf('\\', searchFrom);
        return separator <= 0 ? string.Empty : normalized[..separator];
    }

    private static string[] Split(string value) =>
        value.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);

    private static bool MatchSegments(string[] path, int pi, string[] pattern, int qi)
    {
        while (true)
        {
            if (qi == pattern.Length)
            {
                return pi == path.Length;
            }

            if (pattern[qi] == "**")
            {
                // Collapse runs of ** so "a/**/**/b" costs no more than "a/**/b".
                while (qi + 1 < pattern.Length && pattern[qi + 1] == "**")
                {
                    qi++;
                }

                // Zero segments, then one, then two... Recursion depth is bounded by the
                // number of path segments, which the path-length limit already bounds.
                for (var skip = pi; skip <= path.Length; skip++)
                {
                    if (MatchSegments(path, skip, pattern, qi + 1))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (pi == path.Length || !MatchSegment(path[pi].AsSpan(), pattern[qi].AsSpan()))
            {
                return false;
            }

            pi++;
            qi++;
        }
    }

    /// <summary>
    /// Matches one path segment. Iterative with a single backtrack point for <c>*</c>, so a
    /// pathological pattern such as <c>*a*a*a*a*b</c> cannot blow the stack or hang - which
    /// matters because patterns come from a config file an operator typed.
    /// </summary>
    private static bool MatchSegment(ReadOnlySpan<char> name, ReadOnlySpan<char> pattern)
    {
        int n = 0, p = 0, starP = -1, starN = -1;

        while (n < name.Length)
        {
            if (p < pattern.Length && pattern[p] == '*')
            {
                starP = p;
                starN = n;
                p++;
                continue;
            }

            var probe = p;
            if (p < pattern.Length && MatchAtom(pattern, ref probe, name[n]))
            {
                p = probe;
                n++;
                continue;
            }

            if (starP < 0)
            {
                return false;
            }

            // Give the star one more character and retry from just after it.
            starN++;
            n = starN;
            p = starP + 1;
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }

    /// <summary>
    /// Consumes one pattern atom - a literal, <c>?</c>, or a bracket expression - and reports
    /// whether it matches <paramref name="c"/>. <paramref name="p"/> advances only on a match,
    /// so a failed probe leaves the caller's position untouched.
    /// </summary>
    private static bool MatchAtom(ReadOnlySpan<char> pattern, ref int p, char c)
    {
        if (pattern[p] == '?')
        {
            p++;
            return true;
        }

        if (pattern[p] == '[')
        {
            return MatchBracket(pattern, ref p, c);
        }

        if (char.ToUpperInvariant(pattern[p]) != char.ToUpperInvariant(c))
        {
            return false;
        }

        p++;
        return true;
    }

    private static bool MatchBracket(ReadOnlySpan<char> pattern, ref int p, char c)
    {
        var i = p + 1;
        var negated = false;

        if (i < pattern.Length && (pattern[i] == '!' || pattern[i] == '^'))
        {
            negated = true;
            i++;
        }

        var matched = false;
        var first = true;

        while (i < pattern.Length && (pattern[i] != ']' || first))
        {
            first = false;

            // "a-z", but a trailing '-' before the ']' is a literal hyphen.
            if (i + 2 < pattern.Length && pattern[i + 1] == '-' && pattern[i + 2] != ']')
            {
                var lo = char.ToUpperInvariant(pattern[i]);
                var hi = char.ToUpperInvariant(pattern[i + 2]);
                var u = char.ToUpperInvariant(c);
                if (u >= lo && u <= hi)
                {
                    matched = true;
                }

                i += 3;
                continue;
            }

            if (char.ToUpperInvariant(pattern[i]) == char.ToUpperInvariant(c))
            {
                matched = true;
            }

            i++;
        }

        // Unterminated bracket: treat the '[' as a literal rather than swallowing the rest of
        // the pattern. A typo should narrow what matches, never widen it.
        if (i >= pattern.Length)
        {
            if (c != '[')
            {
                return false;
            }

            p++;
            return true;
        }

        p = i + 1;
        return matched != negated;
    }
}
