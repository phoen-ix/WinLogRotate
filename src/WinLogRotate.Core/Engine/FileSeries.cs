using System.Globalization;
using WinLogRotate.Core.Compression;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Globbing;
using WinLogRotate.Core.Safety;

namespace WinLogRotate.Core.Engine;

/// <summary>One archive, with whatever ordering information could be recovered from its name.</summary>
public sealed record ArchiveFile
{
    public required MatchedFile File { get; init; }

    /// <summary>Rotation index parsed from a name like <c>app.log.3</c>, if there is one.</summary>
    public int? Index { get; init; }

    /// <summary>Date parsed from a dateext-style name, if there is one.</summary>
    public DateTimeOffset? Stamp { get; init; }

    public bool IsCompressed { get; init; }

    public string Path => File.Path;
    public long Length => File.Length;
    public DateTimeOffset LastWriteUtc => File.LastWriteUtc;
}

/// <summary>
/// Orders a set of matched files from newest to oldest.
/// </summary>
/// <remarks>
/// <para>
/// Ordering is what decides which files die, so it is done on the best evidence available
/// rather than on filename alone. A date embedded in the name is parsed and compared as a
/// date - not as a string - because a format such as <c>-dd-MM-yyyy</c> sorts lexically in
/// completely the wrong order, and a retention pass that trusts the string would delete the
/// newest archives.
/// </para>
/// <para>
/// Where no date or index can be recovered, modification time is the fallback, with the
/// ordinal path as a final tie-break so the result is deterministic on every filesystem.
/// </para>
/// </remarks>
public static class FileSeries
{
    /// <summary>Classifies matched files and returns them newest first.</summary>
    public static IReadOnlyList<ArchiveFile> Order(
        IEnumerable<MatchedFile> files, string? dateFormat = null)
    {
        var archives = files.Select(f => Classify(f, dateFormat)).ToList();

        archives.Sort((a, b) =>
        {
            // A parsed date beats everything else: it is what the producer actually meant.
            if (a.Stamp is { } sa && b.Stamp is { } sb && sa != sb)
            {
                return sb.CompareTo(sa);
            }

            // Then the rotation index, ascending - .1 is newer than .2.
            if (a.Index is { } ia && b.Index is { } ib && ia != ib)
            {
                return ia.CompareTo(ib);
            }

            if (a.LastWriteUtc != b.LastWriteUtc)
            {
                return b.LastWriteUtc.CompareTo(a.LastWriteUtc);
            }

            return string.CompareOrdinal(a.Path, b.Path);
        });

        return archives;
    }

    internal static ArchiveFile Classify(MatchedFile file, string? dateFormat)
    {
        var name = WinPath.FileName(file.Path);

        var compressed =
            name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

        // Strip only the compression suffix, and by length - GetFileNameWithoutExtension
        // would also eat the ".log" of "app.log", losing the very text the index and date
        // parsers need to look at.
        var stem = compressed ? name[..name.LastIndexOf('.')] : name;

        return new ArchiveFile
        {
            File = file,
            IsCompressed = compressed,
            Index = ParseIndex(stem),
            Stamp = ParseStamp(stem, dateFormat),
        };
    }

    /// <summary>Reads the trailing number from <c>app.log.3</c>.</summary>
    private static int? ParseIndex(string stem)
    {
        var dot = stem.LastIndexOf('.');
        if (dot < 0 || dot == stem.Length - 1)
        {
            return null;
        }

        var tail = stem[(dot + 1)..];
        return int.TryParse(tail, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
            ? index
            : null;
    }

    /// <summary>
    /// Finds a date in the name, trying the configured format first and then the shapes real
    /// Windows producers actually emit - IIS's <c>u_exYYMMDD</c> above all.
    /// </summary>
    private static DateTimeOffset? ParseStamp(string stem, string? dateFormat)
    {
        ReadOnlySpan<string> formats = dateFormat is null
            ? ["yyyyMMddHH", "yyyyMMdd", "yyyy-MM-dd", "yyMMdd", "yyyyMM"]
            : [dateFormat.TrimStart('-', '_', '.'), "yyyyMMddHH", "yyyyMMdd", "yyyy-MM-dd", "yyMMdd", "yyyyMM"];

        // Walk right to left: the date is nearly always the tail of the name, and scanning
        // from the end avoids matching a number that happens to live in the application's name.
        foreach (var format in formats)
        {
            var length = CountDigitsIn(format);
            if (length == 0 || stem.Length < length)
            {
                continue;
            }

            for (var start = stem.Length - length; start >= 0; start--)
            {
                var slice = stem.Substring(start, length);
                if (!slice.All(c => char.IsAsciiDigit(c) || c is '-' or '_'))
                {
                    continue;
                }

                if (DateTimeOffset.TryParseExact(slice, format, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                {
                    return parsed;
                }
            }
        }

        return null;
    }

    private static int CountDigitsIn(string format) => format.Length;

    /// <summary>True when a file already carries a compression extension.</summary>
    public static bool IsAlreadyCompressed(string path, CompressType type) =>
        type != CompressType.None &&
        path.EndsWith(Compressor.Extension(type), StringComparison.OrdinalIgnoreCase);
}
