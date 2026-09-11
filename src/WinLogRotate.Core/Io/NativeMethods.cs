using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace WinLogRotate.Core.Io;

/// <summary>
/// The Win32 calls the engine needs, with the share-mode and access flags spelled out.
/// </summary>
/// <remarks>
/// These are hand-declared rather than reached through <c>FileStream</c> because the whole
/// point is controlling the exact <c>dwDesiredAccess</c> and <c>dwShareMode</c> combination -
/// which is what decides whether a rename will be permitted. <c>FileStream</c> deliberately
/// hides that.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static partial class NativeMethods
{
    internal const uint GenericRead = 0x80000000;
    internal const uint GenericWrite = 0x40000000;
    internal const uint Delete = 0x00010000;

    internal const uint ShareRead = 0x00000001;
    internal const uint ShareWrite = 0x00000002;
    internal const uint ShareDelete = 0x00000004;

    internal const uint OpenExisting = 3;

    internal const uint FlagBackupSemantics = 0x02000000;
    internal const uint FlagOpenReparsePoint = 0x00200000;
    internal const uint FlagSequentialScan = 0x08000000;

    // MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH.
    // MOVEFILE_COPY_ALLOWED is deliberately NOT set: it silently degrades a cross-volume
    // rename into a non-atomic copy-and-delete, which for a multi-gigabyte log is both slow
    // and a window in which a crash loses data. Failing loudly is the correct behaviour, and
    // it is what tells the operator their olddir is on the wrong volume.
    internal const uint MoveFileReplaceExisting = 0x00000001;
    internal const uint MoveFileWriteThrough = 0x00000008;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool MoveFileEx(string lpExistingFileName, string? lpNewFileName, uint dwFlags);

    /// <summary>Enough of BY_HANDLE_FILE_INFORMATION to identify a file.</summary>
    /// <remarks>
    /// Blittable and laid out sequentially so the source-generated stub can marshal it without a
    /// hand-written unsafe block, which is the promise this project's csproj makes.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ByHandleFileInformation
    {
        internal uint FileAttributes;
        internal long CreationTime;
        internal long LastAccessTime;
        internal long LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    /// <summary>
    /// Both zero, which looks like a mistake and is not.
    /// </summary>
    /// <remarks>
    /// <c>FILE_NAME_NORMALIZED</c> is what turns <c>PROGRA~1</c> back into
    /// <c>Program Files</c>, and that is half the reason this call exists: the path guard
    /// compares strings, and an 8.3 short name is a different string for the same directory.
    /// </remarks>
    internal const uint FileNameNormalized = 0x00000000;
    internal const uint VolumeNameDos = 0x00000000;

    /// <summary>For a volume with no drive letter - a mounted folder.</summary>
    internal const uint VolumeNameGuid = 0x00000001;

    /// <summary>
    /// The real path behind a handle, with every component's link followed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>StringMarshalling.Utf16</c> is required even though no <c>string</c> appears: the
    /// source generator refuses a bare <c>char</c> parameter without it.
    /// </para>
    /// <para>
    /// The length contract is an off-by-one worth stating. On success the return <b>excludes</b>
    /// the terminating NUL; when the buffer is too small it <b>includes</b> it. So success is
    /// <c>0 &lt; len &lt; cch</c>, zero is failure, and <c>len &gt;= cch</c> means try again with
    /// <c>len</c> characters.
    /// </para>
    /// </remarks>
    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial uint GetFinalPathNameByHandle(
        SafeFileHandle hFile, ref char lpszFilePath, uint cchFilePath, uint dwFlags);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetFileInformationByHandle(
        SafeFileHandle hFile, out ByHandleFileInformation lpFileInformation);
}
