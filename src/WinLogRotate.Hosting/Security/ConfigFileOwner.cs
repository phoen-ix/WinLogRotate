using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace WinLogRotate.Hosting.Security;

/// <summary>
/// Leaves a file the run host will read owned by Administrators.
/// </summary>
/// <remarks>
/// <para>
/// Rewriting a directory's DACL recomputes what its children inherit. It changes neither a
/// child's own explicit entries nor a child's owner - and an owner holds implicit WRITE_DAC, so
/// a job file stays its author's to rewrite across the exact command <c>doctor</c> prints as the
/// fix. That is why the gate judges ownership per file, and why ownership has to be set where
/// the file is created rather than only repaired afterwards.
/// </para>
/// <para>
/// It matters that this is not a one-off migration. An elevated administrator's created objects
/// are owned by that account's own SID, not by <c>BUILTIN\Administrators</c> - a fact this
/// product learned from a shipped release, recorded in <c>HostCommand</c>. So every
/// <c>import</c>, every <c>secret set</c> that rewrites <c>config.toml</c>, and every GUI edit
/// that reaches those, produces a file owned by a principal the gate does not trust. Without
/// this, hooks would go off on healthy machines as a matter of routine, and the printed remedy
/// would fix it only until the next edit.
/// </para>
/// </remarks>
public static class ConfigFileOwner
{
    /// <summary>
    /// Sets the file's owner to Administrators, if that can be done at all.
    /// </summary>
    /// <returns>
    /// False when it could not be, which is not an error worth reporting: on a per-user
    /// installation the owner is the installing user by design, and the gate already refuses
    /// hooks there for that reason and says so in those words.
    /// </returns>
    public static bool Claim(string path) => OperatingSystem.IsWindows() && ClaimOnWindows(path);

    [SupportedOSPlatform("windows")]
    private static bool ClaimOnWindows(string path)
    {
        try
        {
            var info = new FileInfo(path);
            var security = info.GetAccessControl(AccessControlSections.Owner);

            security.SetOwner(new SecurityIdentifier(Sddl.WellKnown.Administrators));
            info.SetAccessControl(security);

            return true;
        }
        catch (Exception e) when (e is IOException
                                      or UnauthorizedAccessException
                                      or PrivilegeNotHeldException
                                      or InvalidOperationException)
        {
            // An unelevated account cannot give a file away, and an account outside the
            // Administrators group cannot give one to it. Both are ordinary, both leave a file
            // the gate will refuse to execute hooks from, and refusing is the correct outcome.
            return false;
        }
    }
}
