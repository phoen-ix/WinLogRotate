namespace WinLogRotate.Hosting.Diagnostics;

/// <summary>What a budget says to do with one event.</summary>
/// <remarks>
/// Three values rather than a bool, because the middle one is not a kind of success. The cap
/// announcement is a different event, with a different id and a different message, written
/// <i>instead of</i> the caller's - and returning its result as the caller's is how a notification
/// digest came to be recorded as reported without ever having been written.
/// </remarks>
internal enum EventLogBudgetVerdict
{
    /// <summary>Inside the allowance. Write the event as asked.</summary>
    Write,

    /// <summary>
    /// The allowance has just run out. Write the announcement instead of this event, and tell the
    /// caller its own event did not go.
    /// </summary>
    Announce,

    /// <summary>Spent, and already announced. Write nothing.</summary>
    Drop,
}

/// <summary>What became of one event, from the point of view of whoever asked for it.</summary>
internal enum EventLogOutcome
{
    /// <summary>Handed to the Event Log.</summary>
    Written,

    /// <summary>No source is registered on this machine. Normal after a per-user install.</summary>
    Unavailable,

    /// <summary>The Event Log was asked and declined.</summary>
    Refused,

    /// <summary>This stream's allowance for the invocation was already spent.</summary>
    OverBudget,
}

/// <summary>
/// How many events one stream may put in the Application log during one invocation.
/// </summary>
/// <remarks>
/// <para>
/// One instance per stream, and the streams are deliberately not one. A job whose directory is
/// unreachable can produce a diagnostic per file, and a few hundred near-identical entries is how
/// an administrator decides this source is noise and filters it out - taking the one event that
/// mattered with it. That is an argument about the <i>mirrored diagnostics</i>, whose volume is
/// unbounded. It is not an argument about the notification digest, which is one message per job.
/// </para>
/// <para>
/// The same shape as <c>NotifyBudget</c> and <c>RetrySchedule</c>: a decision with no I/O in it,
/// and no platform annotation, so the whole of it runs on the Linux leg. That matters more here
/// than it looks - on neither CI leg is an event source registered, so anything that reaches the
/// real writer returns before the counting is consulted, and a test of the cap written against
/// <c>EventLogWriter</c> would be measuring the absence of advapi32.
/// </para>
/// <para>
/// The count is of <i>attempts</i>, not of events that landed. Counting successes would leave the
/// allowance unbounded on a log that refuses everything - the cap would stop capping exactly when
/// it is needed - so the announcement says "attempted" and means it.
/// </para>
/// </remarks>
internal sealed class EventLogBudget
{
    private readonly int? _max;
    private int _attempted;
    private bool _announced;

    private EventLogBudget(int? max, string announcement)
    {
        _max = max;
        Announcement = announcement;
    }

    /// <summary>What to write in place of the event that ran out of allowance.</summary>
    public string Announcement { get; }

    /// <summary>
    /// The mirrored diagnostics, whose volume is bounded by what went wrong rather than by the
    /// configuration.
    /// </summary>
    public static EventLogBudget ForDiagnostics() =>
        new(
            MaxDiagnosticsPerRun,
            $"More than {MaxDiagnosticsPerRun} diagnostics were attempted by a single run; the "
            + "rest were not written here. Run \"winlogrotate journal\" for the full record.");

    /// <summary>
    /// The notification digests, which have no count ceiling.
    /// </summary>
    /// <remarks>
    /// Not an oversight and not an exemption bolted onto the other allowance. The planner emits one
    /// digest per job plus one for the run, so the number is bounded by the configuration an
    /// operator wrote rather than by how badly the night went. Capping it at the diagnostics'
    /// fifty would silently drop the tail on an install with more jobs than that - which is the
    /// defect this type exists to remove, at a different scale.
    /// </remarks>
    public static EventLogBudget ForDigests() => new(null, string.Empty);

    /// <summary>Most mirrored diagnostics one invocation may write before it stops.</summary>
    public const int MaxDiagnosticsPerRun = 50;

    /// <summary>Spends one of the allowance, and says what that buys.</summary>
    public EventLogBudgetVerdict Take()
    {
        if (_max is not { } max || _attempted < max)
        {
            _attempted++;
            return EventLogBudgetVerdict.Write;
        }

        if (_announced)
        {
            return EventLogBudgetVerdict.Drop;
        }

        _announced = true;
        return EventLogBudgetVerdict.Announce;
    }
}

/// <summary>Turns a budget's verdict into what the caller is told.</summary>
/// <remarks>
/// <para>
/// Separate, pure, and unannotated on purpose. The defect this milestone exists to remove was that
/// the announcement's success became the caller's answer, and the enum alone does not prevent that
/// - a single ternary in the writer would restore it and compile cleanly. This is the mapping
/// stated once, as a total function over the verdicts, where a test on either leg can assert that
/// <see cref="EventLogBudgetVerdict.Announce"/> is never a success.
/// </para>
/// <para>
/// What remains beyond its reach is one line of wiring per arm inside the writer, on a path no
/// test leg can execute because no event source is registered on either.
/// </para>
/// </remarks>
internal static class EventLogOutcomes
{
    /// <summary>What the caller is told, given what the budget decided and whether the write went.</summary>
    public static EventLogOutcome For(EventLogBudgetVerdict verdict, bool reported) => verdict switch
    {
        EventLogBudgetVerdict.Write => reported ? EventLogOutcome.Written : EventLogOutcome.Refused,

        // Both, and without consulting `reported`. Something may well have been written - the
        // announcement - but it was not this caller's event, and the whole defect was treating
        // those as the same thing.
        EventLogBudgetVerdict.Announce or EventLogBudgetVerdict.Drop => EventLogOutcome.OverBudget,

        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "unknown verdict"),
    };
}
