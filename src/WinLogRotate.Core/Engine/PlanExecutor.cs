using WinLogRotate.Contracts;
using WinLogRotate.Core.Compression;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Io;
using WinLogRotate.Core.Journaling;
using WinLogRotate.Core.Safety;

namespace WinLogRotate.Core.Engine;

/// <summary>What actually happened when a plan was carried out.</summary>
public sealed record ExecutionResult
{
    public required int Completed { get; init; }
    public required int Failed { get; init; }
    public required int Skipped { get; init; }
    public required long BytesFreed { get; init; }
    public required IReadOnlyList<string> Errors { get; init; }

    /// <summary>The same failures as <see cref="Errors"/>, classified and attributed.
    /// Errors keeps the flat strings the envelope has always carried.</summary>
    public IReadOnlyList<CliDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>
    /// Live logs that were actually moved out of the way.
    /// </summary>
    /// <remarks>
    /// The rotation clock advances from this and from nothing else. Advancing it because a
    /// rotation was <i>planned</i> would record a log that failed on a share violation as
    /// rotated, and it would then never be due again - a locked file rotating exactly once and
    /// falling silent for ever. Only the executor knows which renames really happened, so the
    /// answer is produced here rather than inferred by the caller.
    /// </remarks>
    public IReadOnlyList<string> Rotated { get; init; } = [];
}

/// <summary>
/// Carries out a plan, journaling both halves of every operation.
/// </summary>
/// <remarks>
/// Each destructive step emits a <c>plan</c> event and then an <c>apply</c> event, so a run
/// killed between them leaves a readable record of an intention that was never completed. A
/// dry run emits only the first half - it is the same code path, stopped one step earlier,
/// rather than a separate description that could drift from what the executor really does.
/// </remarks>
public sealed class PlanExecutor(IJournal journal, PathGuard guard, TimeProvider clock)
{
    public ExecutionResult Execute(JobPlan plan, EffectiveJob job, bool dryRun)
    {
        var completed = 0;
        var failed = 0;
        var skipped = 0;
        long freed = 0;
        var errors = new List<string>();
        var diagnostics = new List<CliDiagnostic>();
        var rotated = new List<string>();

        foreach (var op in plan.Operations)
        {
            if (op.Action == PlannedAction.Skip)
            {
                skipped++;
                Emit(plan, op, Phase.Plan, OpResult.Skipped, null, 0);
                continue;
            }

            Emit(plan, op, Phase.Plan, null, null, 0);

            if (dryRun)
            {
                continue;
            }

            // Re-check immediately before acting, not only at plan time. The plan may be
            // seconds old, and this is the last moment before something is destroyed.
            var decision = guard.CheckPath(op.Source);
            if (!decision.IsAllowed)
            {
                failed++;
                var refusal = Diagnose.Refusal(decision, plan.JobName);
                errors.Add(refusal.Message);
                diagnostics.Add(refusal);
                Emit(plan, op, Phase.Apply, OpResult.Failed, refusal.Message, 0);
                continue;
            }

            var started = clock.GetTimestamp();
            try
            {
                var bytes = Apply(op, job);
                completed++;
                if (op.Action == PlannedAction.Delete)
                {
                    freed += op.Bytes;
                }

                // Only the three that move the live log out of the way. Compressing or deleting
                // an older generation is not what the rotation clock measures, and counting it
                // would mark a log as rotated on a night it was not.
                if (op.Action is PlannedAction.Rename or PlannedAction.Copy or PlannedAction.CopyTruncate)
                {
                    rotated.Add(op.Source);
                }

                Emit(plan, op, Phase.Apply, OpResult.Ok, null,
                    (long)clock.GetElapsedTime(started).TotalMilliseconds, bytes);
            }
            // ExternalException covers Win32Exception, which nothing here threw until hooks
            // existed and which this filter did not match. It escaped Execute, and then RunCommand
            // and Program - neither of which has a catch at all - so a missing executable ended the
            // process with the journal's run.end record never written. RetryPolicy.ErrorCode has
            // always understood the type; only the filter stood in the way.
            catch (Exception e) when (
                e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
            {
                failed++;
                var failure = Diagnose.Failure(op.Action, op.Source, e, plan.JobName);
                errors.Add(failure.Message);
                diagnostics.Add(failure);
                Emit(plan, op, Phase.Apply, OpResult.Failed, failure.Message,
                    (long)clock.GetElapsedTime(started).TotalMilliseconds);
            }
        }

        return new ExecutionResult
        {
            Completed = completed,
            Failed = failed,
            Skipped = skipped,
            BytesFreed = freed,
            Errors = errors,
            Diagnostics = diagnostics,
            Rotated = rotated,
        };
    }

    private long? Apply(PlannedOp op, EffectiveJob job)
    {
        // Applying a plan is where the Win32 surface begins. Guarding here rather than marking
        // the whole executor Windows-only keeps Execute platform-neutral, which is what lets
        // the dry-run path - the property that matters most, that --dry-run changes nothing -
        // be tested on the Linux CI leg alongside the planners.
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Applying a rotation plan requires Windows. Use --dry-run to plan anywhere.");
        }

        return ApplyOnWindows(op, job);
    }

