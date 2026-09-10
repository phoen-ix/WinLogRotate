using Shouldly;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Engine;
using WinLogRotate.Core.Globbing;
using WinLogRotate.Core.Safety;
using Xunit;

namespace WinLogRotate.Core.Tests;

public class RotatePlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 3, 0, 0, TimeSpan.Zero);

    private static MatchedFile F(string name, int daysOld = 0, long bytes = 5000) => new()
    {
        Path = @"C:\logs\" + name,
        Length = bytes,
        LastWriteUtc = Now.AddDays(-daysOld),
    };

    private static EffectiveJob Job(
        int rotate = 4, bool compress = true, bool delayCompress = false,
        bool dateExt = false, int? maxAge = null, string? oldDir = null,
        LockStrategy strategy = LockStrategy.Rename, int start = 1) => new()
        {
            Name = "app",
            Kind = JobKind.Rotate,
            Paths = [@"C:\logs\app.log"],
            Enabled = true,
            Schedule = Schedule.Daily,
            Weekday = 0,
            MonthDay = 0,
            Rotate = rotate,
            Start = start,
            MaxAge = maxAge,
            MinAge = null,
            MinSize = null,
            MaxSize = null,
            SizeThreshold = 1 << 20,
            Compress = compress,
            CompressType = compress ? CompressType.Gzip : CompressType.None,
            DelayCompress = delayCompress,
            DateExt = dateExt,
            DateFormat = "-yyyyMMdd",
            MissingOk = false,
            NotIfEmpty = true,
            OldDir = oldDir,
            CreateOldDir = false,
            LockStrategy = strategy,
            LiveFiles = 1,
            MaxFiles = 1000,
            RetryCount = 5,
            RetryIntervalMs = 100,
            PreRotate = [],
            PostRotate = [],
            HookTimeout = TimeSpan.FromSeconds(60),
            AllowDangerous = [],
        };

    private static JobPlan PlanFor(
        EffectiveJob job, MatchedFile live, params MatchedFile[] archives)
    {
        var generation = new LogGeneration
        {
            Live = live,
            Archives = FileSeries.Order(archives, job.DateExt ? job.DateFormat : null),
        };

        var verdicts = new Dictionary<string, DueVerdict>
        {
            [live.Path] = new() { Due = true, Reason = DueReason.Scheduled, Explanation = "a new day has begun" },
        };

        return RotateJobPlanner.Plan(job, [generation], verdicts, Now);
    }

    [Fact]
    public void ALogThatIsNotDueIsSkippedWithTheReason()
    {
        var generation = new LogGeneration { Live = F("app.log"), Archives = [] };
        var verdicts = new Dictionary<string, DueVerdict>
        {
            [generation.Live.Path] = new()
            {
                Due = false,
                Reason = DueReason.NotDue,
                Explanation = "already rotated today",
            },
        };

        var plan = RotateJobPlanner.Plan(Job(), [generation], verdicts, Now);

        var op = plan.Operations.ShouldHaveSingleItem();
        op.Action.ShouldBe(PlannedAction.Skip);
        op.Reason.ShouldBe("already rotated today");
    }

    [Fact]
    public void TheLiveLogBecomesGenerationOneAndIsRecreated()
    {
        var plan = PlanFor(Job(), F("app.log"));

        var rename = plan.Operations.Single(o => o.Action == PlannedAction.Rename);
        WinPath.FileName(rename.Destination!).ShouldBe("app.log.1");

        // Without recreating it, the writer keeps appending to the file we just archived.
        plan.Operations.ShouldContain(o => o.Action == PlannedAction.Create);
    }

    /// <summary>
    /// The chain shifts from the highest index downward, so a move never lands on a file that
    /// has not itself been moved yet.
    /// </summary>
    [Fact]
    public void TheNumberedChainShiftsFromTheTopDown()
    {
        var plan = PlanFor(Job(rotate: 4), F("app.log"),
            F("app.log.1.gz"), F("app.log.2.gz"), F("app.log.3.gz"));

        var shifts = plan.Operations
            .Where(o => o.Action == PlannedAction.Rename && o.Source.Contains(".log.", StringComparison.Ordinal))
            .Select(o => WinPath.FileName(o.Source))
            .ToArray();

        shifts.ShouldBe(["app.log.3.gz", "app.log.2.gz", "app.log.1.gz"]);
    }

    [Fact]
    public void TheGenerationPastTheRetentionCountIsDeleted()
    {
        var plan = PlanFor(Job(rotate: 3), F("app.log"),
            F("app.log.1.gz"), F("app.log.2.gz"), F("app.log.3.gz"));

        var deleted = plan.Operations.Where(o => o.Action == PlannedAction.Delete).ShouldHaveSingleItem();
        WinPath.FileName(deleted.Source).ShouldBe("app.log.3.gz");
        deleted.Reason.ShouldContain("rotate = 3");
    }

    // ---- delaycompress ------------------------------------------------------------------

    /// <summary>
    /// Without delaycompress the newest generation is compressed in the same pass.
    /// </summary>
    [Fact]
    public void CompressionHappensImmediatelyByDefault()
    {
        var plan = PlanFor(Job(delayCompress: false), F("app.log"));

        plan.Operations.ShouldContain(o =>
            o.Action == PlannedAction.Compress && o.Source.EndsWith("app.log.1", StringComparison.Ordinal));
    }

    /// <summary>
    /// With delaycompress the newest generation stays uncompressed for one cycle, so a writer
    /// still holding the renamed file is not compressed out from under it. That is the entire
    /// purpose of the directive.
    /// </summary>
    [Fact]
    public void DelayCompressLeavesTheNewestGenerationUncompressed()
    {
        var plan = PlanFor(Job(delayCompress: true), F("app.log"));

        var rename = plan.Operations.Single(o => o.Action == PlannedAction.Rename);
        rename.Destination.ShouldNotEndWith(".gz");
        plan.Operations.ShouldNotContain(o =>
            o.Action == PlannedAction.Compress && o.Source.EndsWith("app.log.1", StringComparison.Ordinal));
    }

    /// <summary>
    /// The deferred file is compressed on the following run, before the shift, so the chain is
    /// uniformly named by the time anything moves.
    /// </summary>
    [Fact]
    public void TheDeferredGenerationIsCompressedOnTheNextRun()
    {
        var plan = PlanFor(Job(delayCompress: true), F("app.log"),
            F("app.log.1"), F("app.log.2.gz"));

        var catchUp = plan.Operations.First(o => o.Action == PlannedAction.Compress);
        WinPath.FileName(catchUp.Source).ShouldBe("app.log.1");
        catchUp.Reason.ShouldContain("delaycompress");
    }

    // ---- dateext ------------------------------------------------------------------------

    [Fact]
    public void DateExtNamesTheArchiveByDate()
    {
        var plan = PlanFor(Job(dateExt: true), F("app.log"));

        var rename = plan.Operations.Single(o => o.Action == PlannedAction.Rename);
        WinPath.FileName(rename.Destination!).ShouldStartWith("app.log-20260907");
    }

    /// <summary>
    /// dateext refuses to overwrite an existing archive - unlike numbered rotation, which
    /// replaces silently. Rotating twice inside one date period is therefore refused rather
    /// than losing the earlier archive.
    /// </summary>
    [Fact]
    public void DateExtRefusesToOverwriteAnExistingArchive()
    {
        var plan = PlanFor(Job(dateExt: true), F("app.log"), F("app.log-20260907.gz"));

        var op = plan.Operations.ShouldHaveSingleItem();
        op.Action.ShouldBe(PlannedAction.Skip);
        op.Reason.ShouldContain("never overwrites");
    }

    [Fact]
    public void DateExtRetentionKeepsTheNewestByParsedDate()
    {
        var plan = PlanFor(Job(dateExt: true, rotate: 2), F("app.log"),
            F("app.log-20260905.gz", 2), F("app.log-20260904.gz", 3), F("app.log-20260903.gz", 4));

        var deleted = plan.Operations
            .Where(o => o.Action == PlannedAction.Delete)
            .Select(o => WinPath.FileName(o.Source))
            .ToArray();

        deleted.ShouldBe(["app.log-20260903.gz"]);
    }

    // ---- other directives ----------------------------------------------------------------

    [Fact]
    public void MaxAgeDeletesOldArchivesOnTopOfTheCount()
    {
        var plan = PlanFor(Job(rotate: 10, maxAge: 30), F("app.log"),
            F("app.log.1.gz", 1), F("app.log.2.gz", 45));

        plan.Operations
            .Where(o => o.Action == PlannedAction.Delete)
            .ShouldHaveSingleItem()
            .Reason.ShouldContain("maxage = 30");
    }

    [Fact]
    public void OldDirRedirectsArchivesButNotTheLiveLog()
    {
        var plan = PlanFor(Job(oldDir: @"C:\archive"), F("app.log"));

        var rename = plan.Operations.Single(o => o.Action == PlannedAction.Rename);
        rename.Source.ShouldBe(@"C:\logs\app.log");
        rename.Destination.ShouldStartWith(@"C:\archive");
    }

    [Fact]
    public void ARelativeOldDirResolvesAgainstTheLogsOwnDirectory()
    {
        var plan = PlanFor(Job(oldDir: "old"), F("app.log"));

        plan.Operations.Single(o => o.Action == PlannedAction.Rename)
            .Destination.ShouldStartWith(@"C:\logs\old");
    }

    /// <summary>
    /// Under copytruncate the inode never changes, so there is nothing to recreate - emitting
    /// a Create there would truncate the file the writer is still using.
    /// </summary>
    [Fact]
    public void CopyTruncateDoesNotRecreateTheLog()
    {
        var plan = PlanFor(Job(strategy: LockStrategy.CopyTruncate), F("app.log"));

        plan.Operations.ShouldContain(o => o.Action == PlannedAction.CopyTruncate);
        plan.Operations.ShouldNotContain(o => o.Action == PlannedAction.Create);
    }

    [Fact]
    public void StartChangesTheFirstIndex()
    {
        var plan = PlanFor(Job(start: 0), F("app.log"));

        WinPath.FileName(plan.Operations.Single(o => o.Action == PlannedAction.Rename).Destination!)
            .ShouldStartWith("app.log.0");
    }

    [Fact]
    public void CompressionCanBeOffEntirely()
    {
        var plan = PlanFor(Job(compress: false), F("app.log"));

        plan.Operations.ShouldNotContain(o => o.Action == PlannedAction.Compress);
    }
}

