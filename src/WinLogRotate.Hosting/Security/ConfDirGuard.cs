using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using WinLogRotate.Core;

namespace WinLogRotate.Hosting.Security;

/// <summary>How trustworthy the configuration directory is.</summary>
public enum AclVerdict
{
    /// <summary>Locked down as the installer left it. Hooks may run.</summary>
    Hardened,

    /// <summary>Tight, but not protected - it will re-inherit if ProgramData's ACL is touched.</summary>
    Inherited,

    /// <summary>Writable by someone who is not an administrator. Hooks are refused.</summary>
    LooseWritable,

    /// <summary>Owned by a non-administrator, who therefore holds implicit WRITE_DAC.</summary>
    LooseOwner,

    /// <summary>Checked, and the answer could not be established.</summary>
    Unknown,

    /// <summary>
    /// Not checked, because this is not Windows.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Unknown"/>, which means the check ran and could not tell.
    /// <c>doctor</c> reported this as the sentence "not checked (Windows only)" in a field that
    /// otherwise held enum names, so one value on the wire was prose and the rest were not - and
    /// the GUI compared against both.
    /// </remarks>
    NotApplicable,
}

/// <summary>What the check found, and how to fix it.</summary>
public sealed record AclFinding
{
    public required AclVerdict Verdict { get; init; }
    public required string Path { get; init; }
    public IReadOnlyList<string> OffendingAces { get; init; } = [];
    public string? Explanation { get; init; }
    public string? FixCommand { get; init; }

    /// <summary>
    /// True when this verdict follows from how the product was installed rather than from
    /// anything being misconfigured.
    /// </summary>
    /// <remarks>
    /// A per-user installation keeps its configuration in the user's own profile, which that
    /// user can necessarily write. There is nothing to repair and nothing to warn about: hooks
    /// are refused, which is correct, and telling the operator to run icacls against their own
    /// AppData folder would be advice that damages a working installation.
    /// </remarks>
    public bool ExpectedForScope { get; init; }

    /// <summary>Hooks run only from a directory nobody but an administrator can write.</summary>
    /// <remarks>
    /// Deliberately unaffected by <see cref="ExpectedForScope"/>. Knowing why a directory is
    /// writable does not make executing what it contains any safer, so the gate is the same
    /// either way; only the explanation changes.
    /// </remarks>
    public bool HooksAllowed => Verdict == AclVerdict.Hardened;
}

/// <summary>
/// Verifies that the configuration directory cannot be written by a non-administrator.
/// </summary>
/// <remarks>
/// Deliberately biased toward refusing. A false refusal costs a hook and prints an exact fix;
/// a false acceptance hands a local user SYSTEM.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class ConfDirGuard
{
    /// <param name="scope">
    /// How this copy was installed. Only <see cref="InstallScope.PerUser"/> softens the
    /// wording. <see cref="InstallScope.Portable"/> stays strict on purpose: it is what an
    /// explicit --config-dir produces, which is exactly how the installer verifies its own
    /// work against ProgramData.
    /// </param>
    public static AclFinding Verify(
        string directory, string? runAccountSid = null, InstallScope scope = InstallScope.PerMachine)
    {
        if (!Directory.Exists(directory))
        {
            return new AclFinding
            {
                Verdict = AclVerdict.Unknown,
                Path = directory,
                Explanation = "The configuration directory does not exist yet.",
            };
        }

        var finding = ConfigSurfaceGuard.Verify(
            [(directory, true)], ConfigSurfaceGuard.Trusted(runAccountSid), ReadDescriptor);

        // Scoped only where it used to be. A per-user directory that merely inherits is not
        // re-worded, because the sentence Scoped substitutes is about being writable by its
        // owner, and that is not what Inherited says.
        return finding.Verdict is AclVerdict.LooseOwner or AclVerdict.LooseWritable
            ? Scoped(finding, scope)
            : finding;
    }

    /// <summary>
    /// One path's descriptor, reduced to what a verdict is a function of - or null if it could
    /// not be read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Null, never an exception. <c>GetAccessControl</c> throws for four ordinary reasons on a
    /// live installation: the file was replaced between being enumerated and being read, which
    /// is what a text editor's save looks like; the path became unreachable; the descriptor is
    /// readable only with a privilege this process does not hold; or the account is not allowed
    /// to read it at all - which, as <see cref="SecretsFileGuard"/> puts it, is what a correctly
    /// protected path looks like from an unelevated account.
    /// </para>
    /// <para>
    /// Every one of those used to escape to <c>CommandContext.Guarded</c> and come back as
    /// <c>LR1006</c>, exit 4, "This is a defect. Nothing about what was or was not done can be
    /// relied on" - about a machine that was merely busy. It is now
    /// <see cref="AclVerdict.Unknown"/>, which refuses hooks: the answer could not be
    /// established, and the guard is biased toward refusing.
    /// </para>
    /// </remarks>
    private static AclJudgement.Subject? ReadDescriptor(string path, bool isDirectory)
    {
        FileSystemSecurity security;

        try
        {
            security = isDirectory
                ? new DirectoryInfo(path).GetAccessControl(
                    AccessControlSections.Access | AccessControlSections.Owner)
                : new FileInfo(path).GetAccessControl(
                    AccessControlSections.Access | AccessControlSections.Owner);
        }
        catch (Exception e)
            when (e is IOException or UnauthorizedAccessException or PrivilegeNotHeldException)
        {
            return null;
        }

        var allow = new List<AclJudgement.Ace>();

        foreach (FileSystemAccessRule rule in
                 security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow)
            {
                continue;
            }

            allow.Add(new AclJudgement.Ace(
                ((SecurityIdentifier)rule.IdentityReference).Value,
                (int)rule.FileSystemRights,
                $"{Describe(rule.IdentityReference)} : {rule.FileSystemRights}"));
        }

        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;

        return new AclJudgement.Subject(
            path,
            isDirectory,
            owner?.Value,
            owner is null ? "an owner the system would not name" : Describe(owner),
            allow,
            security.AreAccessRulesProtected);
    }

    /// <summary>
    /// Re-words a loose verdict when looseness is inherent to the installation.
    /// </summary>
    /// <remarks>
    /// The verdict itself is never changed - a per-user directory really is writable, and hooks
    /// really are refused. What changes is that it stops being reported as a fault with a
    /// repair command attached, because there is no fault and the repair would lock the owner
    /// out of their own configuration.
    /// </remarks>
    private static AclFinding Scoped(AclFinding finding, InstallScope scope) =>
        scope != InstallScope.PerUser
            ? finding
            : finding with
            {
                ExpectedForScope = true,
                Explanation =
                    $"'{finding.Path}' belongs to a per-user installation, so the account that owns it can " +
                    "write it. That is inherent to installing for one user, not a misconfiguration - but it " +
                    "does mean a job file there is not trustworthy enough to execute, so hooks are refused.",
                FixCommand = "Install for all users if you need hooks: their whole point is running commands, "
                           + "and that is only safe from a directory an ordinary account cannot write.",
            };

    private static string Describe(IdentityReference identity)
    {
        try
        {
            return $"{identity.Translate(typeof(NTAccount))} ({identity.Value})";
        }
        catch (IdentityNotMappedException)
        {
            // An orphaned SID from a deleted account or a departed domain. The raw value is
            // still the useful thing to print.
            return identity.Value;
        }
    }
}
