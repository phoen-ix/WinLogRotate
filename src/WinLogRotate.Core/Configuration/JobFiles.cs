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

    /// <summary>Every job file in <paramref name="directory"/>, or none if it is not there.</summary>
    public static IEnumerable<string> In(string directory) =>
        !Directory.Exists(directory)
            ? []
            : Directory.EnumerateFiles(directory, Pattern).OrderBy(f => f, StringComparer.Ordinal);
}
