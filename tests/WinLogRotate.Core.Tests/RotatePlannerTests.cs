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

    /// <summary>
    /// Plans against a whole directory, through the discovery the product really uses.
    /// </summary>
    /// <remarks>
    /// <see cref="PlanFor"/> hands the planner an archive list chosen by the test, which can only
    /// ever show that the planner copes with what the test thought of. This starts from the files
    /// and lets <see cref="LogSeries.Discover"/> decide what the job's archives are - so the input
    /// is one the product produces, not one an author believed in.
    /// </remarks>
    private static JobPlan PlanOver(EffectiveJob job, FakeFiles files)
    {
        var live = files.All.Single(f => WinPath.FileName(f.Path) == "app.log");

        var verdicts = new Dictionary<string, DueVerdict>
        {
            [live.Path] = new() { Due = true, Reason = DueReason.Scheduled, Explanation = "a new day has begun" },
        };

        return RotateJobPlanner.Plan(job, LogSeries.Discover(job, [live], files), verdicts, Now);
    }

    /// <summary>The directory the plan would leave behind.</summary>
    private static Outcome After(EffectiveJob job, FakeFiles files) =>
        Outcome.Of(PlanOver(job, files), files);

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
    /// The deferred file is compressed on the following run, and the shift moves what
    /// compression left behind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This asserted only the source of the first Compress operation, which is why it stayed green
    /// while the plan it described was unexecutable. <c>Compressor.Compress</c> deletes its source
    /// once the archive is durable, and the chain was read before that step - so the shift went on
    /// to emit <c>Rename app.log.1 -> app.log.2</c>, naming a file compression had consumed, and
    /// naming it to the uncompressed spelling because the entry still said it was not compressed.
    /// </para>
    /// <para>
    /// The consequence was not subtle: one failed operation and a non-zero exit every night, on
    /// every job with <c>delaycompress</c> and compression both on, with <c>app.log.1.gz</c>
    /// stranded at index 1 where the shift never reaches it and retention never counts it. The
    /// class remark promised the opposite - that the chain is uniformly named before anything
    /// moves - and now it is.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheDeferredGenerationIsCompressedOnTheNextRun()
    {
        var files = new FakeFiles(
            @"C:\logs\app.log", @"C:\logs\app.log.1", @"C:\logs\app.log.2.gz");

        var job = Job(rotate: 3, delayCompress: true);
        var plan = PlanOver(job, files);

        var catchUp = plan.Operations.First(o => o.Action == PlannedAction.Compress);
        WinPath.FileName(catchUp.Source).ShouldBe("app.log.1");
        catchUp.Reason.ShouldContain("delaycompress");

        var after = Outcome.Of(plan, files);

        // No operation named a file that was not there when the plan reached it.
        after.Missing.ShouldBeEmpty();

        // The deferred generation is the archive, under its compressed name, one index further up.
        after.Became("app.log.1").ShouldBe(["app.log.2.gz"]);
        after.Became("app.log.2.gz").ShouldBe(["app.log.3.gz"]);
        after.Became("app.log").ShouldBe(["app.log.1"]);

        after.Names.ShouldBe(["app.log", "app.log.1", "app.log.2.gz", "app.log.3.gz"]);
    }

    /// <summary>
    /// A generation retention is about to delete is not compressed on its way to the bin.
    /// </summary>
    /// <remarks>
    /// With <c>rotate = 1</c> the newest archive is also the one the count disposes of, so the
    /// catch-up compress and the delete would name the same file in the same pass - spending a
    /// full read and write of a multi-gigabyte archive to produce a file this plan then removes,
    /// every run. <c>ManageJobPlanner</c> already declines to compress a condemned file; this is
    /// the same rule on the numbered path.
    /// </remarks>
    [Fact]
    public void AGenerationAboutToBeDeletedIsNotCompressedFirst()
    {
        var files = new FakeFiles(@"C:\logs\app.log", @"C:\logs\app.log.1");

        var plan = PlanOver(Job(rotate: 1, delayCompress: true), files);

        plan.Operations.ShouldNotContain(o => o.Action == PlannedAction.Compress);
        plan.Operations.ShouldContain(o =>
            o.Action == PlannedAction.Delete && o.Source.EndsWith("app.log.1", StringComparison.Ordinal));
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

    /// <summary>
    /// maxage deletes the file it names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The age pass ran last, after the shift was already queued, and condemned the name the file
    /// had before the shift. Operations execute in list order, so the plan renamed the 45-day-old
    /// <c>app.log.2.gz</c> up to <c>.3.gz</c>, renamed yesterday's <c>app.log.1.gz</c> down into
    /// <c>.2.gz</c>, and then deleted <c>.2.gz</c> - destroying yesterday's archive while telling
    /// the operator, in the journal and in the envelope, that it had removed a file from a month
    /// ago. The month-old one survived.
    /// </para>
    /// <para>
    /// The subject of the delete operation is <c>app.log.2.gz</c> either way, which is exactly why
    /// the old assertion - the reason string alone - could not fail. What discriminates is the
    /// <b>order</b>, and the only way to see order is to ask which file is left.
    /// </para>
    /// </remarks>
    [Fact]
    public void MaxAgeDeletesOldArchivesOnTopOfTheCount()
    {
        var files = new FakeFiles(@"C:\logs\app.log")
            .Add(@"C:\logs\app.log.1.gz", Now.AddDays(-1))
            .Add(@"C:\logs\app.log.2.gz", Now.AddDays(-45));

        var job = Job(rotate: 10, maxAge: 30);
        var plan = PlanOver(job, files);

        var deleted = plan.Operations.Where(o => o.Action == PlannedAction.Delete).ShouldHaveSingleItem();
        deleted.Reason.ShouldContain("maxage = 30");

        var after = Outcome.Of(plan, files);

        // The old archive is gone and yesterday's is not. Under the defect this was the other way
        // round, with the same operation subject and the same reason string.
        after.Lost("app.log.2.gz").ShouldBeTrue("the 45-day-old archive is the one maxage condemned");
        after.Became("app.log.1.gz").ShouldBe(["app.log.2.gz"]);

        after.Missing.ShouldBeEmpty();
    }

    /// <summary>
    /// A file both rules condemn is deleted once, and says which rule took it.
    /// </summary>
    /// <remarks>
    /// The age pass used to dedupe against deletions already in the list, which worked only
    /// because it ran last. Deciding first empties that set, so the protection now runs the other
    /// way and the count pass skips what age has taken. Without it one file gets two delete
    /// operations, the second of which names a path that is no longer there.
    /// </remarks>
    [Fact]
    public void AFileBothRulesCondemnIsDeletedOnce()
    {
        var files = new FakeFiles(@"C:\logs\app.log")
            .Add(@"C:\logs\app.log.1.gz", Now.AddDays(-1))
            .Add(@"C:\logs\app.log.2.gz", Now.AddDays(-2))
            .Add(@"C:\logs\app.log.3.gz", Now.AddDays(-90));

        var job = Job(rotate: 3, maxAge: 30);
        var plan = PlanOver(job, files);

        var deleted = plan.Operations.Where(o => o.Action == PlannedAction.Delete).ShouldHaveSingleItem();
        WinPath.FileName(deleted.Source).ShouldBe("app.log.3.gz");
        deleted.Reason.ShouldContain("maxage", Case.Insensitive);

        Outcome.Of(plan, files).Missing.ShouldBeEmpty();
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

    // ---- the directory a plan leaves behind -------------------------------------------------

    /// <summary>
    /// An ordinary numbered rotation leaves the chain it promises.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The simplest case there is, asserted the way nothing in this file asserted anything until
    /// now: not that an operation of some kind exists, but that a named file ends up at a named
    /// place. Every test above stops at the plan, which is why a plan that renames a file it has
    /// already deleted, or deletes the newest archive while reporting the oldest, could pass all
    /// eighteen of them.
    /// </para>
    /// <para>
    /// This case the planner already gets right. It is here to show the model is faithful before
    /// anything rests on it - and it is the only fact in this section that was green when it was
    /// written.
    /// </para>
    /// </remarks>
    [Fact]
    public void APlainNumberedRotationLeavesTheChainItPromises()
    {
        var files = new FakeFiles(
            @"C:\logs\app.log", @"C:\logs\app.log.1", @"C:\logs\app.log.2");

        var after = After(Job(rotate: 4, compress: false), files);

        after.Names.ShouldBe(["app.log", "app.log.1", "app.log.2", "app.log.3"]);

        // Each generation moved up exactly one, and the live log became the first.
        after.Became("app.log").ShouldBe(["app.log.1"]);
        after.Became("app.log.1").ShouldBe(["app.log.2"]);
        after.Became("app.log.2").ShouldBe(["app.log.3"]);

        // The recreated log is not the one that was archived - it is a new empty file, and the
        // distinction is what lets a later assertion say which file a deletion really took.
        after.At(@"C:\logs\app.log")!.Origin.ShouldBeNull();

        after.Missing.ShouldBeEmpty();
        after.Clobbered.ShouldBeEmpty();
    }

    /// <summary>
    /// Two spellings of one generation are survivable, and both are kept.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>LogSeries</c> probes both spellings of every index on purpose, and
    /// <c>FileSeries.Classify</c> strips the compression suffix before reading the index, so
    /// <c>app.log.1</c> and <c>app.log.1.gz</c> both answer 1. The planner keyed a dictionary on
    /// that and <c>ToDictionary</c> threw <c>ArgumentException</c> - a type in no catch filter
    /// anywhere, so the whole invocation ended at exit 4 and abandoned every job after it.
    /// </para>
    /// <para>
    /// It arrives by ordinary means. <c>RunCommand</c>'s recovery path warns that a killed run may
    /// have left "an uncompressed archive about" and assures the operator the planners cope; the
    /// delaycompress chain produced the same pair every night on its own; and an operator who
    /// gzips an archive by hand produces it in one command.
    /// </para>
    /// <para>
    /// Driven from a directory rather than a handed-over archive list, because what is under test
    /// is that discovery and the planner disagree about what an index is. Feeding the pair in by
    /// hand would only show the planner is fragile.
    /// </para>
    /// </remarks>
    [Fact]
    public void BothSpellingsOfOneGenerationAreSurvivable()
    {
        var files = new FakeFiles(
            @"C:\logs\app.log", @"C:\logs\app.log.1", @"C:\logs\app.log.1.gz");

        var after = Should.NotThrow(() => After(Job(rotate: 3, compress: true), files));

        // Neither is chosen against: they are one generation as far as retention is concerned, so
        // they shift together and keep their own spellings.
        after.Became("app.log.1").ShouldBe(["app.log.2"]);
        after.Became("app.log.1.gz").ShouldBe(["app.log.2.gz"]);
        after.Lost("app.log.1").ShouldBeFalse();
        after.Lost("app.log.1.gz").ShouldBeFalse();
    }

    /// <summary>The operator is told, because the state is evidence something did not finish.</summary>
    [Fact]
    public void ADuplicatedGenerationIsReported()
    {
        var files = new FakeFiles(
            @"C:\logs\app.log", @"C:\logs\app.log.1", @"C:\logs\app.log.1.gz");

        var live = files.All.Single(f => WinPath.FileName(f.Path) == "app.log");
        var verdicts = new Dictionary<string, DueVerdict>
        {
            [live.Path] = new() { Due = true, Reason = DueReason.Scheduled, Explanation = "due" },
        };

        var job = Job(rotate: 3, compress: true);
        var said = new List<Contracts.CliDiagnostic>();

        RotateJobPlanner.Plan(job, LogSeries.Discover(job, [live], files), verdicts, Now, said.Add);

        var d = said.ShouldHaveSingleItem();
        d.Code.ShouldBe(Contracts.DiagnosticCode.DuplicateGeneration);
        d.Message.ShouldContain("app.log.1");
        d.Message.ShouldContain("app.log.1.gz");

        // It must not tell anyone to delete one of them - that is the judgement the planner
        // declined to make, and the remedy is where it would leak back in.
        d.Remedy.ShouldNotBeNull().ShouldNotContain("delete ", Case.Insensitive);
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
