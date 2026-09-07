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

        // Anyone permitted beyond SYSTEM, Administrators and the expected per-user owner is a
        // finding. Comparing against an allowed set rather than looking for specific bad
        // principals is the difference between a check and a guess: BUILTIN\Users is the ACE
        // this exists to catch, but it is not the only one that could appear.
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Sddl.WellKnown.LocalSystem,
            Sddl.WellKnown.Administrators,
        };

        if (ownerSid is not null)
        {
            allowed.Add(ownerSid);
        }

        var unexpected = new List<string>();
        foreach (FileSystemAccessRule rule in
                 security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow)
            {
                continue;
            }

            var sid = rule.IdentityReference.Value;
            if (!allowed.Contains(sid))
            {
                unexpected.Add(sid);
            }
        }

        if (unexpected.Count > 0)
        {
            return new SecretsAclFinding
            {
                Verdict = SecretsAclVerdict.TooOpen,
                Path = path,
                Detail = $"The secrets file grants access to {string.Join(", ", unexpected)}.",
                Remedy = $"icacls \"{path}\" /inheritance:r /grant *{Sddl.WellKnown.LocalSystem}:F "
                         + $"/grant *{Sddl.WellKnown.Administrators}:F",
            };
        }

        return new SecretsAclFinding { Verdict = SecretsAclVerdict.Hardened, Path = path };
    }
}
