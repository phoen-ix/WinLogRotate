namespace WinLogRotate.Gui.Cli;

/// <summary>What the Settings page interrupts for, when the configuration directory is not safe.</summary>
public sealed record SecurityWarning
{
    /// <summary>The sentence in the dialog.</summary>
    public required string Message { get; init; }

    /// <summary>The command that would fix it, when doctor offered one.</summary>
    public string? Details { get; init; }
}

/// <summary>
/// Decides what the Settings page warns about, from what <c>doctor --json</c> reported.
/// </summary>
/// <remarks>
/// <para>
/// The page warned "can be written by an account that is not an administrator - repair
/// permissions to fix it" whenever hooks were refused and the verdict was neither Hardened nor
/// NotApplicable. That is every per-user installation, by design: <c>ConfDirGuard.Scoped</c>
/// keeps the loose verdict because the directory is in the user's own profile, and Repair then
/// declines to touch it for the same reason. So the one operator whose directory cannot be
/// secured was shown a security modal on every visit to the page, pointing at a button that
/// would refuse.
/// </para>
/// <para>
/// <c>doctor</c> itself makes this distinction - it reports the per-user case as Info and the
/// rest as Critical - and this follows the same rule, on the same fields. A pure function of the
/// four facts rather than of the envelope, so the page keeps the one walk it has always had and
/// the decision is asserted for both scopes on the leg that cannot open a page.
/// </para>
/// </remarks>
public static class SettingsProjection
{
    /// <summary>The warning to show, or null when there is nothing to interrupt for.</summary>
    /// <param name="aclVerdict">The wire spelling of <c>AclVerdict</c>: Hardened, LooseWritable, and so on.</param>
    /// <param name="scope">The wire spelling of <c>InstallScope</c>: Portable, PerUser or PerMachine.</param>
    /// <param name="aclFix">The command doctor offered to fix it, if it offered one.</param>
    public static SecurityWarning? PermissionsWarning(
        bool hooksAllowed, string aclVerdict, string scope, string? aclFix)
    {
        if (hooksAllowed)
        {
            return null;
        }

        // By design for a per-user installation, and doctor says so as Info: the directory is in
        // the user's own profile, and the hardened descriptor would take away their write access
        // to their own jobs. Portable stays strict, as ConfDirGuard keeps it.
        if (scope.Equals("PerUser", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Hardened and NotApplicable refuse hooks for reasons that are not about writers; Unknown
        // could not be established, and the sentence below would claim something nobody checked.
        if (aclVerdict.Equals("Hardened", StringComparison.OrdinalIgnoreCase)
            || aclVerdict.Equals("NotApplicable", StringComparison.OrdinalIgnoreCase)
            || aclVerdict.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new SecurityWarning
        {
            Message = "The configuration directory can be written by an account that is not an "
                    + "administrator, so hooks have been disabled for safety.\r\n\r\n"
                    + "Repair permissions to fix it.",
            Details = aclFix,
        };
    }
}
