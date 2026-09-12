using Microsoft.Extensions.Time.Testing;
using Shouldly;
using WinLogRotate.Contracts;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Engine;
using WinLogRotate.Core.Io;
using WinLogRotate.Core.Journaling;
using WinLogRotate.Core.Safety;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>A journal that keeps everything in memory, so what the executor recorded can be
/// asserted directly.</summary>
internal sealed class RecordingJournal : IJournal
{
    public List<CliEvent> Entries { get; } = [];

    public string RunId => "TESTRUN";

    public void Write(CliEvent entry) => Entries.Add(entry);

    public void Dispose() { }
}

public sealed class PlanExecutorTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("winlogrotate-exec-");
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 7, 3, 0, 0, TimeSpan.Zero));
    private readonly RecordingJournal _journal = new();

    public void Dispose()
    {
        try
        {
            _dir.Delete(recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private static EffectiveJob Job() => new()
    {
        Name = "iis",
        Kind = JobKind.Manage,
        Paths = ["x"],
        Enabled = true,
        Schedule = Schedule.Daily,
        Weekday = 0,
        MonthDay = 0,
        Rotate = 3,
        Start = 1,
        MaxAge = null,
        MinAge = null,
        MinSize = null,
        MaxSize = null,
        SizeThreshold = 1 << 20,
        Compress = true,
        CompressType = CompressType.Zip,
        DelayCompress = false,
        DateExt = false,
        DateFormat = "-yyyyMMdd",
        MissingOk = true,
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

    private JobPlan PlanDeleting(params string[] names)
    {
        var ops = names.Select(n => new PlannedOp
        {
            Action = PlannedAction.Delete,
            Source = Path.Combine(_dir.FullName, n),
            Reason = "rotate = 3 exceeded",
            Bytes = 1024,
        }).ToArray();

        foreach (var op in ops)
        {
            File.WriteAllText(op.Source, "content");
        }

        return new JobPlan { JobName = "iis", Operations = ops, MatchedFiles = ops.Length };
    }

    /// <summary>
    /// The property that makes --dry-run worth trusting. It is the same code path as a real
    /// run, stopped one step earlier - not a separate description that could drift from what
    /// the executor actually does.
    /// </summary>
    [Fact]
    public void ADryRunChangesNothingOnDisk()
    {
        var plan = PlanDeleting("a.log.gz", "b.log.gz");
        var executor = new PlanExecutor(_journal, new PathGuard(new GuardOptions()), _clock);

        var result = executor.Execute(plan, Job(), dryRun: true);

        result.Completed.ShouldBe(0);
        result.Failed.ShouldBe(0);
        Directory.GetFiles(_dir.FullName).Length.ShouldBe(2);
    }

    /// <summary>
    /// A dry run emits only the "plan" half of each event. A real run emits both, so a crash
    /// between them leaves a record of an intention that was never carried out.
    /// </summary>
    [Fact]
    public void ADryRunJournalsIntentionsOnly()
    {
        var plan = PlanDeleting("a.log.gz", "b.log.gz");
        new PlanExecutor(_journal, new PathGuard(new GuardOptions()), _clock)
            .Execute(plan, Job(), dryRun: true);

        _journal.Entries.ShouldAllBe(e => e.Phase == Phase.Plan);
        _journal.Entries.Count(e => e.Operation == Op.Delete).ShouldBe(2);
    }

    /// <summary>"Which rule condemned this file?" is exactly what an operator asks when a log
    /// they wanted has gone, so every deletion carries its reason.</summary>
    [Fact]
    public void EveryJournalledDeletionCarriesItsReasonAndSize()
    {
        var plan = PlanDeleting("a.log.gz");
        new PlanExecutor(_journal, new PathGuard(new GuardOptions()), _clock)
            .Execute(plan, Job(), dryRun: true);

        var entry = _journal.Entries.Single(e => e.Operation == Op.Delete);
        entry.Reason.ShouldBe("rotate = 3 exceeded");
        entry.BytesBefore.ShouldBe(1024);
        entry.Job.ShouldBe("iis");
    }

    [Fact]
    public void SkippedFilesAreRecordedRatherThanOmitted()
    {
        var plan = new JobPlan
        {
            JobName = "iis",
            MatchedFiles = 1,
            Operations =
            [
                new PlannedOp
                {
                    Action = PlannedAction.Skip,
                    Source = @"C:\logs\live.log",
                    Reason = "newest file - the application is still writing it",
                },
            ],
        };

        var result = new PlanExecutor(_journal, new PathGuard(new GuardOptions()), _clock)
            .Execute(plan, Job(), dryRun: false);

        result.Skipped.ShouldBe(1);
        _journal.Entries.ShouldContain(e => e.Result == OpResult.Skipped);
    }

    /// <summary>A resolver that claims every directory really lives somewhere else.</summary>
    private sealed class Redirect(string to) : ILinkResolver
    {
        public LinkTarget Resolve(string path) => LinkTarget.At(to);
    }

    /// <summary>A resolver that cannot say where anything leads.</summary>
    /// <remarks>
    /// ERROR_ACCESS_DENIED, because that is the ordinary way this happens: a junction to a share
    /// this account may not open, or to a directory whose permissions changed since the plan was
    /// made. The plan may be seconds old.
    /// </remarks>
    private sealed class Unresolvable : ILinkResolver
    {
        public LinkTarget Resolve(string path) => LinkTarget.Unresolvable(5);
    }

    /// <summary>
    /// A directory whose real location cannot be established is not acted on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The executor used to fall back to the spelled path when resolution failed, arguing that an
    /// unresolvable path was then no worse guarded than before. But the spelling is exactly what
    /// the guard cannot trust at this point, and this is the last check before a file is
    /// destroyed - the plan may be seconds old, and a directory can be swapped for a junction in
    /// between.
    /// </para>
    /// <para>
    /// <c>FileEnumerator.Vet</c> has answered the identical question by refusing since it was
    /// written. One product cannot hold two opinions about whether an unverifiable link may be
    /// acted on, and the half that deletes files was the half that said yes.
    /// </para>
    /// <para>
    /// Asserted on the verdict rather than on the failure count: <c>CheckPath</c> turns down a
    /// Unix path as not absolute, so on the Linux leg this executor refuses everything it is
    /// given, and a test that only counted failures would pass with the change removed entirely.
    /// </para>
    /// </remarks>
    [Fact]
    public void ADirectoryThatCannotBeResolvedIsNotActedOn()
    {
        var plan = PlanDeleting("a.log.gz");

        var result = new PlanExecutor(
                _journal,
                new PathGuard(new GuardOptions { ProtectedRoots = [] }),
                _clock,
                new Unresolvable())
            .Execute(plan, Job(), dryRun: false);

        result.Failed.ShouldBe(1);
        result.Completed.ShouldBe(0);
        File.Exists(Path.Combine(_dir.FullName, "a.log.gz")).ShouldBeTrue("and it was not deleted");

        var refusal = result.Diagnostics.ShouldHaveSingleItem();
        refusal.Message.ShouldNotBeNull().ShouldContain("could not be established");
    }

    /// <summary>
    /// The last-moment check asks where the file really is, not where it is spelled.
    /// </summary>
    /// <remarks>
    /// Its comment has always said "the plan may be seconds old, and this is the last moment
    /// before something is destroyed" - and it compared a string, so a directory swapped for a
    /// junction between planning and acting passed it, as did one reached by an 8.3 name.
    /// </remarks>
    [Fact]
    public void TheApplyTimeCheckLooksWhereTheFileReallyIs()
    {
        var plan = PlanDeleting("a.log.gz");

        var executor = new PlanExecutor(
            _journal,
            new PathGuard(new GuardOptions { ProtectedRoots = [@"C:\Windows"] }),
            _clock,
            new Redirect(@"C:\Windows\System32"));

        var result = executor.Execute(plan, Job(), dryRun: false);

        result.Failed.ShouldBe(1);
        result.Completed.ShouldBe(0);
        File.Exists(Path.Combine(_dir.FullName, "a.log.gz")).ShouldBeTrue("and it was not deleted");

        // Asserted on the REASON, not just on the refusal. CheckPath turns down a Unix path as
        // not absolute, so on the Linux leg this executor refuses everything it is given - and a
        // test that only counted failures would pass with the resolution deleted entirely.
        var refusal = result.Diagnostics.ShouldHaveSingleItem();
        refusal.Code.ShouldBe(DiagnosticCode.DangerousPathRefused);
        refusal.Message.ShouldContain(@"C:\Windows");
    }

    /// <summary>
    /// A destination inside a protected location is refused, and named.
    /// </summary>
    /// <remarks>
    /// The executor's only guard call passed op.Source. op.Destination went straight to FileOps -
    /// so olddir = "C:/Windows/System32" was written to, with the guard holding that exact path in
    /// ProtectedRoots and never once being asked about it. Under copytruncate the archive and its
    /// ".part" sibling landed there with no diagnostic at all; under rename MoveFileEx put them
    /// there too, gated only by NTFS permissions, and the service runs as SYSTEM.
    /// </remarks>
    [Fact]
    public void ADestinationInsideAProtectedLocationIsRefused()
    {
        // Synthetic Windows paths, and no file on disk. The guard runs before any I/O, so the
        // refusal is reached without one - and a real temp path would be refused as not-absolute
        // by CheckPath on the Linux leg, which is the source arm answering, not the destination.
        var plan = new JobPlan
        {
            JobName = "iis",
            MatchedFiles = 1,
            Operations =
            [
                new PlannedOp
                {
                    Action = PlannedAction.Rename,
                    Source = @"C:\logs\app.log",
                    Destination = @"C:\Windows\System32\app.log.1",
                    Reason = "rotating",
                },
            ],
        };

        var result = new PlanExecutor(
                _journal,
                new PathGuard(new GuardOptions { ProtectedRoots = [@"C:\Windows"] }),
                _clock)
            .Execute(plan, Job(), dryRun: false);

        result.Completed.ShouldBe(0);
        result.Failed.ShouldBe(1);

        // On the subject, not the count. The source is a permitted path, so the only thing that
        // can refuse here is the destination check - and naming it in the assertion is what makes
        // deleting that check turn this red rather than leaving it green for another reason.
        var refusal = result.Diagnostics.ShouldHaveSingleItem();
        refusal.Code.ShouldBe(DiagnosticCode.DangerousPathRefused);
        refusal.Message.ShouldContain(@"C:\Windows\System32\app.log.1");
    }

    /// <summary>A path that resolves to itself is not rewritten on its way to the guard.</summary>
    /// <remarks>
    /// The common case, and the one a rebuild would quietly break: nothing moved, so the guard
    /// must see exactly the path the plan named.
    /// </remarks>
    [Fact]
    public void AnOrdinaryPathIsUnaffectedByTheResolution()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Applies a real plan, and CheckPath wants Windows paths.");

        new PlanExecutor(_journal, new PathGuard(new GuardOptions()), _clock)
            .Execute(PlanDeleting("a.log.gz"), Job(), dryRun: false)
            .Completed.ShouldBe(1);
    }
}
