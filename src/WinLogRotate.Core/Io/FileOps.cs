using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using WinLogRotate.Core.Safety;

namespace WinLogRotate.Core.Io;

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
    /// zero-fill the gap - see <see cref="NulFillDetector"/>. The size before truncation is
    /// returned so the caller can record it and judge that on the following run.
    /// </para>
    /// </remarks>
    public static long CopyTruncate(string source, string destination, bool truncate)
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

        using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
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

        if (truncate)
        {
            // Cut at the offset we copied to, not at zero: bytes the writer appended while the
            // copy was running are preserved rather than silently dropped.
            var written = stream.Length;
            if (written > length)
            {
                var tail = new byte[written - length];
                stream.Position = length;
                var read = stream.Read(tail, 0, tail.Length);
                stream.SetLength(0);
                stream.Position = 0;
                stream.Write(tail, 0, read);
            }
            else
            {
                stream.SetLength(0);
            }

            stream.Flush();
        }

        return length;
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
