using System.Runtime.Versioning;
using WinLogRotate.Core.Compression;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Io;

namespace WinLogRotate.Core.Engine;

/// <summary>What carrying out one operation left behind.</summary>
public sealed record AppliedOp
{
    /// <summary>
    /// The size the operation reports, where it has one to report: the archive after a compress,
    /// the source before a copy. Null where there is nothing to measure.
    /// </summary>
    public long? BytesAfter { get; init; }

    /// <summary>
    /// The two numbers a truncation is judged by afterwards. Set by copytruncate and by nothing
    /// else.
    /// </summary>
    public TruncationOutcome? Truncation { get; init; }

    /// <summary>An operation with nothing to say about its result.</summary>
    public static AppliedOp Nothing { get; } = new();
}

/// <summary>
/// Carries out one planned operation against the file system.
/// </summary>
/// <remarks>
/// A seam for the reason <see cref="IArchiveSource"/> and <see cref="Io.IWriterInspector"/> are
/// seams. <see cref="PlanExecutor.Execute"/> decides what happens when an operation fails - which
/// later operations may still run, what the journal records, what the rotation clock learns - and
/// every one of those rules was reachable only from a Windows box, because the switch that touched
/// files sat inside the method that held them. The decision layer is platform-neutral; only this
/// is not, and a fake of it is how the Linux leg gets to fail one operation and watch what the
/// executor does about the rest.
/// </remarks>
public interface IPlanApplier
{
    /// <summary>Applies one operation, or throws the exception that stopped it.</summary>
    /// <param name="onRetry">
    /// Told about every attempt beyond the first, so a contended file leaves evidence.
    /// </param>
    AppliedOp Apply(PlannedOp op, EffectiveJob job, Action<int, Exception> onRetry);
}

/// <summary>The real file system: <see cref="FileOps"/> and <see cref="Compressor"/>, which need Windows.</summary>
public sealed class WindowsPlanApplier : IPlanApplier
{
    /// <inheritdoc />
    public AppliedOp Apply(PlannedOp op, EffectiveJob job, Action<int, Exception> onRetry)
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

        return OnWindows(op, job, onRetry);
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
    [SupportedOSPlatform("windows")]
    private static AppliedOp OnWindows(PlannedOp op, EffectiveJob job, Action<int, Exception> onRetry)
    {
        switch (op.Action)
        {
            case PlannedAction.Compress:
                var result = Compressor.Compress(
                    op.Source, job.CompressType,
                    retryCount: job.RetryCount, retryIntervalMs: job.RetryIntervalMs,
                    onRetry: onRetry);
                return new AppliedOp { BytesAfter = result.BytesAfter };

            case PlannedAction.Delete:
                RetryPolicy.Execute(
                    () => FileOps.Delete(op.Source), job.RetryCount, job.RetryIntervalMs, onRetry);
                return new AppliedOp { BytesAfter = 0 };

            case PlannedAction.CreateDirectory:
                RetryPolicy.Execute(
                    () => FileOps.CreateDirectory(op.Source), job.RetryCount, job.RetryIntervalMs, onRetry);
                return new AppliedOp { BytesAfter = 0 };

            case PlannedAction.Rename:
                RetryPolicy.Execute(
                    () => FileOps.Rename(op.Source, op.Destination!),
                    job.RetryCount, job.RetryIntervalMs, onRetry);
                return AppliedOp.Nothing;

            case PlannedAction.CopyTruncate:
            case PlannedAction.Copy:
                var truncate = op.Action == PlannedAction.CopyTruncate;

                // The retries live inside, and that is the whole of the fix. Wrapped from out
                // here, an attempt that archived the log and then failed to empty it was retried
                // from the top: the second attempt measured a source the first had already
                // truncated, copied nothing, and moved the nothing over the archive it had just
                // saved. The call returned normally and the run reported a success.
                //
                // FileOps.CopyTruncate now retries opening, copying and cutting separately, so a
                // cut that fails is retried alone against a handle whose archive is committed.
                // Restoring a wrapper here restores the defect in full.
                var outcome = FileOps.CopyTruncate(
                    op.Source, op.Destination!, truncate,
                    attempts: job.RetryCount, intervalMs: job.RetryIntervalMs, onRetry: onRetry);

                // Reported so the run can judge whether the writer honoured the truncation or
                // resumed at a cached offset and left NTFS to zero-fill the gap. Both numbers,
                // not just the size: the gap begins where the cut left the file, and sampling
                // from byte zero finds the preserved tail rather than the NUL run.
                return new AppliedOp
                {
                    BytesAfter = outcome.SizeBefore,
                    Truncation = truncate ? outcome : null,
                };

            case PlannedAction.Create:
                RetryPolicy.Execute(() => FileOps.Create(op.Destination ?? op.Source),
                    job.RetryCount, job.RetryIntervalMs, onRetry);
                return new AppliedOp { BytesAfter = 0 };

            default:
                throw new NotSupportedException($"{op.Action} has no implementation.");
        }
    }
}
