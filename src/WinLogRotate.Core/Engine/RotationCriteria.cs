using WinLogRotate.Core.Configuration;

namespace WinLogRotate.Core.Engine;

/// <summary>Why a log is or is not due.</summary>
public enum DueReason
{
    NotDue,
    Scheduled,

    /// <summary>Size alone, ignoring the calendar.</summary>
    SizeThreshold,

    /// <summary>maxsize forced an early rotation.</summary>
    MaxSizeExceeded,

    Forced,
    FirstSighting,

    // Suppressions
    TooSmall,
    TooYoung,
    Empty,

    /// <summary>No strategy can touch this file, so nothing was planned for it.</summary>
    StrategyRefused,

    /// <summary>Its olddir cannot be written to, so there is nowhere for the archive to go.</summary>
    DestinationUnusable,
}

/// <summary>The verdict for one log file.</summary>
public sealed record DueVerdict
{
    public required bool Due { get; init; }
    public required DueReason Reason { get; init; }
    public required string Explanation { get; init; }

    /// <summary>
    /// The strategy this rotation will actually use, once <c>auto</c> has been resolved.
    /// </summary>
    /// <remarks>
    /// Carried on the verdict rather than looked up separately, because the runner already
    /// produces one of these per file and the planner already receives them. Null where the
    /// caller did not resolve a strategy, in which case the job's configured one stands - which
    /// keeps every existing construction site of this record compiling unchanged.
    /// </remarks>
    public LockStrategy? Strategy { get; init; }
}

/// <summary>
/// Decides whether a log is due, reproducing logrotate's rules including the surprising ones.
/// </summary>
/// <remarks>
/// <para>
/// The gate ordering is the part people get wrong, and it is load-bearing: the schedule sets
/// the initial answer, <c>maxsize</c> may force it on, and then <c>minsize</c>, <c>minage</c>
/// and <c>notifempty</c> may each turn it back off. Because <c>--force</c> only sets the
/// initial answer, it does <b>not</b> override any of those three. That surprises people, it
/// is exactly what upstream does, and a tool claiming to imitate logrotate has to match it.
/// </para>
/// <para>
/// The calendar comparisons are date-field comparisons, not elapsed-time ones. A daily log is
/// due because the day number changed, not because 24 hours passed - so a run at 23:58 and
/// another at 00:02 rotates twice, and a machine that was off for a week rotates once, not
/// seven times. At most one rotation per log per invocation, always.
/// </para>
/// </remarks>
public static class RotationCriteria
{
    public static DueVerdict Evaluate(
        EffectiveJob job,
        DateTimeOffset? lastRotated,
        DateTimeOffset now,
        long fileSize,
        DateTimeOffset fileModified,
        bool force)
    {
        // A log seen for the first time gets a baseline and nothing else, matching logrotate.
        // Anyone who has deleted a state file and wondered why nothing rotated that night has
        // met this rule.
        if (lastRotated is null)
        {
            return new DueVerdict
            {
                Due = false,
                Reason = DueReason.FirstSighting,
                Explanation = "first time this log has been seen; recording a baseline and waiting one interval",
            };
        }

        var last = lastRotated.Value;
        var (due, reason, explanation) = force
            ? (true, DueReason.Forced, "--force")
            : EvaluateSchedule(job, last, now, fileSize);

        // maxsize forces an early rotation, but only when the schedule is time-based: under a
        // size criterion the threshold already is the rule.
        if (!due && job.Schedule != Schedule.Size && job.MaxSize is { } maxSize && fileSize > maxSize)
        {
            due = true;
            reason = DueReason.MaxSizeExceeded;
            explanation = $"maxsize {maxSize:N0} exceeded ({fileSize:N0} bytes)";
        }

        // The three suppressions below run after --force, which is why --force cannot override
        // them. Upstream behaves the same way.
        if (due && job.MinSize is { } minSize && fileSize < minSize)
        {
            return new DueVerdict
            {
                Due = false,
                Reason = DueReason.TooSmall,
                Explanation = $"due, but minsize {minSize:N0} is not met ({fileSize:N0} bytes)",
            };
        }

        if (due && job.MinAge is { } minAge && (now - fileModified).TotalDays < minAge)
        {
            return new DueVerdict
            {
                Due = false,
                Reason = DueReason.TooYoung,
                Explanation = $"due, but the log was modified less than minage ({minAge} day(s)) ago",
            };
        }

        if (due && job.NotIfEmpty && fileSize == 0)
        {
            return new DueVerdict
            {
                Due = false,
                Reason = DueReason.Empty,
                Explanation = "due, but the log is empty and notifempty is set",
            };
        }

        return new DueVerdict { Due = due, Reason = reason, Explanation = explanation };
    }

