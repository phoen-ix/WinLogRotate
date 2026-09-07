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
                var message = decision.Message ?? $"{op.Source} was refused.";
                errors.Add(message);
                Emit(plan, op, Phase.Apply, OpResult.Failed, message, 0);
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

                Emit(plan, op, Phase.Apply, OpResult.Ok, null,
                    (long)clock.GetElapsedTime(started).TotalMilliseconds, bytes);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                failed++;
                var code = RetryPolicy.ErrorCode(e);
                var message = code == 0
                    ? $"{op.Action} {op.Source}: {e.Message}"
                    : $"{op.Action} {op.Source}: {Win32Error.Describe(code)}";
                errors.Add(message);
                Emit(plan, op, Phase.Apply, OpResult.Failed, message,
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
                var sizeBefore = RetryPolicy.Execute(
                    () => FileOps.CopyTruncate(op.Source, op.Destination!, truncate),
                    job.RetryCount, job.RetryIntervalMs);

                // Recorded so the next run can judge whether the writer honoured the
                // truncation or resumed at a cached offset and left NTFS to zero-fill the gap.
                if (truncate)
                {
                    RecordTruncation?.Invoke(op.Source, sizeBefore);
                }

                return sizeBefore;

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
    public Action<string, long>? RecordTruncation { get; set; }

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
            Reason = op.Reason,
            Error = error,
            Ms = ms == 0 ? null : ms,
        });
}
