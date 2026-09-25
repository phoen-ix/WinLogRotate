using Microsoft.Extensions.Time.Testing;
using Shouldly;
using WinLogRotate.Contracts;
using WinLogRotate.Core;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Engine;
using WinLogRotate.Core.Globbing;
using WinLogRotate.Core.Journaling;
using WinLogRotate.Core.Safety;
using WinLogRotate.Core.State;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// A run writes down what it did, even when some of it failed.
/// </summary>
/// <remarks>
/// <c>docs/logrotate-compatibility.md</c> lists this among the behaviours reproduced exactly:
/// "Exit codes 0, 1, 3 - unchanged, and 1 still writes state - a failing job must not make the
/// healthy ones re-rotate forever." <c>RotationRunner</c> says the same in its own words. Nothing
/// asserted it, which the rule added alongside this test is what found.
/// </remarks>
[Collection(RotationGateCollection.Name)]
public sealed class RunStateTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("winlogrotate-runstate-");
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 3, 1, 2, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); } catch (IOException) { }
    }

    private string StatePath => Path.Combine(_dir.FullName, "state.json");

    /// <summary>A run with a failure in it still records the clocks it advanced.</summary>
    /// <remarks>
    /// The alternative is the re-rotation storm: one permanently broken job, and every healthy log
    /// on the machine rotates again on every run because nothing was written down. That is
    /// upstream's reasoning too, and it is why exit 1 is not exit 2.
    /// </remarks>
    [Fact]
    public void AFailedRunStillWritesItsState()
    {
        var state = StateStore.Load(StatePath, out _);

        // A path the guard refuses, so the run has a genuine failure in it and no Win32 is
        // reached on this leg.
        var report = new RotationRunner(
                new NullJournal(), new PathGuard(new GuardOptions { ProtectedRoots = ["C:\\Windows"] }),
                state, _clock, new NoArchives(), null, null, null, new NoFiles())
            .Run(
                new LoadedConfig
                {
                    Jobs = [Fixtures.Job() with { Kind = JobKind.Rotate, Paths = [@"C:\Windows\*.log"] }],
                    Diagnostics = [],
                    Paths = InstallPaths.Resolve(_dir.FullName),
                    Quarantined = [],
                },
                new RunOptions { DryRun = false });

        report.Failed.ShouldBeGreaterThan(0, "the fixture has to actually fail for this to mean anything");
        report.ExitCode.ShouldBe(ExitCode.Errors);

        File.Exists(StatePath).ShouldBeTrue(
            "a failing job must not make the healthy ones re-rotate for ever");
    }

    /// <summary>
    /// A state file from a newer build refuses the run cleanly and is left exactly as it was.
    /// </summary>
    /// <remarks>
    /// <c>StateStore.Load</c> threw <c>InvalidOperationException</c> for a version it did not know,
    /// and nothing caught it: every night after a downgrade was exit 4 and LR1006, "a defect in the
    /// product". It is the same shape as a broken configuration - nothing was attempted - and now
    /// answers the same way, with its own code and the file untouched for the build that wrote it.
    /// </remarks>
    [Fact]
    public void AStateFileFromANewerBuildRefusesTheRunAndIsLeftAlone()
    {
        var conf = Directory.CreateDirectory(Path.Combine(_dir.FullName, "conf"));
        File.WriteAllText(Path.Combine(conf.FullName, "config.toml"), "schema = 1\n");
        Directory.CreateDirectory(Path.Combine(conf.FullName, "conf.d"));
        File.WriteAllText(StatePath, "{\"version\": 2, \"paths\": {}}");

        var sink = new Recorder();
        var parse = Cli.Commands.CommandTree.Build().Parse(["run", "--no-notify", "--no-event-log"]);

        var exit = Cli.Commands.RunCommand.Run(
            new Cli.Commands.CommandContext(sink, parse), new RunOptions(), conf.FullName, StatePath);

        exit.ShouldBe(ExitCode.ConfigInvalid);
        sink.Diagnostics.ShouldContain(d => d.Code == DiagnosticCode.StateUnusable && d.Severity == Severity.Error);
        File.ReadAllText(StatePath).ShouldContain("\"version\": 2", Case.Sensitive, "the file is for the build that wrote it");
    }

    /// <summary>
    /// A state file something else holds open refuses the run rather than starting over.
    /// </summary>
    /// <remarks>
    /// The tempting answer is LR3107's fresh baseline. But a lock at 03:00 is a backup agent, and it
    /// has let go by the time the run saves - so the baseline would replace every clock and every
    /// NUL-fill verdict in a file that could have been read tomorrow. Refusing costs one night,
    /// which re-baselining would have cost anyway.
    /// </remarks>
    [Fact]
    public void AStateFileHeldOpenRefusesTheRunRatherThanStartingOver()
    {
        var conf = Directory.CreateDirectory(Path.Combine(_dir.FullName, "conf"));
        File.WriteAllText(Path.Combine(conf.FullName, "config.toml"), "schema = 1\n");
        Directory.CreateDirectory(Path.Combine(conf.FullName, "conf.d"));
        File.WriteAllText(StatePath, "{\"version\": 1, \"paths\": {\"X\": {\"path\": \"X\", \"nulFill\": \"Confirmed\"}}}");
        using var held = new FileStream(StatePath, FileMode.Open, FileAccess.Read, FileShare.None);

        var sink = new Recorder();
        var parse = Cli.Commands.CommandTree.Build().Parse(["run", "--no-notify", "--no-event-log"]);

        var exit = Cli.Commands.RunCommand.Run(
            new Cli.Commands.CommandContext(sink, parse), new RunOptions(), conf.FullName, StatePath);

        exit.ShouldBe(ExitCode.ConfigInvalid);
        sink.Diagnostics.ShouldContain(d => d.Code == DiagnosticCode.StateUnusable);
        sink.Diagnostics.ShouldNotContain(d => d.Code == DiagnosticCode.StateUnreadable, "nothing was started over");
    }

    /// <summary>A state file that will not parse is named where it is, --state or not.</summary>
    /// <remarks>
    /// The refusal above named the <c>--state</c> file; the fresh-baseline warning beside it named
    /// the installed one, which is not the file that would not parse.
    /// </remarks>
    [Fact]
    public void AStateFileThatWillNotParseIsNamedWhereItIs()
    {
        var conf = Directory.CreateDirectory(Path.Combine(_dir.FullName, "conf"));
        File.WriteAllText(Path.Combine(conf.FullName, "config.toml"), "schema = 1\n");
        Directory.CreateDirectory(Path.Combine(conf.FullName, "conf.d"));
        File.WriteAllText(StatePath, "{ this is not json");

        var sink = new Recorder();
        var parse = Cli.Commands.CommandTree.Build().Parse(["run", "--dry-run", "--no-notify", "--no-event-log"]);

        Cli.Commands.RunCommand.Run(
            new Cli.Commands.CommandContext(sink, parse), new RunOptions { DryRun = true }, conf.FullName, StatePath);

        sink.Diagnostics.Single(d => d.Code == DiagnosticCode.StateUnreadable).Path.ShouldBe(StatePath);
    }

    /// <summary>A sink that keeps what it was told, for a verb driven in-process.</summary>
    private sealed class Recorder : Cli.Output.IOutputSink
    {
        private readonly List<CliDiagnostic> _diagnostics = [];

        public bool Verbose => false;

        public IReadOnlyList<CliDiagnostic> Diagnostics => _diagnostics;

        public void Diagnostic(CliDiagnostic d) => _diagnostics.Add(d);

        public void Event(CliEvent e) { }

        public void Line(string text) { }

        public int Complete<T>(string verb, int exitCode, T? result) => exitCode;
    }

    /// <summary>A dry run writes nothing, which is the other half of the same promise.</summary>
    [Fact]
    public void ADryRunWritesNoState()
    {
        var state = StateStore.Load(StatePath, out _);

        new RotationRunner(
                new NullJournal(), new PathGuard(new GuardOptions()),
                state, _clock, new NoArchives(), null, null, null, new NoFiles())
            .Run(
                new LoadedConfig
                {
                    Jobs = [Fixtures.Job() with { Kind = JobKind.Rotate, Paths = [@"C:\logs\app.log"] }],
                    Diagnostics = [],
                    Paths = InstallPaths.Resolve(_dir.FullName),
                    Quarantined = [],
                },
                new RunOptions { DryRun = true, Force = true });

        File.Exists(StatePath).ShouldBeFalse("a dry run changes nothing, state included");
    }

    /// <summary>
    /// A clock that could not be written is a failed run, not a defect in the product.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing caught <c>state.Save</c>, and <c>RunCommand</c> does not guard the runner, so a
    /// full disk reached <c>CommandContext.Guarded</c> and came out as <c>LR1006</c> - documented
    /// as "a defect in the product, not a problem with the machine or the configuration", with a
    /// remedy reading "nothing about what was or was not done can be relied on". Both false: the
    /// rotations happened, and they are in the journal.
    /// </para>
    /// <para>
    /// The consequence is the part that arrives later. With no clock written, every log the run
    /// rotated is due again on the next one - the storm <c>RotationRunner</c>'s own comment says
    /// writing state on failure exists to prevent. A full disk is exactly when it fires.
    /// </para>
    /// <para>
    /// The obstruction is a directory where <c>AtomicJson</c> wants its temporary sibling, which
    /// is the portable idiom <c>SecretCommandTests</c> already uses - it fails the same way on
    /// both legs.
    /// </para>
    /// </remarks>
    [Fact]
    public void AClockThatCouldNotBeWrittenIsTheRunsResult()
    {
        var state = StateStore.Load(StatePath, out _);

        Directory.CreateDirectory(StatePath + ".tmp");

        var reported = new List<CliDiagnostic>();

        var report = Run(state, reported);

        report.Failed.ShouldBeGreaterThan(0);
        report.ExitCode.ShouldBe(ExitCode.Errors, "the run happened; writing down that it did failed");

        var diagnostic = reported.ShouldHaveSingleItem();

        diagnostic.Code.ShouldBe(DiagnosticCode.StateNotSaved);
        diagnostic.Severity.ShouldBe(Severity.Error);
        diagnostic.Path.ShouldBe(StatePath);
        diagnostic.Remedy.ShouldNotBeNull().ShouldContain("due again");
    }

    /// <summary>
    /// The journal does not say the run succeeded before the clock is written.
    /// </summary>
    /// <remarks>
    /// <c>run.end</c> carried <c>Result = Ok</c> and was written before the save, so the three
    /// channels <c>docs/diagnostics.md</c> defines gave three answers at once: the journal said
    /// the run succeeded, the state file said it never happened, and the exit code said nobody
    /// knew.
    /// </remarks>
    [Fact]
    public void TheJournalDoesNotCallARunOkBeforeItsClockIsWritten()
    {
        var state = StateStore.Load(StatePath, out _);

        Directory.CreateDirectory(StatePath + ".tmp");

        var journal = new Recording();

        Run(state, [], journal);

        journal.Entries
            .Where(e => e.Operation == Op.RunEnd)
            .ShouldHaveSingleItem()
            .Result.ShouldBe(OpResult.Failed);
    }

    /// <summary>
    /// A journal that cannot be opened does not stop the rotation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>JournalWriter.Open</c> was called with nothing around it, so a journal directory that
    /// had become a file - or a disk with nothing left on it - took the whole run down before a
    /// single log was touched, and reported <c>LR1006</c>: "a defect in the product, not a
    /// problem with the machine". Measured on the real CLI, the same fixture as here: exit 4 and
    /// a null payload before, exit 0 and a completed run after.
    /// </para>
    /// <para>
    /// <c>docs/diagnostics.md</c> names three channels, each for a different reader, and this
    /// one's is "whoever is asking what happened to a specific file". Losing it costs that reader
    /// and nobody else. <c>JournalMaintenance</c>, ten lines above the open, has always reported
    /// its own failures this way; opening the file was the half nobody had guarded.
    /// </para>
    /// </remarks>
    [Fact]
    public void AJournalThatCannotBeOpenedDoesNotStopTheRotation()
    {
        var confd = Directory.CreateDirectory(Path.Combine(_dir.FullName, "conf.d"));
        File.WriteAllText(Path.Combine(_dir.FullName, "config.toml"), "schema = 1\n");
        File.WriteAllText(Path.Combine(confd.FullName, "a.toml"), """
            schema = 1
            [job]
            name      = "app"
            kind      = "manage"
            paths     = ["C:/app/logs/*.log"]
            missingok = true
            """);

        // A file where the journal directory goes, which is what a botched restore leaves behind.
        File.WriteAllText(Path.Combine(_dir.FullName, "journal"), "not a directory");

        var sink = new Collecting();
        var parse = Cli.Commands.CommandTree.Build().Parse(["run", "--no-notify", "--no-event-log"]);

        var exit = Cli.Commands.RunCommand.Run(
            new Cli.Commands.CommandContext(sink, parse),
            new RunOptions(),
            _dir.FullName,
            StatePath);

        exit.ShouldBe(ExitCode.Ok, "the logs still needed rotating");

        var diagnostic = sink.Diagnostics.ShouldHaveSingleItem();

        diagnostic.Code.ShouldBe(DiagnosticCode.JournalUnavailable);
        diagnostic.Severity.ShouldBe(Severity.Warning);

        File.Exists(StatePath).ShouldBeTrue("the run happened, so its clocks were written");
    }

    private sealed class Collecting : Cli.Output.IOutputSink
    {
        private readonly List<CliDiagnostic> _diagnostics = [];

        public bool Verbose => false;

        public IReadOnlyList<CliDiagnostic> Diagnostics => _diagnostics;

        public void Diagnostic(CliDiagnostic d) => _diagnostics.Add(d);

        public void Event(CliEvent evt)
        {
        }

        public void Line(string text)
        {
        }

        public int Complete<T>(string verb, int exitCode, T? result) => exitCode;
    }

    private RunReport Run(StateStore state, List<CliDiagnostic> reported, IJournal? journal = null)
    {
        var runner = new RotationRunner(
            journal ?? new NullJournal(),
            new PathGuard(new GuardOptions { ProtectedRoots = ["C:\\Windows"] }),
            state, _clock, new NoArchives(), null, null, null, new NoFiles());

        var report = runner.Run(
            new LoadedConfig
            {
                Jobs = [Fixtures.Job() with { Kind = JobKind.Rotate, Paths = [@"C:\logs\app.log"] }],
                Diagnostics = [],
                Paths = InstallPaths.Resolve(_dir.FullName),
                Quarantined = [],
            },
            new RunOptions { DryRun = false });

        reported.AddRange(report.Diagnostics);
        return report;
    }

    private sealed class Recording : IJournal
    {
        public List<CliEvent> Entries { get; } = [];

        public string RunId => "test";

        public void Write(CliEvent entry) => Entries.Add(entry);

        public void Dispose()
        {
        }
    }

    private sealed class NoArchives : IArchiveSource
    {
        public MatchedFile? Find(string path) => null;

        public IReadOnlyList<MatchedFile> Glob(string pattern) => [];
    }

    private sealed class NoFiles : IFileSource
    {
        public EnumerationResult Resolve(string pattern) => new() { Files = [], Refusals = [] };
    }
}
