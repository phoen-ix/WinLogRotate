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
}
