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
        EffectiveJob job, FakeFiles files, FakeApplier applier, StateStore state, IHookHost? host = null) =>
        Run([job], files, applier, state, host);

    private RunReport Run(
        EffectiveJob[] jobs, FakeFiles files, FakeApplier applier, StateStore state, IHookHost? host = null) =>
        new RotationRunner(
                new NullJournal(), new PathGuard(new GuardOptions()), state, _clock,
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
}
