using Shouldly;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Engine;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// logrotate's scheduling rules, including the ones that surprise people. These are pure
/// arithmetic over an injected clock, so every edge case is reachable without waiting.
/// </summary>
public class RotationCriteriaTests
{
    private static EffectiveJob Job(
        Schedule schedule = Schedule.Daily, int weekday = 0, int monthDay = 0,
        long? minSize = null, long? maxSize = null, int? minAge = null,
        bool notIfEmpty = true, long sizeThreshold = 1 << 20) => new()
        {
            Name = "app",
            Kind = JobKind.Rotate,
            Paths = ["x"],
            Enabled = true,
            Schedule = schedule,
            Weekday = weekday,
            MonthDay = monthDay,
            Rotate = 7,
            Start = 1,
            MaxAge = null,
            MinAge = minAge,
            MinSize = minSize,
            MaxSize = maxSize,
            SizeThreshold = sizeThreshold,
            Compress = true,
            CompressType = CompressType.Zip,
            DelayCompress = false,
            DateExt = false,
            DateFormat = "-yyyyMMdd",
            MissingOk = false,
            NotIfEmpty = notIfEmpty,
            OldDir = null,
            CreateOldDir = false,
            LockStrategy = LockStrategy.Rename,
            LiveFiles = 1,
            MaxFiles = 1000,
            RetryCount = 5,
            RetryIntervalMs = 100,
            PreRotate = [],
            PostRotate = [],
            AllowDangerous = [],
        };

    private static DateTimeOffset At(int y, int m, int d, int h = 12, int min = 0) =>
        new(y, m, d, h, min, 0, TimeSpan.Zero);

    private static DueVerdict Check(
        EffectiveJob job, DateTimeOffset? last, DateTimeOffset now,
        long size = 5000, DateTimeOffset? modified = null, bool force = false) =>
        RotationCriteria.Evaluate(job, last, now, size, modified ?? now, force);

    // ---- first run ----------------------------------------------------------------------

    /// <summary>
    /// logrotate records a baseline and rotates nothing. Anyone who has deleted a state file
    /// and wondered why nothing happened that night has met this.
    /// </summary>
    [Fact]
    public void AFirstSightingIsNeverDue()
    {
        var verdict = Check(Job(), last: null, At(2026, 9, 7));

        verdict.Due.ShouldBeFalse();
        verdict.Reason.ShouldBe(DueReason.FirstSighting);
    }

    [Fact]
    public void EvenForceDoesNotRotateAFirstSighting() =>
        Check(Job(), last: null, At(2026, 9, 7), force: true).Due.ShouldBeFalse();

    // ---- daily --------------------------------------------------------------------------

    [Fact]
    public void DailyIsDueWhenTheCalendarDayChanges() =>
        Check(Job(), At(2026, 9, 6, 23), At(2026, 9, 7, 1)).Due.ShouldBeTrue();

    [Fact]
    public void DailyIsNotDueTwiceInOneDay() =>
        Check(Job(), At(2026, 9, 7, 1), At(2026, 9, 7, 23)).Due.ShouldBeFalse();

    /// <summary>
    /// Not an elapsed-24-hours test. Two minutes apart across midnight is a new day, and this
    /// is genuinely how logrotate behaves.
    /// </summary>
    [Fact]
    public void DailyRotatesTwoMinutesApartAcrossMidnight() =>
        Check(Job(), At(2026, 9, 6, 23, 58), At(2026, 9, 7, 0, 2)).Due.ShouldBeTrue();

    /// <summary>
    /// At most one rotation per log per invocation. A machine off for a month comes back and
    /// rotates once, not thirty times - the missed days are gone, not queued.
    /// </summary>
    [Fact]
    public void AMonthOfMissedRunsStillProducesOneRotation() =>
        Check(Job(), At(2026, 8, 7), At(2026, 9, 7)).Due.ShouldBeTrue();

    // ---- weekly -------------------------------------------------------------------------

    [Fact]
    public void WeeklyIsDueOnTheConfiguredWeekday()
    {
        // 2026-09-07 is a Monday.
        At(2026, 9, 7).DayOfWeek.ShouldBe(DayOfWeek.Monday);
        Check(Job(Schedule.Weekly, weekday: 1), At(2026, 9, 5), At(2026, 9, 7)).Due.ShouldBeTrue();
    }

    [Fact]
    public void WeeklyIsDueAfterSevenDaysWhateverTheWeekday() =>
        Check(Job(Schedule.Weekly, weekday: 0), At(2026, 8, 30), At(2026, 9, 7)).Due.ShouldBeTrue();

    /// <summary>weekday 7 means pure seven-day spacing rather than a particular day.</summary>
    [Fact]
    public void WeekdaySevenIgnoresTheDayOfTheWeek()
    {
        var job = Job(Schedule.Weekly, weekday: 7);
        Check(job, At(2026, 9, 6), At(2026, 9, 7)).Due.ShouldBeFalse();
        Check(job, At(2026, 8, 31), At(2026, 9, 7)).Due.ShouldBeTrue();
    }

    [Fact]
    public void WeeklyIsNotDueOnTheSameDayItRotated() =>
        Check(Job(Schedule.Weekly, weekday: 1), At(2026, 9, 7, 1), At(2026, 9, 7, 23)).Due.ShouldBeFalse();

    // ---- monthly ------------------------------------------------------------------------

