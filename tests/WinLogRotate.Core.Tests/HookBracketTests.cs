using Microsoft.Extensions.Time.Testing;
using Shouldly;
using WinLogRotate.Contracts;
using WinLogRotate.Core;
using WinLogRotate.Core.Compression;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Engine;
using WinLogRotate.Core.Hooks;
using WinLogRotate.Core.Journaling;
using WinLogRotate.Core.Safety;
using WinLogRotate.Core.State;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>A hook host that records what it was asked to do, and answers as told.</summary>
internal sealed class FakeHookHost(params HookResult[] answers) : IHookHost
{
    private int _calls;

    public List<PlannedHook> Ran { get; } = [];

    /// <summary>Set to throw from Run, to prove the boundary catches it.</summary>
    public Exception? Throw { get; init; }

    public TimeSpan? LastTimeout { get; private set; }

    public HookOutcome Run(PlannedHook hook, TimeSpan timeout)
    {
        Ran.Add(hook);
        LastTimeout = timeout;

        if (Throw is { } e)
        {
            throw e;
        }

        var result = _calls < answers.Length ? answers[_calls] : HookResult.Ok;
        _calls++;

        return result == HookResult.Ok
            ? HookOutcome.Succeeded(TimeSpan.FromMilliseconds(1))
            : new HookOutcome { Result = result, ExitCode = 1, Detail = "as instructed" };
    }
}

