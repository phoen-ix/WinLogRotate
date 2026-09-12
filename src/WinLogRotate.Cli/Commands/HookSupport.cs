using System.Runtime.Versioning;
using WinLogRotate.Contracts;
using WinLogRotate.Core;
using WinLogRotate.Core.Hooks;
using WinLogRotate.Hosting.Hooks;
using WinLogRotate.Hosting.Security;

namespace WinLogRotate.Cli.Commands;

/// <summary>What this machine is prepared to let a configuration file execute.</summary>
internal sealed record HookSupport
{
    /// <summary>Null where nothing can run hooks - anywhere that is not Windows.</summary>
    public IHookHost? Host { get; init; }

    public required HookGate Gate { get; init; }

    /// <summary>
    /// The whole finding about the configuration directory, for reporting once per run.
    /// </summary>
    /// <remarks>
    /// Separate from the per-hook refusal on purpose. <c>LR9001</c> is a fact about the machine -
    /// "this directory can be written by somebody who should not be able to" - and <c>LR9003</c> is
    /// a fact about one job. They go to different people and they have different remedies. Until
    /// now nothing on the run path emitted <c>LR9001</c> at all: it was reachable only from
    /// <c>doctor</c> and <c>host</c>, so an operator who never ran either was never told.
    /// </remarks>
    public CliDiagnostic? Finding { get; init; }

    /// <summary>
    /// Decides, once, whether this run may execute what its configuration names.
    /// </summary>
    /// <remarks>
    /// Called immediately before rotation, not at configuration load. A run reads its
    /// configuration and then rotates for an hour, and a directory's permissions can be changed in
    /// between - by exactly the person this check exists to stop.
    /// </remarks>
    public static HookSupport ForThisMachine(InstallPaths paths) =>
        OperatingSystem.IsWindows()
            ? OnWindows(paths)
            : new HookSupport
            {
                // Said as a platform fact, the way SenderTable words a missing eventlog:
                // transport, so nobody goes looking for a build that has it.
                Gate = HookGate.Shut("hooks need Windows"),
            };

    [SupportedOSPlatform("windows")]
    private static HookSupport OnWindows(InstallPaths paths)
    {
        var finding = ConfDirGuard.Verify(paths);

        if (finding.HooksAllowed)
        {
            return new HookSupport { Host = new WindowsHookHost(), Gate = HookGate.Open };
        }

        return new HookSupport
        {
            // Built even when the gate is shut. Nothing in this type decides whether a hook runs -
            // HookGate does - and constructing the host anyway means a future caller cannot
            // accidentally treat "a host exists" as "hooks are permitted".
            Host = new WindowsHookHost(),
            Gate = HookGate.Shut(Reason(finding), finding.FixCommand),
            Finding = new CliDiagnostic
            {
                // Not an error where it follows from how the product was installed. A per-user
                // installation's directory is writable by its owner by design; reporting that as a
                // fault every night would train somebody to ignore the nights it is not one.
                Severity = finding.ExpectedForScope ? Severity.Warning : Severity.Error,
                Code = DiagnosticCode.ConfigDirectoryInsecure,
                Message = finding.Explanation
                          ?? $"'{finding.Path}' is not safe to execute a configuration file from.",
                Path = finding.Path,
                Remedy = finding.FixCommand,
            },
        };
    }

    /// <summary>The clause that completes "this hook runs code, and ...".</summary>
    private static string Reason(AclFinding finding) => finding.Verdict switch
    {
        AclVerdict.LooseWritable =>
            "the configuration directory can be written by an account that is not an administrator",

        AclVerdict.LooseOwner =>
            "the configuration directory is owned by a non-administrator, who can rewrite its "
            + "permissions at will",

        AclVerdict.Inherited =>
            "the configuration directory inherits its permissions, so it will re-inherit "
            + "ProgramData's the moment anyone changes them",

        AclVerdict.Unknown => "the configuration directory could not be checked",

        _ => "the configuration directory is not safe to execute from",
    };
}
