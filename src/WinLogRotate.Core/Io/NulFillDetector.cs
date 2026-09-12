using WinLogRotate.Core.State;

namespace WinLogRotate.Core.Io;

/// <summary>What the detector concluded about one truncation.</summary>
public sealed record NulFillObservation
{
    public required NulFillVerdict Verdict { get; init; }
    public required long SizeBeforeTruncation { get; init; }
    public required long SizeAfterWriterResumed { get; init; }
    public NulFillEvidence Evidence { get; init; }
    public string? Explanation { get; init; }
}

/// <summary>
/// Detects the copytruncate failure that silently fills disks on Windows.
/// </summary>
/// <remarks>
/// <para>
/// After we truncate a file to zero, a writer using a true append handle
/// (<c>FILE_APPEND_DATA</c>, .NET's <c>FileMode.Append</c>, MSVCRT's <c>"a"</c>) resumes at
/// offset zero and everything is fine. But a writer that caches its own file position - log4net,
/// and many C++ <c>std::ofstream</c> implementations - does not. Its next write lands at the
/// offset it remembers, and NTFS zero-fills everything in between.
/// </para>
/// <para>
/// The result is a file of, say, four gigabytes of NUL bytes followed by one line of log. It
/// looks rotated for a moment and then instantly reappears at its old size, every rotation,
/// forever. Nothing about it raises an error, and no existing Windows log rotator detects it.
/// </para>
/// <para>
/// The check is cheap: remember the size before truncating, and on the next run compare what
/// the file grew back to. A file that returns to roughly its former size in one interval,
/// while a real log would have grown gradually, has been zero-filled. Once confirmed, the
/// verdict is permanent for that path - see <see cref="NulFillVerdict.Confirmed"/>.
/// </para>
/// </remarks>
public static class NulFillDetector
{
    /// <summary>How close to the pre-truncation size counts as "it came straight back".</summary>
    public const double ReGrowthRatio = 0.9;

    /// <summary>Below this, proportional reasoning is noise rather than evidence.</summary>
    public const long MinimumInterestingSize = 1L << 20;

    /// <summary>
    /// Judges a file whose previous truncation size we recorded.
    /// </summary>
    /// <param name="sizeBefore">Size captured immediately before the last truncation.</param>
    /// <param name="sizeNow">Size observed at the start of this run.</param>
    /// <param name="nulRun">
    /// Consecutive NUL bytes found at the offset the truncation left the file, or null when the
    /// file could not be read.
    /// </param>
    /// <remarks>
    /// <paramref name="nulRun"/> is nullable, and the distinction is load-bearing: zero means "we
    /// looked and found log text", which is evidence the writer behaves, while null means "we
    /// could not look", which is no evidence at all. Collapsing them into 0 would let an
    /// unreadable file be recorded as clean.
    /// </remarks>
    public static NulFillObservation Judge(long sizeBefore, long sizeNow, int? nulRun = null)
    {
        // Asked before the size floor, not after. The floor exists because "proportional
        // reasoning is noise" below a megabyte - and a NUL run is not proportional reasoning. A
        // 512 KB log with four thousand NUL bytes at the resume offset is the failure, whatever
        // its size, and returning Unknown for it was the floor overreaching.
        if (nulRun >= 512)
        {
            return new NulFillObservation
            {
                Verdict = NulFillVerdict.Confirmed,
                SizeBeforeTruncation = sizeBefore,
                SizeAfterWriterResumed = sizeNow,
                Evidence = NulFillEvidence.NulSignature,
                Explanation =
                    $"The file carries {nulRun} NUL bytes at the offset the truncation left it. The writer is " +
                    "holding a cached file offset and resumed at it, so NTFS zero-filled the gap.",
            };
        }

        if (sizeBefore < MinimumInterestingSize)
        {
            return new NulFillObservation
            {
                Verdict = NulFillVerdict.Unknown,
                SizeBeforeTruncation = sizeBefore,
                SizeAfterWriterResumed = sizeNow,
                Explanation = "Too small for the size comparison to mean anything.",
            };
        }

        // Computed once as an integer rather than compared as doubles: at multi-gigabyte
        // sizes, `sizeNow >= sizeBefore * ratio` lands either side of the boundary depending
        // on rounding, and this verdict permanently disables copytruncate for a path. A
        // decision that consequential should not depend on floating-point luck.
        var threshold = (long)(sizeBefore * ReGrowthRatio);

        if (sizeNow >= threshold)
        {
            return new NulFillObservation
            {
                Verdict = NulFillVerdict.Confirmed,
                SizeBeforeTruncation = sizeBefore,
                SizeAfterWriterResumed = sizeNow,
                Evidence = NulFillEvidence.ReGrowth,
                Explanation =
                    $"The file returned to {sizeNow:N0} bytes having been truncated from {sizeBefore:N0}. " +
                    "A log that genuinely regrew would have done so gradually; this is a writer resuming at a cached offset.",
            };
        }

        return new NulFillObservation
        {
            Verdict = NulFillVerdict.Clean,
            SizeBeforeTruncation = sizeBefore,
            SizeAfterWriterResumed = sizeNow,
            Evidence = nulRun is not null ? NulFillEvidence.NulSignature : NulFillEvidence.None,
            Explanation = "The file resumed from empty, so the writer honours truncation.",
        };
    }

    /// <summary>Counts leading NUL bytes, up to <paramref name="limit"/>.</summary>
    public static int CountLeadingNuls(Stream stream, int limit = 4096)
    {
        Span<byte> buffer = stackalloc byte[Math.Min(limit, 4096)];
        var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);

        var count = 0;
        while (count < read && buffer[count] == 0)
        {
            count++;
        }

        return count;
    }
}
