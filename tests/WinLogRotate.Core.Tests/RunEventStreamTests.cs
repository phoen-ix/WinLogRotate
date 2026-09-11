using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using WinLogRotate.Cli.Output;
using WinLogRotate.Contracts;
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
/// Whether a run says what it did on the channel a machine reads.
/// </summary>
/// <remarks>
/// <para>
/// It did not. <c>RunCommand</c> called <c>Output.Event</c> zero times and printed every
/// per-file line through <c>Output.Line</c> - the one channel <c>--json</c> discards on the
/// stated grounds that "the same information is carried as diagnostics and events". It was not:
/// nothing carried it. So <c>--json-stream</c>, a flag that exists for exactly one caller, was
/// silent about the only verb that caller runs for real work.
/// </para>
/// <para>
/// The tee is what closes it, and these tests drive the real engine through it rather than
/// asserting against hand-made events, because the defect was never in what an event looks like
/// - it was in whether anything was listening.
/// </para>
/// </remarks>
public sealed class RunEventStreamTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("winlogrotate-stream-");
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 11, 3, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); } catch (IOException) { }
    }

    /// <summary>Everything a sink was told, kept apart by channel.</summary>
    private sealed class Watcher : IOutputSink
    {
        private readonly List<CliDiagnostic> _diagnostics = [];

        public List<string> Lines { get; } = [];

        public List<CliEvent> Events { get; } = [];

        public bool Verbose => true;

        public IReadOnlyList<CliDiagnostic> Diagnostics => _diagnostics;

        public void Diagnostic(CliDiagnostic d) => _diagnostics.Add(d);

        public void Event(CliEvent e) => Events.Add(e);

        public void Line(string text) => Lines.Add(text);

        public int Complete<T>(string verb, int exitCode, T? result) => exitCode;
    }

    /// <summary>Files that exist only as names, so a rotation can be planned anywhere.</summary>
    private sealed class Patterns(int count) : IFileSource
    {
        public EnumerationResult Resolve(string pattern) => new()
        {
            Files = [.. Enumerable.Range(0, count).Select(i => new MatchedFile
            {
                Path = pattern.Replace("*", i.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal),
                Length = 4096,
                LastWriteUtc = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero),
            })],
            Refusals = [],
        };
    }

    private sealed class NoArchives : IArchiveSource
    {
        public MatchedFile? Find(string path) => null;

        public IReadOnlyList<MatchedFile> Glob(string pattern) => [];
    }

    private static EffectiveJob Job() => new()
    {
        Name = "app",
        Kind = JobKind.Rotate,
        Paths = [@"C:\logs\app*.log"],
        Enabled = true,
        Schedule = Schedule.Daily,
        Weekday = 0,
        MonthDay = 0,
        Rotate = 2,
        Start = 1,
        MaxAge = null,
        MinAge = null,
        MinSize = null,
        MaxSize = null,
        SizeThreshold = 0,
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
        PreRotate = [],
        PostRotate = [],
        HookTimeout = TimeSpan.FromSeconds(60),
        AllowDangerous = [],
    };

    /// <summary>
    /// A real dry run, journaled through the tee, with the sink the CLI would have passed.
    /// </summary>
    /// <remarks>
    /// Dry, because applying a plan is Win32 and this has to hold on the Linux leg - and because
    /// dry is the run that most needs a voice: it produces nothing but its account of itself,
    /// and it journals to <c>NullJournal</c>, so anything the tee forgets to do is invisible
    /// exactly where it matters most.
    /// </remarks>
    private (RunReport Report, RecordingJournal Journal, Watcher Sink) Run(int files = 4)
    {
        var journal = new RecordingJournal();
        var sink = new Watcher();

        using var tee = new TeeJournal(journal, sink, _clock, dryRun: true);

        var report = new RotationRunner(
                tee, new PathGuard(new GuardOptions()),
                StateStore.Load(Path.Combine(_dir.FullName, "state.json"), out _),
                _clock, new NoArchives(), null, null, null, new Patterns(files))
            .Run(
                new LoadedConfig
                {
                    Jobs = [Job()],
                    Diagnostics = [],
                    Paths = InstallPaths.Resolve(_dir.FullName),
                    Quarantined = [],
                },
                new RunOptions { DryRun = true, Force = true });

        return (report, journal, sink);
    }

    /// <summary>
    /// The sink is told about every operation the run planned.
    /// </summary>
    /// <remarks>
    /// The assertion that is the whole milestone. Before the tee this count was zero for any
    /// number of planned operations, and the stream the GUI tails held nothing at all.
    /// </remarks>
    [Fact]
    public void EveryPlannedOperationReachesTheSink()
    {
        var (report, _, sink) = Run();

        var planned = report.Plans.SelectMany(p => p.Operations).Select(o => o.Source).ToList();

        // Self-check: an empty plan would make every assertion below true for the wrong reason.
        planned.Count.ShouldBeGreaterThan(2, "the fixture must actually plan work");

        var announced = sink.Events
            .Where(e => e.Src is not null)
            .Select(e => e.Src!)
            .ToList();

        foreach (var source in planned)
        {
            announced.ShouldContain(source);
        }
    }

    /// <summary>
    /// And about the run and job it happened inside.
    /// </summary>
    /// <remarks>
    /// Brackets rather than bare operations, so a watcher can tell a run that finished from one
    /// that was killed. The GUI's pane is written against exactly this.
    /// </remarks>
    [Fact]
    public void TheRunAndJobBracketsReachTheSinkToo()
    {
        var (_, _, sink) = Run();

        var ops = sink.Events.Select(e => e.Operation).ToList();

        ops.ShouldContain(Op.RunStart);
        ops.ShouldContain(Op.JobStart);
        ops.ShouldContain(Op.JobEnd);
        ops.ShouldContain(Op.RunEnd);
    }

    /// <summary>
    /// Every event the sink is given carries a timestamp and a run id.
    /// </summary>
    /// <remarks>
    /// The trap the tee exists around. The engine writes both fields empty and lets the journal
    /// stamp them - but a dry run journals to <c>NullJournal</c>, which stamps nothing, so
    /// teeing the entry as handed over would send blank timestamps on precisely the run a
    /// watcher is watching. The tee stamps before it forwards, and the journal's own stamping is
    /// left alone.
    /// </remarks>
    [Fact]
    public void EveryEventReachingTheSinkIsStamped()
    {
        var (_, journal, sink) = Run();

        sink.Events.ShouldNotBeEmpty();

        foreach (var e in sink.Events)
        {
            e.Run.ShouldBe(journal.RunId);
            DateTimeOffset.TryParse(e.Ts, CultureInfo.InvariantCulture, out _)
                .ShouldBeTrue($"'{e.Ts}' is not a timestamp");
        }
    }

    // ---- the verb ------------------------------------------------------------------------------

    /// <summary>
    /// The real <c>run</c> verb, with a sink that keeps the two channels apart.
    /// </summary>
    /// <remarks>
    /// The job's paths are spelled for Windows and match nothing here, which is the point: what
    /// is being asserted is which channel the verb reports through, and that is settled before
    /// a single file is touched.
    /// </remarks>
    private Watcher RunTheVerb(bool dryRun)
    {
        var conf = Directory.CreateDirectory(Path.Combine(_dir.FullName, "conf"));
        File.WriteAllText(Path.Combine(conf.FullName, "config.toml"), "schema = 1\n");

        var confd = Directory.CreateDirectory(Path.Combine(conf.FullName, "conf.d"));
        File.WriteAllText(Path.Combine(confd.FullName, "app.toml"), """
            schema = 1
            [job]
            name      = "app"
            paths     = ["C:/logs/app*.log"]
            missingok = true
            """);

        var sink = new Watcher();
        var parse = Cli.Commands.CommandTree.Build().Parse(["run", "--no-notify", "--no-event-log"]);

        Cli.Commands.RunCommand.Run(
            new Cli.Commands.CommandContext(sink, parse),
            new RunOptions { DryRun = dryRun, Force = true },
            conf.FullName,
            Path.Combine(_dir.FullName, "state.json"));

        return sink;
    }

    /// <summary>
    /// The verb itself gives the sink events, and not only under <c>--dry-run</c>.
    /// </summary>
    /// <remarks>
    /// Where the wiring actually has to be. The tee could be correct in every detail and reach
    /// nothing, which is the state this milestone found: <c>Output.Event</c> was called twice in
    /// the whole CLI, both times by the notification phase, and never by the verb that does the
    /// work. Both runs are checked because they take different journals - a real one, and the
    /// <c>NullJournal</c> a dry run gets - and the tee has to wrap both.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheRunVerbReportsThroughEvents(bool dryRun)
    {
        var sink = RunTheVerb(dryRun);

        sink.Events.Select(e => e.Operation).ShouldContain(Op.RunStart);
        sink.Events.Select(e => e.Operation).ShouldContain(Op.RunEnd);
    }

    /// <summary>
    /// And does not hand-print its own account beside them.
    /// </summary>
    /// <remarks>
    /// <c>RunCommand</c> printed one header per job and one line per operation through
    /// <c>Output.Line</c> - the channel <c>--json</c> discards - in a format of its own, while
    /// <c>TextOutputSink.Event</c> held a renderer for the same facts whose arms for delete,
    /// compress, rename, copytruncate, copy, create and mkdir were all unreachable. Two
    /// renderers for one truth. Teeing without deleting the loop doubles every line a human
    /// sees, so the header is what this watches: it was printed for every plan, including a
    /// job like this one that matched nothing.
    /// </remarks>
    [Fact]
    public void TheRunVerbDoesNotAlsoHandPrintTheAccount()
    {
        var sink = RunTheVerb(dryRun: true);

        // Self-check: the verb must have got far enough to have had a plan to print.
        sink.Events.Select(e => e.Operation).ShouldContain(Op.JobStart);

        sink.Lines.ShouldNotContain(
            l => l.Contains("file(s) matched", StringComparison.Ordinal),
            "the per-operation report belongs to the sink now");
    }

    // ---- the sentence -------------------------------------------------------------------------

    /// <summary>What the text sink writes for one event.</summary>
    private static string Rendered(CliEvent e)
    {
        var written = new StringWriter();
        new TextOutputSink(verbose: true, color: false, written).Event(e);
        return written.ToString().Trim();
    }

    /// <summary>
    /// An operation is described by what became of it, not only by when it was described.
    /// </summary>
    /// <remarks>
    /// The verb was "would" in the plan phase and "did" otherwise, which was true while the only
    /// events reaching a sink were the notification phase's. It is not true of an operation that
    /// threw: a delete that failed on a share violation was rendered "did delete C:\logs\app.log"
    /// with the truth left to a diagnostic printed underneath it.
    /// </remarks>
    [Theory]
    [InlineData(Phase.Plan, null, "would delete")]
    [InlineData(Phase.Apply, OpResult.Ok, "did delete")]
    [InlineData(Phase.Apply, OpResult.Failed, "failed to delete")]
    public void AnOperationIsDescribedByWhatBecameOfIt(string phase, string? result, string expected)
    {
        Rendered(Half(phase, result)).ShouldBe(
            $@"[app] {expected} C:\logs\app.log.3  (rotate 2)");
    }

    /// <summary>
    /// A file that was considered and left alone says so.
    /// </summary>
    /// <remarks>
    /// A skip is journaled as Op.Plan - the one PlannedAction that PlanExecutor does not map to a
    /// named operation - so it fell to Describe's fallback arm and rendered as
    /// "plan C:\logs\app.log", which names the phase where a human expects the decision.
    /// "Why was this file not touched?" is the question a dry run exists to answer.
    /// </remarks>
    [Fact]
    public void AFileLeftAloneIsDescribedAsSkipped()
    {
        var skip = Half(Phase.Plan, OpResult.Skipped) with
        {
            Operation = Op.Plan,
            Reason = "newest file - the application is still writing it",
        };

        Rendered(skip).ShouldBe(
            @"[app] skip C:\logs\app.log.3  (newest file - the application is still writing it)");
    }

    // ---- the last word ------------------------------------------------------------------------

    private static CliEvent Half(string phase, string? result) => new()
    {
        Ts = string.Empty,
        Run = string.Empty,
        Operation = Op.Delete,
        Phase = phase,
        Result = result,
        Job = "app",
        Src = @"C:\logs\app.log.3",
        Reason = "rotate 2",
    };

    private static (RecordingJournal Journal, Watcher Sink, TeeJournal Tee) Tee(bool dryRun)
    {
        var journal = new RecordingJournal();
        var sink = new Watcher();
        return (journal, sink, new TeeJournal(journal, sink, TimeProvider.System, dryRun));
    }

    /// <summary>
    /// A real run announces an operation once - when it is over, not when it is intended.
    /// </summary>
    /// <remarks>
    /// The journal is the forensic record and keeps both halves, so a run killed between them
    /// leaves evidence of an intention that was never carried out. A watcher wants the last
    /// word: told "would delete" and then nothing, an operator cannot tell a completed deletion
    /// from an abandoned one.
    /// </remarks>
    [Fact]
    public void AnOperationIsAnnouncedOnceAndOnlyWhenItIsSettled()
    {
        var (journal, sink, tee) = Tee(dryRun: false);
        using (tee)
        {
            tee.Write(Half(Phase.Plan, null));
            tee.Write(Half(Phase.Apply, OpResult.Ok));
        }

        journal.Entries.Count.ShouldBe(2, "the record keeps the intention and the outcome");

        var announced = sink.Events.ShouldHaveSingleItem();
        announced.Phase.ShouldBe(Phase.Apply);
        announced.Result.ShouldBe(OpResult.Ok);
    }

    /// <summary>
    /// A dry run's intention is its outcome, so the plan half is the last word.
    /// </summary>
    /// <remarks>
    /// Suppressing the plan half unconditionally would leave <c>--dry-run</c> announcing nothing
    /// but its brackets - which is the state this milestone is fixing, reintroduced one layer
    /// down and harder to see.
    /// </remarks>
    [Fact]
    public void ADryRunAnnouncesThePlanHalf()
    {
        var (_, sink, tee) = Tee(dryRun: true);
        using (tee)
        {
            tee.Write(Half(Phase.Plan, null));
        }

        sink.Events.ShouldHaveSingleItem().Phase.ShouldBe(Phase.Plan);
    }

    /// <summary>
    /// A decision taken at plan time and never applied is announced at plan time.
    /// </summary>
    /// <remarks>
    /// Skips, guard refusals and honoured overrides are all recorded in the plan phase with a
    /// result, and no apply half ever follows. Waiting for one would drop "why was this file not
    /// touched?" - the most common question a run has to answer - from the stream entirely.
    /// </remarks>
    [Theory]
    [InlineData(OpResult.Skipped)]
    [InlineData(OpResult.Failed)]
    public void APlanTimeDecisionIsAnnouncedWhenItIsTaken(string result)
    {
        var (_, sink, tee) = Tee(dryRun: false);
        using (tee)
        {
            tee.Write(Half(Phase.Plan, result));
        }

        sink.Events.ShouldHaveSingleItem().Result.ShouldBe(result);
    }

    /// <summary>The journal is given the same entry the sink is, stamped, and nothing else.</summary>
    [Fact]
    public void TheJournalSeesEverythingTheSinkDoes()
    {
        var (journal, sink, tee) = Tee(dryRun: true);
        using (tee)
        {
            tee.Write(Half(Phase.Plan, null));
        }

        journal.Entries.ShouldHaveSingleItem().ShouldBe(sink.Events.ShouldHaveSingleItem());
        journal.Entries[0].Run.ShouldBe(journal.RunId);
    }
}