    private static (bool Due, DueReason Reason, string Explanation) EvaluateSchedule(
        EffectiveJob job, DateTimeOffset last, DateTimeOffset now, long fileSize)
    {
        if (job.Schedule == Schedule.Size)
        {
            return fileSize >= job.SizeThreshold
                ? (true, DueReason.SizeThreshold, $"size {fileSize:N0} reached the threshold of {job.SizeThreshold:N0}")
                : (false, DueReason.NotDue, $"size {fileSize:N0} is below the threshold of {job.SizeThreshold:N0}");
        }

        // Date-only, so a rotation at 23:00 and a run at 01:00 the next day counts as one day
        // elapsed rather than two hours.
        var elapsedDays = (now.Date - last.Date).Days;

        return job.Schedule switch
        {
            Schedule.Hourly =>
                now.Hour != last.Hour || now.Date != last.Date
                    ? (true, DueReason.Scheduled, "a new hour has begun")
                    : (false, DueReason.NotDue, "still within the same hour"),

            Schedule.Daily =>
                now.Date != last.Date
                    ? (true, DueReason.Scheduled, "a new day has begun")
                    : (false, DueReason.NotDue, "already rotated today"),

            // Either a full week has passed, or it is the configured weekday and at least one
            // day has passed. weekday 7 means pure seven-day spacing, ignoring the weekday.
            Schedule.Weekly =>
                elapsedDays >= 7 || (job.Weekday != 7 && elapsedDays >= 1 && (int)now.DayOfWeek == job.Weekday)
                    ? (true, DueReason.Scheduled,
                        elapsedDays >= 7 ? "seven days have passed" : $"it is {now.DayOfWeek}")
                    : (false, DueReason.NotDue, $"{elapsedDays} day(s) since the last rotation"),

            Schedule.Monthly => EvaluateMonthly(job, last, now, elapsedDays),

            Schedule.Yearly =>
                now.Year != last.Year
                    ? (true, DueReason.Scheduled, "a new year has begun")
                    : (false, DueReason.NotDue, "already rotated this year"),

            _ => (false, DueReason.NotDue, "no schedule"),
        };
    }

    private static (bool, DueReason, string) EvaluateMonthly(
        EffectiveJob job, DateTimeOffset last, DateTimeOffset now, int elapsedDays)
    {
        // monthday 0 is upstream's default: the first run in a new calendar month, whatever
        // day of the month that happens to be.
        if (job.MonthDay == 0)
        {
            return now.Month != last.Month || now.Year != last.Year
                ? (true, DueReason.Scheduled, "a new month has begun")
                : (false, DueReason.NotDue, "already rotated this month");
        }

        if (elapsedDays < 1)
        {
            return (false, DueReason.NotDue, "already rotated today");
        }

        if (elapsedDays >= 31)
        {
            return (true, DueReason.Scheduled, "31 days have passed");
        }

        if (now.Day == job.MonthDay)
        {
            return (true, DueReason.Scheduled, $"it is day {job.MonthDay} of the month");
        }

        // The clause that makes monthday 31 work in February: on the last day of any month,
        // a monthday that never arrives is treated as having arrived.
        var lastDayOfMonth = DateTime.DaysInMonth(now.Year, now.Month);
        if (now.Day == lastDayOfMonth && job.MonthDay > lastDayOfMonth)
        {
            return (true, DueReason.Scheduled,
                $"day {job.MonthDay} does not exist in {now:MMMM}, so the last day of the month is used");
        }

        return (false, DueReason.NotDue, $"waiting for day {job.MonthDay}");
    }
}