public class ArchiveNamingTests
{
    private static EffectiveJob Job(bool dateExt = false, string format = "-yyyyMMdd") => new()
    {
        Name = "app",
        Kind = JobKind.Rotate,
        Paths = ["x"],
        Enabled = true,
        Schedule = Schedule.Daily,
        Weekday = 0,
        MonthDay = 0,
        Rotate = 4,
        Start = 1,
        MaxAge = null,
        MinAge = null,
        MinSize = null,
        MaxSize = null,
        SizeThreshold = 1 << 20,
        Compress = true,
        CompressType = CompressType.Gzip,
        DelayCompress = false,
        DateExt = dateExt,
        DateFormat = format,
        MissingOk = false,
        NotIfEmpty = true,
        OldDir = null,
        CreateOldDir = false,
        LockStrategy = LockStrategy.Rename,
        LiveFiles = 1,
        MaxFiles = 1000,
        RetryCount = 5,
        RetryIntervalMs = 100,
        PreRotate = [],
        PostRotate = [],
        HookTimeout = TimeSpan.FromSeconds(60),
        AllowDangerous = [],
    };

    /// <summary>
    /// The derived glob needs character classes, which Win32 wildcards do not have - one of
    /// the reasons the matcher is ours rather than the platform's.
    /// </summary>
    [Fact]
    public void TheDateExtGlobUsesDigitClasses()
    {
        var glob = ArchiveNaming.DateExtGlob(Job(dateExt: true), @"C:\logs\app.log");

        glob.ShouldContain("[0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]");
        glob.ShouldContain("app.log-");
    }

    [Fact]
    public void TheGlobMatchesWhatTheNamerProduces()
    {
        var job = Job(dateExt: true);
        var produced = ArchiveNaming.FirstRotation(job, @"C:\logs\app.log",
            new DateTimeOffset(2026, 9, 7, 3, 0, 0, TimeSpan.Zero));

        Glob.IsMatch(produced, ArchiveNaming.DateExtGlob(job, @"C:\logs\app.log")).ShouldBeTrue();
    }
}
