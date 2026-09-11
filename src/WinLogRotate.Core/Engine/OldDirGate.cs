using WinLogRotate.Contracts;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Safety;

namespace WinLogRotate.Core.Engine;

/// <summary>What one job's <c>olddir</c> turns out to be.</summary>
public sealed record OldDirDecision
{
    /// <summary>False when nothing may be written there, so nothing should be planned.</summary>
    public required bool Usable { get; init; }

    /// <summary>The directory to make first, where <c>createolddir</c> asked for one.</summary>
    public PlannedOp? Create { get; init; }

    public CliDiagnostic? Diagnostic { get; init; }
}

/// <summary>
/// Decides whether a job's archive directory may be written to, and whether it must be made.
/// </summary>
/// <remarks>
/// <para>
/// Pure, in <c>LockChoice</c>'s sense: it is handed the guard's verdict and whether the directory
/// exists rather than going and looking, so every rule here is provable on the Linux leg.
/// </para>
/// <para>
/// This has to run at <b>plan</b> time, not in the executor. <c>PlanExecutor</c> returns on
/// <c>dryRun</c> before it reaches its own guard call, so a destination check placed only there
/// leaves <c>--dry-run</c> silent about <c>olddir = "C:/Windows/System32"</c> - which is the one
/// command an operator runs precisely to find that out. The executor still re-checks every
/// destination at the last moment; the two are not alternatives.
/// </para>
/// <para>
/// Three things were wrong here and they were tangled. <c>olddir</c> was never guarded at all -
/// <c>PlanExecutor</c>'s only <c>CheckPath</c> call passes the source, so
/// <c>olddir = "C:/Windows/System32"</c> was written to, with the guard holding that path in
/// <c>ProtectedRoots</c> and never being asked. <c>createolddir</c> was bound, merged and read by
/// nothing. And the two lock strategies disagreed about the same configuration: a missing
/// directory made <c>rename</c> fail for ever with a message naming the live log, while
/// <c>copytruncate</c> silently created it - recursively, and regardless of <c>createolddir</c>.
/// </para>
/// </remarks>
public static class OldDirGate
{
    /// <param name="guard">
    /// What the path guard said about the resolved directory. Asked <b>before</b> existence, so
    /// <c>createolddir = true</c> can never make a directory somewhere we would refuse to write.
    /// </param>
    public static OldDirDecision Check(
        EffectiveJob job, string directory, bool exists, GuardDecision guard)
    {
        if (!guard.IsAllowed)
        {
            return new OldDirDecision
            {
                Usable = false,
                Diagnostic = Diagnose.Refusal(guard, job.Name),
            };
        }

        if (exists)
        {
            return new OldDirDecision { Usable = true };
        }

        if (!job.CreateOldDir)
        {
            // logrotate's rule, and the diagnostic names the destination. Until now this produced
            // ERROR_PATH_NOT_FOUND from MoveFileEx, which Win32Error rendered as "the file no
            // longer exists" against the live log - a file that plainly did exist - once per
            // rotation, for ever, because the following Create op put the log back each time.
            return new OldDirDecision
            {
                Usable = false,
                Diagnostic = new CliDiagnostic
                {
                    Severity = Severity.Error,
                    Code = DiagnosticCode.RotationFailed,
                    Message = $"[{job.Name}] olddir '{directory}' does not exist.",
                    Path = directory,
                    Job = job.Name,
                    Remedy = "Create it, or set createolddir = true to have WinLogRotate make it.",
                },
            };
        }

        return new OldDirDecision
        {
            Usable = true,
            Create = new PlannedOp
            {
                Action = PlannedAction.CreateDirectory,

                // Source rather than Destination, so the executor's existing source guard covers
                // the creation with no special case: the directory we are about to make is checked
                // by literally the same rule as everything else it touches.
                Source = directory,
                Reason = $"createolddir = true; '{directory}' does not exist",
            },
        };
    }
}
