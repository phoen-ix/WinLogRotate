namespace WinLogRotate.Hosting.Security;

/// <summary>
/// Windows access-mask bits, and the arithmetic over them.
/// </summary>
/// <remarks>
/// Plain integers rather than <c>FileSystemRights</c>, and deliberately not marked
/// <c>[SupportedOSPlatform("windows")]</c> - the same choice, for the same reason, that
/// <c>Win32Error</c> makes. These are documented numeric constants, not API calls, and deciding
/// whether one mask intersects another is ordinary arithmetic that ought to be testable on any
/// machine. Referencing the enum members would not allow that: they carry the platform
/// attribute individually, so the analyzer rejects them from neutral code - which would leave
/// the one piece of logic here that most needs a test unable to have one on the Linux leg.
/// </remarks>
internal static class AclMask
{
    public const int WriteData = 0x000002;
    public const int AppendData = 0x000004;
    public const int WriteExtendedAttributes = 0x000010;
    public const int DeleteSubdirectoriesAndFiles = 0x000040;
    public const int WriteAttributes = 0x000100;
    public const int Delete = 0x010000;
    public const int ChangePermissions = 0x040000;
    public const int TakeOwnership = 0x080000;

    /// <summary>FILE_ALL_ACCESS as <c>FileSystemRights.FullControl</c> reports it.</summary>
    public const int FullControl = 0x1F01FF;

    /// <summary>
    /// What <c>Sddl.ConfigDirectory</c> grants BUILTIN\Users on purpose:
    /// FILE_GENERIC_READ | FILE_EXECUTE, so the unelevated read-only GUI works.
    /// </summary>
    public const int UsersReadExecute = 0x1200A9;

    /// <summary>GENERIC_ALL and GENERIC_WRITE, which an ACE can carry verbatim.</summary>
    /// <remarks>
    /// <para>
    /// Windows maps the generic bits onto specific ones at access-check time, not at set time.
    /// A descriptor applied from an SDDL string containing <c>GA</c> therefore stores 0x10000000
    /// and nothing else: every specific bit below is clear, <see cref="GrantsWrite"/> answered
    /// false, and a directory Everyone could write reported as Hardened.
    /// </para>
    /// <para>
    /// The PowerShell half of this same check has looked for <c>GA</c> since it was written -
    /// <c>.github/scripts/installer-smoke-helpers.ps1</c> - and the C# half never did. Two
    /// spellings of one rule, which is the thing this file exists to prevent.
    /// </para>
    /// <para>
    /// GENERIC_READ (0x80000000) and GENERIC_EXECUTE (0x20000000) are deliberately absent, for
    /// the same reason <see cref="FullControl"/> is: Users are granted read on purpose, and a
    /// mask of write-ish rights that matches a read grant refuses every hook on every install.
    /// </para>
    /// </remarks>
    public const int GenericAll = 0x10000000;

    /// <inheritdoc cref="GenericAll"/>
    public const int GenericWrite = 0x40000000;

    /// <summary>
    /// Rights that amount to being able to change what the run host executes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="FullControl"/> is deliberately absent, and its absence is the whole reason
    /// this lives in its own file. It is not a flag: it is the composite 0x1F01FF, which
    /// contains every read bit as well as every write one. Including it in a mask of write-ish
    /// rights makes <c>rights &amp; Writeish</c> non-zero for a purely read-only entry.
    /// </para>
    /// <para>
    /// That was not hypothetical. With FullControl in this mask,
    /// <c>UsersReadExecute &amp; Writeish</c> is 0x1200A9 - non-zero - so a <em>correctly</em>
    /// hardened configuration directory was reported as writable by a non-administrator, and
    /// every hook was refused on every installation. The hardening tripped its own guard, and
    /// the more correct the ACL was, the more certainly it failed.
    /// </para>
    /// <para>
    /// Nothing is lost by omitting it: an entry granting Full Control still matches, because
    /// Full Control contains <see cref="WriteData"/> and the rest listed here.
    /// </para>
    /// </remarks>
    public const int Writeish =
        WriteData | AppendData | WriteExtendedAttributes | DeleteSubdirectoriesAndFiles |
        WriteAttributes | Delete | ChangePermissions | TakeOwnership |
        GenericAll | GenericWrite;

    /// <summary>True if <paramref name="rights"/> confers any ability to change our contents.</summary>
    public static bool GrantsWrite(int rights) => (rights & Writeish) != 0;
}
