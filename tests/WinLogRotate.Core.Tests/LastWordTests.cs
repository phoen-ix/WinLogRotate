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
/// Turning the record of a run back into the operations it performed.
/// </summary>
/// <remarks>
/// <para>
/// The journal writes two records per destructive operation - plan, then apply - so a run killed
/// between them leaves a readable account of an intention that was never carried out. Every
/// reader of it counted the records instead: <c>winlogrotate journal</c> printed both halves as
/// two lines identical but for the timestamp and reported twice the count, and the GUI's History
/// page did the same in a grid.
/// </para>
/// <para>
/// Every fixture here is a journal directory on disk, written by a real <c>PlanExecutor</c>
/// through a real <c>JournalWriter</c> and read back by a real <c>JournalReader</c>. Hand-built
/// events would assert only the pairing its author believed in, and the pairing is the thing
/// under test.
/// </para>
/// </remarks>
public sealed class LastWordTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("winlogrotate-lastword-");
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 11, 3, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); } catch (IOException) { }
    }

    /// <summary>A resolver that claims every directory really lives somewhere protected.</summary>
    /// <remarks>
    /// How a genuine apply-phase failure is reached on a platform with no Win32: the executor's
    /// last-moment guard check asks where the file really is, this answers with a protected root,
    /// and the refusal is emitted before any I/O is attempted.
    /// </remarks>
    private sealed class Redirect : ILinkResolver
    {
        public LinkTarget Resolve(string path) => LinkTarget.At(@"C:\Windows\System32");
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
        Compress = false,
        CompressType = CompressType.None,
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

    /// <summary>Deletions of paths that are never touched - the guard refuses first.</summary>
    private static JobPlan Deleting(params string[] sources) => new()
    {
        JobName = "iis",
        MatchedFiles = sources.Length,
        Operations =
        [
            .. sources.Select(s => new PlannedOp
            {
                Action = PlannedAction.Delete,
                Source = s,
                Reason = "rotate = 3 exceeded",
                Bytes = 1024,
            }),
        ],
    };

    /// <summary>
    /// Runs a plan into the journal under one run id, and returns nothing - the record is the
    /// point.
    /// </summary>
    /// <param name="dryRun">
    /// False writes both halves of every operation, because the guard refuses at apply time.
    /// True writes the plan halves and stops, which is byte-for-byte what a run killed between
    /// the halves leaves behind - and the reason this needs no separate "kill the process"
    /// fixture.
    /// </param>
    private void Journal(string runId, params (bool DryRun, JobPlan Plan)[] steps)
    {
        using var journal = JournalWriter.Open(
            Path.Combine(_dir.FullName, "journal"), _clock, runId);

        var executor = new PlanExecutor(
            journal,
            new PathGuard(new GuardOptions { ProtectedRoots = [@"C:\Windows"] }),
            _clock,
            new Redirect());

        foreach (var (dryRun, plan) in steps)
        {
            executor.Execute(plan, Job(), dryRun);
        }
    }

    private void Journal(string runId, bool dryRun, JobPlan plan) =>
        Journal(runId, (dryRun, plan));

    private IReadOnlyList<CliEvent> Written() =>
        [.. new JournalReader(Path.Combine(_dir.FullName, "journal")).Read()];

    /// <summary>
    /// An operation that was carried out is one operation, however many records it took.
    /// </summary>
    /// <remarks>
    /// The defect, at its smallest. Two records, one delete - and every reader of this journal
    /// said two.
    /// </remarks>
    [Fact]
    public void AnOperationThatWasCarriedOutIsOneEntry()
    {
        Journal("RUNA", dryRun: false, Deleting(@"C:\logs\app.log.3"));

        var written = Written();
        written.Count.ShouldBe(2, "the record keeps the intention and the outcome");

        var collapsed = CliEventLastWord.Collapse(written);

        var only = collapsed.ShouldHaveSingleItem();
        only.Phase.ShouldBe(Phase.Apply, "the outcome is the last word, not the intention");
        only.Result.ShouldBe(OpResult.Failed);
    }

    /// <summary>
    /// An operation that was planned and never applied survives.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The assertion this whole design exists for, and the one that goes red if the fold is ever
    /// "simplified" into the predicate the live stream uses. <c>Settles</c> is false for a plan
    /// half with no result - correct for a watcher, because an apply half is coming, and
    /// catastrophic for a reader, because on a finished journal that record is the evidence of a
    /// run that died mid-operation.
    /// </para>
    /// <para>
    /// The abandoned run deletes the same paths as the completed one and differs only in its run
    /// id, so an identity that forgot to include the run would let the completed run's outcome
    /// swallow the abandoned run's evidence. That is not hypothetical: it is a nightly job
    /// deleting the same archive name every night.
    /// </para>
    /// </remarks>
    [Fact]
    public void AnOperationPlannedAndNeverAppliedSurvivesTheCollapse()
    {
        Journal("RUNA", dryRun: false, Deleting(@"C:\logs\app.log.3"));
        Journal("RUNB", dryRun: true, Deleting(@"C:\logs\app.log.3"));

        var collapsed = CliEventLastWord.Collapse(Written());

        collapsed.Count.ShouldBe(2, "one completed operation and one abandoned one");

        var abandoned = collapsed.Where(e => e.Run == "RUNB").ShouldHaveSingleItem();
        abandoned.Phase.ShouldBe(Phase.Plan);
        abandoned.Result.ShouldBeNull("nothing was ever said about how it went");
    }

    /// <summary>
    /// Collapsing something already collapsed changes nothing.
    /// </summary>
    /// <remarks>
    /// Relied upon by the GUI, which is versioned separately from the CLI it drives and cannot
    /// know whether the journal it was handed has been through this already. Idempotence is what
    /// lets it simply apply the rule instead of negotiating.
    /// </remarks>
    [Fact]
    public void CollapsingTwiceChangesNothing()
    {
        Journal("RUNA", dryRun: false, Deleting(@"C:\logs\a.log", @"C:\logs\b.log"));
        Journal("RUNB", dryRun: true, Deleting(@"C:\logs\c.log"));

        var once = CliEventLastWord.Collapse(Written());
        var twice = CliEventLastWord.Collapse(once);

        twice.ShouldBe(once);
    }

    /// <summary>
    /// The order the records were written in is the order they come back in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A journal is read chronologically or it is not a journal, and an abandoned operation is
    /// worth nothing if it is not reported next to what happened around it.
    /// </para>
    /// <para>
    /// The middle operation is the one that settles, so the two obvious wrong implementations
    /// both show: gathering the settled records and appending the abandoned ones puts b first,
    /// and the reverse puts it last. An earlier version of this test settled every operation and
    /// could not fail - the orderings only differ where the two kinds interleave.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheOrderTheRecordsWereWrittenInIsKept()
    {
        Journal(
            "RUNA",
            (DryRun: true, Plan: Deleting(@"C:\logs\a.log")),
            (DryRun: false, Plan: Deleting(@"C:\logs\b.log")),
            (DryRun: true, Plan: Deleting(@"C:\logs\c.log")));

        var written = Written();
        written.Count.ShouldBe(4, "one abandoned, one pair, one abandoned");

        var collapsed = CliEventLastWord.Collapse(written);

        collapsed.Select(e => e.Src)
            .ShouldBe([@"C:\logs\a.log", @"C:\logs\b.log", @"C:\logs\c.log"]);

        collapsed[1].Phase.ShouldBe(Phase.Apply, "and the settled one is reported by its outcome");
    }

    /// <summary>A sink that keeps what it was told, so the tee's account can be compared.</summary>
    private sealed class Watcher : Cli.Output.IOutputSink
    {
        public List<CliEvent> Events { get; } = [];

        public bool Verbose => true;

        public IReadOnlyList<CliDiagnostic> Diagnostics => [];

        public void Diagnostic(CliDiagnostic d) { }

        public void Event(CliEvent e) => Events.Add(e);

        public void Line(string text) { }

        public int Complete<T>(string verb, int exitCode, T? result) => exitCode;
    }

    /// <summary>
    /// What a watcher is told is exactly what the record settles.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The live channel and the persisted one now share one predicate, and this is the assertion
    /// that keeps them sharing it. The four tests in <c>RunEventStreamTests</c> pin the tee's
    /// behaviour; this pins that the behaviour is the rule's, so a change made for a reader's
    /// benefit cannot quietly leave the watcher behind.
    /// </para>
    /// <para>
    /// A whole run rather than a plan, because the two are only distinguishable where a record
    /// carries an apply phase and no result - which is what <c>run.start</c> and <c>job.start</c>
    /// are on a real run, and what a plan of deletes never produces. An earlier version of this
    /// test drove the executor alone, and every rule anyone would plausibly write agreed on its
    /// events, so it could not fail.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheTeeAnnouncesExactlyWhatTheRecordSettles()
    {
        var journal = new RecordingJournal();
        var sink = new Watcher();

        using (var tee = new Cli.Output.TeeJournal(journal, sink, _clock, dryRun: false))
        {
            // Matches nothing, so the run is its own brackets and reaches no Win32 at all - the
            // one shape of real, non-dry run this leg can execute.
            new RotationRunner(
                    tee, new PathGuard(new GuardOptions()),
                    StateStore.Load(Path.Combine(_dir.FullName, "state.json"), out _),
                    _clock, new NoArchives(), null, null, null, new NoFiles())
                .Run(
                    new LoadedConfig
                    {
                        Jobs = [Job() with { Kind = JobKind.Rotate, Paths = [@"C:\logs\app.log"] }],
                        Diagnostics = [],
                        Paths = InstallPaths.Resolve(_dir.FullName),
                        Quarantined = [],
                    },
                    new RunOptions { DryRun = false, Force = true });
        }

        journal.Entries.ShouldContain(
            e => e.Phase == Phase.Apply && e.Result == null,
            "the fixture must contain a record the two candidate rules disagree about");

        sink.Events.ShouldBe(journal.Entries.Where(CliEventLastWord.Settles));
    }

    private sealed class NoArchives : IArchiveSource
    {
        public MatchedFile? Find(string path) => null;

        public IReadOnlyList<MatchedFile> Glob(string pattern) => [];
    }

    private sealed class NoFiles : IFileSource
    {
        public EnumerationResult Resolve(string pattern) => new()
        {
            Files = [],
            Refusals = [],
        };
    }

    /// <summary>
    /// Every record the journal kept is still reachable without the rule.
    /// </summary>
    /// <remarks>
    /// The other half of the contract: collapsing is a reading decision and changes nothing
    /// about what is written. If this ever fails, the fix went into the writer.
    /// </remarks>
    [Fact]
    public void TheRecordItselfStillHoldsBothHalves()
    {
        Journal("RUNA", dryRun: false, Deleting(@"C:\logs\app.log.3"));

        var written = Written();

        written.Count(e => e.Phase == Phase.Plan).ShouldBe(1);
        written.Count(e => e.Phase == Phase.Apply).ShouldBe(1);
    }
}
