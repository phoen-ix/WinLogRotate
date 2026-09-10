using Shouldly;
using WinLogRotate.Core.Compression;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Engine;
using WinLogRotate.Core.Globbing;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// Which files a rotate job is allowed to consider its own.
/// </summary>
/// <remarks>
/// This is the code that decides what may be deleted, so these are the tests that matter most in
/// the milestone. The planner shifts only archives whose index it recognises - but its maxage pass
/// deletes every archive it was handed, on age alone. A file discovery cannot identify is
/// therefore not ignored, it is destroyed.
/// </remarks>
public sealed class LogSeriesTests
{
    /// <summary>A file system that is a dictionary, so discovery is testable without one.</summary>
    private sealed class FakeFiles(params string[] paths) : IArchiveSource
    {
        private readonly Dictionary<string, MatchedFile> _files = paths.ToDictionary(
            p => p,
            p => new MatchedFile
            {
                Path = p,
                Length = 10,
                LastWriteUtc = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
            },
            StringComparer.OrdinalIgnoreCase);

        public List<string> Probed { get; } = [];

        public MatchedFile? Find(string path)
        {
            Probed.Add(path);
            return _files.GetValueOrDefault(path);
        }

        public IReadOnlyList<MatchedFile> Glob(string pattern) =>
            [.. _files.Values.Where(f => Globbing.Glob.IsMatch(f.Path, pattern))];
    }

