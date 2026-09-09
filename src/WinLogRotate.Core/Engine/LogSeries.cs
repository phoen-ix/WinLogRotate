using WinLogRotate.Core.Compression;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Globbing;
using WinLogRotate.Core.Safety;

namespace WinLogRotate.Core.Engine;

/// <summary>Where a series looks for the files it already wrote.</summary>
/// <remarks>
/// A seam rather than direct file access, so discovery - the part that decides what may be
/// deleted - is exercised on the Linux leg against a dictionary.
/// </remarks>
public interface IArchiveSource
{
    /// <summary>One exact path, or null if it is not there.</summary>
    MatchedFile? Find(string path);

    /// <summary>Everything matching a pattern.</summary>
    IReadOnlyList<MatchedFile> Glob(string pattern);
}

/// <summary>
/// Pairs each live log with the archives previously rotated from it.
/// </summary>
/// <remarks>
/// <para>
/// A job's glob matches live logs and not their archives - <c>C:/logs/*.log</c> finds
/// <c>app.log</c> and never <c>app.log.1</c> - so the archives have to be found separately. That
/// makes this the code that decides which files the planner is allowed to consider, and therefore
/// which files can be deleted.
/// </para>
/// <para>
/// <b>So it generates names rather than parsing them.</b> The tempting implementation - list
/// everything beginning with <c>app.log</c> and let <see cref="FileSeries"/> sort out what it is -
/// is dangerous, because of what happens downstream:
/// <see cref="RotateJobPlanner"/> only <i>shifts</i> archives whose index it recognises, but its
/// <c>maxage</c> pass iterates every archive it was handed and deletes on
/// <c>Stamp ?? LastWriteUtc</c>. A file that discovery cannot identify is therefore not ignored,
/// it is deleted on age: <c>app.log.old</c>, <c>app.log.bak</c>, the copy an administrator took
/// before an upgrade. Each one older than <c>maxage</c>, each one beside the live log, each one
/// removed by a job that was only ever asked to rotate.
/// </para>
/// <para>
/// The same rule the rest of the product follows, in <see cref="Safety.PathGuard"/>'s words: a
/// false refusal costs one config edit and a clear message, a false permit deletes something
/// nobody agreed to lose.
/// </para>
/// </remarks>
public static class LogSeries
{
    /// <summary>
    /// How far past the retention window a numbered chain is followed.
    /// </summary>
    /// <remarks>
    /// Stragglers exist: an operator who lowers <c>rotate</c> from 14 to 7 leaves
    /// <c>app.log.8</c> through <c>app.log.14</c> behind, and nothing else will ever tidy them.
    /// Probing continues while files are found rather than stopping at the window, and this caps
    /// it so a directory of ten thousand numbered files cannot turn one job into ten thousand
    /// existence checks.
    /// </remarks>
    public const int MaxNumberedProbe = 1000;

    public static IReadOnlyList<LogGeneration> Discover(
        EffectiveJob job, IReadOnlyList<MatchedFile> live, IArchiveSource source)
    {
        var generations = new List<LogGeneration>(live.Count);

        foreach (var log in live)
        {
            var archives = job.DateExt ? DatedArchives(job, log, source) : NumberedArchives(job, log, source);

            generations.Add(new LogGeneration
            {
                Live = log,
                Archives = FileSeries.Order(archives, job.DateExt ? job.DateFormat : null),
            });
        }

        return generations;
    }

