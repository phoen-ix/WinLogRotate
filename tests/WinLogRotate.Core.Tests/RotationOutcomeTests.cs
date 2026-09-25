using Microsoft.Extensions.Time.Testing;
using Shouldly;
using WinLogRotate.Contracts;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Engine;
using WinLogRotate.Core.Hooks;
using WinLogRotate.Core.Journaling;
using WinLogRotate.Core.Safety;
using WinLogRotate.Core.State;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// What a run does when an operation fails part-way through a plan.
/// </summary>
/// <remarks>
/// <para>
/// Driven through <see cref="RotationRunner.Run"/> against a dictionary file system and a
/// <see cref="FakeApplier"/> that fails the operations a test names. <c>HookBracketTests</c> proves
/// the same bracket against real files and therefore only on Windows; this is the leg where an
/// operation can be made to fail on purpose, which no real disk offers on demand.
/// </para>
/// <para>
/// Every path is spelled as Windows would spell it, because <c>PathGuard</c> refuses anything
/// else - and because the state file keys on it.
/// </para>
/// </remarks>
public sealed class RotationOutcomeTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("winlogrotate-outcome-");
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 10, 3, 0, 0, TimeSpan.Zero));

    private const string Live = @"C:\logs\app.log";
    private const string Reload = @"command:C:\tools\reload.exe --now";

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); } catch (IOException) { }
    }

    private DateTimeOffset Now => _clock.GetUtcNow();

    /// <summary>A store in which the live log was rotated two days ago, so a daily job is due.</summary>
    private StateStore State()
    {
        var state = StateStore.Load(Path.Combine(_dir.FullName, "state.json"), out _);
        state.Set(Live, new PathState { Path = Live, LastRotated = Now.AddDays(-2) });
        return state;
    }

    private static EffectiveJob Job(int rotate = 4, bool compress = false, string[]? post = null) => new()
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
        MaxAge = null,
        MinAge = null,
        MinSize = null,
        MaxSize = null,
        SizeThreshold = 1 << 20,
        Compress = compress,
        CompressType = compress ? CompressType.Zip : CompressType.None,
        DelayCompress = false,
        DateExt = false,
        DateFormat = "-yyyyMMdd",
        MissingOk = false,
        NotIfEmpty = false,
        OldDir = null,
        CreateOldDir = false,
        LockStrategy = LockStrategy.Rename,
        LiveFiles = 1,
        MaxFiles = 1000,
        RetryCount = 5,
        RetryIntervalMs = 100,
        PreRotate = [],
        PostRotate = post ?? [],
        HookTimeout = TimeSpan.FromSeconds(30),
        AllowDangerous = [],
    };

    private RunReport Run(
        EffectiveJob job, FakeFiles files, FakeApplier applier, StateStore state, IHookHost? host = null,
        TimeProvider? clock = null) =>
        Run([job], files, applier, state, host, clock);

    private RunReport Run(
        EffectiveJob[] jobs, FakeFiles files, FakeApplier applier, StateStore state, IHookHost? host = null,
        TimeProvider? clock = null) =>
        new RotationRunner(
                new NullJournal(), new PathGuard(new GuardOptions()), state, clock ?? _clock,
                archiveSource: files, hookHost: host, hookGate: HookGate.Open,
                files: new FakeFileSource(files), applier: applier)
            .Run(
                new LoadedConfig
                {
                    Jobs = jobs,
                    Diagnostics = [],
                    Paths = InstallPaths.Resolve(_dir.FullName),
                    Quarantined = [],
                },
                new RunOptions());

    /// <summary>
    /// The run's calendar is the machine's, not Greenwich's.
    /// </summary>
    /// <remarks>
    /// The task fires at 03:00 local time. 03:00 on 1 September in a UTC+10 zone is 17:00 on
    /// 31 August in UTC, and the calendar used to be read in UTC: a daily job rotated at 03:00
    /// local on the 31st was "already rotated today", and a dateext archive carried the 31st.
    /// </remarks>
    [Fact]
    public void TheCalendarIsReadInLocalTime()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 31, 17, 0, 0, TimeSpan.Zero));
        clock.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("Test+10", TimeSpan.FromHours(10), "Test+10", "Test+10"));

        var files = new FakeFiles().Add(Live, bytes: 100);
        var state = StateStore.Load(Path.Combine(_dir.FullName, "state.json"), out _);
        state.Set(Live, new PathState
        {
            Path = Live,
            LastRotated = new DateTimeOffset(2026, 8, 31, 3, 0, 0, TimeSpan.FromHours(10)),
        });

        Run(Job() with { DateExt = true, DateFormat = "-yyyyMMdd" }, files, new FakeApplier(files), state, clock: clock);

        files.Exists(@"C:\logs\app.log-20260901").ShouldBeTrue("1 September in the machine's calendar, and due");
        files.Exists(@"C:\logs\app.log-20260831").ShouldBeFalse();
        state.Get(Live).ShouldNotBeNull().LastRotated.ShouldBe(clock.GetUtcNow(), "a clock is an instant whichever way it is written");
    }

    /// <summary>
    /// An exception nothing expected costs the job it happened in, and nothing else.
    /// </summary>
    /// <remarks>
    /// The executor's catch filter names the exceptions a file operation is expected to throw.
    /// Anything else - a zip container refusing a 1979 timestamp, a retry count of zero meeting
    /// RetryPolicy's argument check - left the runner's loop: the remaining jobs were never
    /// looked at, state was never saved so every log already rotated was due again tomorrow, and
    /// the process exited 4. Job A here throws on its first operation; job B must still rotate,
    /// and the clocks must still be written.
    /// </remarks>
    [Fact]
    public void AnUnexpectedExceptionCostsOneJobAndNotTheRun()
    {
        const string liveB = @"C:\logs\b\app.log";
        var files = new FakeFiles().Add(Live, bytes: 100).Add(liveB, bytes: 100);
        var applier = new FakeApplier(files)
            .Surprise(op => op.Source.StartsWith(@"C:\logs\app", StringComparison.Ordinal),
                _ => new InvalidOperationException("the container refused"));
        var state = State();
        state.Set(liveB, new PathState { Path = liveB, LastRotated = Now.AddDays(-2) });

        var jobA = Job();
        var jobB = Job() with { Name = "b", Paths = [@"C:\logs\b\*.log"] };

        var report = Run([jobA, jobB], files, applier, state);

        files.Exists(@"C:\logs\b\app.log.1").ShouldBeTrue("the second job still ran");
        state.Get(liveB).ShouldNotBeNull().LastRotated.ShouldBe(Now, "and its clock was written");
        File.Exists(Path.Combine(_dir.FullName, "state.json")).ShouldBeTrue("state was saved");

        report.Failed.ShouldBeGreaterThan(0);
        report.Diagnostics.ShouldContain(d =>
            d.Code == DiagnosticCode.RotationFailed && d.Job == "app"
            && d.Message.Contains("InvalidOperationException", StringComparison.Ordinal));
    }

    /// <summary>
    /// A shift that succeeded is not a rotation when the live log's own rename failed.
    /// </summary>
    /// <remarks>
    /// The share violation that stops a live log moving does not stop its archives moving - nothing
    /// holds <c>app.log.1</c> open - so the failure case is exactly the one in which the shifts
    /// succeed. Counting those renames as rotations ran the postrotate hook, telling the writer to
    /// reopen a log that was still exactly where it had been.
    /// </remarks>
    [Fact]
    public void AShiftThatSucceededIsNotARotationWhenTheLiveRenameFailed()
    {
        var files = new FakeFiles().Add(Live, bytes: 100).Add(@"C:\logs\app.log.1", bytes: 50);
        var applier = new FakeApplier(files).Fail(op => op.IsLiveRotation);
        var host = new FakeHookHost();
        var state = State();

        var report = Run(Job(post: [Reload]), files, applier, state, host);

        files.Exists(@"C:\logs\app.log.2").ShouldBeTrue("the shift itself succeeded");
        files.Exists(Live).ShouldBeTrue("the live log never moved");
        report.Failed.ShouldBe(1);

        host.Ran.ShouldBeEmpty("no live log moved, so nothing should be told to reopen one");
        state.Get(Live).ShouldNotBeNull().LastRotated.ShouldBe(Now.AddDays(-2), "the clock advances only for a real move");
    }

    /// <summary>
    /// A live log that could not be moved is one failure, not one per operation behind it.
    /// </summary>
    /// <remarks>
    /// With compression on - the default - the plan renames the log to <c>app.log.1</c> and then
    /// compresses <c>app.log.1</c>. A rename the writer's sharing refused left nothing to
    /// compress, and the compress was attempted anyway and reported as a second failure: "the
    /// file no longer exists", about a file that never existed, for one locked log.
    /// </remarks>
    [Fact]
    public void ALogThatCouldNotBeMovedIsOneFailure()
    {
        var files = new FakeFiles().Add(Live, bytes: 100);
        var applier = new FakeApplier(files).Fail(op => op.IsLiveRotation);

        var report = Run(Job(compress: true), files, applier, State());

        report.Failed.ShouldBe(1);
        report.Diagnostics.ShouldHaveSingleItem().Message.ShouldContain("held by another process");
        applier.Attempted.ShouldNotContain(op => op.Action == PlannedAction.Compress, "there was nothing to compress");
    }

    /// <summary>A shifted archive is not a log and gets no rotation clock of its own.</summary>
    /// <remarks>
    /// Every completed rename used to become a state row with <c>LastRotated = now</c>, refreshed
    /// nightly for as long as the chain shifted - <c>rotate</c> junk rows per log, for ever.
    /// </remarks>
    [Fact]
    public void AShiftedArchiveGetsNoRotationClockOfItsOwn()
    {
        var files = new FakeFiles().Add(Live, bytes: 100).Add(@"C:\logs\app.log.1", bytes: 50);
        var host = new FakeHookHost();
        var state = State();

        var report = Run(Job(post: [Reload]), files, new FakeApplier(files), state, host);

        report.Failed.ShouldBe(0);
        host.Ran.ShouldHaveSingleItem().Stage.ShouldBe(HookStage.PostRotate);
        state.Get(Live).ShouldNotBeNull().LastRotated.ShouldBe(Now);
        state.Paths.Keys.ShouldBe([WinPath.CanonicalKey(Live)], "only the live log has a clock");
    }

    // ---- a manage job owns the archives it compressed ------------------------------------------

    /// <summary>An IIS directory: one file per day, u_exYYMMDD.log, the last one still being written.</summary>
    private static FakeFiles IisDays(int from, int to)
    {
        var files = new FakeFiles();

        for (var day = from; day <= to; day++)
        {
            files.Add(IisDay(day), new DateTimeOffset(2026, 9, day, 23, 0, 0, TimeSpan.Zero), bytes: 1000);
        }

        return files;
    }

    private static string IisDay(int day) => $@"C:\iis\u_ex2609{day:00}.log";

    private static EffectiveJob Manage(int rotate, int? maxAge = null) =>
        Job(rotate, compress: true) with
        {
            Name = "iis",
            Kind = JobKind.Manage,
            Paths = [@"C:\iis\u_ex*.log"],
            MaxAge = maxAge,
        };

    private static string[] Zips(FakeFiles files) =>
        [.. files.All.Select(f => f.Path).Where(p => p.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).Order()];

    /// <summary>
    /// A manage job keeps <c>rotate</c> archives however many nights it runs.
    /// </summary>
    /// <remarks>
    /// The job's pattern names what the application writes - <c>u_ex*.log</c> - and the archive it
    /// compressed last night is <c>u_ex*.log.zip</c>, which that pattern does not match. So from the
    /// second night on, retention was counting only the files nobody had compressed yet: every
    /// archive the job made was never counted, never aged and never deleted, and the directory grew
    /// by one archive a night for ever. The README's own IIS example, <c>scan</c>'s suggestion and
    /// the GUI's picked-file pattern all produce exactly this job. One night could not show it,
    /// which is all the installer smoke has ever run.
    /// </remarks>
    [Fact]
    public void AManageJobKeepsRotateArchivesNightAfterNight()
    {
        var files = IisDays(1, 5);
        var state = State();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 6, 3, 0, 0, TimeSpan.Zero));

        for (var night = 6; night <= 10; night++)
        {
            Run(Manage(rotate: 2), files, new FakeApplier(files), state, clock: clock)
                .Failed.ShouldBe(0);

            Zips(files).Length.ShouldBeLessThanOrEqualTo(2, $"night {night}: rotate = 2 keeps two archives");

            files.Add(IisDay(night), clock.GetUtcNow().AddHours(20), bytes: 1000);
            clock.Advance(TimeSpan.FromDays(1));
        }

        // The last run saw day 9 as the file being written: it stays, and the two before it are
        // the archives rotate = 2 keeps.
        Zips(files).ShouldBe([IisDay(7) + ".zip", IisDay(8) + ".zip"]);
        files.Exists(IisDay(9)).ShouldBeTrue("the file the application is writing is never touched");
    }

    /// <summary>maxage reaches a compressed archive too, whichever format it was written in.</summary>
    [Fact]
    public void AManageJobAgesItsCompressedArchives()
    {
        var files = IisDays(9, 9);
        files.Add(IisDay(1) + ".zip", new DateTimeOffset(2026, 9, 1, 23, 0, 0, TimeSpan.Zero));
        files.Add(IisDay(2) + ".gz", new DateTimeOffset(2026, 9, 2, 23, 0, 0, TimeSpan.Zero));
        files.Add(IisDay(8) + ".zip", new DateTimeOffset(2026, 9, 8, 23, 0, 0, TimeSpan.Zero));

        Run(Manage(rotate: -1, maxAge: 5), files, new FakeApplier(files), State()).Failed.ShouldBe(0);

        files.Exists(IisDay(1) + ".zip").ShouldBeFalse("older than maxage");
        files.Exists(IisDay(2) + ".gz").ShouldBeFalse("a gzip archive from before a compresstype change is still this job's");
        files.Exists(IisDay(8) + ".zip").ShouldBeTrue("inside maxage");
        files.Exists(IisDay(9)).ShouldBeTrue();
    }

    /// <summary>
    /// A pattern that already matches its archives is not reported as matching them twice.
    /// </summary>
    /// <remarks>
    /// <c>app-*</c> names <c>app-01.log.zip</c> itself, and so does the compressed spelling the job
    /// adds to it. That is one file found by two routes inside one pattern, not two patterns
    /// overlapping, and <c>LR2006</c> says the latter.
    /// </remarks>
    [Fact]
    public void APatternThatAlreadyMatchesItsArchivesIsNotAnOverlap()
    {
        var files = new FakeFiles()
            .Add(@"C:\app\app-01.log.zip", new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero))
            .Add(@"C:\app\app-02.log", new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero));

        var report = Run(Manage(rotate: 5) with { Paths = [@"C:\app\app-*"] }, files, new FakeApplier(files), State());

        report.Diagnostics.ShouldNotContain(d => d.Code == DiagnosticCode.MatchedMoreThanOnce);
    }

    /// <summary>
    /// maxfiles still counts what the pattern names, not the archives the job made from it.
    /// </summary>
    /// <remarks>
    /// The ceiling exists to catch a typo, and a job's own archives are not one - a rotate job's
    /// archives have never counted against it either. Counting them would also have refused, on
    /// the first run after this fix, every job whose directory had been filling up with them.
    /// </remarks>
    [Fact]
    public void MaxfilesIgnoresAManageJobsOwnArchives()
    {
        var files = IisDays(8, 9);
        for (var day = 1; day <= 5; day++)
        {
            files.Add(IisDay(day) + ".zip", new DateTimeOffset(2026, 9, day, 23, 0, 0, TimeSpan.Zero));
        }

        var report = Run(Manage(rotate: 10) with { MaxFiles = 2 }, files, new FakeApplier(files), State());

        report.Diagnostics.ShouldNotContain(d => d.Code == DiagnosticCode.DangerousPathRefused);
        report.Failed.ShouldBe(0);
    }
}
