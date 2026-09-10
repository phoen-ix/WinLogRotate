using Shouldly;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Engine;
using WinLogRotate.Core.Globbing;
using WinLogRotate.Core.Safety;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// The manage planner is pure - files in, intentions out - so the whole of it is testable
/// without touching a disk.
/// </summary>
public class ManagePlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 3, 0, 0, TimeSpan.Zero);

    private static MatchedFile File(string name, int daysOld = 0, long bytes = 1_000_000) => new()
    {
        Path = @"C:\inetpub\logs\LogFiles\W3SVC1\" + name,
        Length = bytes,
        LastWriteUtc = Now.AddDays(-daysOld),
    };

    private static EffectiveJob Job(
        int rotate = 30, int? maxAge = null, int liveFiles = 1,
        CompressType compress = CompressType.Zip) => new()
        {
            Name = "IIS W3SVC1",
            Kind = JobKind.Manage,
            Paths = [@"C:\inetpub\logs\LogFiles\W3SVC1\u_ex*.log"],
            Enabled = true,
            Schedule = Schedule.Daily,
            Weekday = 0,
            MonthDay = 0,
            Rotate = rotate,
            Start = 1,
            MaxAge = maxAge,
            MinAge = null,
            MinSize = null,
            MaxSize = null,
            SizeThreshold = 1 << 20,
            Compress = compress != CompressType.None,
            CompressType = compress,
            DelayCompress = false,
            DateExt = false,
            DateFormat = "-yyyyMMdd",
            MissingOk = true,
            NotIfEmpty = true,
            OldDir = null,
            CreateOldDir = false,
            LockStrategy = LockStrategy.Rename,
            LiveFiles = liveFiles,
            MaxFiles = 1000,
            RetryCount = 5,
            RetryIntervalMs = 100,
            PreRotate = [],
            PostRotate = [],
            HookTimeout = TimeSpan.FromSeconds(60),
            AllowDangerous = [],
        };

    /// <summary>
    /// The single most important property of this job kind. IIS has no way to reopen its log
    /// file - appcmd site stop/start does not release the W3SVC handle, only iisreset does -
    /// so touching the live file is never acceptable.
    /// </summary>
    [Fact]
    public void TheNewestFileIsNeverTouched()
    {
        var files = new[]
        {
            File("u_ex260907.log"),
            File("u_ex260906.log", 1),
            File("u_ex260905.log", 2),
        };

        var plan = ManageJobPlanner.Plan(Job(), files, Now);

        var newest = plan.Operations.Single(o => o.Source.EndsWith("u_ex260907.log", StringComparison.Ordinal));
        newest.Action.ShouldBe(PlannedAction.Skip);
        newest.Reason.ShouldContain("still writing");

        plan.Destructive.ShouldNotContain(o => o.Source.EndsWith("u_ex260907.log", StringComparison.Ordinal));
    }

    [Fact]
    public void FinishedFilesAreCompressed()
    {
        var files = new[] { File("u_ex260907.log"), File("u_ex260906.log", 1), File("u_ex260905.log", 2) };

        var plan = ManageJobPlanner.Plan(Job(), files, Now);

        plan.Operations
            .Where(o => o.Action == PlannedAction.Compress)
            .Select(o => WinPath.FileName(o.Source))
            .ShouldBe(["u_ex260906.log", "u_ex260905.log"], ignoreOrder: true);
    }

    [Fact]
    public void AlreadyCompressedFilesAreLeftAlone()
    {
        var files = new[] { File("u_ex260907.log"), File("u_ex260906.log.zip", 1) };

        ManageJobPlanner.Plan(Job(), files, Now)
            .Operations.ShouldNotContain(o => o.Action == PlannedAction.Compress);
    }

    [Fact]
    public void RetentionKeepsTheNewestN()
    {
        var files = Enumerable.Range(0, 10)
            .Select(i => File($"u_ex2609{(7 - i):00}.log.zip", i))
            .ToArray();

        var plan = ManageJobPlanner.Plan(Job(rotate: 3), files, Now);

        // One live file plus three retained; the remaining six go.
        plan.Operations.Count(o => o.Action == PlannedAction.Delete).ShouldBe(6);
        plan.Operations.Where(o => o.Action == PlannedAction.Delete)
            .ShouldAllBe(o => o.Reason.Contains("rotate = 3"));
    }

    /// <summary>
    /// Compressing a multi-gigabyte log and then deleting it is a slow way to achieve nothing.
    /// Retention therefore runs first, and condemned files are excluded from compression.
    /// </summary>
    [Fact]
    public void AFileAboutToBeDeletedIsNotCompressedFirst()
    {
        var files = Enumerable.Range(0, 6).Select(i => File($"u_ex2609{(7 - i):00}.log", i)).ToArray();

        var plan = ManageJobPlanner.Plan(Job(rotate: 2), files, Now);

        var deleted = plan.Operations.Where(o => o.Action == PlannedAction.Delete).Select(o => o.Source).ToHashSet();
        var compressed = plan.Operations.Where(o => o.Action == PlannedAction.Compress).Select(o => o.Source);

        compressed.ShouldNotContain(p => deleted.Contains(p));
    }

    [Fact]
    public void MaxAgeDeletesByTheDateInTheName()
    {
        var files = new[]
        {
            File("u_ex260907.log"),
            File("u_ex260906.log", 1),
            File("u_ex260601.log", 98),   // older than 90 days
        };

        var plan = ManageJobPlanner.Plan(Job(rotate: 100, maxAge: 90), files, Now);

        var deleted = plan.Operations.Where(o => o.Action == PlannedAction.Delete).ShouldHaveSingleItem();
        deleted.Source.ShouldEndWith("u_ex260601.log");
        deleted.Reason.ShouldContain("maxage = 90");
    }

    [Fact]
    public void EveryDeletionCarriesTheRuleThatCausedIt()
    {
        var files = Enumerable.Range(0, 8).Select(i => File($"u_ex2609{(7 - i):00}.log.zip", i)).ToArray();

        var plan = ManageJobPlanner.Plan(Job(rotate: 2, maxAge: 5), files, Now);

        plan.Operations
            .Where(o => o.Action == PlannedAction.Delete)
            .ShouldAllBe(o => o.Reason.Length > 0 && o.Bytes > 0);
    }

    [Fact]
    public void MoreLiveFilesLeavesMoreAlone()
    {
        var files = Enumerable.Range(0, 5).Select(i => File($"u_ex2609{(7 - i):00}.log", i)).ToArray();

        ManageJobPlanner.Plan(Job(liveFiles: 3), files, Now)
            .Operations.Count(o => o.Action == PlannedAction.Skip).ShouldBe(3);
    }

    [Fact]
    public void NothingMatchedMeansNothingPlanned()
    {
        var plan = ManageJobPlanner.Plan(Job(), [], Now);

        plan.Operations.ShouldBeEmpty();
        plan.MatchedFiles.ShouldBe(0);
    }

    [Fact]
    public void CompressionCanBeTurnedOffEntirely()
    {
        var files = new[] { File("u_ex260907.log"), File("u_ex260906.log", 1) };

        ManageJobPlanner.Plan(Job(compress: CompressType.None), files, Now)
            .Operations.ShouldNotContain(o => o.Action == PlannedAction.Compress);
    }
}

