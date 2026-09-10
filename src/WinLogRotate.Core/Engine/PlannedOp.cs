using WinLogRotate.Core.Configuration;

namespace WinLogRotate.Core.Engine;

/// <summary>What the engine intends to do to one file.</summary>
public enum PlannedAction
{
    Compress,
    Delete,
    Rename,
    CopyTruncate,
    Copy,
    Create,
    MoveToOldDir,

    /// <summary>Considered and deliberately left alone. Carried in the plan rather than
    /// omitted, because "why was this file not touched?" is the most common question a
    /// dry run has to answer.</summary>
    Skip,
}

/// <summary>
/// A single intended operation. The plan is produced without touching anything, which is what
/// makes <c>--dry-run</c> trustworthy: it is the same code path stopped one step earlier, not a
/// separate description that can drift from what the executor really does.
/// </summary>
public sealed record PlannedOp
{
    public required PlannedAction Action { get; init; }
    public required string Source { get; init; }
    public string? Destination { get; init; }

    /// <summary>Why. Populated for every delete, because "which rule condemned this file"
    /// is exactly what an operator needs when a log they wanted is gone.</summary>
    public required string Reason { get; init; }

    public long Bytes { get; init; }
    public LockStrategy? Strategy { get; init; }

    public override string ToString() =>
        Destination is null
            ? $"{Action} {Source} ({Reason})"
            : $"{Action} {Source} -> {Destination} ({Reason})";
}

/// <summary>Everything one job intends to do.</summary>
public sealed record JobPlan
{
    public required string JobName { get; init; }
    public required IReadOnlyList<PlannedOp> Operations { get; init; }

    /// <summary>Files the job matched, whether or not it plans to act on them.</summary>
    public required int MatchedFiles { get; init; }

    public IEnumerable<PlannedOp> Destructive =>
        Operations.Where(o => o.Action is not PlannedAction.Skip);

    /// <summary>
    /// Whether this plan moves a live log out of the way, as opposed to only tidying older
    /// generations.
    /// </summary>
    /// <remarks>
    /// What decides whether the job's hooks run at all. A night on which nothing was due but a
    /// month-old archive was finally compressed is not a night to signal IIS: a reload hook that
    /// fired because of a deletion would page somebody about a rotation that never happened. The
    /// three actions here are exactly the ones <c>ExecutionResult.Rotated</c> records, so the
    /// prerotate question and the postrotate question are asked of the same rule.
    /// </remarks>
    public bool RotatesALiveLog =>
        Operations.Any(o => o.Action
            is PlannedAction.Rename or PlannedAction.Copy or PlannedAction.CopyTruncate);

    public long BytesFreed =>
        Operations.Where(o => o.Action == PlannedAction.Delete).Sum(o => o.Bytes);
}