/// <summary>
/// The bracket around a job: when hooks run, and what a failure in each stage costs.
/// </summary>
/// <remarks>
/// <para>
/// Driven through <see cref="RotationRunner.Run"/> against real files, not through
/// <see cref="HookRunner"/> directly. Milestone 12 was a complete, well-tested rotation engine
/// that nothing called; a hook suite that only exercised the hook classes would repeat exactly
/// that mistake one layer up.
/// </para>
/// <para>
/// Which is also why they are Windows-only. Driving the real runner means going through
/// <c>PathGuard.CheckPattern</c> and <c>FileEnumerator.Resolve</c>, and both work in Windows paths
/// - a temp directory on the Linux leg is refused as not absolute and then matches nothing, so the
/// job's plan is empty and no hook would ever be reached. <c>FileEnumeratorTests</c> carries the
/// same skip for the same reason. The rules themselves are proved without Windows in
/// <see cref="HookPlanTests"/> and <see cref="HookRunnerTests"/>; what needs the Windows leg is
/// the one question those cannot answer - whether anything calls them.
/// </para>
/// </remarks>
public sealed class HookBracketTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("winlogrotate-hooks-");
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 10, 3, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {

        try { _dir.Delete(recursive: true); } catch (IOException) { }
    }

    private DateTimeOffset Now => _clock.GetUtcNow();

    private string Log => Path.Combine(_dir.FullName, "app.log");

    private StateStore State()
    {
        var state = StateStore.Load(Path.Combine(_dir.FullName, "state.json"), out _);

        // Rotated two days ago, so a daily job is due and the plan really does move the live log.
        state.Set(Log, new PathState { Path = Log, LastRotated = Now.AddDays(-2) });
        return state;
    }

    private EffectiveJob Job(string[]? pre = null, string[]? post = null) => new()
    {
        Name = "app",
        Kind = JobKind.Rotate,
        Paths = [Path.Combine(_dir.FullName, "*.log")],
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
        Compress = false,
        CompressType = CompressType.None,
        DelayCompress = false,
        DateExt = false,
        DateFormat = "-yyyyMMdd",
        MissingOk = true,
        NotIfEmpty = false,
        OldDir = null,
        CreateOldDir = false,
        LockStrategy = LockStrategy.Rename,
        LiveFiles = 1,
        MaxFiles = 1000,
        RetryCount = 5,
        RetryIntervalMs = 100,
        PreRotate = pre ?? [],
        PostRotate = post ?? [],
        HookTimeout = TimeSpan.FromSeconds(30),
        AllowDangerous = [],
    };

    private RunReport Run(
        EffectiveJob job, IHookHost host, StateStore state, HookGate? gate = null,
        RunOptions? options = null) =>
        new RotationRunner(
                new NullJournal(), new PathGuard(new GuardOptions()), state, _clock,
                archiveSource: null, host, gate ?? HookGate.Open)
            .Run(
                new LoadedConfig
                {
                    Jobs = [job],
                    Diagnostics = [],
                    Paths = InstallPaths.Resolve(_dir.FullName),
                    Quarantined = [],
                },
                options ?? new RunOptions());

    private const string Reload = @"command:C:\tools\reload.exe --now";

    // ---- the bracket exists at all -----------------------------------------------------------

    /// <summary>
    /// A configured hook is actually dispatched by a real run.
    /// </summary>
    /// <remarks>
    /// The one test that fails if the bracket is deleted from RotationRunner. Everything else here
    /// would keep passing against a runner that never called a hook, which is precisely the state
    /// milestone 12 found rotation itself in: complete, tested, and invoked by nothing.
    /// </remarks>
    [Fact]
    public void APostrotateHookReachesTheHost()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "Rotates real files, which needs Windows path resolution.");

        File.WriteAllText(Log, "content");
        var host = new FakeHookHost();

        Run(Job(post: [Reload]), host, State());

        host.Ran.ShouldHaveSingleItem().Stage.ShouldBe(HookStage.PostRotate);
        File.Exists(Log + ".1").ShouldBeTrue("the rotation itself must still have happened");
    }

    [Fact]
    public void APrerotateHookRunsBeforeTheFilesMove()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "Rotates real files, which needs Windows path resolution.");

        File.WriteAllText(Log, "content");
        var moved = false;
        var host = new RecordingOrder(() => moved = File.Exists(Log + ".1"));

        Run(Job(pre: [Reload]), host, State());

        moved.ShouldBeFalse("prerotate must run before anything is renamed");
        File.Exists(Log + ".1").ShouldBeTrue();
    }

    private sealed class RecordingOrder(Action onRun) : IHookHost
    {
        public HookOutcome Run(PlannedHook hook, TimeSpan timeout)
        {
            onRun();
            return HookOutcome.Succeeded(TimeSpan.Zero);
        }
    }

    // ---- logrotate's asymmetry ---------------------------------------------------------------

    /// <summary>
    /// A failing prerotate skips the job and touches no file.
    /// </summary>
    /// <remarks>
    /// The hook is the job's precondition: if the service did not stop or the buffer was not
    /// flushed, rotating anyway does exactly the damage the hook was written to prevent. Asserted
    /// on the file system rather than on a counter, because a flag can be right while the disk is
    /// wrong.
    /// </remarks>
    [Fact]
    public void AFailingPrerotateSkipsTheJobAndTouchesNoFile()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "Rotates real files, which needs Windows path resolution.");

        File.WriteAllText(Log, "content");
        var state = State();

        var report = Run(Job(pre: [Reload]), new FakeHookHost(HookResult.Failed), state);

        File.Exists(Log).ShouldBeTrue();
        File.Exists(Log + ".1").ShouldBeFalse("nothing may move when the precondition failed");
        report.Failed.ShouldBe(1);
        report.Completed.ShouldBe(0);

        // And the clock must not advance, or the log silently waits another whole interval.
        state.Get(Log).ShouldNotBeNull().LastRotated.ShouldBe(Now.AddDays(-2));

        report.Diagnostics.ShouldContain(d => d.Code == DiagnosticCode.HookFailed);
        report.Diagnostics.First(d => d.Code == DiagnosticCode.HookFailed)
            .Remedy.ShouldNotBeNull().ShouldContain("no file was touched");

        // And the run reports the nothing that happened, not the rename it had intended. The plan
        // is the engine's intention; once the job is abandoned, printing it would tell an operator
        // a file moved that is still exactly where it was.
        var reported = report.Plans.ShouldHaveSingleItem();
        reported.Operations.ShouldAllBe(o => o.Action == PlannedAction.Skip);
        reported.Operations.ShouldContain(o => o.Reason.Contains("prerotate hook"));
    }

    /// <summary>
    /// A failing postrotate is an error, and the rotation stands.
    /// </summary>
    /// <remarks>
    /// The other half of logrotate's asymmetry. The files have already moved, so there is nothing
    /// to skip and nothing to undo - rolling a rotation back because a reload script exited 1 would
    /// be much the more surprising of the two.
    /// </remarks>
    [Fact]
    public void AFailingPostrotateFailsTheJobButKeepsTheRotation()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "Rotates real files, which needs Windows path resolution.");

        File.WriteAllText(Log, "content");
        var state = State();

        var report = Run(Job(post: [Reload]), new FakeHookHost(HookResult.Failed), state);

        File.Exists(Log + ".1").ShouldBeTrue("the rotation had already happened");
        report.Failed.ShouldBe(1);
        state.Get(Log).ShouldNotBeNull().LastRotated.ShouldBe(Now, "the log really was rotated");

        report.Diagnostics.First(d => d.Code == DiagnosticCode.HookFailed)
            .Remedy.ShouldNotBeNull().ShouldContain("rotation stands");
    }

    /// <summary>A prerotate that fails means the postrotate never runs either.</summary>
    [Fact]
    public void APostrotateDoesNotRunAfterAFailedPrerotate()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "Rotates real files, which needs Windows path resolution.");

        File.WriteAllText(Log, "content");
        var host = new FakeHookHost(HookResult.Failed);

        Run(Job(pre: [Reload], post: [Reload]), host, State());

        host.Ran.ShouldHaveSingleItem().Stage.ShouldBe(HookStage.PreRotate);
    }

    // ---- when hooks do not run ---------------------------------------------------------------

    /// <summary>
    /// Nothing due means no hooks.
    /// </summary>
    /// <remarks>
    /// A reload hook that fired on every run whether or not anything rotated would signal a
    /// service nightly for no reason, and the first real rotation would be indistinguishable from
    /// the three hundred that did nothing.
    /// </remarks>
    [Fact]
    public void NoRotationMeansNoHooks()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "Rotates real files, which needs Windows path resolution.");

        File.WriteAllText(Log, "content");
        var state = StateStore.Load(Path.Combine(_dir.FullName, "state.json"), out _);
        state.Set(Log, new PathState { Path = Log, LastRotated = Now.AddMinutes(-5) });

        var host = new FakeHookHost();
        Run(Job(pre: [Reload], post: [Reload]), host, state);

        host.Ran.ShouldBeEmpty();
    }

    /// <summary>
    /// A dry run describes its hooks and runs none of them.
    /// </summary>
    /// <remarks>
    /// --dry-run is trusted against production because it is the same code path stopped one step
    /// earlier. A reload script is not the exception to discover that with.
    /// </remarks>
    [Fact]
    public void ADryRunRunsNoHooks()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "Rotates real files, which needs Windows path resolution.");

        File.WriteAllText(Log, "content");
        var host = new FakeHookHost();

        Run(Job(pre: [Reload], post: [Reload]), host, State(), options: new RunOptions { DryRun = true });

        host.Ran.ShouldBeEmpty();
        File.Exists(Log + ".1").ShouldBeFalse();
    }

    // ---- the gate ----------------------------------------------------------------------------

    /// <summary>
    /// A shut gate refuses the hook, and a refused prerotate still skips the job.
    /// </summary>
    /// <remarks>
    /// The refusal has the same consequence as a failure, and deliberately: "the hook did not run"
    /// is the same fact about the job's precondition either way. What differs is the diagnostic -
    /// LR9003 for a gate that said no, LR3103 for one that ran and failed - because they go to
    /// different people.
    /// </remarks>
    [Fact]
    public void AShutGateRefusesTheHookAndStillSkipsTheJob()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "Rotates real files, which needs Windows path resolution.");

        File.WriteAllText(Log, "content");
        var host = new FakeHookHost();

        var report = Run(
            Job(pre: [Reload]), host, State(), HookGate.Shut("conf.d is writable by everyone"));

        host.Ran.ShouldBeEmpty();
        File.Exists(Log + ".1").ShouldBeFalse();
        report.Diagnostics.ShouldContain(d => d.Code == DiagnosticCode.HookRefused);
    }

    // ---- the never-throws boundary -----------------------------------------------------------

    /// <summary>
    /// A hook host that throws does not take the run with it.
    /// </summary>
    /// <remarks>
    /// The ordinary way to get here is a missing executable: Process.Start throws Win32Exception
    /// for ERROR_FILE_NOT_FOUND. RotationRunner has no try, RunCommand has no catch and Program has
    /// no catch, so an exception raised here would leave the process by the front door with the
    /// journal's run.end record never written.
    /// </remarks>
    [Fact]
    public void AMissingExecutableIsReportedNotThrown()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "Rotates real files, which needs Windows path resolution.");

        File.WriteAllText(Log, "content");

        var host = new FakeHookHost
        {
            Throw = new System.ComponentModel.Win32Exception(2, "The system cannot find the file specified"),
        };

        var report = Run(Job(post: [Reload]), host, State());

        report.Failed.ShouldBe(1);
        report.Diagnostics.First(d => d.Code == DiagnosticCode.HookFailed)
            .Message.ShouldContain("cannot find the file");
    }
}