    /// <summary>
    /// The names this job would itself have written, probed one at a time.
    /// </summary>
    /// <remarks>
    /// Both spellings of each index, because <c>delaycompress</c> leaves exactly one generation
    /// uncompressed among compressed ones and retention has to see all of them.
    /// </remarks>
    private static List<MatchedFile> NumberedArchives(
        EffectiveJob job, MatchedFile log, IArchiveSource source)
    {
        var found = new List<MatchedFile>();

        // The whole retention window is probed even where it is sparse, because a gap in the
        // chain is ordinary - somebody deleted one - and stopping at the first miss would hide
        // every generation above it from both the shift and retention.
        var window = job.Rotate < 0 ? 0 : job.Rotate + job.Start - 1;

        for (var index = job.Start; index < job.Start + MaxNumberedProbe; index++)
        {
            var uncompressed = source.Find(ArchiveNaming.Numbered(job, log.Path, index, compressed: false));
            var compressed = job.CompressType == CompressType.None
                ? null
                : source.Find(ArchiveNaming.Numbered(job, log.Path, index, compressed: true));

            if (uncompressed is not null)
            {
                found.Add(uncompressed);
            }

            if (compressed is not null)
            {
                found.Add(compressed);
            }

            // Past the retention window, the chain is followed only while it continues. Inside
            // it, every index is checked.
            if (uncompressed is null && compressed is null && index >= window)
            {
                break;
            }
        }

        return found;
    }

    /// <summary>
    /// Archives whose name matches the date pattern <b>and</b> whose date actually parses.
    /// </summary>
    /// <remarks>
    /// The glob alone is not enough. <see cref="ArchiveNaming.DateExtGlob"/> ends in <c>*</c> so
    /// it catches both the compressed archives and the one <c>delaycompress</c> left behind - and
    /// that trailing wildcard would also catch <c>app.log-20260909.bak</c>, or anything else
    /// somebody happened to leave beside them. Requiring the stamp to parse is what turns a
    /// pattern match into an identification.
    /// </remarks>
    private static List<MatchedFile> DatedArchives(
        EffectiveJob job, MatchedFile log, IArchiveSource source)
    {
        var candidates = source.Glob(ArchiveNaming.DateExtGlob(job, log.Path));
        var found = new List<MatchedFile>(candidates.Count);

        foreach (var candidate in candidates)
        {
            // The live log is never one of its own archives, whatever the pattern matched.
            if (string.Equals(candidate.Path, log.Path, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (FileSeries.Order([candidate], job.DateFormat) is not [{ Stamp: { } stamp }])
            {
                continue;
            }

            // Reconstructed and compared, not merely parsed. FileSeries finds a date anywhere in
            // the name, so app.log-2026-09-08.bak parses perfectly well - and would then be
            // deleted on age like any other archive. The name has to be exactly the one this job
            // would have written, plus at most a compression extension.
            var expected = WinPath.FileName(log.Path)
                + stamp.ToString(job.DateFormat, System.Globalization.CultureInfo.InvariantCulture);

            if (IsExactly(WinPath.FileName(candidate.Path), expected))
            {
                found.Add(candidate);
            }
        }

        return found;
    }

    /// <summary>
    /// The archive's name, or that name plus a compression extension, and nothing else.
    /// </summary>
    /// <remarks>
    /// Every known extension rather than only the job's current one: an operator who switches
    /// <c>compresstype</c> from gzip to zip still owns the .gz archives written last month, and
    /// leaving them undiscovered would strand them on disk for ever.
    /// </remarks>
    private static bool IsExactly(string name, string expected)
    {
        if (string.Equals(name, expected, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var kind in Enum.GetValues<CompressType>())
        {
            if (kind != CompressType.None
                && string.Equals(name, expected + Compressor.Extension(kind), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>The real file system.</summary>
public sealed class FileArchiveSource : IArchiveSource
{
    public MatchedFile? Find(string path)
    {
        try
        {
            var info = new FileInfo(path);

            return info.Exists
                ? new MatchedFile
                {
                    Path = WinPath.Normalize(path),
                    Length = info.Length,
                    LastWriteUtc = info.LastWriteTimeUtc,
                }
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // An archive we cannot stat is an archive we will not touch. Reporting it here would
            // put a diagnostic in front of an operator for a file the job never asked about.
            return null;
        }
    }

    public IReadOnlyList<MatchedFile> Glob(string pattern) => FileEnumerator.Resolve(pattern);
}
