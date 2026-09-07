namespace WinLogRotate.Core.Io;

/// <summary>
/// Win32 error codes the engine reasons about.
/// </summary>
/// <remarks>
/// Deliberately not marked <c>[SupportedOSPlatform("windows")]</c>, unlike the P/Invoke
/// declarations. These are documented numeric constants, not API calls, and the retry logic
/// that classifies them is ordinary decision-making that ought to be testable on any machine.
/// Gating them would push a platform attribute onto every caller for no benefit.
/// </remarks>
public static class Win32Error
{
    public const int FileNotFound = 2;
    public const int PathNotFound = 3;
    public const int AccessDenied = 5;

    /// <summary>The process cannot access the file because another process has it open with an
    /// incompatible share mode. The single most important error code in this product.</summary>
    public const int SharingViolation = 32;

    public const int LockViolation = 33;
    public const int AlreadyExists = 183;

    /// <summary>Returned by MoveFileEx for a cross-volume move when COPY_ALLOWED is not set.</summary>
    public const int NotSameDevice = 17;

    /// <summary>Human wording for the codes an operator is likely to meet.</summary>
    public static string Describe(int code) => code switch
    {
        FileNotFound or PathNotFound => "the file no longer exists",
        AccessDenied => "access was denied",
        SharingViolation => "another process has it open and did not permit sharing",
        LockViolation => "part of the file is locked by another process",
        NotSameDevice => "the destination is on a different volume, so the move could not be atomic",
        AlreadyExists => "the destination already exists",
        _ => $"Win32 error {code}",
    };
}
