using Shouldly;
using WinLogRotate.Contracts;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Engine;
using WinLogRotate.Core.Safety;
using WinLogRotate.Core.Secrets;
using WinLogRotate.Core.State;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// That the run verb actually delivers, and that <c>notify test</c> actually does not record.
/// </summary>
/// <remarks>
/// Every dispatcher test passes even if <c>NotifyPhase</c> never builds a channel or never calls
/// the dispatcher at all. These drive the real phase and the real command, against a port nothing
/// is listening on, which is the only thing that catches that.
/// </remarks>
public sealed class NotifyPhaseWiringTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("winlogrotate-wiring-");

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); } catch (IOException) { }
    }

    /// <summary>Collects what a verb said, so a phase can be driven without a console.</summary>
    private sealed class RecordingSink : Cli.Output.IOutputSink
    {
        private readonly List<CliDiagnostic> _diagnostics = [];

        public List<string> Lines { get; } = [];

        public List<CliEvent> Events { get; } = [];

        public bool Verbose => true;

        public IReadOnlyList<CliDiagnostic> Diagnostics => _diagnostics;

        public void Diagnostic(CliDiagnostic diagnostic) => _diagnostics.Add(diagnostic);

        public void Event(CliEvent evt) => Events.Add(evt);

        public void Line(string text) => Lines.Add(text);

        public int Complete<T>(string verb, int exitCode, T? result) => exitCode;
    }

    /// <summary>Port 1 on loopback. Refused immediately, so no test waits for a timeout.</summary>
    private const string Unreachable = "http://127.0.0.1:1/hook";

    private Core.InstallPaths Write(string configToml)
    {
        File.WriteAllText(Path.Combine(_dir.FullName, "config.toml"), configToml);
        Directory.CreateDirectory(Path.Combine(_dir.FullName, "conf.d"));
        return Core.InstallPaths.Resolve(_dir.FullName);
    }

    private static LoadedConfig Load(Core.InstallPaths paths) =>
        ConfigLoader.Load(paths, new PathGuard(new GuardOptions()), new UnknownSecretLookup(),
            quarantineBadFiles: false);

    private static (RecordingSink Sink, Cli.Commands.CommandContext Ctx) Context()
    {
        var sink = new RecordingSink();
        var parse = Cli.Commands.CommandTree.Build().Parse(["run"]);
        return (sink, new Cli.Commands.CommandContext(sink, parse));
    }

    /// <summary>
    /// One healthy run, so what follows is a job that STARTS failing.
    /// </summary>
    /// <remarks>
    /// Without it the planner records a baseline and correctly says nothing - installing
    /// monitoring on an already-broken machine must not alert about what was already there. Done
    /// by running the real phase rather than by hand-writing state, so these tests cannot pass
    /// against a baseline shape the phase no longer produces.
    /// </remarks>
    private void Baseline(Core.InstallPaths paths)
    {
        var (_, ctx) = Context();
        Cli.Commands.NotifyPhase.Run(
            ctx, paths, Load(paths), report: null, new RunOptions(), DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// A sink already carrying the failure a run would have reported.
    /// </summary>
    /// <remarks>
    /// Run-scoped, with no job name, because that is the finding <see cref="Baseline"/> can
    /// actually establish a healthy prior for: a job is only baselined once a run has observed it,
    /// and a phase called with no report observes none. It is also the more important case - "the
    /// configuration is so broken that nothing rotated" is the night an operator most wants to
    /// hear about.
    /// </remarks>
    private static (RecordingSink Sink, Cli.Commands.CommandContext Ctx) Failing()
    {
        var (sink, ctx) = Context();
        sink.Diagnostic(new CliDiagnostic
        {
            Severity = Severity.Error,
            Code = DiagnosticCode.ConfigDirectoryInsecure,
            Message = "conf.d is writable by Users.",
            Path = @"C:\ProgramData\WinLogRotate\conf.d",
        });

        return (sink, ctx);
    }

    // ---- the run verb ------------------------------------------------------------------------

    /// <summary>
    /// The wiring test: the phase must really build channels and really try to send.
    /// </summary>
    /// <remarks>
    /// Before milestone 10 this printed "would send" and stopped. If it ever regresses to that,
    /// every dispatcher test still passes - they hand the dispatcher its channels directly.
    /// </remarks>
    [Fact]
    public void TheRunPhaseActuallyAttemptsDelivery()
    {
        var paths = Write($"""
            schema = 1
            [notify]
            to = ["{Unreachable}"]
            retries = 0
            """);

        Baseline(paths);

        var (sink, ctx) = Failing();

        Cli.Commands.NotifyPhase.Run(
            ctx, paths, Load(paths), report: null, new RunOptions(), DateTimeOffset.UtcNow);

        sink.Diagnostics.ShouldContain(
            d => d.Code == DiagnosticCode.NotifyFailed,
            "nothing is listening on port 1, so the attempt must be reported as a failure");

        sink.Lines.ShouldNotContain(l => l.Contains("would send", StringComparison.Ordinal));
    }

    /// <summary>
    /// A failed delivery must leave the job looking un-reported, so the next run says it again.
    /// </summary>
    [Fact]
    public void AFailedDeliveryDoesNotAdvanceTheJobState()
    {
        var paths = Write($"""
            schema = 1
            [notify]
            to = ["{Unreachable}"]
            retries = 0
            """);

        Baseline(paths);

        var (sink, ctx) = Failing();

        Cli.Commands.NotifyPhase.Run(
            ctx, paths, Load(paths), report: null, new RunOptions(), DateTimeOffset.UtcNow);

        var state = NotifyStateStore.Load(paths.NotifyStateFile);

        // The run scope is where a config-load finding lands. It may have been baselined, but it
        // must not claim to have been reported.
        state.JobOrDefault(NotifyStateDocument.RunScope).NotifiedAt.ShouldBeNull();
    }

    /// <summary>A channel that failed has to be remembered, or the breaker can never count.</summary>
    [Fact]
    public void AFailedChannelIsRecordedSoTheBreakerCanEventuallyOpen()
    {
        var paths = Write($"""
            schema = 1
            [notify]
            to = ["{Unreachable}"]
            retries = 0
            """);

        Baseline(paths);

        var (sink, ctx) = Failing();

        Cli.Commands.NotifyPhase.Run(
            ctx, paths, Load(paths), report: null, new RunOptions(), DateTimeOffset.UtcNow);

        NotifyStateStore.Load(paths.NotifyStateFile)
            .Channels.Values.ShouldContain(c => c.ConsecutiveFailures > 0);
    }

    /// <summary>
    /// A dry run describes and does not send, and does not move a breaker counter.
    /// </summary>
    /// <remarks>
    /// The guard used to sit BELOW the message loop, which was harmless only while the loop did
    /// nothing. Delivery above a dry-run check would make <c>--dry-run</c> page the on-call.
    /// </remarks>
    [Fact]
    public void ADryRunDescribesAndDoesNotSend()
    {
        var paths = Write($"""
            schema = 1
            [notify]
            to = ["{Unreachable}"]
            """);

        Baseline(paths);
        var stateBefore = File.ReadAllBytes(paths.NotifyStateFile);

        var (sink, ctx) = Failing();

        Cli.Commands.NotifyPhase.Run(
            ctx, paths, Load(paths), report: null,
            new RunOptions { DryRun = true }, DateTimeOffset.UtcNow);

        sink.Lines.ShouldContain(l => l.Contains("would send", StringComparison.Ordinal));
        sink.Diagnostics.ShouldNotContain(d => d.Code == DiagnosticCode.NotifyFailed);

        // The baseline run above wrote the file, so the assertion is that the dry run did not
        // touch it - a dry run must leave notification history exactly as it found it.
        File.ReadAllBytes(paths.NotifyStateFile).ShouldBe(stateBefore);
    }

    /// <summary>
    /// A run that has already used its whole deadline notifies nobody, and says why.
    /// </summary>
    [Fact]
    public void ARunThatOverranItsDeadlineReportsTheClampAndSendsNothing()
    {
        var paths = Write($"""
            schema = 1
            [notify]
            to = ["{Unreachable}"]
            """);

        Baseline(paths);

        var (sink, ctx) = Failing();

        Cli.Commands.NotifyPhase.Run(
            ctx, paths, Load(paths), report: null,
            new RunOptions { RunDeadline = TimeSpan.FromHours(1) },

            // Started 61 minutes ago against a one-hour limit.
            DateTimeOffset.UtcNow - TimeSpan.FromMinutes(61));

        sink.Diagnostics.ShouldContain(d => d.Code == DiagnosticCode.NotifyBudgetClamped);
        sink.Diagnostics.ShouldNotContain(d => d.Code == DiagnosticCode.NotifyFailed);
    }

    // ---- notify test -------------------------------------------------------------------------

    /// <summary>
    /// <c>notify test</c> is inert: it sends, and records nothing whatsoever.
    /// </summary>
    /// <remarks>
    /// A diagnostic that mutates the thing it is diagnosing is not a diagnostic. If testing a
    /// channel could open its breaker, the command an operator reaches for during an incident
    /// would be the one that suppresses their alerts.
    /// </remarks>
    [Fact]
    public void NotifyTestWritesNothing()
    {
        var paths = Write($"""
            schema = 1
            [notify]
            to = ["{Unreachable}"]
            retries = 0
            """);

        var (sink, ctx) = Context();

        Cli.Commands.NotifyTestCommand.Run(ctx, target: null, paths.Root);

        sink.Lines.ShouldContain(l => l.Contains("FAILED", StringComparison.Ordinal));
        File.Exists(paths.NotifyStateFile).ShouldBeFalse(
            "notify test must not create notification history");
    }

    [Fact]
    public void NotifyTestLeavesAnExistingHistoryByteIdentical()
    {
        var paths = Write($"""
            schema = 1
            [notify]
            to = ["{Unreachable}"]
            retries = 0
            """);

        var state = NotifyStateStore.Load(paths.NotifyStateFile);
        state.SetChannel("something", new ChannelNotifyState { ConsecutiveFailures = 3 });
        state.Save(TimeProvider.System);

        var before = File.ReadAllBytes(paths.NotifyStateFile);

        var (_, ctx) = Context();
        Cli.Commands.NotifyTestCommand.Run(ctx, target: null, paths.Root);

        File.ReadAllBytes(paths.NotifyStateFile).ShouldBe(before);
    }

    [Fact]
    public void NotifyTestSaysSoWhenNothingIsConfigured()
    {
        var paths = Write("schema = 1\n");
        var (sink, ctx) = Context();

        Cli.Commands.NotifyTestCommand.Run(ctx, target: null, paths.Root);

        sink.Lines.ShouldContain(l => l.Contains("no notification targets", StringComparison.OrdinalIgnoreCase));
    }

    // ---- a job that could not be loaded -------------------------------------------------------

    /// <summary>Writes a job that will fail validation, so it lands in SkippedJobs.</summary>
    private Core.InstallPaths WithUnloadableJob(string extra)
    {
        var paths = Write("""
            schema = 1
            [notify]
            enabled = true
            on = "change"
            to = ["eventlog:"]
            """);

        File.WriteAllText(Path.Combine(_dir.FullName, "conf.d", "bad.toml"), $"""
            schema = 1
            [job]
            name  = "broken"
            kind  = "manage"
            paths = ["C:/Windows/System32/LogFiles/*.log"]
            {extra}
            """);

        return paths;
    }

    /// <summary>
    /// What RunCommand does before it calls the phase: forward the config's diagnostics.
    /// </summary>
    /// <remarks>
    /// The phase reads the sink's list rather than the config's, deliberately - IOutputSink's own
    /// doc comment explains that config-load diagnostics exist only there. A test that hands the
    /// phase a LoadedConfig without doing this hands it nothing to plan from.
    /// </remarks>
    private static void Forward(RecordingSink sink, LoadedConfig config)
    {
        foreach (var d in config.Diagnostics)
        {
            sink.Diagnostic(new CliDiagnostic
            {
                Severity = d.Severity,
                Code = d.Code,
                Message = d.Message,
                Path = d.File,
                Remedy = d.Remedy,
                Job = d.Job,
            });
        }
    }

    /// <summary>
    /// A job that could not be loaded is reported on the night it breaks.
    /// </summary>
    /// <remarks>
    /// Through the real phase and in dry run, so the plan is described without anything being
    /// delivered - delivery is where channel resolution can fail for unrelated reasons and turn
    /// this into a test that passes because nothing happened at all.
    /// </remarks>
    [Fact]
    public void AJobThatCouldNotBeLoadedIsReportedImmediately()
    {
        var paths = WithUnloadableJob(string.Empty);
        var config = Load(paths);

        config.SkippedJobs.ShouldHaveSingleItem().Name.ShouldBe("broken");

        var (sink, ctx) = Context();
        Forward(sink, config);

        Cli.Commands.NotifyPhase.Run(
            ctx, paths, config, report: null, new RunOptions { DryRun = true },
            DateTimeOffset.UtcNow);

        sink.Lines.ShouldContain(
            l => l.Contains("would send", StringComparison.Ordinal),
            "a job whose configuration will not validate is a change made minutes ago, "
            + "not a pre-existing condition to baseline");
    }

    /// <summary>
    /// notify = false is honoured for a job that could not be loaded.
    /// </summary>
    /// <remarks>
    /// The defect was in how the phase builds its muted set, not in how the planner reads one:
    /// MutedJobs came from config.Jobs, and a skipped job is by definition absent from that list,
    /// so the operator's mute stopped applying at exactly the moment the job broke. A
    /// planner-level test hands a muted set in directly and cannot see this.
    /// </remarks>
    [Fact]
    public void AMutedJobThatCouldNotBeLoadedSendsNothing()
    {
        var paths = WithUnloadableJob("notify = false");
        var config = Load(paths);

        // Otherwise this passes for the wrong reason if SkippedJobs ever stops carrying the job.
        config.SkippedJobs.ShouldHaveSingleItem().Notify.ShouldBeFalse();

        var (sink, ctx) = Context();
        Forward(sink, config);

        Cli.Commands.NotifyPhase.Run(
            ctx, paths, config, report: null, new RunOptions { DryRun = true },
            DateTimeOffset.UtcNow);

        sink.Lines.ShouldNotContain(l => l.Contains("would send", StringComparison.Ordinal));
        sink.Lines.ShouldContain(l => l.Contains("notify = false", StringComparison.Ordinal));
    }
}