public class FileSeriesTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 0, 0, 0, TimeSpan.Zero);

    private static MatchedFile F(string name, int daysOld = 0) => new()
    {
        Path = @"C:\logs\" + name,
        Length = 1000,
        LastWriteUtc = Now.AddDays(-daysOld),
    };

    [Fact]
    public void OrdersNewestFirstByTheDateInTheName()
    {
        // mtime is deliberately in the wrong order: a copy or a restore rewrites it, and the
        // name is the better evidence of when the log is actually from.
        var files = new[]
        {
            F("u_ex260905.log", 0),
            F("u_ex260907.log", 10),
            F("u_ex260906.log", 5),
        };

        FileSeries.Order(files).Select(a => WinPath.FileName(a.Path))
            .ShouldBe(["u_ex260907.log", "u_ex260906.log", "u_ex260905.log"]);
    }

    [Fact]
    public void OrdersByRotationIndexWhenThereIsNoDate()
    {
        var files = new[] { F("app.log.3"), F("app.log.1"), F("app.log.2") };

        FileSeries.Order(files).Select(a => WinPath.FileName(a.Path))
            .ShouldBe(["app.log.1", "app.log.2", "app.log.3"]);
    }

    [Fact]
    public void FallsBackToModificationTime()
    {
        var files = new[] { F("alpha.log", 5), F("beta.log", 1), F("gamma.log", 3) };

        FileSeries.Order(files).Select(a => WinPath.FileName(a.Path))
            .ShouldBe(["beta.log", "gamma.log", "alpha.log"]);
    }

    /// <summary>
    /// A lexical sort of dd-MM-yyyy names orders them by day-of-month, which for a retention
    /// pass means deleting the newest archives. Parsing the date is not an optimisation.
    /// </summary>
    [Fact]
    public void ParsesTheDateRatherThanComparingTheStringForFormatsThatDoNotSortLexically()
    {
        var files = new[] { F("app-2026-01-15.log"), F("app-2026-11-02.log"), F("app-2026-03-30.log") };

        FileSeries.Order(files, "yyyy-MM-dd").Select(a => WinPath.FileName(a.Path))
            .ShouldBe(["app-2026-11-02.log", "app-2026-03-30.log", "app-2026-01-15.log"]);
    }

    [Theory]
    [InlineData("app.log.1.gz", true)]
    [InlineData("app.log.1.zip", true)]
    [InlineData("app.log.1", false)]
    public void RecognisesCompressedArchives(string name, bool expected) =>
        FileSeries.Order([F(name)]).Single().IsCompressed.ShouldBe(expected);
}
