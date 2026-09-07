using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using WinLogRotate.Core.Safety;
using WinLogRotate.Core.State;

namespace WinLogRotate.Core.Io;

/// <summary>What a probe found, and why.</summary>
public sealed record ProbeResult
{
    public required string Path { get; init; }
    public required ProbeVerdict Verdict { get; init; }

    /// <summary>The Win32 error that stopped the next-best strategy, or 0.</summary>
    public int BlockingError { get; init; }

    public string? Explanation { get; init; }

    /// <summary>True when nothing else holds the file open at all.</summary>
    public bool Unlocked { get; init; }
}

/// <summary>
/// Works out what can actually be done to a log file that something else is writing.
/// </summary>
/// <remarks>
/// <para>
/// Renaming or deleting a file on Windows requires opening it with <c>DELETE</c> access, and
/// that is only granted if <b>every</b> existing handle was opened with
/// <c>FILE_SHARE_DELETE</c>. Most Windows loggers do not: MSVCRT's <c>fopen("a")</c>, .NET's
/// default <c>FileShare.Read</c>, IIS, http.sys and SQL Server all withhold it. log4net's
/// default <c>ExclusiveLock</c> withholds write sharing too, which means nothing outside the
/// process can rotate that file by any means - a fact worth telling the operator plainly
/// rather than failing at three in the morning.
/// </para>
/// <para>
/// So instead of assuming, we ask: attempt the opens each strategy would need, in descending
/// order of preference, and report the best one that succeeded. The probe opens and
/// immediately closes; it never writes, and it always permits full sharing itself so it cannot
/// disturb the writer it is inspecting.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class LockProbe
{
    public static ProbeResult Classify(string path)
    {
        var native = WinPath.ToExtendedLength(WinPath.Normalize(path));

        // Would a rename work? Needs DELETE access, which needs every other handle to have
        // permitted FILE_SHARE_DELETE.
        if (TryOpen(native, NativeMethods.Delete | NativeMethods.GenericRead, out var error))
        {
            return new ProbeResult
            {
                Path = path,
                Verdict = ProbeVerdict.Rename,
                Unlocked = TryOpen(native, NativeMethods.GenericRead | NativeMethods.GenericWrite, out _, shareMode: 0),
                Explanation = "The writer permits FILE_SHARE_DELETE, so the file can be renamed atomically.",
            };
        }

        var renameError = error;

        // Would copytruncate work? Needs read to copy and write to truncate.
        if (TryOpen(native, NativeMethods.GenericRead | NativeMethods.GenericWrite, out error))
        {
            return new ProbeResult
            {
                Path = path,
                Verdict = ProbeVerdict.CopyTruncate,
                BlockingError = renameError,
                Explanation =
                    $"Rename is not possible ({Win32Error.Describe(renameError)}), but the file can be copied and truncated in place.",
            };
        }

        var truncateError = error;

        // Read-only: we can archive a copy, but the original will keep growing.
        if (TryOpen(native, NativeMethods.GenericRead, out error))
        {
            return new ProbeResult
            {
                Path = path,
                Verdict = ProbeVerdict.Copy,
                BlockingError = truncateError,
                Explanation =
                    "The file can only be read, so a copy can be archived but the original will keep growing. " +
                    "This is what log4net's default ExclusiveLock looks like: nothing outside the process can rotate it.",
            };
        }

        return new ProbeResult
        {
            Path = path,
            Verdict = ProbeVerdict.None,
            BlockingError = error,
            Explanation = $"The file cannot be opened at all: {Win32Error.Describe(error)}.",
        };
    }

    /// <summary>Maps a probe verdict onto the strategy a job configured, or reports why not.</summary>
    public static bool Supports(ProbeVerdict verdict, Configuration.LockStrategy strategy) =>
        strategy switch
        {
            Configuration.LockStrategy.Rename => verdict is ProbeVerdict.Rename,
            Configuration.LockStrategy.CopyTruncate => verdict is ProbeVerdict.Rename or ProbeVerdict.CopyTruncate,
            Configuration.LockStrategy.Copy => verdict is not ProbeVerdict.None,
            Configuration.LockStrategy.Auto => verdict is not ProbeVerdict.None,
            _ => false,
        };

    /// <summary>The best strategy a verdict actually allows, for <c>lockstrategy = "auto"</c>.</summary>
    public static Configuration.LockStrategy? Best(ProbeVerdict verdict) => verdict switch
    {
        ProbeVerdict.Rename => Configuration.LockStrategy.Rename,
        ProbeVerdict.CopyTruncate => Configuration.LockStrategy.CopyTruncate,
        ProbeVerdict.Copy => Configuration.LockStrategy.Copy,
        _ => null,
    };

    private static bool TryOpen(string path, uint access, out int error, uint? shareMode = null)
    {
        // Share everything by default: the probe must never lock out the very writer whose
        // behaviour it is measuring. The one exception is the "is anything else holding this?"
        // question, which deliberately asks for exclusive access.
        var share = shareMode
            ?? (NativeMethods.ShareRead | NativeMethods.ShareWrite | NativeMethods.ShareDelete);

        using var handle = NativeMethods.CreateFile(
            path, access, share, 0, NativeMethods.OpenExisting,
            NativeMethods.FlagOpenReparsePoint, 0);

        if (!handle.IsInvalid)
        {
            error = 0;
            return true;
        }

        error = Marshal.GetLastWin32Error();
        return false;
    }
}
