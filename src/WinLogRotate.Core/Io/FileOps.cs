using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
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
    public static TruncationOutcome CopyTruncate(
        string source, string destination, bool truncate,
        int attempts = 1, int intervalMs = 0, Action<int, Exception>? onRetry = null)
    {
        var native = WinPath.ToExtendedLength(WinPath.Normalize(source));

        // Three units, one handle. Opening is worth its own because antivirus and the Search
        // Indexer produce sharing violations there; the copy and the cut are separate because
        // that is the whole point of this change.
        using var handle = RetryPolicy.Execute(
            () => Open(native, source, truncate), attempts, intervalMs, onRetry);

        var copied = RetryPolicy.Execute(
            () => CopyAside(handle, destination), attempts, intervalMs, onRetry);

        var resumeOffset = 0L;

        if (truncate)
        {
            // Read once, cut many. The bytes the writer appended while the copy was running are
            // captured before the retried unit, not inside it: a cut that applies SetLength and
            // then fails to put the tail back has changed the file its own next attempt would
            // measure, and would take the empty branch and drop them. Same mistake as the one
            // above, one level down.
            var tail = RetryPolicy.Execute(
                () => ReadTail(handle, copied), attempts, intervalMs, onRetry);

            resumeOffset = RetryPolicy.Execute(
                () => Cut(handle, tail), attempts, intervalMs, onRetry);
        }

        return new TruncationOutcome
        {
            SizeBefore = copied,
            SizeAfter = resumeOffset,
        };
    }

    private static SafeFileHandle Open(string native, string source, bool truncate)
    {
        var handle = NativeMethods.CreateFile(
            native,
            NativeMethods.GenericRead | (truncate ? NativeMethods.GenericWrite : 0),
            NativeMethods.ShareRead | NativeMethods.ShareWrite | NativeMethods.ShareDelete,
            0, NativeMethods.OpenExisting, NativeMethods.FlagSequentialScan, 0);

        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException(
                $"Could not open '{source}': {Win32Error.Describe(error)}.",
                unchecked((int)(0x80070000 | (uint)error)));
        }

        return handle;
    }

    /// <summary>
    /// Copies the live log aside and moves it into place, and reports how many bytes that was.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every read names its own offset rather than relying on a position the previous attempt
    /// left behind, so re-running this is re-running it rather than resuming it. That is what
    /// makes it safe as a retry unit, and it is the property the cut below needs too.
    /// </para>
    /// <para>
    /// No Directory.CreateDirectory here, deliberately. It used to make a missing olddir -
    /// recursively, and regardless of createolddir - which meant the same configuration failed
    /// for ever under rename and silently succeeded under copytruncate, and that createolddir
    /// governed neither. Whether the directory exists is the planner's question now; see
    /// OldDirGate.
    /// </para>
    /// </remarks>
    private static long CopyAside(SafeFileHandle handle, string destination)
    {
        var length = RandomAccess.GetLength(handle);
        var staging = destination + ".part";

        using (var output = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[81920];
            var offset = 0L;

            while (offset < length)
            {
                var wanted = (int)Math.Min(buffer.Length, length - offset);
                var read = RandomAccess.Read(handle, buffer.AsSpan(0, wanted), offset);
                if (read == 0)
                {
                    break;
                }

                output.Write(buffer, 0, read);
                offset += read;
            }

            output.Flush(flushToDisk: true);
        }

        File.Move(staging, destination, overwrite: true);
        return length;
    }

    /// <summary>
    /// The bytes the writer appended while the copy was running, or none.
    /// </summary>
    /// <remarks>
    /// Read rather than assumed short: a single read may return fewer bytes than asked for, and
    /// what it would drop is precisely the tail this exists to save.
    /// </remarks>
    private static byte[] ReadTail(SafeFileHandle handle, long copied)
    {
        var written = RandomAccess.GetLength(handle);

        if (written <= copied)
        {
            return [];
        }

        var tail = new byte[written - copied];
        var got = 0;

        while (got < tail.Length)
        {
            var read = RandomAccess.Read(handle, tail.AsSpan(got), copied + got);
            if (read == 0)
            {
                // The writer truncated underneath us. What is there is what there is.
                return tail[..got];
            }

            got += read;
        }

        return tail;
    }

    /// <summary>
    /// Empties the live log, puts the captured tail back, and reports where it now ends.
    /// </summary>
    /// <remarks>
    /// Idempotent, which is what makes it safe to retry: it is handed the tail rather than
    /// deriving it, so running it twice sets the same length and writes the same bytes. Cutting
    /// to zero and restoring the tail - rather than cutting at the copied offset - is what keeps
    /// bytes that arrived during the copy out of the next archive and in the live log.
    /// </remarks>
    private static long Cut(SafeFileHandle handle, byte[] tail)
    {
        RandomAccess.SetLength(handle, 0);

        if (tail.Length > 0)
        {
            RandomAccess.Write(handle, tail, 0);
        }

        RandomAccess.FlushToDisk(handle);

        // Read back rather than computed. This is where a writer holding a cached offset will
        // leave a NUL gap beginning, and the detector samples at exactly this offset - so it has
        // to be what the file really is, not what the arithmetic above expected.
        return RandomAccess.GetLength(handle);
    }

    /// <summary>Makes a directory, and every level above it that is missing.</summary>
    /// <remarks>
    /// Idempotent, so it is safe under <c>RetryPolicy</c>. This exists so that making a job's
    /// olddir is a planned operation the executor carries out - journalled, guarded and visible in
    /// a dry run - rather than something a file primitive does on the way past.
    /// </remarks>
    public static void CreateDirectory(string path) =>
        Directory.CreateDirectory(WinPath.ToExtendedLength(WinPath.Normalize(path)));

    /// <summary>Recreates the log a rename moved away, so the writer finds it again.</summary>
    public static void Create(string path)
    {
        // Kept, unlike CopyTruncate's. This path is always the live log's own directory
        // (RotateJobPlanner sets Destination = live.Path), which existed a moment ago - so the
        // call only fires in the race where the tree vanished mid-run, and its effect there is
        // that the writer gets its file back instead of losing it.
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
