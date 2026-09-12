using WinLogRotate.Core;
using WinLogRotate.Core.Configuration;

namespace WinLogRotate.Hosting.Security;

/// <summary>
/// Every path a run takes its configuration from, judged in the order it would have to be fixed.
/// </summary>
/// <remarks>
/// <para>
/// Descriptors arrive through a delegate rather than being read here, so the whole of the
/// decision runs on both CI legs. <see cref="AclJudgement"/> gives the argument in full.
/// </para>
/// <para>
/// First refusal in surface order wins, and the order is outermost first. That is not a severity
/// ranking: repairing a job file inside a directory a local user can write repairs nothing, so
/// the container is always the answer to give first.
/// </para>
/// </remarks>
internal static class ConfigSurfaceGuard
{
    /// <summary>
    /// Reads one path's descriptor, or returns null if it cannot be read at all.
    /// </summary>
    /// <remarks>
    /// Null rather than an exception, and it is the fail-closed answer: it becomes
    /// <see cref="AclVerdict.Unknown"/>, which does not allow hooks. Unguarded, a descriptor
    /// that could not be read - an ordinary thing on a live <c>conf.d</c>, where an editor
    /// replaces a file between the enumeration and the read - escaped as <c>LR1006</c> exit 4,
    /// "This is a defect", about a machine that was merely busy.
    /// </remarks>
    internal delegate AclJudgement.Subject? Descriptor(string path, bool isDirectory);

    /// <summary>The principals whose write access is not a finding.</summary>
    internal static HashSet<string> Trusted(string? runAccountSid)
    {
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

        return trusted;
    }

    /// <summary>
    /// Every path a run takes its configuration from, outermost first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Out here rather than inside <c>ConfDirGuard</c> on purpose. Which paths are judged is the
    /// substance of the gate - the whole defect was that the list held one directory - and a
    /// list assembled inside a <c>[SupportedOSPlatform("windows")]</c> class is a list only
    /// windows-2025 can ever check. Deleting the job files from it would then leave every test
    /// on the Linux leg green, which is exactly how the directory-only gate survived six
    /// milestones.
    /// </para>
    /// <para>
    /// <c>config.toml</c> is here because <c>[defaults]</c> accepts <c>prerotate</c> and
    /// <c>postrotate</c>, and a hook written once there is inherited by every job. The root is
    /// here because it is what <c>config.toml</c> sits in.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<(string Path, bool IsDirectory)> SurfaceOf(InstallPaths paths)
    {
        List<(string Path, bool IsDirectory)> surface =
        [
            (paths.Root, true),
            (paths.ConfigDirectory, true),
        ];

        if (File.Exists(paths.ConfigFile))
        {
            surface.Add((paths.ConfigFile, false));
        }

        // The children come last, and the first-refusal rule means their descriptors are read
        // only once the directories are clean: a loose directory refuses hooks whatever its
        // files say. On the path that does read them it is one descriptor per job file -
        // strictly fewer syscalls than ConfigLoader spends opening and parsing that same set a
        // moment later, on every run.
        surface.AddRange(JobFiles.In(paths.ConfigDirectory).Select(f => (f, false)));

        return surface;
    }

    /// <summary>
    /// The first path in <paramref name="surface"/> that is not safe, or Hardened if none is.
    /// </summary>
    internal static AclFinding Verify(
        IReadOnlyList<(string Path, bool IsDirectory)> surface,
        IReadOnlySet<string> trusted,
        Descriptor read)
    {
        foreach (var (path, isDirectory) in surface)
        {
            if (read(path, isDirectory) is not { } subject)
            {
                return new AclFinding
                {
                    Verdict = AclVerdict.Unknown,
                    Path = path,
                    Explanation =
                        $"The permissions on '{path}' could not be read, so whether a " +
                        "non-administrator can change it is not established. Hooks are refused " +
                        "for the whole run.",
                    FixCommand = FixCommand(path),
                };
            }

            var (verdict, offending) = AclJudgement.Judge(subject, trusted);

            if (verdict != AclVerdict.Hardened)
            {
                return Describe(verdict, subject, offending);
            }
        }

        return new AclFinding { Verdict = AclVerdict.Hardened, Path = surface[0].Path };
    }

    private static AclFinding Describe(
        AclVerdict verdict, AclJudgement.Subject subject, IReadOnlyList<string> offending)
    {
        var path = subject.Path;

        return verdict switch
        {
            AclVerdict.LooseOwner => new AclFinding
            {
                Verdict = verdict,
                Path = path,
                OffendingAces = offending,
                Explanation =
                    $"'{path}' is owned by {subject.OwnerDescribe}, who can therefore " +
                    "rewrite its permissions at will.",
                FixCommand = FixCommand(path),
            },

            AclVerdict.LooseWritable => new AclFinding
            {
                Verdict = verdict,
                Path = path,
                OffendingAces = offending,
                Explanation = subject.IsDirectory
                    ? $"'{path}' can be written by an account that is not an administrator, so a " +
                      "job file placed there would be executed by the run host. Hooks are refused " +
                      "for the whole run."
                    : $"'{path}' can be written by an account that is not an administrator, so the " +
                      "commands it defines would be executed by the run host as written by them. " +
                      "Hooks are refused for the whole run.",
                FixCommand = FixCommand(path),
            },

            AclVerdict.Inherited => new AclFinding
            {
                Verdict = verdict,
                Path = path,
                Explanation =
                    $"'{path}' inherits permissions from its parent. It is tight today, but it will " +
                    "re-inherit ProgramData's permissive entries as soon as anyone changes them.",
                FixCommand = FixCommand(path),
            },

            _ => new AclFinding { Verdict = verdict, Path = path, OffendingAces = offending },
        };
    }

    private static string FixCommand(string path) =>
        $"winlogrotate host repair --acl   (or: icacls \"{path}\" /inheritance:r " +
        $"/grant:r *{Sddl.WellKnown.LocalSystem}:(OI)(CI)F " +
        $"*{Sddl.WellKnown.Administrators}:(OI)(CI)F " +
        $"*{Sddl.WellKnown.Users}:(OI)(CI)RX)";
}
