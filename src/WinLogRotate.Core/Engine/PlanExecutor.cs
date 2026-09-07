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

    private static long? Apply(PlannedOp op, EffectiveJob job)
    {
        switch (op.Action)
        {
            case PlannedAction.Compress:
                var result = Compressor.Compress(
                    op.Source, job.CompressType,
                    retryCount: job.RetryCount, retryIntervalMs: job.RetryIntervalMs);
                return result.BytesAfter;

            case PlannedAction.Delete:
                RetryPolicy.Execute(() => File.Delete(op.Source), job.RetryCount, job.RetryIntervalMs);
                return 0;

            default:
                throw new NotSupportedException(
                    $"{op.Action} is not implemented yet - rotate jobs land in a later milestone.");
        }
    }

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
