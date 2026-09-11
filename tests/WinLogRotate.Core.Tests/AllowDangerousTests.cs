using Shouldly;
using WinLogRotate.Contracts;
using WinLogRotate.Core;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Safety;
using WinLogRotate.Core.Secrets;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// The escape hatch, end to end, and what a refusal costs when there is no hatch.
/// </summary>
/// <remarks>
/// <para>
/// There was no test here at all, which is how <c>allowdangerous</c> shipped bound, merged,
/// carried on <c>EffectiveJob</c> and read by nothing - while the guard's own remedy told
/// operators to use it. Every test in this file drives <c>ConfigLoader.Load</c> rather than the
/// guard directly, because the guard was never the part that was broken.
/// </para>
/// <para>
/// <c>ProtectedRoots</c> is stated rather than defaulted. <c>DefaultProtectedRoots</c> asks the
/// running system first and only falls back to literals when it answers nothing, so a test that
/// relied on the default would be asserting something different on each CI leg.
/// </para>
/// </remarks>
public sealed class AllowDangerousTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("winlogrotate-allow-");

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); } catch (IOException) { }
    }

    private LoadedConfig Load(params (string File, string Toml)[] jobs) =>
        Load(OverrideGate.Open, jobs);

    private LoadedConfig Load(OverrideGate gate, params (string File, string Toml)[] jobs)
    {
        File.WriteAllText(Path.Combine(_dir.FullName, "config.toml"), "schema = 1\n");
        var confd = Directory.CreateDirectory(Path.Combine(_dir.FullName, "conf.d"));
        foreach (var (name, toml) in jobs)
        {
            File.WriteAllText(Path.Combine(confd.FullName, name), toml);
        }

        return ConfigLoader.Load(
            InstallPaths.Resolve(_dir.FullName),
            new PathGuard(new GuardOptions
            {
                ProtectedRoots = [@"C:\Windows", @"C:\Program Files"],
                Overrides = gate,
            }),
            new UnknownSecretLookup(),
            quarantineBadFiles: false);
    }

    private static string Job(string name, string paths, string extra = "") => $"""
        schema = 1
        [job]
        name  = "{name}"
        kind  = "manage"
        paths = [{paths}]
        {extra}
        """;

    /// <summary>
    /// One job naming a protected path does not stop the others.
    /// </summary>
    /// <remarks>
    /// The headline defect. A refused pattern was an Error, which set <c>HasErrors</c>, which made
    /// <c>run</c> return <c>ExitCode.ConfigInvalid</c> with nothing attempted - for every job on
    /// the machine. One operator's typo in one file became a disk-space incident on a box with
    /// forty healthy jobs. <c>ConfigValidator.CheckHooks</c> had already written this argument out
    /// in full for hooks; this is the half that applies it to paths.
    /// </remarks>
    [Fact]
    public void OneJobNamingAProtectedPathDoesNotStopTheOthers()
    {
        var config = Load(
            ("bad.toml", Job("iis-logs", "\"C:/Windows/System32/LogFiles/*.log\"")),
            ("good.toml", Job("app-logs", "\"C:/app/logs/*.log\"")));

        config.HasErrors.ShouldBeFalse(
            "a refused path belongs to one job, so it must not stop the whole machine rotating");

        config.Jobs.ShouldHaveSingleItem().Name.ShouldBe("app-logs");
        config.SkippedJobs.ShouldHaveSingleItem().ShouldBe("iis-logs");
    }

    /// <summary>A skipped job is named, and the refusal is attributed to it.</summary>
    /// <remarks>
    /// A log that stops being rotated with nobody told is the worst outcome this product has, so
    /// "it did not stop the others" is only half the requirement.
    /// </remarks>
    [Fact]
    public void TheRefusalIsAttributedToTheJobThatCausedIt()
    {
        var refusal = Load(("bad.toml", Job("iis-logs", "\"C:/Windows/System32/LogFiles/*.log\"")))
            .Diagnostics.ShouldHaveSingleItem();

        refusal.Severity.ShouldBe(Severity.Error);
        refusal.Code.ShouldBe(DiagnosticCode.DangerousPathRefused);
        refusal.Job.ShouldBe("iis-logs");
    }

    /// <summary>An override lets the job through, and says out loud that it did.</summary>
    /// <remarks>
    /// Asserting "no errors" would not do. With the severity partitioning in place a job whose
    /// override was ignored also produces no whole-config error, so an errors-only assertion would
    /// stay green with the plumbing reverted - which is exactly the defect this milestone fixed.
    /// The message clause is the only thing that proves the override was consulted.
    /// </remarks>
    [Fact]
    public void AnOverriddenJobLoadsAndSaysTheOverrideWasUsed()
    {
        var config = Load(("cbs.toml", Job(
            "cbs", "\"C:/Windows/Logs/CBS/*.log\"",
            "allowdangerous = [\"C:/Windows/Logs/CBS\"]")));

        config.Jobs.ShouldHaveSingleItem().Name.ShouldBe("cbs");
        config.SkippedJobs.ShouldBeEmpty();

        var warning = config.Diagnostics.ShouldHaveSingleItem();
        warning.Severity.ShouldBe(Severity.Warning);
        warning.Message.ShouldContain("allowDangerous",
            customMessage: "the override must leave a mark, not pass silently");
    }

    /// <summary>An override covers the archives the job will compress and delete, not just the log.</summary>
    /// <remarks>
    /// The whole job is planned and applied against concrete files. An override that unlocked only
    /// the files matching the pattern would validate, plan, and then refuse every generation at
    /// apply time - see AnOverrideAlsoPermitsTheArchivesBesideTheLog for the unit-level form.
    /// </remarks>
    [Fact]
    public void AnOverrideCoversTheWholeDirectoryTheJobWorksIn()
    {
        var job = Load(("cbs.toml", Job(
            "cbs", "\"C:/Windows/Logs/CBS/*.log\"",
            "allowdangerous = [\"C:/Windows/Logs/CBS\"]")))
            .Jobs.ShouldHaveSingleItem();

        var guard = new PathGuard(new GuardOptions
        {
            ProtectedRoots = [@"C:\Windows"],
            Overrides = OverrideGate.Open,
        });

        guard.CheckPath(@"C:\Windows\Logs\CBS\CbsPersist.log.3.zip", job.GuardScope)
            .IsAllowed.ShouldBeTrue();
    }

    /// <summary>An entry broad enough to unlock a protected root is refused, and the job with it.</summary>
    [Theory]
    [InlineData("C:/**")]
    [InlineData("C:/Windows/**")]
    public void AnOverBroadEntryIsRefusedAndTheJobIsSkipped(string entry)
    {
        var config = Load(("cbs.toml", Job(
            "cbs", "\"C:/Windows/Logs/CBS/*.log\"", $"allowdangerous = [\"{entry}\"]")));

        config.Jobs.ShouldBeEmpty();
        config.SkippedJobs.ShouldHaveSingleItem().ShouldBe("cbs");

        config.Diagnostics
            .Any(d => d.Severity >= Severity.Error && d.Message.Contains("allowdangerous", StringComparison.Ordinal))
            .ShouldBeTrue("the entry itself has to be named, not just the pattern it failed to rescue");
    }

    /// <summary>A careless entry does not disable a careful one beside it.</summary>
    [Fact]
    public void OneBadEntryDoesNotDisarmTheOthers()
    {
        var config = Load(("cbs.toml", Job(
            "cbs", "\"C:/Windows/Logs/CBS/*.log\"",
            "allowdangerous = [\"C:/**\", \"C:/Windows/Logs/CBS\"]")));

        // The job is still skipped - the bad entry is an error in its own right - but the good
        // entry was honoured, so the pattern itself was never the complaint.
        config.SkippedJobs.ShouldHaveSingleItem();

        // Severity, not message text: an honoured override keeps the refusal's original wording
        // and appends "Permitted by...", so matching on the sentence alone finds the success.
        config.Diagnostics
            .Where(d => d.Severity >= Severity.Error)
            .ShouldAllBe(d => d.Message.Contains("allowdangerous entry", StringComparison.Ordinal));

        config.Diagnostics
            .ShouldContain(d => d.Severity == Severity.Warning
                && d.Message.Contains("Permitted by", StringComparison.Ordinal));
    }

    /// <summary>
    /// An override is ignored on a machine whose conf.d anyone can write.
    /// </summary>
    /// <remarks>
    /// The job is skipped rather than run: an override this machine will not honour leaves the
    /// pattern refused exactly as if it had never been written, which is the whole point.
    /// </remarks>
    [Fact]
    public void AnOverrideIsIgnoredWhenTheConfigurationDirectoryIsLoose()
    {
        var config = Load(
            OverrideGate.Shut("conf.d can be written by an ordinary user"),
            ("cbs.toml", Job("cbs", "\"C:/Windows/Logs/CBS/*.log\"",
                "allowdangerous = [\"C:/Windows/Logs/CBS\"]")));

        config.Jobs.ShouldBeEmpty();
        config.SkippedJobs.ShouldHaveSingleItem().ShouldBe("cbs");

        config.Diagnostics.ShouldHaveSingleItem()
            .Message.ShouldContain("was not honoured because");
    }

    /// <summary>
    /// A fault with no single job to blame still stops everything.
    /// </summary>
    /// <remarks>
    /// The converse of the headline, and the reason HasErrors was narrowed rather than deleted.
    /// A duplicate job name makes the journal and every diagnostic ambiguous, and it belongs to no
    /// one job - so it is still the case that nothing should be attempted.
    /// </remarks>
    [Fact]
    public void AWholeConfigurationFaultStillStopsEverything()
    {
        var config = Load(
            ("a.toml", Job("twins", "\"C:/app/a/*.log\"")),
            ("b.toml", Job("twins", "\"C:/app/b/*.log\"")));

        config.HasErrors.ShouldBeTrue();
    }

    // ---- what config check tells a pipeline ---------------------------------------------------

    private sealed class Sink : Cli.Output.IOutputSink
    {
        private readonly List<CliDiagnostic> _diagnostics = [];

        public bool Verbose => false;

        public IReadOnlyList<CliDiagnostic> Diagnostics => _diagnostics;

        public void Diagnostic(CliDiagnostic diagnostic) => _diagnostics.Add(diagnostic);

        public void Event(CliEvent evt) { }

        public void Line(string text) { }

        public int Complete<T>(string verb, int exitCode, T? result) => exitCode;
    }

    private int Check(params (string File, string Toml)[] jobs)
    {
        File.WriteAllText(Path.Combine(_dir.FullName, "config.toml"), "schema = 1\n");
        var confd = Directory.CreateDirectory(Path.Combine(_dir.FullName, "conf.d"));
        foreach (var (name, toml) in jobs)
        {
            File.WriteAllText(Path.Combine(confd.FullName, name), toml);
        }

        var parse = Cli.Commands.CommandTree.Build().Parse(["config", "check"]);

        return Cli.Commands.ConfigCommand.Check(
            new Cli.Commands.CommandContext(new Sink(), parse), _dir.FullName);
    }

    /// <summary>
    /// A refused path exits 1 from config check, the way it does from run.
    /// </summary>
    /// <remarks>
    /// It exited 2, whose own contract is that nothing was attempted - which milestone 16 made
    /// false for a job-scoped error. So the verb an operator runs to find out why run refused
    /// disagreed with run about what a refused path costs, and a pipeline gating on config check
    /// could not tell one bad pattern from a config.toml that will not parse.
    /// </remarks>
    [Fact]
    public void ConfigCheckExitsOneForAJobScopedError() =>
        Check(("bad.toml", Job("iis-logs", "\"C:/Windows/System32/LogFiles/*.log\"")))
            .ShouldBe(ExitCode.Errors);

    /// <summary>A fault with no job to blame still exits 2.</summary>
    [Fact]
    public void ConfigCheckStillExitsTwoForAWholeConfigurationFault() =>
        Check(
            ("a.toml", Job("twins", "\"C:/app/a/*.log\"")),
            ("b.toml", Job("twins", "\"C:/app/b/*.log\"")))
            .ShouldBe(ExitCode.ConfigInvalid);

    /// <summary>A clean configuration still exits 0.</summary>
    [Fact]
    public void ConfigCheckExitsZeroWhenNothingIsWrong() =>
        Check(("good.toml", Job("app-logs", "\"C:/app/logs/*.log\""))).ShouldBe(ExitCode.Ok);
}
