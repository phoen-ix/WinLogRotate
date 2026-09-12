using WinLogRotate.Contracts;
using WinLogRotate.Core.Journaling;
using WinLogRotate.Core.Notify.Delivery;

namespace WinLogRotate.Core.Hooks;

/// <summary>What one stage's hooks came to.</summary>
public sealed record HookStageResult
{
    /// <summary>
    /// Whether every hook this stage names either ran and succeeded, or was not there at all.
    /// </summary>
    /// <remarks>
    /// What a <c>prerotate</c> caller acts on. False means the job's precondition was not
    /// established, so nothing is touched - see <see cref="HookRunner"/> for why a
    /// <c>postrotate</c> caller must not act on it the same way.
    /// </remarks>
    public required bool Ok { get; init; }

    public required int Failed { get; init; }
    public required IReadOnlyList<CliDiagnostic> Diagnostics { get; init; }

    public static readonly HookStageResult Nothing =
        new() { Ok = true, Failed = 0, Diagnostics = [] };
}

/// <summary>
/// Runs a job's hooks: the gate, the timeouts, the record of what happened.
/// </summary>
/// <remarks>
/// <para>
/// Hooks run <b>once per job</b>, not once per file. That is logrotate's <c>sharedscripts</c>
/// behaviour and it is what the importer has always written into the configurations it generates;
/// the bracket therefore goes around <c>PlanExecutor.Execute</c> rather than inside its loop. A
/// reload script that fired once per matched file would signal IIS forty times on a directory of
/// forty logs.
/// </para>
/// <para>
/// <b>The asymmetry is deliberate and is logrotate's.</b> A failing <c>prerotate</c> means its
/// precondition was not met - the service did not stop, the buffer was not flushed - so the job is
/// skipped and no file is touched, because rotating a log something is still writing to is exactly
/// what the hook existed to prevent. A failing <c>postrotate</c> comes after the files have
/// already moved: the rotation stands, and the failure is reported against the job. Undoing a
/// rotation because a reload script exited 1 would be the more surprising of the two by a wide
/// margin.
/// </para>
/// </remarks>
public sealed class HookRunner(IJournal journal, IHookHost? host, HookGate gate, TimeProvider clock)
{
    /// <summary>The gate this runner was built with, so a caller can report it once.</summary>
    public HookGate Gate => gate;

    /// <param name="raw">The job's configured strings for this stage.</param>
    /// <param name="timeout">The per-hook limit, before the run deadline is taken into account.</param>
    /// <param name="started">When the invocation began, for the deadline clamp.</param>
    /// <param name="deadline">The whole invocation's limit, or null when nothing will kill it.</param>
    public HookStageResult Run(
        string jobName, HookStage stage, IReadOnlyList<string> raw, TimeSpan timeout,
        bool dryRun, DateTimeOffset started, TimeSpan? deadline, string? sourceFile = null)
    {
        if (raw.Count == 0)
        {
            return HookStageResult.Nothing;
        }

        var planned = HookPlan.For(jobName, stage, raw, gate, sourceFile);
        var diagnostics = new List<CliDiagnostic>(planned.Refusals);
        var failed = planned.Refusals.Count;

        foreach (var hook in planned.Hooks)
        {
            journal.Write(Event(hook, Phase.Plan, null, null, null));

            // Above execution, not below it. A dry run has to describe the hook it would run
            // without running it: --dry-run is trusted against production precisely because it is
            // the same code path stopped one step earlier, and a reload script is not something to
            // discover the exception to that rule with.
            if (dryRun)
            {
                continue;
            }

            // The same arithmetic the notification phase uses, and for the same reason: a hook
            // that outlives the scheduled task's ExecutionTimeLimit is killed by Task Scheduler,
            // which reports it as 0x41306 - indistinguishable from an operator pressing Stop. The
            // rotation would have succeeded and the only machine-readable evidence of it would say
            // "terminated".
            var (allowed, _) = NotifyBudget.For(timeout, deadline, started, clock.GetUtcNow());

            var outcome = allowed <= TimeSpan.Zero
                ? HookOutcome.CouldNotStart(
                    "the run had no time left before the scheduled task's limit")
                : host is null
                    ? HookOutcome.CouldNotStart("no hook host is available on this platform")
                    : Guarded(hook, allowed);

            journal.Write(Event(
                hook, Phase.Apply, outcome.Ok ? OpResult.Ok : OpResult.Failed,
                outcome.Ok ? null : Describe(outcome),
                (long)outcome.Elapsed.TotalMilliseconds));

            if (outcome.Ok)
            {
                continue;
            }

            failed++;
            diagnostics.Add(Failure(jobName, hook, outcome, allowed));
        }

        return new HookStageResult
        {
            Ok = failed == 0,
            Failed = failed,
            Diagnostics = diagnostics,
        };
    }

