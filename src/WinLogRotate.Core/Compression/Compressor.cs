using System.IO.Compression;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Io;

namespace WinLogRotate.Core.Compression;

/// <summary>The outcome of compressing one file.</summary>
public sealed record CompressionResult
{
    public required string Source { get; init; }
    public required string Destination { get; init; }
    public required long BytesBefore { get; init; }
    public required long BytesAfter { get; init; }
}

/// <summary>
/// Compresses a rotated file, atomically.
/// </summary>
/// <remarks>
/// <para>
/// The ordering is the whole point: write to a temporary sibling, flush it to disk, move it
/// over the destination, and only then delete the source. A crash at any point therefore
/// leaves either the original file or a complete archive - never a truncated <c>.gz</c> that
/// looks like a backup and contains half a log.
/// </para>
/// <para>
/// That was true of the destination and not of the directory. A failure left the staging file
/// behind, and nothing in this product would ever have collected it - discovery probes the exact
/// names a job would have written, so a stray <c>.tmp</c> is invisible to retention for ever, in
/// the one tool whose job is to stop directories filling up. It is removed on the way out now.
/// </para>
/// </remarks>
public static class Compressor
{
    /// <summary>Removes a staging file, and does not care if it was never there.</summary>
    /// <remarks>
    /// Best effort on purpose: this runs on the way out of a failure, and an exception here would
    /// replace the real one with a worse one. Same shape as <c>AtomicJson.TryDelete</c>.
    /// </remarks>
    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>The extension a given type appends.</summary>
    public static string Extension(CompressType type) => type switch
    {
        CompressType.Gzip => ".gz",
        CompressType.Zip => ".zip",
        _ => string.Empty,
    };

    /// <summary>Every extension this product writes, whatever any one job's type is now.</summary>
    /// <remarks>
    /// One list for every place that has to find an archive again. A job that switched
    /// <c>compresstype</c> still owns what it wrote the other way, so discovery asks for all of
    /// them - and two lists of "all of them" is how one of the two misses a format.
    /// </remarks>
    public static IReadOnlyList<string> Extensions { get; } =
        [.. Enum.GetValues<CompressType>().Where(k => k != CompressType.None).Select(Extension)];

    public static CompressionResult Compress(
        string source, CompressType type, CompressionLevel level = CompressionLevel.Optimal,
        int retryCount = 5, int retryIntervalMs = 100, Action<int, Exception>? onRetry = null)
    {
        // Every type without an extension, not only None. The destination is the source plus the
        // extension, so a type with none - an undefined value the binder once let through - named
        // the log itself: the archive was moved over it and it was then deleted, and nothing threw.
        var extension = Extension(type);
        if (extension.Length == 0)
        {
            throw new ArgumentException($"{type} is not a compression type, so there is nothing to do.", nameof(type));
        }

        var destination = source + extension;
        var temp = destination + ".tmp";
        var info = new FileInfo(source);
        var before = info.Length;
        var modified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);

        // The try opens here, before the write, not after it. Scoped to the stamp and the move it
        // would clean up after the two rarest failures and leave the most ordinary one - a full
        // disk, or an unreadable source - orphaning a .tmp that nothing in this product ever
        // collects: discovery probes exact names, and LogSeriesTests pins that a .tmp is
        // deliberately not found. AtomicJson.Write is the shape being followed.
        //
        // The retry units stay separate inside it. Retrying is about transient contention;
        // cleaning up is about what is left behind when retrying has finished failing.
        try
        {
            RetryPolicy.Execute(() =>
            {
                using var input = new FileStream(
                    source, FileMode.Open, FileAccess.Read,
                    // Share generously: antivirus and indexers will be looking at this file too,
                    // and denying them turns a scan into a failed rotation.
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 81920, FileOptions.SequentialScan);

                using var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None);

                if (type == CompressType.Gzip)
                {
                    GzipWriter.Compress(input, output, source, modified, level);
                }
                else
                {
                    using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
                    var entry = archive.CreateEntry(Path.GetFileName(source), level);
                    entry.LastWriteTime = DosClamped(modified);
                    using var entryStream = entry.Open();
                    input.CopyTo(entryStream);
                }

                output.Flush();
                output.Flush(flushToDisk: true);
            }, retryCount, retryIntervalMs, onRetry);

            // The archive carries the log's own timestamp, not the moment we compressed it.
            // Age-based retention reads mtime, so stamping "now" here would make every archive
            // look brand new and quietly defeat maxage. Retried too: an antivirus handle on the
            // file we have just written is the same transient as any other, and this used to sit
            // outside every retry in the method.
            RetryPolicy.Execute(
                () => File.SetLastWriteTimeUtc(temp, modified.UtcDateTime), retryCount, retryIntervalMs, onRetry);

            RetryPolicy.Execute(
                () => File.Move(temp, destination, overwrite: true), retryCount, retryIntervalMs, onRetry);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }

        var after = new FileInfo(destination).Length;

        // Only now is it safe to lose the original.
        RetryPolicy.Execute(() => File.Delete(source), retryCount, retryIntervalMs, onRetry);

        return new CompressionResult
        {
            Source = source,
            Destination = destination,
            BytesBefore = before,
            BytesAfter = after,
        };
    }

    /// <summary>
    /// A timestamp the zip container can hold.
    /// </summary>
    /// <remarks>
    /// Zip stores MS-DOS dates, 1980-01-01 to 2107-12-31, and <see cref="ZipArchiveEntry.LastWriteTime"/>
    /// throws <see cref="ArgumentOutOfRangeException"/> for anything outside them. A log dated 1970,
    /// or 1601, exists: restored from a backup that kept no timestamps, or written before a machine's
    /// clock was set. That exception is not an <see cref="IOException"/>, so nothing caught it and the
    /// run ended at exit 4 over a container's limitation. The archive file's own modification time,
    /// set from the unclamped value, is what retention reads.
    /// </remarks>
    private static DateTimeOffset DosClamped(DateTimeOffset modified)
    {
        var earliest = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var latest = new DateTimeOffset(2107, 12, 31, 23, 59, 58, TimeSpan.Zero);

        return modified < earliest ? earliest : modified > latest ? latest : modified;
    }
}
