namespace WinLogRotate.Core.Configuration;

/// <summary>
/// The job files a run will read, in the order it will read them.
/// </summary>
/// <remarks>
/// <para>
/// One enumeration, shared by the loader that opens these files, the guard that decides whether
/// what they contain may execute anything, and the repair that fixes their permissions. Two
/// spellings of "which files are job files" is the exact shape of the defect this closes: the
/// guard inspected one <c>DirectoryInfo</c>, the loader opened the files, and the file - the
/// thing actually run as SYSTEM - was judged by nobody.
/// </para>
/// <para>
/// Ordinal order so that two machines given the same files load them in the same sequence, and
/// so that the first refusal a guard reports is the same one twice running.
/// </para>
/// <para>
/// A quarantined <c>.toml.bad</c> is deliberately not one of these. It is not loaded, so nothing
/// is executed from it; if an administrator renames one back, it becomes a job file again and is
/// judged and repaired as one.
/// </para>
/// </remarks>
public static class JobFiles
{
    /// <summary>The only shape a job file has.</summary>
    public const string Pattern = "*.toml";

    /// <summary>
    /// The file a job of this name is written to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Hoisted out of <c>LogrotateImporter</c>, which lowercased and replaced spaces and nothing
    /// else. That was survivable for names taken from a logrotate file; it is not for a name
    /// somebody types. A job called <c>IIS: W3SVC1</c> produced <c>iis:-w3svc1.toml</c> - a legal
    /// filename on Linux, and an alternate-data-stream spelling on Windows.
    /// </para>
    /// <para>
    /// Every character Windows forbids becomes a dash, runs collapse, and the result is trimmed
    /// of leading and trailing dashes and dots - a name is not allowed to produce a dotfile or a
    /// file whose stem is empty.
    /// </para>
    /// </remarks>
    public static string NameFor(string jobName)
    {
        var stem = new System.Text.StringBuilder(jobName.Length);

        foreach (var c in jobName.ToLowerInvariant())
        {
            if (Usable(c))
            {
                stem.Append(c);
            }
            else if (stem.Length > 0 && stem[^1] != '-')
            {
                stem.Append('-');
            }
        }

        var name = stem.ToString().Trim('-', '.');

        // Capped so a pasted sentence cannot produce a path Windows refuses to create, and
        // trimmed again in case the cut landed on a dash.
        if (name.Length > 120)
        {
            name = name[..120].TrimEnd('-', '.');
        }

        return (name.Length == 0 ? "job" : name) + ".toml";
    }

    /// <summary>
    /// Whether a character may appear in a file name on the platform this product runs on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Spelled out rather than asked of <see cref="Path.GetInvalidFileNameChars"/>, which answers
    /// for the <b>running</b> platform. On Linux that set is <c>{ '\0', '/' }</c> - so every test
    /// leg that matters here would have watched <c>*</c>, <c>?</c>, <c>"</c> and <c>\</c> pass
    /// straight through, and the one platform this product is for is the one no test would have
    /// asked about. A rule that only one CI leg executes is a rule that is not being checked.
    /// </para>
    /// <para>
    /// The set is Windows' own: the ASCII control characters, the five path and stream separators,
    /// and the three wildcard and redirection characters. A colon is in it, which is the case that
    /// prompted this: <c>IIS: W3SVC1</c> used to yield <c>iis:-w3svc1.toml</c>, which names an
    /// alternate data stream on a file called <c>iis</c> rather than a file.
    /// </para>
    /// </remarks>
    private static bool Usable(char c) =>
        !char.IsWhiteSpace(c)
        && !char.IsControl(c)
        && "\\/:*?\"<>|".IndexOf(c, StringComparison.Ordinal) < 0;

    /// <summary>Every job file in <paramref name="directory"/>, or none if it is not there.</summary>
    public static IEnumerable<string> In(string directory) =>
        !Directory.Exists(directory)
            ? []
            : Directory.EnumerateFiles(directory, Pattern).OrderBy(f => f, StringComparer.Ordinal);
}