    /// <summary>Runs one hook, converting anything that escapes into an outcome.</summary>
    /// <remarks>
    /// <see cref="IHookHost"/> says it never throws, and this is what makes that true rather than
    /// merely asked for. The alternative is an exception raised in the middle of the job loop -
    /// which has no try of its own, and neither does RunCommand or Program, so it would leave the
    /// process by the front door with the journal's run.end record never written. A hook whose
    /// executable is missing is the ordinary way to reach it: Process.Start throws Win32Exception
    /// for ERROR_FILE_NOT_FOUND.
    /// </remarks>
    private HookOutcome Guarded(PlannedHook hook, TimeSpan allowed)
    {
        try
        {
            return host!.Run(hook, allowed) ?? HookOutcome.CouldNotStart("the hook host returned nothing");
        }
        catch (Exception e)
        {
            return HookOutcome.CouldNotStart($"{e.GetType().Name}: {e.Message}");
        }
    }

    private static CliEvent Event(
        PlannedHook hook, string phase, string? result, string? error, long? ms) => new()
        {
            Ts = string.Empty,
            Run = string.Empty,
            Operation = Op.Hook,
            Phase = phase,
            Result = result,
            Job = hook.JobName,
            Src = hook.Display,
            Reason = hook.StageName,
            Error = error,
            Ms = ms,
        };

    private static CliDiagnostic Failure(
        string jobName, PlannedHook hook, HookOutcome outcome, TimeSpan allowed) => new()
        {
            Severity = Severity.Error,
            Code = DiagnosticCode.HookFailed,
            Message = $"[{jobName}] the {hook.StageName} hook '{hook.Display}' {Describe(outcome)}",
            Job = jobName,

            // The consequence, said out loud. Which of the two it is depends on the stage, and an
            // operator reading one line at 03:00 should not have to know the asymmetry by heart.
            Remedy = hook.Stage == HookStage.PreRotate
                ? "The job was skipped and no file was touched, because a prerotate hook is the "
                  + "job's precondition."
                : outcome.Result == HookResult.TimedOut
                    ? $"The rotation stands. Raise hook_timeout above {allowed.TotalSeconds:0}s if "
                      + "the hook is simply slow."
                    : Silent(outcome)

                        // Only standard error is repeated - see WindowsHookHost.Tail. A hook that
                        // explains itself on standard output leaves nothing here, so say where to
                        // look rather than leaving an exit code and no thread to pull.
                        ? "The rotation stands; only the hook failed. It printed nothing to "
                          + "standard error, so run it by hand to see what it says."
                        : "The rotation stands; only the hook failed.",
        };

    /// <summary>A hook that failed without explaining itself where anyone was listening.</summary>
    private static bool Silent(HookOutcome outcome) =>
        outcome.Result == HookResult.Failed && string.IsNullOrEmpty(outcome.Detail);

    private static string Describe(HookOutcome outcome) => outcome.Result switch
    {
        HookResult.TimedOut =>
            $"was still running after {outcome.Elapsed.TotalSeconds:0}s and was killed"
            + (outcome.Detail is { } d ? $" ({d})" : "."),

        HookResult.CouldNotStart => $"could not be started: {outcome.Detail}",

        _ => $"exited {outcome.ExitCode}"
             + (outcome.Detail is { Length: > 0 } text ? $": {text}" : "."),
    };
}