    /// <summary>
    /// The Win32 half, split out and annotated rather than guarded inline.
    /// <para>
    /// CA1416's flow analysis does not follow a platform check into a lambda, and every call
    /// below is wrapped in one for the retry policy - so an inline guard silences nothing.
    /// Splitting the method is what actually lets the analyzer verify the boundary instead of
    /// having it suppressed.
    /// </para>
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private long? ApplyOnWindows(PlannedOp op, EffectiveJob job)
    {
        switch (op.Action)
        {
            case PlannedAction.Compress:
                var result = Compressor.Compress(
                    op.Source, job.CompressType,
                    retryCount: job.RetryCount, retryIntervalMs: job.RetryIntervalMs);
                return result.BytesAfter;

            case PlannedAction.Delete:
                RetryPolicy.Execute(() => FileOps.Delete(op.Source), job.RetryCount, job.RetryIntervalMs);
                return 0;

            case PlannedAction.Rename:
            case PlannedAction.MoveToOldDir:
                RetryPolicy.Execute(
                    () => FileOps.Rename(op.Source, op.Destination!),
                    job.RetryCount, job.RetryIntervalMs);
                return null;

            case PlannedAction.CopyTruncate:
            case PlannedAction.Copy:
                var truncate = op.Action == PlannedAction.CopyTruncate;
                var outcome = RetryPolicy.Execute(
                    () => FileOps.CopyTruncate(op.Source, op.Destination!, truncate),
                    job.RetryCount, job.RetryIntervalMs);

                // Recorded so the run can judge whether the writer honoured the truncation or
                // resumed at a cached offset and left NTFS to zero-fill the gap. Both numbers,
                // not just the size: the gap begins where the cut left the file, and sampling
                // from byte zero finds the preserved tail rather than the NUL run.
                if (truncate)
                {
                    RecordTruncation?.Invoke(op.Source, outcome);
                }

                return outcome.SizeBefore;

            case PlannedAction.Create:
                RetryPolicy.Execute(() => FileOps.Create(op.Destination ?? op.Source),
                    job.RetryCount, job.RetryIntervalMs);
                return 0;

            default:
                throw new NotSupportedException($"{op.Action} has no implementation.");
        }
    }

    /// <summary>
    /// Called after a truncation with the size the file had beforehand, so the caller can store
    /// it in state. Without that number the NUL-fill detector has nothing to compare against on
    /// the following run, and the failure it exists to catch stays invisible.
    /// </summary>
    public Action<string, TruncationOutcome>? RecordTruncation { get; set; }

    private void Emit(
        JobPlan plan, PlannedOp op, string phase, string? result,
        string? error, long ms, long? bytesAfter = null) =>
        journal.Write(new CliEvent
        {
            Ts = string.Empty,
            Run = string.Empty,
            Operation = op.Action switch
            {
                PlannedAction.Compress => Op.Compress,
                PlannedAction.Delete => Op.Delete,
                PlannedAction.Rename => Op.Rename,
                PlannedAction.CopyTruncate => Op.CopyTruncate,
                PlannedAction.Copy => Op.Copy,
                PlannedAction.Create => Op.Create,
                PlannedAction.MoveToOldDir => Op.MoveOldDir,
                _ => Op.Plan,
            },
            Phase = phase,
            Result = result,
            Job = plan.JobName,
            Src = op.Source,
            Dst = op.Destination,
            BytesBefore = op.Bytes,
            BytesAfter = bytesAfter,

            // CliEvent.Strategy is documented as "which locked-file strategy was used, when one
            // was" and was set by nothing, so it was null on every journal line this product has
            // ever written. It is also the only way to tell afterwards what lockstrategy = "auto"
            // actually resolved to, which is now a question worth being able to answer.
            Strategy = op.Strategy?.ToString().ToLowerInvariant(),
            Reason = op.Reason,
            Error = error,
            Ms = ms == 0 ? null : ms,
        });
}
