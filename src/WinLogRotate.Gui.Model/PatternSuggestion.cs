using System.Text.RegularExpressions;
using WinLogRotate.Core.Globbing;

namespace WinLogRotate.Gui.Cli;

/// <summary>
/// Turns a file somebody picked in a dialog into the line that names it, and its series.
/// </summary>
/// <remarks>
/// <para>
/// A picked file is the one visible thing a new user has; what a job needs is the shape all
/// its siblings fit. A name that carries a date - <c>u_ex240915.log</c>, IIS's daily file - is
/// one of a series, and naming it exactly would rotate one day for ever; a name without one -
/// <c>app.log</c> - is the live log, and inventing a wildcard for it would be a guess. The
/// preview line shows the consequence either way, before anything is saved.
/// </para>
/// <para>
/// Pure, so both readings are pinned on the leg that cannot open a dialog.
/// </para>
/// </remarks>
public static partial class PatternSuggestion
{
    /// <summary>The line for a picked file: its series when the name carries a date, else itself.</summary>
    public static string FromPick(string path)
    {
        var slashed = path.Replace('\\', '/');
        var cut = slashed.LastIndexOf('/');
        var folder = cut < 0 ? string.Empty : slashed[..(cut + 1)];
        var file = cut < 0 ? slashed : slashed[(cut + 1)..];

        var dot = file.LastIndexOf('.');
        var stem = dot <= 0 ? file : file[..dot];
        var extension = dot <= 0 ? string.Empty : file[dot..];

        var runs = DigitRun().Matches(stem).ToArray();
        var dated = runs.Where(m => m.Length >= 4).ToArray();
        if (dated.Length == 0)
        {
            return folder + file;
        }

        // From the first dated digit to the end of the last number: app-2026-09-18 is one
        // date, not three numbers, and error20260918-1 is a date and a sequence number.
        var first = dated.Min(m => m.Index);
        var last = runs.Where(m => m.Index >= first).Max(m => m.Index + m.Length);

        return folder + stem[..first] + "*" + stem[last..] + extension;
    }

    /// <summary>The folder a line starts from, for a dialog to open in; empty when it has none.</summary>
    public static string Folder(string line)
    {
        // LiteralPrefix answers in the file system's own separator, and for a line with no
        // wildcard it already answers with the directory.
        var prefix = Glob.LiteralPrefix(line.Trim()).Replace('\\', '/').TrimEnd('/');
        return prefix.Contains('/') ? prefix : string.Empty;
    }

    [GeneratedRegex("[0-9]+")]
    private static partial Regex DigitRun();
}

/// <summary>
/// A job name from the folder its files live in, offered once and never forced.
/// </summary>
public static class JobNameSuggestion
{
    private static readonly string[] Generic = ["logs", "log", "logfiles"];

    /// <summary>The folder's name, lower-cased, past any segment that only says "logs"; empty when nothing is left.</summary>
    public static string From(string line)
    {
        var segments = line.Trim().Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Where(s => !s.EndsWith(':') && s.IndexOfAny(['*', '?', '[']) < 0)
            .ToList();

        // The last segment is the file when the line named one exactly.
        if (segments.Count > 0 && segments[^1].Contains('.'))
        {
            segments.RemoveAt(segments.Count - 1);
        }

        for (var i = segments.Count - 1; i >= 0; i--)
        {
            if (!Generic.Contains(segments[i], StringComparer.OrdinalIgnoreCase))
            {
                var name = segments[i].Split('.')[0].ToLowerInvariant().Replace(' ', '-');
                return name.Length > 0 ? name : string.Empty;
            }
        }

        return string.Empty;
    }

    /// <summary>The name to show: the suggestion, unless the box already holds something the person typed.</summary>
    public static string Apply(string current, string previousSuggestion, string suggestion) =>
        current.Length == 0 || string.Equals(current, previousSuggestion, StringComparison.Ordinal)
            ? suggestion
            : current;
}
