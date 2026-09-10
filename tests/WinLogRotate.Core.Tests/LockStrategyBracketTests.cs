using Microsoft.Extensions.Time.Testing;
using Shouldly;
using WinLogRotate.Contracts;
using WinLogRotate.Core;
using WinLogRotate.Core.Compression;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Engine;
using WinLogRotate.Core.Journaling;
using WinLogRotate.Core.Safety;
using WinLogRotate.Core.State;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// The lock strategy, driven through a real run against a real held handle.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart of <c>HookBracketTests</c>, and for the same reason: <see cref="LockChoiceTests"/>
/// proves every rule without Windows, and only this can answer the one question those cannot -
/// whether anything calls them. Three consecutive milestones shipped a complete, well-tested
/// subsystem with no caller, and unit tests were what kept each one looking healthy.
/// </para>
/// <para>
/// Windows-only, because driving the real runner means going through <c>PathGuard.CheckPattern</c>
/// and <c>FileEnumerator.Resolve</c>, both of which work in Windows paths - and because
/// <c>FileOps.CopyTruncate</c> is <c>CreateFileW</c>. It uses the default seams throughout, so it
/// names no Windows-only type and stays in the neutral test project.
/// </para>
/// </remarks>
public sealed class LockStrategyBracketTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("winlogrotate-lock-");
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 10, 3, 0, 0, TimeSpan.Zero));

    /// <summary>Above <c>NulFillDetector.MinimumInterestingSize</c>.</summary>
    private const int LogSize = 2 << 20;

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); } catch (IOException) { }
    }

    private DateTimeOffset Now => _clock.GetUtcNow();

    private string Log => Path.Combine(_dir.FullName, "app.log");

    private StateStore State()
    {
        var state = StateStore.Load(Path.Combine(_dir.FullName, "state.json"), out _);
        state.Set(Log, new PathState { Path = Log, LastRotated = Now.AddDays(-2) });
        return state;
    }

    private EffectiveJob Job(LockStrategy strategy) => new()
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

    private RunReport Run(EffectiveJob job, StateStore state) =>
        new RotationRunner(new NullJournal(), new PathGuard(new GuardOptions()), state, _clock)
            .Run(
                new LoadedConfig
                {
                    Jobs = [job],
                    Diagnostics = [],
                    Paths = InstallPaths.Resolve(_dir.FullName),
                    Quarantined = [],
                },
                new RunOptions());

    /// <summary>Opens the log the way a real Windows logger does, and keeps the handle.</summary>
    private FileStream Hold(FileShare share) =>
        new(Log, FileMode.Open, FileAccess.Write, share);

    private void Seed(int bytes = LogSize) => File.WriteAllBytes(Log, new byte[bytes]);

    /// <summary>
    /// <c>auto</c> against a handle that forbids renaming really copytruncates.
    /// </summary>
    /// <remarks>
    /// The whole milestone, on real Windows. Before this, <c>auto</c> fell through to
    /// <c>rename</c> - which a holder withholding FILE_SHARE_DELETE denies - so this rotation
    /// simply failed, every night, on exactly the writer <c>auto</c> was chosen to cope with.
    /// </remarks>
    [Fact]
    public void AutoAgainstARealHeldHandleReallyCopytruncates()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Rotates a real file against a real handle.");

        Seed(4096);
        using var held = Hold(FileShare.ReadWrite);

        var report = Run(Job(LockStrategy.Auto), State());

        report.Failed.ShouldBe(0);
        new FileInfo(Log + ".1").Length.ShouldBe(4096, "the archive is the log we truncated");
        new FileInfo(Log).Length.ShouldBe(0, "and the original was emptied in place");

        // The writer is untouched by all of it, which is the entire point of the strategy.
        held.Write("still writing\r\n"u8);
        held.Flush();
    }

    /// <summary>
    /// <c>auto</c> against a handle nothing can open rotates nothing and says why.
    /// </summary>
    /// <remarks>
    /// log4net's default ExclusiveLock. Reporting it is new: <c>auto</c> used to attempt a rename
    /// and produce a bare sharing violation with no indication that the strategy was the problem.
    /// </remarks>
    [Fact]
    public void AutoAgainstARealExclusiveHandleRotatesNothingAndSaysWhy()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Rotates a real file against a real handle.");

        Seed(4096);
        var state = State();
        using var held = Hold(FileShare.None);

        var report = Run(Job(LockStrategy.Auto), state);

        File.Exists(Log + ".1").ShouldBeFalse();
        report.Diagnostics.ShouldContain(d => d.Code == DiagnosticCode.FileLocked);

        // And the clock did not advance, so it will be tried again rather than waiting an interval.
        state.Get(Log).ShouldNotBeNull().LastRotated.ShouldBe(Now.AddDays(-2));
    }

    /// <summary>
    /// A real truncation records the two numbers the detector needs.
    /// </summary>
    /// <remarks>
    /// <c>PlanExecutor.RecordTruncation</c> was declared, invoked behind a null-conditional, and
    /// assigned by nothing - so this number was computed on every truncation and dropped every
    /// time. This is the test that fails if the runner stops subscribing.
    /// </remarks>
    [Fact]
    public void ARealTruncationRecordsWhereItLeftTheFile()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Truncates a real file.");

        Seed(4096);
        var state = State();

        Run(Job(LockStrategy.CopyTruncate), state);

        var remembered = state.Get(Log).ShouldNotBeNull();
        remembered.LastTruncatedFrom.ShouldBe(4096);
        remembered.LastTruncatedTo.ShouldNotBeNull();
    }

    /// <summary>
    /// A real NUL-fill is caught, and the path is refused from then on.
    /// </summary>
    /// <remarks>
    /// End to end on real NTFS: a rotation truncates the log, a writer holding a cached offset
    /// resumes at it, the run judges what it left behind, and the path never gets copytruncate
    /// again. Every piece of this existed before this milestone and none of it was connected.
    /// </remarks>
    [Fact]
    public void AConfirmedNulFillRefusesCopytruncateForEver()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Needs real NTFS zero-filling.");

        Seed();
        var state = State();

        using (var writer = Hold(FileShare.ReadWrite))
        {
            writer.Position = LogSize;

            var first = Run(Job(LockStrategy.CopyTruncate), state);
            first.Completed.ShouldBeGreaterThan(0);

            // The writer has no idea it was truncated, and resumes where it thinks it was.
            writer.Write("resumed at a cached offset\r\n"u8);
            writer.Flush();
        }

        // A second run: due again, and this time there is evidence to judge.
        _clock.Advance(TimeSpan.FromDays(2));
        var second = Run(Job(LockStrategy.CopyTruncate), state);

        state.Get(Log).ShouldNotBeNull().NulFill.ShouldBe(NulFillVerdict.Confirmed);
        second.Diagnostics.ShouldContain(d => d.Code == DiagnosticCode.NulFillDetected);

        // And a third: refused, with the reason, rather than truncating again.
        _clock.Advance(TimeSpan.FromDays(2));
        var third = Run(Job(LockStrategy.CopyTruncate), state);

        third.Diagnostics.ShouldContain(d =>
            d.Code == DiagnosticCode.StrategyUnavailable && d.Severity == Severity.Error);

        third.Plans.ShouldHaveSingleItem().Operations
            .ShouldNotContain(o => o.Action == PlannedAction.CopyTruncate);
    }

    /// <summary>The journal records which strategy actually ran.</summary>
    /// <remarks>
    /// <c>CliEvent.Strategy</c> is documented as carrying this and was set by nothing, so it was
    /// null on every line the product has ever written - and it is the only way to tell after the
    /// fact what <c>auto</c> resolved to.
    /// </remarks>
    [Fact]
    public void TheJournalRecordsWhichStrategyWasUsed()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Rotates a real file.");

        Seed(4096);
        var journal = new RecordingJournal();
        using var held = Hold(FileShare.ReadWrite);

        new RotationRunner(journal, new PathGuard(new GuardOptions()), State(), _clock)
            .Run(
                new LoadedConfig
                {
                    Jobs = [Job(LockStrategy.Auto)],
                    Diagnostics = [],
                    Paths = InstallPaths.Resolve(_dir.FullName),
                    Quarantined = [],
                },
                new RunOptions());

        journal.Entries.ShouldContain(e =>
            e.Operation == Op.CopyTruncate && e.Strategy == "copytruncate");
    }
}
