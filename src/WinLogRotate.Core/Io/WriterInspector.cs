using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using WinLogRotate.Core.Safety;
using WinLogRotate.Core.State;

namespace WinLogRotate.Core.Io;

/// <summary>One reading of a live log, taken through a single handle.</summary>
/// <remarks>
/// Size and NUL run come from the same open, deliberately. They are two facts about a file another
/// process is actively writing, and taking them separately would compare a size from one moment
/// against bytes from another.
/// </remarks>
public sealed record WriterSample
{
    public required bool Opened { get; init; }

    /// <summary>The Win32 error, when <see cref="Opened"/> is false.</summary>
    public int Error { get; init; }

    /// <summary>
    /// The size as the handle reports it.
    /// </summary>
    /// <remarks>
    /// Not <c>MatchedFile.Length</c>, which comes from <c>FileInfo</c> and therefore from the
    /// directory entry - and Windows does not update that entry while another process holds the
    /// file open. It is stale for exactly the writer-held files this exists to inspect, and stale
    /// low, which biases the detector toward reporting a NUL-filling path as clean.
    /// </remarks>
    public long Size { get; init; }

    /// <summary>Volume serial and file index, or null where the filesystem cannot say.</summary>
    public string? Identity { get; init; }

    /// <summary>
    /// How many bytes were actually read. Zero means nothing could be sampled, which is not the
    /// same as having sampled and found no NULs.
    /// </summary>
    public int BytesRead { get; init; }

    /// <summary>Consecutive NUL bytes from the offset that was asked for.</summary>
    public int NulRun { get; init; }

    /// <summary>What the detector should be told, distinguishing "no NULs" from "did not look".</summary>
    public int? NulRunOrNull => BytesRead > 0 ? NulRun : null;

    public static WriterSample Unreadable(int error) => new() { Opened = false, Error = error };
}

/// <summary>
/// The two questions about a live log that only the operating system can answer.
/// </summary>
/// <remarks>
/// A seam for the reason <c>IArchiveSource</c> is one: the code deciding which strategy a rotation
/// uses - and whether it happens at all - deserves tests that need no Win32, and
/// <c>RotateJobPlanner</c> and <c>RotationRunner</c> stay platform-neutral so most of the suite
/// keeps running on the Linux leg.
/// </remarks>
public interface IWriterInspector
{
    /// <summary>What the writer permits.</summary>
    ProbeResult Classify(string path);

    /// <summary>Reads <paramref name="limit"/> bytes from <paramref name="fromOffset"/>.</summary>
    WriterSample Sample(string path, long fromOffset, int limit = 4096);
}

/// <summary>The real operating system, where there is one.</summary>
public sealed class WriterInspector : IWriterInspector
{
    public ProbeResult Classify(string path) =>
        OperatingSystem.IsWindows()
            ? LockProbe.Classify(path)
            : new ProbeResult
            {
                Path = path,
                Verdict = ProbeVerdict.Unknown,
                Explanation = "this run could not ask the operating system what the writer permits",
            };

    public WriterSample Sample(string path, long fromOffset, int limit = 4096) =>
        OperatingSystem.IsWindows()
            ? OnWindows(path, fromOffset, limit)

            // Not an error, and not a lie either: nothing was read, so NulRunOrNull is null and
            // the detector treats it as no evidence rather than as evidence of health.
            : WriterSample.Unreadable(0);

    [SupportedOSPlatform("windows")]
    private static WriterSample OnWindows(string path, long fromOffset, int limit)
    {
        var native = WinPath.ToExtendedLength(WinPath.Normalize(path));

        // Read-only, sharing everything. LockProbe states the rule this follows: the inspection
        // must never lock out the very writer whose behaviour it is measuring.
        using var handle = NativeMethods.CreateFile(
            native, NativeMethods.GenericRead,
            NativeMethods.ShareRead | NativeMethods.ShareWrite | NativeMethods.ShareDelete,
            0, NativeMethods.OpenExisting, NativeMethods.FlagOpenReparsePoint, 0);

        if (handle.IsInvalid)
        {
            return WriterSample.Unreadable(Marshal.GetLastWin32Error());
        }

        string? identity = null;
        if (NativeMethods.GetFileInformationByHandle(handle, out var info))
        {
            var index = ((long)info.FileIndexHigh << 32) | info.FileIndexLow;
            identity = $"{info.VolumeSerialNumber:X8}-{index:X16}";
        }

        using var stream = new FileStream(handle, FileAccess.Read);
        var size = stream.Length;

        if (fromOffset >= size)
        {
            // The writer has not passed the point the truncation left it, so there is nothing to
            // judge yet. Reported as read-nothing rather than as zero NULs.
            return new WriterSample { Opened = true, Size = size, Identity = identity };
        }

        stream.Position = fromOffset;
        Span<byte> buffer = stackalloc byte[Math.Min(limit, 4096)];
        var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);

        var run = 0;
        while (run < read && buffer[run] == 0)
        {
            run++;
        }

        return new WriterSample
        {
            Opened = true,
            Size = size,
            Identity = identity,
            BytesRead = read,
            NulRun = run,
        };
    }
}
