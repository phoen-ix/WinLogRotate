using Microsoft.Extensions.Time.Testing;
using Shouldly;
using WinLogRotate.Contracts;
using WinLogRotate.Core.Compression;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Engine;
using WinLogRotate.Core.Globbing;
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
}
