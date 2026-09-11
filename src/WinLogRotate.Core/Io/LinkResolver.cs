using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using WinLogRotate.Core.Safety;

namespace WinLogRotate.Core.Io;

/// <summary>The real path a name leads to, or why that could not be established.</summary>
public sealed record LinkTarget
{
    public required bool Resolved { get; init; }

    /// <summary>The final path, normalised and without an extended-length prefix.</summary>
    public string? FinalPath { get; init; }

    /// <summary>The Win32 error, when <see cref="Resolved"/> is false.</summary>
    public int Error { get; init; }

    public static LinkTarget Unresolvable(int error) => new() { Resolved = false, Error = error };

    public static LinkTarget At(string path) => new() { Resolved = true, FinalPath = path };
}

/// <summary>
/// Answers what a path really leads to, with every component's link followed.
/// </summary>
/// <remarks>
/// <para>
/// A seam for the reason <c>IArchiveSource</c> and <c>IWriterInspector</c> are seams: the rule that
/// decides whether a directory is walked deserves tests that need no Win32, and the enumerator and
/// the guard stay platform-neutral.
/// </para>
/// <para>
/// <b>Not <c>FileSystemInfo.ResolveLinkTarget</c>.</b> That answers only for the leaf - it returns
/// null for "not a link" - so for a junctioned <c>C:\App</c> containing an ordinary
/// <c>C:\App\logs</c> it reports nothing while the path resolves into somewhere else entirely.
/// That is precisely the case this exists to catch. It also answers "independently if the target
/// exists or not", which is a name we would have to open rather than the object we did.
/// </para>
/// </remarks>
public interface ILinkResolver
{
    LinkTarget Resolve(string path);
}

/// <summary>The operating system, where it has anything to say.</summary>
public sealed class LinkResolver : ILinkResolver
{
    public LinkTarget Resolve(string path) =>
        OperatingSystem.IsWindows()

            // Deliberately not WriterInspector's "no evidence" answer. There are no junctions here
            // to escape through, and this platform is a test host for the portable half of the
            // engine rather than somewhere anything rotates - so reporting every path as
            // unverifiable would refuse everything for no security gain at all.
            ? OnWindows(path)

            // Unchanged, not normalised. Normalising would rewrite a Unix path into Windows form
            // and hand the guard something it correctly refuses as not absolute - turning a seam
            // that is meant to be inert off Windows into one that breaks every caller.
            : LinkTarget.At(path);

    [SupportedOSPlatform("windows")]
    private static LinkTarget OnWindows(string path)
    {
        var native = WinPath.ToExtendedLength(WinPath.Normalize(path));

        // dwDesiredAccess is 0, and that is stronger than sharing everything. Windows compares the
        // requested access against existing handles' share modes, and the requested share mode
        // against their granted access; asking for no access at all makes the first comparison
        // vacuous, and sharing everything makes the second one. This open cannot collide with a
        // writer in either direction, and it succeeds on a directory whose DACL denies reading.
        //
        // FILE_FLAG_OPEN_REPARSE_POINT is deliberately ABSENT. Its three other uses in this
        // project all want the link itself; this wants what the link leads to, and setting it here
        // turns the whole check into a no-op that agrees with itself.
        using var handle = NativeMethods.CreateFile(
            native, 0,
            NativeMethods.ShareRead | NativeMethods.ShareWrite | NativeMethods.ShareDelete,
            0, NativeMethods.OpenExisting, NativeMethods.FlagBackupSemantics, 0);

        if (handle.IsInvalid)
        {
            return LinkTarget.Unresolvable(Marshal.GetLastWin32Error());
        }

        if (Name(handle, NativeMethods.VolumeNameDos) is { } dos)
        {
            return LinkTarget.At(dos);
        }

        // A volume with no drive letter - a mounted folder. It has no shorter spelling, and a
        // path under it is by construction not inside a lettered protected root.
        return Name(handle, NativeMethods.VolumeNameGuid) is { } guid
            ? LinkTarget.At(guid)
            : LinkTarget.Unresolvable(Marshal.GetLastWin32Error());
    }

    [SupportedOSPlatform("windows")]
    private static string? Name(Microsoft.Win32.SafeHandles.SafeFileHandle handle, uint volumeFlag)
    {
        Span<char> buffer = stackalloc char[512];
        var length = NativeMethods.GetFinalPathNameByHandle(
            handle, ref buffer[0], (uint)buffer.Length,
            NativeMethods.FileNameNormalized | volumeFlag);

        // Zero is failure. A length at or above the buffer means it did not fit - and in that one
        // case the count INCLUDES the terminating NUL, where on success it excludes it.
        if (length == 0)
        {
            return null;
        }

        if (length >= buffer.Length)
        {
            var bigger = new char[length];
            length = NativeMethods.GetFinalPathNameByHandle(
                handle, ref bigger[0], length, NativeMethods.FileNameNormalized | volumeFlag);

            if (length == 0 || length >= bigger.Length)
            {
                return null;
            }

            return WinPath.Normalize(WinPath.FromExtendedLength(new string(bigger, 0, (int)length)));
        }

        return WinPath.Normalize(
            WinPath.FromExtendedLength(new string(buffer[..(int)length])));
    }
}