    private static EffectiveJob Job(
        bool dateExt = false, int rotate = 5, int? maxAge = null,
        CompressType compress = CompressType.None, string? oldDir = null) => new()
        {
            Name = "app",
            Kind = JobKind.Rotate,
            Paths = [@"C:\logs\*.log"],
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
            DateExt = dateExt,
            DateFormat = "-yyyy-MM-dd",
            MissingOk = false,
            NotIfEmpty = true,
            OldDir = oldDir,
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

    private static MatchedFile Live(string path = @"C:\logs\app.log") => new()
    {
        Path = path,
        Length = 100,
        LastWriteUtc = new DateTimeOffset(2026, 9, 9, 0, 0, 0, TimeSpan.Zero),
    };

    private static string[] Found(IReadOnlyList<LogGeneration> generations) =>
        [.. generations.Single().Archives.Select(a => a.Path)];

    // ---- the trap ----------------------------------------------------------------------------

    /// <summary>
    /// A file that is not an archive of this log is never handed to the planner.
    /// </summary>
    /// <remarks>
    /// Each of these begins with the log's own name, so a prefix match would take every one of
    /// them. The consequence is not that they sort oddly - it is that maxage deletes them.
    /// </remarks>
    [Fact]
    public void AnUnrelatedFileBesideTheLogIsNeverDiscovered()
    {
        var files = new FakeFiles(
            @"C:\logs\app.log",
            @"C:\logs\app.log.1",
            @"C:\logs\app.log.old",
            @"C:\logs\app.log.bak",
            @"C:\logs\app.log.1.tmp",
            @"C:\logs\app.logger.txt",
            @"C:\logs\app.log.save-before-upgrade");

        Found(LogSeries.Discover(Job(), [Live()], files))
            .ShouldBe([@"C:\logs\app.log.1"]);
    }

    /// <summary>
    /// And the consequence, asserted directly: none of them is deleted.
    /// </summary>
    /// <remarks>
    /// The test above would still pass against a discovery that merely ordered the strays
    /// differently. This is the one that fails if they reach the planner at all, because maxage
    /// keys on the file's own timestamp and every stray here is old.
    /// </remarks>
    [Fact]
    public void AnUnrelatedFileBesideTheLogIsNeverDeletedByMaxAge()
    {
        var files = new FakeFiles(
            @"C:\logs\app.log",
            @"C:\logs\app.log.old",
            @"C:\logs\app.log.bak",
            @"C:\logs\app.log.save-before-upgrade");

        var job = Job(maxAge: 7);
        var generations = LogSeries.Discover(job, [Live()], files);

        var plan = RotateJobPlanner.Plan(
            job, generations,
            new Dictionary<string, DueVerdict>
            {
                [@"C:\logs\app.log"] = new()
                {
                    Due = true,
                    Reason = DueReason.Scheduled,
                    Explanation = "due",
                },
            },
            new DateTimeOffset(2026, 9, 9, 3, 0, 0, TimeSpan.Zero));

        plan.Operations
            .Where(o => o.Action == PlannedAction.Delete)
            .Select(o => o.Source)
            .ShouldBeEmpty("a file the job did not write is not the job's to delete");
    }

    [Fact]
    public void ADatedArchiveWithAnUnparseableStampIsNotOurs()
    {
        // The dateext glob ends in "*" so it catches the one delaycompress left uncompressed -
        // and that same wildcard catches anything else somebody parked beside them.
        var files = new FakeFiles(
            @"C:\logs\app.log",
            @"C:\logs\app.log-2026-09-08",
            @"C:\logs\app.log-2026-09-08.bak",
            @"C:\logs\app.log-not-a-date");

        Found(LogSeries.Discover(Job(dateExt: true), [Live()], files))
            .ShouldBe([@"C:\logs\app.log-2026-09-08"]);
    }

    // ---- what it does find -------------------------------------------------------------------

    [Fact]
    public void TheWholeNumberedChainIsFound()
    {
        var files = new FakeFiles(
            @"C:\logs\app.log",
            @"C:\logs\app.log.1", @"C:\logs\app.log.2", @"C:\logs\app.log.3");

        Found(LogSeries.Discover(Job(), [Live()], files)).Length.ShouldBe(3);
    }

    /// <summary>
    /// A gap in the chain does not hide everything above it.
    /// </summary>
    /// <remarks>
    /// Somebody deletes app.log.2 by hand. Stopping at the first miss would leave 3, 4 and 5
    /// invisible to both the shift and to retention - so they would never move and never be
    /// cleaned, and the chain would grow without bound.
    /// </remarks>
    [Fact]
    public void AGapInTheChainDoesNotHideTheGenerationsAboveIt()
    {
        var files = new FakeFiles(
            @"C:\logs\app.log",
            @"C:\logs\app.log.1", @"C:\logs\app.log.3", @"C:\logs\app.log.4");

        Found(LogSeries.Discover(Job(rotate: 5), [Live()], files)).Length.ShouldBe(3);
    }

    /// <summary>
    /// Both spellings of each generation, because delaycompress leaves exactly one uncompressed.
    /// </summary>
    [Fact]
    public void CompressedAndUncompressedGenerationsAreBothFound()
    {
        var files = new FakeFiles(
            @"C:\logs\app.log",
            @"C:\logs\app.log.1",
            @"C:\logs\app.log.2.zip");

        Found(LogSeries.Discover(Job(compress: CompressType.Zip), [Live()], files)).Length.ShouldBe(2);
    }

    /// <summary>
    /// Generations past the retention window are found, so they can be cleaned up.
    /// </summary>
    /// <remarks>
    /// An operator who lowers rotate from 14 to 5 leaves app.log.6 through app.log.14 behind.
    /// Nothing else will ever tidy them, so stopping the search at the window would strand them
    /// on disk for ever.
    /// </remarks>
    [Fact]
    public void StragglersFromAReducedRotateCountAreStillFound()
    {
        var files = new FakeFiles(
            @"C:\logs\app.log",
            @"C:\logs\app.log.1", @"C:\logs\app.log.2", @"C:\logs\app.log.3",
            @"C:\logs\app.log.4", @"C:\logs\app.log.5", @"C:\logs\app.log.6",
            @"C:\logs\app.log.7");

        Found(LogSeries.Discover(Job(rotate: 3), [Live()], files)).Length.ShouldBe(7);
    }

    [Fact]
    public void ArchivesAreFoundInOldDirWhenOneIsSet()
    {
        var files = new FakeFiles(
            @"C:\logs\app.log",
            @"C:\logs\archive\app.log.1",
            @"C:\logs\app.log.2");

        // Only the one in olddir: with olddir set, that is where this job writes and therefore
        // the only place its own archives can be.
        Found(LogSeries.Discover(Job(oldDir: "archive"), [Live()], files))
            .ShouldBe([@"C:\logs\archive\app.log.1"]);
    }

    // ---- the cost of looking -----------------------------------------------------------------

    /// <summary>
    /// Probing stops rather than walking to the cap on every log.
    /// </summary>
    /// <remarks>
    /// A job matching two hundred logs runs this two hundred times. Following the chain to
    /// MaxNumberedProbe each time would be two hundred thousand existence checks per run.
    /// </remarks>
    [Fact]
    public void ProbingStopsOnceTheChainRunsOut()
    {
        var files = new FakeFiles(@"C:\logs\app.log", @"C:\logs\app.log.1");

        LogSeries.Discover(Job(rotate: 3), [Live()], files);

        files.Probed.Count.ShouldBeLessThan(10);
        files.Probed.Count.ShouldBeGreaterThan(2, "the whole retention window is still checked");
    }

    [Fact]
    public void ALogWithNoArchivesYetIsStillAGeneration()
    {
        // The first ever rotation. The live log has to reach the planner with an empty archive
        // list rather than being dropped.
        var generations = LogSeries.Discover(Job(), [Live()], new FakeFiles(@"C:\logs\app.log"));

        generations.ShouldHaveSingleItem().Archives.ShouldBeEmpty();
        generations[0].Live.Path.ShouldBe(@"C:\logs\app.log");
    }
}
