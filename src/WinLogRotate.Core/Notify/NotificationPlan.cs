using WinLogRotate.Contracts;
using WinLogRotate.Core.State;

namespace WinLogRotate.Core.Notify;

/// <summary>Why a message is being sent.</summary>
public enum NotifyReason
{
    /// <summary>A job that was healthy, or had never been reported on, is failing.</summary>
    NewFailure,

    /// <summary>Still failing, but a different failure.</summary>
    Changed,

    /// <summary>Still the same failure, and it has been long enough to say so again.</summary>
    Reminder,

    /// <summary>Nothing remains at or above the threshold.</summary>
    Recovered,

    /// <summary>What remains is below a threshold that has since been raised.</summary>
    Resolved,
}

/// <summary>
/// One line of a message: several diagnostics that are the same problem, counted.
/// </summary>
/// <remarks>
/// The unit of notification is the run, not the file. Four hundred locked files produce one line
/// reading <c>Access denied (x412)</c>, because a message with four hundred lines is a message
/// nobody reads twice.
/// </remarks>
public sealed record DigestLine
{
    public required Severity Severity { get; init; }
    public required string Code { get; init; }
    public required string Job { get; init; }

    /// <summary>The shared wording, taken from the first member of the group.</summary>
    public required string Text { get; init; }

    /// <summary>The directory, or the single path when the group has only one member.</summary>
    /// <remarks>For display only. Fingerprinting uses <see cref="DirectoryKey"/>.</remarks>
    public required string Where { get; init; }

    /// <summary>
    /// The canonical directory this group was gathered under, and the only location that feeds
    /// the fingerprint.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="Where"/>, which names the file when a group has exactly one
    /// member. Fingerprinting a filename means <c>u_ex260909.log</c> becoming
    /// <c>u_ex260910.log</c> at midnight is a brand new problem - a guaranteed daily false
    /// alarm on the most common Windows log job there is.
    /// </remarks>
    public required string DirectoryKey { get; init; }

    public required int Count { get; init; }

    /// <summary>The Win32 error, when one caused the group. Part of its identity.</summary>
    public int NativeError { get; init; }
}

/// <summary>One message that would be sent, and to where.</summary>
public sealed record PlannedNotification
{
    public required NotifyReason Reason { get; init; }

    /// <summary>The job this concerns, or <see cref="NotifyStateDocument.RunScope"/>.</summary>
    public required string Job { get; init; }

    /// <summary>The highest severity that triggered it.</summary>
    public required Severity Severity { get; init; }

    public required string Subject { get; init; }

    /// <summary>The lines at or above the threshold.</summary>
    public required IReadOnlyList<DigestLine> Lines { get; init; }

    /// <summary>
    /// Everything found, including what the threshold filtered out.
    /// </summary>
    /// <remarks>
    /// A recovery is triggered by the threshold but described without it, so the message can say
    /// "recovered - 3 warnings remain" rather than implying the machine is clean. The trigger
    /// answers "is this worth waking somebody"; the body answers "what is actually true".
    /// </remarks>
    public required IReadOnlyList<DigestLine> Context { get; init; }

    public required string Fingerprint { get; init; }

    /// <summary>When this job was first seen failing, for "failing since".</summary>
    public DateTimeOffset? FailingSince { get; init; }

    /// <summary>The state to store if, and only if, this is delivered to at least one channel.</summary>
    public required JobNotifyState NextState { get; init; }
}

/// <summary>
/// What a run decided to say, and what it decided not to.
/// </summary>
public sealed record NotificationPlan
{
    public required IReadOnlyList<PlannedNotification> Messages { get; init; }

    /// <summary>
    /// Why nothing was sent for something that might have warranted it.
    /// </summary>
    /// <remarks>
    /// Printed by <c>--dry-run --verbose</c> and by <c>notify status</c>. Being able to answer
    /// "why did I not get an email?" without reading the source is most of what makes a
    /// notification feature trustworthy.
    /// </remarks>
    public required IReadOnlyList<string> Suppressed { get; init; }

    /// <summary>
    /// Job state to record even though nothing is being sent.
    /// </summary>
    /// <remarks>
    /// The first run is the case this exists for: outcomes are recorded, nothing is sent, and
    /// the next genuine change notifies. Installing monitoring must not produce a wall of alerts
    /// about problems that were already there - that is how a source gets muted in week one.
    /// </remarks>
    public required IReadOnlyList<(string Job, JobNotifyState State)> Baseline { get; init; }

    public bool IsEmpty => Messages.Count == 0;

    public static NotificationPlan Nothing(params string[] why) => new()
    {
        Messages = [],
        Suppressed = why,
        Baseline = [],
    };
}
