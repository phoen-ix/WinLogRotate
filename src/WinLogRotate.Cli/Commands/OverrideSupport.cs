using System.Runtime.Versioning;
using WinLogRotate.Core;
using WinLogRotate.Core.Safety;
using WinLogRotate.Hosting.Security;

namespace WinLogRotate.Cli.Commands;

/// <summary>
/// Whether this machine will let a configuration file relax a path rule.
/// </summary>
/// <remarks>
/// The override half of what <c>HookSupport</c> does for hooks, and separate from it on purpose.
/// <c>LR9001</c> promises operators that a writable <c>conf.d</c> refuses "all hooks and all
/// dangerous-path overrides"; until milestone 16 only the first half of that sentence was
/// implemented, so a local user who could write one file in <c>conf.d</c> could have a
/// SYSTEM-privileged process delete from a protected location.
/// </remarks>
internal static class OverrideSupport
{
    /// <summary>
    /// Decides, before the configuration is read, whether its overrides may be honoured.
    /// </summary>
    /// <remarks>
    /// Called immediately before <c>ConfigLoader.Load</c>, unlike <c>HookSupport.ForThisMachine</c>
    /// which is deliberately called an hour later. The question here is about the file being read
    /// now - were the permissions on the directory it came from safe at the moment it was read? -
    /// and taking it at first-hook time would answer it about a different moment. Loading is also
    /// the only point at which the answer can still reach a run whose configuration turns out to
    /// have errors, which is exactly the run an override exists to rescue.
    /// </remarks>
    public static OverrideGate ForThisMachine(InstallPaths paths) =>
        OperatingSystem.IsWindows()
            ? OnWindows(paths)
            // Open, not shut, and not for convenience. The threat LR9001 describes is a Windows
            // one - a non-administrator writes conf.d while the scheduled task runs as SYSTEM -
            // and there is no equivalent fact to establish elsewhere. Hooks are shut off Windows
            // because process spawning genuinely cannot happen there; nothing about an override
            // is impossible in the same way, so refusing would be a rule with no threat behind it.
            : OverrideGate.Open;

    [SupportedOSPlatform("windows")]
    private static OverrideGate OnWindows(InstallPaths paths)
    {
        var finding = ConfDirGuard.Verify(paths);

        if (finding.HooksAllowed)
        {
            return OverrideGate.Open;
        }

        // Tied to the same verdict hooks use, rather than a second, softer rule. One directory,
        // one question, one answer - and ExpectedForScope deliberately does not soften it, so a
        // per-user installation loses overrides for the same reason it loses hooks. The wording
        // has to carry that, because "expected" and "safe" are not the same thing and the
        // operator is entitled to know which one applies to them.
        var reason = finding.ExpectedForScope
            ? $"'{finding.Path}' is writable by its owner, as a per-user installation's "
              + "configuration directory is by design"
            : $"'{finding.Path}' is not safe to read an override from";

        return OverrideGate.Shut(reason, finding.FixCommand);
    }
}
