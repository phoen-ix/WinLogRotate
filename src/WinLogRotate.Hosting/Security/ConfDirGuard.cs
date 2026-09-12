using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using WinLogRotate.Core;
using WinLogRotate.Core.Configuration;

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
    /// <summary>
    /// Judges every path a run would take its configuration from, outermost first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The gate used to inspect one <c>DirectoryInfo</c>. The thing executed as SYSTEM is a
    /// <i>file</i>, and rewriting a directory's DACL recomputes only what a child inherits - it
    /// changes neither a child's explicit entries nor a child's owner, and an owner holds
    /// implicit WRITE_DAC. So a job file dropped into <c>conf.d</c> while ProgramData's
    /// <c>CREATOR OWNER</c> was granting its author Full Control stayed theirs to rewrite, with
    /// the directory reported <c>Hardened</c> throughout and <c>doctor</c> printing a repair
    /// that did not reach it. <c>docs/hooks.md</c> has stated the rule for files all along.
    /// </para>
    /// <para>
    /// <c>config.toml</c> and the root are in the surface for the same reason:
    /// <c>[defaults]</c> accepts <c>prerotate</c> and <c>postrotate</c>, and a hook written once
    /// there is inherited by every job - from a file and a directory the gate had never looked
    /// at.
    /// </para>
    /// <para>
    /// Outermost first, and the first refusal wins. Not a severity ranking: repairing a job file
    /// inside a directory a local user can write repairs nothing, so the container has to be the
    /// answer given first or the operator fixes the wrong thing and is told it is still broken.
    /// </para>
    /// </remarks>
    public static AclFinding Verify(InstallPaths paths, string? runAccountSid = null)
    {
        if (!Directory.Exists(paths.ConfigDirectory))
        {
            return new AclFinding
            {
                Verdict = AclVerdict.Unknown,
                Path = paths.ConfigDirectory,
                Explanation = "The configuration directory does not exist yet.",
            };
        }

        var finding = ConfigSurfaceGuard.Verify(
            ConfigSurfaceGuard.SurfaceOf(paths),
            ConfigSurfaceGuard.Trusted(runAccountSid),
            ReadDescriptor);

        return finding.Verdict is AclVerdict.LooseOwner or AclVerdict.LooseWritable
            ? Scoped(finding, paths.Scope)
            : finding;
    }

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
    /// Applies the hardened descriptor to the directories this product owns, and to every file
    /// inside them that a run would take its configuration from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each directory is set explicitly rather than relying on the root's inheritable entries
    /// propagating downward. Propagation is not enough here, and the reason is ProgramData's
    /// <c>CREATOR OWNER:(OI)(CI)(IO)(F)</c>: when a subdirectory is created beneath it, that
    /// entry materialises as a Full Control ACE for whoever created it - the elevated account
    /// running the installer, whose own SID is not <c>BUILTIN\Administrators</c>. It cost a
    /// shipped release to learn that. The installer hardened the root, the smoke test asserted
    /// the root, and conf.d - the only directory whose permissions decide anything, because it
    /// holds the files the run host executes - was checked by neither.
    /// </para>
    /// <para>
    /// The files are the half that was missing, and they are the half that matters. Rewriting a
    /// parent's DACL recomputes what a child inherits; it changes neither a child's own explicit
    /// entries nor a child's owner, and an owner holds implicit WRITE_DAC. So a job file dropped
    /// into conf.d while ProgramData's CREATOR OWNER was still granting Full Control stayed its
    /// author's to rewrite across the exact command <c>doctor</c> prints as the fix - the guard
    /// reported <c>Hardened</c>, and the attacker went on editing their own file's
    /// <c>postrotate</c>.
    /// </para>
    /// <para>
    /// This lives here rather than in the CLI because its sibling does:
    /// <see cref="SecretsFileGuard"/> has both halves, and a guard whose repair is somewhere
    /// else is a guard whose repair can drift from it - which is what happened.
    /// </para>
    /// </remarks>
    public static void Apply(InstallPaths paths)
    {
        // run\ is in this list because of what it holds: the record of when the rotation gate
        // was first found held. Until this milestone nothing created it, so it appeared lazily
        // under whatever ProgramData handed out - which means the local account that record
        // exists to detect would own the file recording it, and could hold the first refusal
        // forward for ever.
        foreach (var directory in
                 new[] { paths.Root, paths.ConfigDirectory, paths.JournalDirectory, paths.RunDirectory })
        {
            Directory.CreateDirectory(directory);

            var security = new DirectorySecurity();
            security.SetSecurityDescriptorSddlForm(Sddl.ConfigDirectory);

            // Severing inheritance is stated twice on purpose. The D:P in the SDDL says it, but
            // whether that survives the managed persist path is not something to take on trust:
            // if it does not, the directory silently keeps its parent's entries - which for
            // ProgramData means CREATOR OWNER, materialised as Full Control for whoever ran the
            // installer. Saying it through the API as well costs one line and cannot be lost.
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            new DirectoryInfo(directory).SetAccessControl(security);
        }

        foreach (var file in JobFiles.In(paths.ConfigDirectory))
        {
            Reset(file);
        }
    }

    /// <summary>Gives one file back to Administrators and back to its parent's rule.</summary>
    private static void Reset(string path)
    {
        var info = new FileInfo(path);
        var security = info.GetAccessControl(
            AccessControlSections.Access | AccessControlSections.Owner);

        // Ownership first. Without it the two steps below are undone by whoever still owns the
        // file, at a moment of their choosing.
        security.SetOwner(new SecurityIdentifier(Sddl.WellKnown.Administrators));

        // Inheritance re-enabled and every explicit entry discarded, rather than a descriptor of
        // the file's own. The parent is protected and carries OICI SYSTEM and Administrators full
        // control with Users read, so what a child inherits is exactly the rule - and stays
        // exactly the rule when the rule changes.
        security.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);

        foreach (FileSystemAccessRule rule in security.GetAccessRules(
                     includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier)))
        {
            security.RemoveAccessRuleSpecific(rule);
        }

        info.SetAccessControl(security);
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
