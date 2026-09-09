using Shouldly;
using WinLogRotate.Contracts;
using WinLogRotate.Core;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Journaling;
using WinLogRotate.Core.Safety;
using WinLogRotate.Core.State;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// The per-job opt-out, from the configuration side: how <c>notify</c> merges, and the two
/// things it must never be able to reach.
/// </summary>
public sealed class NotifyOptOutTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("winlogrotate-optout-");

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); } catch (IOException) { }
    }

    private LoadedConfig Load(string configToml, params (string File, string Toml)[] jobs)
    {
        File.WriteAllText(Path.Combine(_dir.FullName, "config.toml"), configToml);
        var confd = Directory.CreateDirectory(Path.Combine(_dir.FullName, "conf.d"));
        foreach (var (name, toml) in jobs)
        {
            File.WriteAllText(Path.Combine(confd.FullName, name), toml);
        }

        return ConfigLoader.Load(
            InstallPaths.Resolve(_dir.FullName), new PathGuard(new GuardOptions()),
            quarantineBadFiles: false);
    }

    private static string Job(string name, string extra = "") => $"""
        schema = 1
        [job]
        name  = "{name}"
        kind  = "manage"
        paths = ["C:/logs/{name}/*.log"]
        {extra}
        """;

    // ---- the merge -----------------------------------------------------------------------

    [Fact]
    public void NotifyDefaultsToTrueWhenNothingSaysAnything()
    {
        // A forgotten value means notifications on, which is the safe direction and the reason
        // this member is not `required` like almost every other one.
        Load("schema = 1\n", ("a.toml", Job("a")))
            .Jobs.ShouldHaveSingleItem().Notify.ShouldBeTrue();
    }

    [Fact]
    public void AJobCanTurnItselfOff()
    {
        Load("schema = 1\n", ("a.toml", Job("a", "notify = false")))
            .Jobs.ShouldHaveSingleItem().Notify.ShouldBeFalse();
    }

    [Fact]
    public void DefaultsCanTurnItOffAndAJobCanTurnItBackOn()
    {
        // Opt-in mode, which falls out of the three-level merge for free. Driven through the
        // real binder rather than through SettingsMerge alone, because BindJob copies every
        // setting by hand into JobConfig - a member bound and not copied there binds and is
        // then silently discarded.
        var config = Load("schema = 1\n[defaults]\nnotify = false\n",
            ("quiet.toml", Job("quiet")),
            ("loud.toml", Job("loud", "notify = true")));

        config.Jobs.Single(j => j.Name == "quiet").Notify.ShouldBeFalse();
        config.Jobs.Single(j => j.Name == "loud").Notify.ShouldBeTrue();
    }

    // ---- the name that must not exist ------------------------------------------------------

    [Fact]
    public void AJobCannotBeCalledStar()
    {
        // "*" is how findings belonging to the run rather than to any job are recorded. A job of
        // that name would share an outcome, an aggregation group and a fingerprint with them -
        // so an unrelated job going green could report the configuration as recovered while
        // conf.d was still world-writable. This refusal is what makes the doc comment on
        // NotifyStateDocument.RunScope true, and it is what defends the run scope from a mute.
        var config = Load("schema = 1\n", ("star.toml", Job(NotifyStateDocument.RunScope)));

        config.Jobs.ShouldBeEmpty();
        config.Diagnostics.ShouldContain(d =>
            d.Code == DiagnosticCode.ConfigInvalid && d.Severity == Severity.Error);
    }

    [Fact]
    public void TheStarRefusalUsesTheConstantSoTheCouplingIsGreppable()
    {
        // If RunScope is ever changed, this refusal has to move with it.
        NotifyStateDocument.RunScope.ShouldBe("*");
    }

    // ---- the journal's own upkeep -----------------------------------------------------------

    [Fact]
    public void TheJournalIsQuietUnlessAskedFor()
    {
        // A tool that pages the on-call about its own bookkeeping is a tool people mute. The
        // failure still reaches stdout, the Event Log and the journal.
        Load("schema = 1\n").Journal.Notify.ShouldBeFalse();
    }

    [Fact]
    public void TheJournalCanBeAskedFor()
    {
        Load("schema = 1\n[journal]\nnotify = true\n").Journal.Notify.ShouldBeTrue();
    }

    [Fact]
    public void JournalMaintenanceHasAJobNameToBeMutedUnder()
    {
        // Attributed rather than run-scoped, so it obeys the same rules as any other job and
        // stops merging into the run scope's fingerprint - where a full journal directory made
        // "the configuration is broken" look like a different problem each time it changed.
        JournalMaintenance.JobName.ShouldNotBeNullOrWhiteSpace();
        JournalMaintenance.JobName.ShouldNotBe(NotifyStateDocument.RunScope);
    }

    // ---- the wiring ---------------------------------------------------------------------------

    /// <summary>Collects what a verb said, so a phase can be driven without a console.</summary>
    private sealed class RecordingSink : Cli.Output.IOutputSink
    {
        private readonly List<CliDiagnostic> _diagnostics = [];

        public List<string> Lines { get; } = [];

        public bool Verbose => true;

        public IReadOnlyList<CliDiagnostic> Diagnostics => _diagnostics;

        public void Diagnostic(CliDiagnostic diagnostic) => _diagnostics.Add(diagnostic);

        public void Event(CliEvent evt) { }

        public void Line(string text) => Lines.Add(text);

        public int Complete<T>(string verb, int exitCode, T? result) => exitCode;
    }

    [Fact]
    public void TheNotifyPhaseActuallyPassesTheMutedJobsThrough()
    {
        // Every planner test above passes even if NotifyPhase never populates MutedJobs - the
        // planner would simply be told nothing is muted. This is the only test that catches
        // that, which is why it drives the real phase rather than the planner.
        var config = Load("schema = 1\n[notify]\nto = [\"eventlog:\"]\n",
            ("noisy.toml", Job("noisy")),
            ("quiet.toml", Job("quiet", "notify = false")));

        var sink = new RecordingSink();
        sink.Diagnostic(new CliDiagnostic
        {
            Severity = Severity.Error,
            Code = DiagnosticCode.FileLocked,
            Message = "locked",
            Job = "quiet",
            Path = @"C:\logs\quiet\a.log",
        });

        var parse = Cli.Commands.CommandTree.Build().Parse(["run"]);
        var ctx = new Cli.Commands.CommandContext(sink, parse);

        Cli.Commands.NotifyPhase.Run(
            ctx, InstallPaths.Resolve(_dir.FullName), config, report: null,
            new Engine.RunOptions());

        sink.Lines.ShouldContain(l => l.Contains("[quiet]: notify = false", StringComparison.Ordinal));
        sink.Lines.ShouldNotContain(l => l.Contains("would send", StringComparison.Ordinal));
    }

    [Fact]
    public void TheJournalIsMutedThroughTheSameMechanismAsAnyOtherJob()
    {
        // Not a special case inside the planner: it is put into MutedJobs like anything else, so
        // it obeys exactly the rules everything else does and switching it on is a config key.
        var config = Load("schema = 1\n[notify]\nto = [\"eventlog:\"]\n", ("a.toml", Job("a")));

        var sink = new RecordingSink();
        sink.Diagnostic(new CliDiagnostic
        {
            Severity = Severity.Warning,
            Code = DiagnosticCode.RotationFailed,
            Message = "Journal maintenance: disk full",
            Job = JournalMaintenance.JobName,
        });

        var parse = Cli.Commands.CommandTree.Build().Parse(["run"]);
        Cli.Commands.NotifyPhase.Run(
            new Cli.Commands.CommandContext(sink, parse),
            InstallPaths.Resolve(_dir.FullName), config, report: null, new Engine.RunOptions());

        sink.Lines.ShouldNotContain(l => l.Contains("would send", StringComparison.Ordinal));
        sink.Lines.ShouldContain(l =>
            l.Contains(JournalMaintenance.JobName, StringComparison.Ordinal)
            && l.Contains("notify = false", StringComparison.Ordinal));
    }

    // ---- the second read that is no longer there --------------------------------------------

    [Fact]
    public void TheJournalTableIsCarriedOnTheLoadedConfig()
    {
        // So config.toml is read once. RunCommand used to parse it a second time purely to reach
        // this table, and two readers of one file is two chances to disagree about it.
        Load("schema = 1\n[journal]\nretain = 7\nnotify = true\n").Journal.Retain.ShouldBe(7);
    }
}
