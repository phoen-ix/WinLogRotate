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

    public double Ratio => BytesBefore == 0 ? 0 : 1 - (BytesAfter / (double)BytesBefore);
}

/// <summary>
/// Compresses a rotated file, atomically.
/// </summary>
/// <remarks>
/// The ordering is the whole point: write to a temporary sibling, flush it to disk, move it
/// over the destination, and only then delete the source. A crash at any point therefore
/// leaves either the original file or a complete archive - never a truncated <c>.gz</c> that
/// looks like a backup and contains half a log.
/// </remarks>
public static class Compressor
{
    /// <summary>The extension a given type appends.</summary>
    public static string Extension(CompressType type) => type switch
    {
        CompressType.Gzip => ".gz",
        CompressType.Zip => ".zip",
        _ => string.Empty,
    };

    public static CompressionResult Compress(
        string source, CompressType type, CompressionLevel level = CompressionLevel.Optimal,
        int retryCount = 5, int retryIntervalMs = 100)
    {
        if (type == CompressType.None)
        {
            throw new ArgumentException("Nothing to do for CompressType.None.", nameof(type));
        }

        var destination = source + Extension(type);
        var temp = destination + ".tmp";
        var info = new FileInfo(source);
        var before = info.Length;
        var modified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);

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
                entry.LastWriteTime = modified;
                using var entryStream = entry.Open();
                input.CopyTo(entryStream);
            }

            output.Flush();
            output.Flush(flushToDisk: true);
        }, retryCount, retryIntervalMs);

        // The archive carries the log's own timestamp, not the moment we compressed it.
        // Age-based retention reads mtime, so stamping "now" here would make every archive
        // look brand new and quietly defeat maxage.
        File.SetLastWriteTimeUtc(temp, modified.UtcDateTime);

        File.Move(temp, destination, overwrite: true);

        var after = new FileInfo(destination).Length;

        // Only now is it safe to lose the original.
        RetryPolicy.Execute(() => File.Delete(source), retryCount, retryIntervalMs);

        return new CompressionResult
        {
            Source = source,
            Destination = destination,
            BytesBefore = before,
            BytesAfter = after,
        };
    }

    /// <summary>
    /// Maps a logrotate-style compression option onto a .NET level.
    /// </summary>
    /// <remarks>
    /// .NET has no 1-9 scale, so the mapping is deliberately coarse and anything unrecognised
    /// falls back to Optimal rather than being silently ignored.
    /// </remarks>
    public static CompressionLevel MapLevel(string? option) => option?.Trim() switch
    {
        "-1" or "--fast" or "fast" => CompressionLevel.Fastest,
        "-9" or "--best" or "best" => CompressionLevel.SmallestSize,
        "0" or "none" => CompressionLevel.NoCompression,
        _ => CompressionLevel.Optimal,
    };
}
