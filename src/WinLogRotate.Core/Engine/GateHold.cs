namespace WinLogRotate.Core.Engine;

/// <summary>What it means that the rotation gate is held by somebody else.</summary>
public enum GateHold
{
    /// <summary>Nothing is on record, so there is nothing to judge.</summary>
    Unknown,

    /// <summary>
    /// A rotation is running. The expected outcome when a manual run overlaps the scheduled one.
    /// </summary>
    Overlapping,

    /// <summary>Held for longer than any rotation lasts, so it is not a rotation.</summary>
    Implausible,
}

/// <summary>
/// How long the rotation gate may be held by somebody else before it stops being a rotation.
/// </summary>
/// <remarks>
/// <para>
/// <c>Global\WinLogRotate.Rotation</c> grants <c>Everyone</c> the right to synchronise, and that
/// cannot be narrowed. To take part in mutual exclusion on a named mutex a principal needs
/// <c>SYNCHRONIZE</c>; <c>SYNCHRONIZE</c> <i>is</i> the right to wait, and a satisfied wait
/// <i>is</i> ownership. There is no right that grants waiting while withholding holding.
/// Removing the entry would mean the SYSTEM task and an unelevated GUI create two mutexes that
/// cannot see each other, which is two rotations over the same files - the worst bug this
/// product could have. Narrowing it to SYSTEM and Administrators would break per-user and
/// portable installs, where the legitimate rotator is neither.
/// </para>
/// <para>
/// So any local account can hold it for ever, and that is not the defect being fixed here. A
/// local account can also stop a rotation by holding a log file open, and the product reports
/// that as <c>LR3002</c>. The defect is that this one reported <c>0x0</c>: every scheduled run
/// returned the task's <c>--lock-held-exit</c>, so Task Scheduler's Last Run Result column - the
/// column an administrator actually reads - said the machine was fine while nothing on it had
/// rotated for a week.
/// </para>
/// <para>
/// The gate cannot tell <i>who</i> holds it. It can tell <i>how long</i>, and that is enough: a
/// legitimate holder is a rotation, and a rotation is bounded by the scheduled task's
/// <c>ExecutionTimeLimit</c>.
/// </para>
/// </remarks>
public static class GateHoldRule
{
    /// <summary>How long a hold may last before it stops being a rotation.</summary>
    /// <remarks>
    /// <para>
    /// Four hours, chosen against the scheduled task rather than guessed.
    /// <c>ExecutionTimeLimit</c> bounds a run and Task Scheduler reports <c>0x41306</c> when it
    /// bites, so a gate still held four hours after the first refusal has outlived every run
    /// that could have been holding it. Shorter fires on a genuinely long rotation of a very
    /// large tree; longer lets a whole day of rotations pass with the column an administrator
    /// reads saying <c>0x0</c>.
    /// </para>
    /// <para>
    /// It is a floor, not a latency. The judgement is only made when a run is refused, so the
    /// first report arrives after <c>max(4h, one run interval)</c> - for the shipped daily task,
    /// the following night.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan Implausible = TimeSpan.FromHours(4);

    /// <summary>What to make of a gate first refused at <paramref name="firstRefusedAt"/>.</summary>
    public static GateHold Judge(DateTimeOffset? firstRefusedAt, DateTimeOffset now) =>
        firstRefusedAt is not { } first ? GateHold.Unknown
        : now - first >= Implausible ? GateHold.Implausible
        : GateHold.Overlapping;
}
