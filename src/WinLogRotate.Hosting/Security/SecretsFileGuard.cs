using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace WinLogRotate.Hosting.Security;

/// <summary>What an inspection of the secrets file concluded.</summary>
public enum SecretsAclVerdict
{
    /// <summary>SYSTEM and Administrators only, inheritance severed.</summary>
    Hardened,

    /// <summary>There is no file yet.</summary>
    Missing,

    /// <summary>Readable by principals that should not read it.</summary>
    TooOpen,

    /// <summary>The descriptor could not be read from this account.</summary>
    Unknown,
}

/// <summary>The finding, with the exact command that fixes it.</summary>
public sealed record SecretsAclFinding
{
    public required SecretsAclVerdict Verdict { get; init; }
    public required string Path { get; init; }
    public string? Detail { get; init; }
    public string? Remedy { get; init; }

    public bool IsSafe => Verdict is SecretsAclVerdict.Hardened or SecretsAclVerdict.Missing;
}

/// <summary>
/// Whether one descriptor keeps <c>secrets.dat</c> to the principals that may read it.
/// </summary>
/// <remarks>
/// <para>
/// Unannotated, so the whole of the decision runs on both CI legs. <see cref="AclJudgement"/>
/// gives the argument in full; the short version is that a rule only windows-2025 can execute is
/// a rule nobody keeps, and this file's owner rule went six milestones without one.
/// </para>
/// <para>
/// The opposite question to <see cref="AclJudgement"/>'s. That one asks who can <i>write</i>,
/// because writing a job file is SYSTEM code execution. This asks who can read at all: reading a
/// stored credential is the whole harm, so any grant to anyone outside the trusted set counts,
/// read-only ones included.
/// </para>
/// </remarks>
internal static class SecretsAclJudgement
{
    internal static (SecretsAclVerdict Verdict, IReadOnlyList<string> Offending) Judge(
        AclJudgement.Subject subject, IReadOnlySet<string> trusted)
    {
        // An owner holds implicit WRITE_DAC, so it can grant itself read whenever it likes - and
        // the DACL check below would go on reporting Hardened right up until it did. This is the
        // same hole the configuration directory's gate had, in the file that holds credentials.
        // Sddl.SecretsFile sets O:BA, so Apply has always produced the right owner; nothing ever
        // checked that the file on disk still had it.
        if (subject.OwnerSid is { } owner && !trusted.Contains(owner))
        {
            return (SecretsAclVerdict.TooOpen, [$"owner: {subject.OwnerDescribe}"]);
        }

        // Compared against an allowed set rather than against a list of bad principals, which is
        // the difference between a check and a guess: BUILTIN\Users is the entry this exists to
        // catch, but it is not the only one that could appear.
        var unexpected = subject.Allow
            .Where(ace => !trusted.Contains(ace.Sid))
            .Select(ace => ace.Sid)
            .ToList();

        return unexpected.Count > 0
            ? (SecretsAclVerdict.TooOpen, unexpected)
            : (SecretsAclVerdict.Hardened, []);
    }
}

/// <summary>
/// Applies and verifies the descriptor on <c>secrets.dat</c>.
/// </summary>
/// <remarks>
/// Separate from <see cref="ConfDirGuard"/> because it asks the opposite question. The
/// configuration directory is checked for being <em>writable</em> by the wrong people, since
/// that is what turns a job file into SYSTEM code execution. This file is checked for being
/// <em>readable</em> by them.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class SecretsFileGuard
{
    /// <summary>
    /// Applies the descriptor. Called on the temporary file, before it is moved into place.
    /// </summary>
    /// <param name="ownerSid">
    /// The additional principal for a per-user install, whose owner is not necessarily an
    /// administrator and would otherwise be locked out of their own store. Null per-machine.
    /// </param>
    public static void Apply(string path, string? ownerSid = null)
    {
        var security = new FileSecurity();
        security.SetSecurityDescriptorSddlForm(
            ownerSid is null ? Sddl.SecretsFile : Sddl.SecretsFileFor(ownerSid));
        new FileInfo(path).SetAccessControl(security);
    }

    /// <summary>Reports whether the file on disk is actually protected.</summary>
    /// <param name="ownerSid">The principal a per-user install is expected to permit.</param>
    public static SecretsAclFinding Verify(string path, string? ownerSid = null)
    {
        if (!File.Exists(path))
        {
            return new SecretsAclFinding { Verdict = SecretsAclVerdict.Missing, Path = path };
        }

        FileSecurity security;
        try
        {
            security = new FileInfo(path).GetAccessControl();
        }
        catch (Exception e) when (e is UnauthorizedAccessException or PrivilegeNotHeldException
            or System.Security.SecurityException)
        {
            // Being unable to read the descriptor is what a correctly protected file looks like
            // from an unelevated account, so this is reported as unknown rather than as a fault.
            return new SecretsAclFinding
            {
                Verdict = SecretsAclVerdict.Unknown,
                Path = path,
                Detail = "The secrets file's permissions could not be read from this account.",
            };
        }

        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Sddl.WellKnown.LocalSystem,
            Sddl.WellKnown.Administrators,
        };

        if (ownerSid is not null)
        {
            allowed.Add(ownerSid);
        }

        var entries = new List<AclJudgement.Ace>();
        foreach (FileSystemAccessRule rule in
                 security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow)
            {
                continue;
            }

            var sid = ((SecurityIdentifier)rule.IdentityReference).Value;
            entries.Add(new AclJudgement.Ace(sid, (int)rule.FileSystemRights, sid));
        }

        var fileOwner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;

        var (verdict, offending) = SecretsAclJudgement.Judge(
            new AclJudgement.Subject(
                path,
                IsDirectory: false,
                fileOwner?.Value,
                fileOwner?.Value ?? "an owner the system would not name",
                entries,
                Protected: true),
            allowed);

        if (verdict != SecretsAclVerdict.Hardened)
        {
            return new SecretsAclFinding
            {
                Verdict = verdict,
                Path = path,
                Detail = $"The secrets file grants access to {string.Join(", ", offending)}.",
                Remedy = $"icacls \"{path}\" /inheritance:r /grant *{Sddl.WellKnown.LocalSystem}:F "
                         + $"/grant *{Sddl.WellKnown.Administrators}:F "
                         + $"&& icacls \"{path}\" /setowner *{Sddl.WellKnown.Administrators}",
            };
        }

        return new SecretsAclFinding { Verdict = SecretsAclVerdict.Hardened, Path = path };
    }
}
