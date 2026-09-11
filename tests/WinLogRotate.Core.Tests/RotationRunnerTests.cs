using Microsoft.Extensions.Time.Testing;
using Shouldly;
using WinLogRotate.Contracts;
using WinLogRotate.Core.Compression;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Engine;
using WinLogRotate.Core.Globbing;
using WinLogRotate.Core.Io;
using WinLogRotate.Core.Journaling;
using WinLogRotate.Core.Safety;
using WinLogRotate.Core.State;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// The rotate branch of the runner - which, until this milestone, did not exist.
/// </summary>
/// <remarks>
/// <c>RotationRunner</c> skipped every job whose kind was not <c>manage</c>, behind a comment
/// saying rotate would arrive in milestone 8. <see cref="JobKind.Rotate"/> is the default kind and
/// the README's headline example, so a job that did not name a kind did nothing at all, silently,
/// and reported success.
/// </remarks>
public sealed class RotationRunnerTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("winlogrotate-runner-");
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 9, 3, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); } catch (IOException) { }
    }

    private DateTimeOffset Now => _clock.GetUtcNow();

    private StateStore State() => StateStore.Load(Path.Combine(_dir.FullName, "state.json"), out _);

    private RotationRunner Runner(StateStore state, IArchiveSource source) =>
        new(new NullJournal(), new PathGuard(new GuardOptions()), state, _clock, source);

    private RotationRunner Runner(StateStore state, IWriterInspector inspector) =>
        new(new NullJournal(), new PathGuard(new GuardOptions()), state, _clock,
            new NoArchives(), hookHost: null, hookGate: null, inspector);

    /// <summary>Nothing on disk; every rotation here is a decision, not an operation.</summary>
    private sealed class NoArchives : IArchiveSource
    {
        public MatchedFile? Find(string path) => null;

        public IReadOnlyList<MatchedFile> Glob(string pattern) => [];
    }

    private static EffectiveJob Job(int rotate = 4) => new()
    {
        Name = "app",
        Kind = JobKind.Rotate,
        Paths = [@"C:\logs\app.log"],
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
        Compress = false,
        CompressType = CompressType.None,
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
        PostRotate = [],
        HookTimeout = TimeSpan.FromSeconds(60),
        AllowDangerous = [],
    };

    private static MatchedFile Live() => new()
    {
        Path = @"C:\logs\app.log",
        Length = 4096,
        LastWriteUtc = new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero),
    };

    private JobPlan Plan(StateStore state, RunOptions options, List<CliDiagnostic>? reported = null) =>
        Runner(state, new NoArchives())
            .PlanRotation(Job(), [Live()], options, Now, d => reported?.Add(d));

    // ---- the first night ---------------------------------------------------------------------

    /// <summary>
    /// A log this machine has never seen is recorded, not rotated.
    /// </summary>
    /// <remarks>
    /// Without it, the first run after installing rotates every log on the server at once -
    /// purely because none of them has a recorded rotation yet - which is indistinguishable from
    /// the tool malfunctioning, and is how a product gets uninstalled on day one.
    /// </remarks>
    [Fact]
    public void AFirstSightingIsBaselinedNotRotated()
    {
        var reported = new List<CliDiagnostic>();
        var state = State();

        var plan = Plan(state, new RunOptions { Force = true }, reported);

        // Skipped, not dropped. The first real rotation on a CI runner reported
        // "0 file(s) matched" for a directory that plainly had one, because a baselined file was
        // removed from the plan entirely - so a dry run said nothing whatsoever about the file it
        // had quietly set aside.
        plan.MatchedFiles.ShouldBe(1);
        plan.Operations.ShouldHaveSingleItem().Action.ShouldBe(PlannedAction.Skip);
        plan.Operations[0].Reason.ShouldContain("first time");

        reported.ShouldHaveSingleItem().Code.ShouldBe(DiagnosticCode.FirstRunBaseline);

        // And the clock is started, so tomorrow it is due like anything else.
        state.Get(@"C:\logs\app.log").ShouldNotBeNull().LastRotated.ShouldBe(Now);
    }

    [Fact]
    public void CatchupRotatesOnTheFirstSightingInstead()
    {
        // Documented as opt-in precisely because the default above is the surprising-but-correct
        // behaviour, and this is the escape hatch for somebody who wants the other one.
        var plan = Plan(State(), new RunOptions { Catchup = true, Force = true });

        plan.Operations.ShouldContain(o => o.Action == PlannedAction.Rename);
    }

    /// <summary>
    /// --catchup still starts the clock, even though it does not baseline.
    /// </summary>
    /// <remarks>
    /// The first draft skipped RecordFirstSighting entirely under --catchup, which looked right
    /// and was not: RotationCriteria refuses a log with no recorded rotation, so the flag could
    /// never rotate anything AND never wrote a clock - leaving the log permanently stuck, which
    /// is worse than the behaviour the flag exists to override.
    /// </remarks>
    [Fact]
    public void CatchupStillStartsTheClock()
    {
        var state = State();
        Plan(state, new RunOptions { Catchup = true, Force = true });

        state.Get(@"C:\logs\app.log").ShouldNotBeNull().LastRotated.ShouldNotBeNull();
    }

    [Fact]
    public void ASecondSightingIsNotBaselinedAgain()
    {
        var state = State();
        Plan(state, new RunOptions());

        var reported = new List<CliDiagnostic>();
        _clock.Advance(TimeSpan.FromDays(2));
        var plan = Plan(state, new RunOptions(), reported);

        reported.ShouldBeEmpty();
        plan.Operations.ShouldContain(o => o.Action == PlannedAction.Rename);
    }

    // ---- being due ---------------------------------------------------------------------------

    [Fact]
    public void ALogRotatedAnHourAgoIsNotDue()
    {
        var state = State();
        state.Set(@"C:\logs\app.log", new PathState
        {
            Path = @"C:\logs\app.log",
            LastRotated = Now.AddHours(-1),
        });

        var plan = Plan(state, new RunOptions());

        plan.Operations.ShouldAllBe(o => o.Action == PlannedAction.Skip);
    }

    [Fact]
    public void ALogRotatedTwoDaysAgoIsDue()
    {
        var state = State();
        state.Set(@"C:\logs\app.log", new PathState
        {
            Path = @"C:\logs\app.log",
            LastRotated = Now.AddDays(-2),
        });

        Plan(state, new RunOptions()).Operations
            .ShouldContain(o => o.Action == PlannedAction.Rename);
    }

    /// <summary>The live log is recreated, or the writer keeps appending to the archive.</summary>
    [Fact]
    public void ARotationRecreatesTheLogTheWriterExpects()
    {
        var state = State();
        state.Set(@"C:\logs\app.log", new PathState
        {
            Path = @"C:\logs\app.log",
            LastRotated = Now.AddDays(-2),
        });

        Plan(state, new RunOptions()).Operations
            .ShouldContain(o => o.Action == PlannedAction.Create);
    }

    // ---- the wiring ---------------------------------------------------------------------------

    private RunReport Run(EffectiveJob job, RunOptions options, StateStore? state = null) =>
        Runner(state ?? State(), new NoArchives()).Run(
            new LoadedConfig
            {
                Jobs = [job],
                Diagnostics = [],
                Paths = InstallPaths.Resolve(_dir.FullName),
                Quarantined = [],
            },
            options);

    /// <summary>
    /// The runner actually dispatches a rotate job.
    /// </summary>
    /// <remarks>
    /// Every other test here calls PlanRotation directly, so all of them pass with the call site
    /// deleted - which is exactly the state this milestone found the product in: a complete,
    /// tested planner that nothing invoked. This is the only test that fails if the branch goes
    /// away again, so it is the one that matters.
    /// </remarks>
    [Fact]
    public void ARotateJobReachesThePlanner()
    {
        // missingok, so the job survives matching nothing and still reaches the planner. The
        // path is Windows-shaped, which the guard accepts on any OS - it checks the shape of a
        // path, not the machine it is running on.
        var job = Job() with { MissingOk = true };

        var report = Run(job, new RunOptions { DryRun = true });

        report.JobsRun.ShouldBe(1, "a rotate job must be planned, not silently skipped");
        report.Plans.ShouldHaveSingleItem().JobName.ShouldBe("app");
    }

    [Fact]
    public void AManageJobStillReachesItsOwnPlanner()
    {
        // The rotate branch is added beside manage, not in place of it.
        var job = Job() with { Kind = JobKind.Manage, MissingOk = true };

        Run(job, new RunOptions { DryRun = true }).JobsRun.ShouldBe(1);
    }

    /// <summary>A dry run reports no rotations, so it advances no clock.</summary>
    /// <remarks>
    /// The property that makes --dry-run safe to point at production: it must not change when
    /// anything next rotates. The clock advances from ExecutionResult.Rotated, and a dry run
    /// never applies an operation, so that list stays empty.
    /// </remarks>
    [Fact]
    public void ADryRunAdvancesNoClock()
    {
        var state = State();
        state.Set(@"C:\logs\app.log", new PathState
        {
            Path = @"C:\logs\app.log",
            LastRotated = Now.AddDays(-30),
        });

        Run(Job() with { MissingOk = true }, new RunOptions { DryRun = true }, state);

        state.Get(@"C:\logs\app.log").ShouldNotBeNull().LastRotated.ShouldBe(Now.AddDays(-30));
    }

    // ---- the default kind --------------------------------------------------------------------

    /// <summary>
    /// A job that does not say which kind it is rotates.
    /// </summary>
    /// <remarks>
    /// This is the defect the whole milestone exists for. JobKind.Rotate is the default in both
    /// ConfigModel and ConfigBinder, so the most ordinary configuration anybody could write -
    /// and the one in the README - selected the branch that did nothing.
    /// </remarks>
    [Fact]
    public void TheDefaultJobKindIsRotate()
    {
        // Asserted through the binder rather than a property default, because that is the path a
        // real config takes: ConfigBinder.BindJob starts from JobKind.Rotate and only moves off it
        // if the file says so.
        var toml = TomlFile.Parse(
            "schema = 1\n[job]\nname = \"app\"\npaths = [\"C:/logs/*.log\"]\n", "job.toml");

        ConfigBinder.BindJob(toml, new DiagnosticBag()).ShouldNotBeNull()
            .Kind.ShouldBe(JobKind.Rotate);
    }

    // ---- the lock strategy --------------------------------------------------------------------

    private JobPlan PlanWith(
        EffectiveJob job, StateStore state, IWriterInspector inspector, RunOptions options,
        List<CliDiagnostic>? reported = null) =>
        Runner(state, inspector)
            .PlanRotation(job, [Live()], options, Now, d => reported?.Add(d));

    private StateStore Due(out string path)
    {
        var state = State();
        path = @"C:\logs\app.log";
        state.Set(path, new PathState { Path = path, LastRotated = Now.AddDays(-2) });
        return state;
    }

    /// <summary>
    /// <c>auto</c> is resolved by asking the file, before anything is planned.
    /// </summary>
    /// <remarks>
    /// The defect this milestone exists for. <c>LockStrategy.Auto</c> fell into
    /// <c>RotateJobPlanner</c>'s discard arm and silently became <c>rename</c> - the strategy that
    /// fails outright on a writer withholding FILE_SHARE_DELETE, which is exactly the writer
    /// somebody chooses <c>auto</c> to cope with. This is the test that fails if the wiring in
    /// PlanRotation is removed.
    /// </remarks>
    [Fact]
    public void AutoIsResolvedByAskingTheFile()
    {
        var state = Due(out var path);
        var probe = new FakeInspector(ProbeVerdict.CopyTruncate);

        var plan = PlanWith(Job() with { LockStrategy = LockStrategy.Auto }, state, probe, new RunOptions());

        probe.Classified.ShouldBe(1);
        plan.Operations.ShouldContain(o => o.Action == PlannedAction.CopyTruncate);

        // copytruncate keeps the inode, so there is nothing to recreate.
        plan.Operations.ShouldNotContain(o => o.Action == PlannedAction.Create);

        // And the operation carries the resolved strategy, not the word "auto".
        plan.Operations.First(o => o.Action == PlannedAction.CopyTruncate)
            .Strategy.ShouldBe(LockStrategy.CopyTruncate);

        state.Get(path).ShouldNotBeNull().Probe.ShouldBe(ProbeVerdict.CopyTruncate);
    }

    /// <summary>A file nothing can open is skipped, with the reason, rather than renamed.</summary>
    [Fact]
    public void AutoSkipsAFileNothingCanOpen()
    {
        var reported = new List<CliDiagnostic>();
        var state = Due(out _);

        var plan = PlanWith(
            Job() with { LockStrategy = LockStrategy.Auto }, state,
            new FakeInspector(ProbeVerdict.None), new RunOptions(), reported);

        plan.Operations.ShouldAllBe(o => o.Action == PlannedAction.Skip);
        reported.ShouldContain(d => d.Code == DiagnosticCode.FileLocked);
    }

    /// <summary>A quarantined path is skipped and the plan says why.</summary>
    [Fact]
    public void AQuarantinedPathIsSkippedWithItsReason()
    {
        var reported = new List<CliDiagnostic>();
        var state = Due(out var path);
        state.Update(path, e => e with { NulFill = NulFillVerdict.Confirmed });

        var plan = PlanWith(
            Job() with { LockStrategy = LockStrategy.CopyTruncate }, state,
            new FakeInspector(), new RunOptions(), reported);

        plan.Operations.ShouldAllBe(o => o.Action == PlannedAction.Skip);
        plan.Operations[0].Reason.ShouldContain("refused");
        reported.ShouldContain(d => d.Code == DiagnosticCode.StrategyUnavailable);
    }

    /// <summary>A log nothing is going to touch is never probed.</summary>
    [Fact]
    public void AFileThatIsNotDueIsNeverProbed()
    {
        var state = State();
        state.Set(@"C:\logs\app.log", new PathState
        {
            Path = @"C:\logs\app.log",
            LastRotated = Now.AddMinutes(-5),
        });

        var probe = new FakeInspector(ProbeVerdict.Rename);
        PlanWith(Job() with { LockStrategy = LockStrategy.Auto }, state, probe, new RunOptions());

        probe.Classified.ShouldBe(0);
    }

    /// <summary>
    /// A dry run asks the file and writes nothing down.
    /// </summary>
    /// <remarks>
    /// It must probe, or it would report rename for a file that would really be copied - breaking
    /// the property that makes --dry-run worth trusting, that it is the same code path stopped one
    /// step earlier. And it must not persist, because judging <i>consumes</i> the recorded
    /// truncation and a dry run destroying the real run's only evidence would be worse than
    /// useless.
    /// </remarks>
    [Fact]
    public void ADryRunProbesAndPersistsNothing()
    {
        var state = Due(out var path);
        var probe = new FakeInspector(ProbeVerdict.CopyTruncate);

        PlanWith(
            Job() with { LockStrategy = LockStrategy.Auto }, state, probe,
            new RunOptions { DryRun = true });

        probe.Classified.ShouldBe(1);
        state.Get(path).ShouldNotBeNull().Probe.ShouldBe(ProbeVerdict.Unknown);
        state.Get(path).ShouldNotBeNull().ProbedAt.ShouldBeNull();
    }

    /// <summary>
    /// A truncation recorded last run is judged, whether or not the log is due now.
    /// </summary>
    /// <remarks>
    /// Gating the judgement on dueness would leave a monthly job's evidence unexamined for a
    /// month, and a minsize-suppressed job's unexamined for ever - which is one more
    /// multi-gigabyte night each time.
    /// </remarks>
    [Fact]
    public void APendingTruncationIsJudgedEvenWhenTheLogIsNotDue()
    {
        var reported = new List<CliDiagnostic>();
        var state = State();
        var path = @"C:\logs\app.log";

        state.Set(path, new PathState
        {
            Path = path,
            LastRotated = Now.AddMinutes(-5),
            LastTruncatedFrom = 2L << 20,
            LastTruncatedTo = 0,
        });

        var probe = new FakeInspector
        {
            Next = new WriterSample { Opened = true, Size = 2L << 20, BytesRead = 4096, NulRun = 4096 },
        };

        PlanWith(Job(), state, probe, new RunOptions(), reported);

        probe.Sampled.ShouldBe(1);
        state.Get(path).ShouldNotBeNull().NulFill.ShouldBe(NulFillVerdict.Confirmed);
        reported.ShouldContain(d => d.Code == DiagnosticCode.NulFillDetected);
    }

    /// <summary>Nothing was truncated, so nothing is looked at.</summary>
    [Fact]
    public void ALogWithNoRecordedTruncationIsNotSampled()
    {
        var probe = new FakeInspector();
        PlanWith(Job(), Due(out _), probe, new RunOptions());

        probe.Sampled.ShouldBe(0);
    }

    /// <summary>
    /// The planner refuses to plan an unresolved <c>auto</c> rather than guessing.
    /// </summary>
    /// <remarks>
    /// Belt to the braces above. The old discard arm made "auto silently means rename" a one-line
    /// regression away; this makes it unreachable instead of merely fixed.
    /// </remarks>
    [Fact]
    public void ThePlannerRefusesAnUnresolvedAuto()
    {
        var verdicts = new Dictionary<string, DueVerdict>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\logs\app.log"] = new()
            {
                Due = true,
                Reason = DueReason.Scheduled,
                Explanation = "due",
            },
        };

        Should.Throw<InvalidOperationException>(() => RotateJobPlanner.Plan(
                Job() with { LockStrategy = LockStrategy.Auto },
                LogSeries.Discover(Job(), [Live()], new NoArchives()),
                verdicts,
                Now))
            .Message.ShouldContain("must be resolved");
    }

    // ---- why a log was held back ---------------------------------------------------------------

    /// <summary>
    /// A log that was due and was held back by notifempty says so.
    /// </summary>
    /// <remarks>
    /// "Why did this not rotate last night?" is the question an operator actually asks, and the
    /// answer used to exist only as a DueReason nothing read and a Skip line invisible without
    /// --verbose. LR2003 has been published in docs/diagnostics.md since before anything raised it.
    /// </remarks>
    [Fact]
    public void ALogHeldBackBecauseItIsEmptyIsReported()
    {
        var reported = new List<CliDiagnostic>();
        var state = State();
        state.Set(@"C:\logs\app.log", new PathState
        {
            Path = @"C:\logs\app.log",
            LastRotated = Now.AddDays(-2),
        });

        Runner(state, new NoArchives()).PlanRotation(
            Job() with { NotIfEmpty = true },
            [Live() with { Length = 0 }],
            new RunOptions(), Now, reported.Add);

        reported.ShouldContain(d => d.Code == DiagnosticCode.FileEmpty && d.Severity == Severity.Info);
    }

    /// <summary>The same for minsize, which is the other "not yet" an operator has to ask about.</summary>
    [Fact]
    public void ALogHeldBackByMinsizeIsReported()
    {
        var reported = new List<CliDiagnostic>();
        var state = State();
        state.Set(@"C:\logs\app.log", new PathState
        {
            Path = @"C:\logs\app.log",
            LastRotated = Now.AddDays(-2),
        });

        Runner(state, new NoArchives()).PlanRotation(
            Job() with { MinSize = 1 << 30 },
            [Live()],
            new RunOptions(), Now, reported.Add);

        reported.ShouldContain(d => d.Code == DiagnosticCode.NotDueYet);
    }

    /// <summary>
    /// A log that is simply not due tonight is not reported.
    /// </summary>
    /// <remarks>
    /// The ordinary case for most logs on most runs. Reporting it would put a line per file per
    /// night in front of an operator and bury the two above.
    /// </remarks>
    [Fact]
    public void ALogThatIsMerelyNotDueIsNotReported()
    {
        var reported = new List<CliDiagnostic>();
        var state = State();
        state.Set(@"C:\logs\app.log", new PathState
        {
            Path = @"C:\logs\app.log",
            LastRotated = Now.AddMinutes(-5),
        });

        Runner(state, new NoArchives()).PlanRotation(
            Job(), [Live()], new RunOptions(), Now, reported.Add);

        reported.ShouldNotContain(d => d.Code == DiagnosticCode.NotDueYet);
    }
}
