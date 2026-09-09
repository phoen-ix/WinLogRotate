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

    Unknown,
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

        var info = new DirectoryInfo(directory);
        var security = info.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);

        var trusted = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Sddl.WellKnown.LocalSystem,
            Sddl.WellKnown.Administrators,
            Sddl.WellKnown.TrustedInstaller,
        };

        if (runAccountSid is not null)
        {
            trusted.Add(runAccountSid);
        }

        // An owner can rewrite the DACL whenever it likes, so a non-admin owner is a write
        // grant wearing a disguise.
        if (security.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner
            && !trusted.Contains(owner.Value))
        {
            return Scoped(new AclFinding
            {
                Verdict = AclVerdict.LooseOwner,
                Path = directory,
                OffendingAces = [$"owner: {Describe(owner)}"],
                Explanation =
                    $"'{directory}' is owned by {Describe(owner)}, who can therefore rewrite its permissions at will.",
                FixCommand = FixCommand(directory),
            }, scope);
        }

        var offending = new List<string>();

        foreach (FileSystemAccessRule rule in
                 security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow)
            {
                continue;
            }

            if (!AclMask.GrantsWrite((int)rule.FileSystemRights))
            {
                continue;
            }

            var sid = ((SecurityIdentifier)rule.IdentityReference).Value;

            // CREATOR OWNER is the specific ProgramData hole: it silently grants full control
            // over whatever a user creates, so a dropped job file is theirs to keep editing.
            if (trusted.Contains(sid))
            {
                continue;
            }

            offending.Add($"{Describe(rule.IdentityReference)} : {rule.FileSystemRights}");
        }

        if (offending.Count > 0)
        {
            return Scoped(new AclFinding
            {
                Verdict = AclVerdict.LooseWritable,
                Path = directory,
                OffendingAces = offending,
                Explanation =
                    $"'{directory}' can be written by an account that is not an administrator, so a job file " +
                    "placed there would be executed by the run host. Hooks are refused for the whole run.",
                FixCommand = FixCommand(directory),
            }, scope);
        }

        if (!security.AreAccessRulesProtected)
        {
            return new AclFinding
            {
                Verdict = AclVerdict.Inherited,
                Path = directory,
                Explanation =
                    $"'{directory}' inherits permissions from its parent. It is tight today, but it will " +
                    "re-inherit ProgramData's permissive entries as soon as anyone changes them.",
                FixCommand = FixCommand(directory),
            };
        }

        return new AclFinding { Verdict = AclVerdict.Hardened, Path = directory };
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

    private static string FixCommand(string directory) =>
        $"winlogrotate host repair --acl   (or: icacls \"{directory}\" /inheritance:r " +
        $"/grant:r *{Sddl.WellKnown.LocalSystem}:(OI)(CI)F " +
        $"*{Sddl.WellKnown.Administrators}:(OI)(CI)F " +
        $"*{Sddl.WellKnown.Users}:(OI)(CI)RX)";

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