    [Fact]
    public void MonthlyWithoutADayIsDueOnTheFirstRunOfANewMonth()
    {
        Check(Job(Schedule.Monthly), At(2026, 8, 31), At(2026, 9, 3)).Due.ShouldBeTrue();
        Check(Job(Schedule.Monthly), At(2026, 9, 3), At(2026, 9, 28)).Due.ShouldBeFalse();
    }

    [Fact]
    public void MonthlyWithADayWaitsForThatDay()
    {
        var job = Job(Schedule.Monthly, monthDay: 15);
        Check(job, At(2026, 9, 1), At(2026, 9, 14)).Due.ShouldBeFalse();
        Check(job, At(2026, 9, 1), At(2026, 9, 15)).Due.ShouldBeTrue();
    }

    /// <summary>
    /// The clause that makes monthday 31 usable at all: February has no 31st, so the last day
    /// of the month stands in. Without it such a job would never rotate in February.
    /// </summary>
    [Fact]
    public void MonthlyThirtyFirstFiresOnTheLastDayOfFebruary()
    {
        var job = Job(Schedule.Monthly, monthDay: 31);
        DateTime.DaysInMonth(2026, 2).ShouldBe(28);
        Check(job, At(2026, 2, 1), At(2026, 2, 28)).Due.ShouldBeTrue();
    }

    // ---- size ---------------------------------------------------------------------------

    [Fact]
    public void SizeIgnoresTheCalendarEntirely()
    {
        var job = Job(Schedule.Size, sizeThreshold: 1000);
        Check(job, At(2026, 9, 7, 1), At(2026, 9, 7, 2), size: 2000).Due.ShouldBeTrue();
        Check(job, At(2026, 1, 1), At(2026, 9, 7), size: 999).Due.ShouldBeFalse();
    }

    [Fact]
    public void MaxSizeForcesAnEarlyRotationOnATimeSchedule()
    {
        var job = Job(Schedule.Daily, maxSize: 1000);
        var verdict = Check(job, At(2026, 9, 7, 1), At(2026, 9, 7, 2), size: 5000);

        verdict.Due.ShouldBeTrue();
        verdict.Reason.ShouldBe(DueReason.MaxSizeExceeded);
    }

    // ---- the suppressions, and why --force cannot beat them ------------------------------

    /// <summary>
    /// The most surprising rule in logrotate, and deliberately reproduced: --force sets the
    /// initial answer, and minsize, minage and notifempty each run afterwards and can turn it
    /// back off. A tool claiming to imitate logrotate has to match this.
    /// </summary>
    [Fact]
    public void ForceDoesNotOverrideNotIfEmpty()
    {
        var verdict = Check(Job(), At(2026, 9, 1), At(2026, 9, 7), size: 0, force: true);

        verdict.Due.ShouldBeFalse();
        verdict.Reason.ShouldBe(DueReason.Empty);
    }

    [Fact]
    public void ForceDoesNotOverrideMinSize()
    {
        var verdict = Check(Job(minSize: 10_000), At(2026, 9, 1), At(2026, 9, 7), size: 500, force: true);

        verdict.Due.ShouldBeFalse();
        verdict.Reason.ShouldBe(DueReason.TooSmall);
    }

    [Fact]
    public void ForceDoesNotOverrideMinAge()
    {
        var verdict = Check(Job(minAge: 3), At(2026, 9, 1), At(2026, 9, 7),
            modified: At(2026, 9, 6), force: true);

        verdict.Due.ShouldBeFalse();
        verdict.Reason.ShouldBe(DueReason.TooYoung);
    }

    [Fact]
    public void ForceOtherwiseRotatesSomethingNotYetDue()
    {
        var verdict = Check(Job(), At(2026, 9, 7, 1), At(2026, 9, 7, 2), force: true);

        verdict.Due.ShouldBeTrue();
        verdict.Reason.ShouldBe(DueReason.Forced);
    }

    [Fact]
    public void AnEmptyLogRotatesWhenNotIfEmptyIsOff() =>
        Check(Job(notIfEmpty: false), At(2026, 9, 1), At(2026, 9, 7), size: 0).Due.ShouldBeTrue();

    // ---- clocks that misbehave -----------------------------------------------------------

    /// <summary>
    /// Austria springs forward at 02:00 on the last Sunday of March, so 02:30 does not exist
    /// that day. The comparison is on date fields rather than elapsed seconds, so the
    /// transition cannot produce a skipped or doubled rotation.
    /// </summary>
    [Fact]
    public void ADaylightSavingTransitionProducesExactlyOneRotation()
    {
        var beforeSpringForward = new DateTimeOffset(2026, 3, 28, 23, 30, 0, TimeSpan.FromHours(1));
        var afterSpringForward = new DateTimeOffset(2026, 3, 29, 3, 30, 0, TimeSpan.FromHours(2));

        Check(Job(), beforeSpringForward, afterSpringForward).Due.ShouldBeTrue();
        Check(Job(), afterSpringForward, afterSpringForward.AddHours(2)).Due.ShouldBeFalse();
    }

    /// <summary>
    /// A VM restored from a snapshot, or an NTP correction, can leave a "last rotated" stamp
    /// in the future. Treating that as due is the right call: parking until that date arrives
    /// would silently stop rotating for however long the clock was wrong.
    /// </summary>
    [Fact]
    public void AStampInTheFutureCountsAsDue() =>
        Check(Job(), last: At(2026, 12, 25), now: At(2026, 9, 7)).Due.ShouldBeTrue();
}
