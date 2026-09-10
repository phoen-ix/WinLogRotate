using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using WinLogRotate.Core.Safety;

namespace WinLogRotate.Core.Io;

/// <summary>What one truncation did, in the two numbers the NUL-fill detector needs.</summary>
/// <remarks>
/// A record rather than the bare pre-truncation size it used to return. <see cref="SizeAfter"/>
/// is the offset the file was left at, and the detector must sample <b>there</b>: tail
/// preservation means a truncated file usually begins with log text, so reading from byte zero
/// finds none of the NUL run that is the failure's actual signature.
/// </remarks>
public sealed record TruncationOutcome
{
    /// <summary>What the file measured immediately before it was cut.</summary>
    public required long SizeBefore { get; init; }

    /// <summary>Where the cut left it, and therefore where a NUL gap would begin.</summary>
    public required long SizeAfter { get; init; }
}

/// <summary>
/// The destructive file primitives, each with the Win32 semantics spelled out.
/// </summary>
[SupportedOSPlatform("windows")]
public static class FileOps
{
    /// <summary>
    /// Renames a file, refusing to cross volumes.
    /// </summary>
    /// <remarks>
    /// <c>MOVEFILE_COPY_ALLOWED</c> is deliberately not set. With it, a cross-volume rename
    /// silently becomes a copy followed by a delete: not atomic, slow for a multi-gigabyte log,
    /// and a window in which a crash loses data. Failing loudly is the correct behaviour, and
    /// the resulting error is what tells an operator their <c>olddir</c> is on the wrong volume.
    /// </remarks>
    public static void Rename(string source, string destination)
    {
        var from = WinPath.ToExtendedLength(WinPath.Normalize(source));
        var to = WinPath.ToExtendedLength(WinPath.Normalize(destination));

        if (!NativeMethods.MoveFileEx(from, to,
                NativeMethods.MoveFileReplaceExisting | NativeMethods.MoveFileWriteThrough))
        {
            var error = Marshal.GetLastWin32Error();
            throw new IOException(
                $"Could not rename '{source}' to '{destination}': {Win32Error.Describe(error)}.",
                unchecked((int)(0x80070000 | (uint)error)));
        }
    }

    /// <summary>
    /// Copies a log aside and then truncates the original in place.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The inode never changes, so a writer holding the file keeps working - which is the
    /// entire point. Two consequences worth stating.
    /// </para>
    /// <para>
    /// There is a small data-loss window between the copy finishing and the truncation
    /// happening; anything written in between is discarded. The window is narrowed by copying
    /// exactly the number of bytes observed at the start and truncating to that same offset
    /// rather than to zero, so writes that arrive during the copy survive.
    /// </para>
    /// <para>
    /// And a writer that caches its own file offset will resume at it, leaving NTFS to
    /// zero-fill the gap - see <see cref="NulFillDetector"/>. The sizes on either side of the
    /// truncation are returned so the caller can record them and judge that afterwards.
    /// </para>
    /// <para>
    /// <b>The archive is written to a temporary name and moved into place before anything is
    /// truncated.</b> This is not tidiness. The whole call is wrapped in
    /// <see cref="RetryPolicy"/>, and <c>SetLength</c> can throw ERROR_LOCK_VIOLATION - which
    /// that policy treats as transient - after the truncation has already applied. Writing
    /// straight to the destination meant the retry reopened a now-empty source and wrote a
    /// zero-byte archive over the one it had just successfully saved. The retry destroyed the
    /// data the rotation existed to preserve.
    /// </para>
    /// </remarks>
    public static TruncationOutcome CopyTruncate(string source, string destination, bool truncate)
    {
        var native = WinPath.ToExtendedLength(WinPath.Normalize(source));

        using var handle = NativeMethods.CreateFile(
            native,
            NativeMethods.GenericRead | (truncate ? NativeMethods.GenericWrite : 0),
            NativeMethods.ShareRead | NativeMethods.ShareWrite | NativeMethods.ShareDelete,
            0, NativeMethods.OpenExisting, NativeMethods.FlagSequentialScan, 0);

        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            throw new IOException(
                $"Could not open '{source}': {Win32Error.Describe(error)}.",
                unchecked((int)(0x80070000 | (uint)error)));
        }

        using var stream = new FileStream(handle, truncate ? FileAccess.ReadWrite : FileAccess.Read);

        // Snapshot the length first and copy exactly that much. Anything the writer appends
        // during the copy is then still present after the truncation below, because we cut at
        // the offset we read to rather than at zero.
        var length = stream.Length;

        var directory = WinPath.DirectoryName(destination);
        if (directory.Length > 0)
        {
            Directory.CreateDirectory(directory);
        }

        // Written aside, then moved. See the remarks: a retry that re-ran this after a partial
        // success used to overwrite a good archive with an empty one.
        var staging = destination + ".part";

        using (var output = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[81920];
            var remaining = length;
            while (remaining > 0)
            {
                var wanted = (int)Math.Min(buffer.Length, remaining);
                var read = stream.Read(buffer, 0, wanted);
                if (read == 0)
                {
                    break;
                }

                output.Write(buffer, 0, read);
                remaining -= read;
            }

            output.Flush(flushToDisk: true);
        }

        File.Move(staging, destination, overwrite: true);

        var resumeOffset = 0L;

        if (truncate)
        {
            // Cut at the offset we copied to, not at zero: bytes the writer appended while the
            // copy was running are preserved rather than silently dropped.
            var written = stream.Length;
            if (written > length)
            {
                var tail = new byte[written - length];
                stream.Position = length;

                // ReadExactly, not Read. A single Read may return short, and what it would drop
                // is precisely the tail this branch exists to save.
                stream.ReadExactly(tail);
                stream.SetLength(0);
                stream.Position = 0;
                stream.Write(tail);
            }
            else
            {
                stream.SetLength(0);
            }

            stream.Flush();

            // Read back rather than computed. This is where a writer holding a cached offset
            // will leave a NUL gap beginning, and the detector samples at exactly this offset -
            // so it has to be what the file really is, not what the arithmetic above expected.
            resumeOffset = stream.Length;
        }

        return new TruncationOutcome
        {
            SizeBefore = length,
            SizeAfter = resumeOffset,
        };
    }

    /// <summary>Recreates the log a rename moved away, so the writer finds it again.</summary>
    public static void Create(string path)
    {
        var directory = WinPath.DirectoryName(path);
        if (directory.Length > 0)
        {
            Directory.CreateDirectory(directory);
        }

        // CreateNew rather than Create: if something already recreated it - the writer itself,
        // very often - we must not truncate what it has already written.
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
        }
        catch (IOException) when (File.Exists(path))
        {
            // The writer got there first. That is the good outcome, not an error.
        }
    }

    /// <summary>Deletes a file, tolerating one that has already gone.</summary>
    public static void Delete(string path)
    {
        try
        {
            File.Delete(WinPath.ToExtendedLength(WinPath.Normalize(path)));
        }
        catch (FileNotFoundException)
        {
            // Already gone: the desired end state either way.
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
