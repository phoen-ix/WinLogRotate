using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Shouldly;
using WinLogRotate.Contracts;
using WinLogRotate.Core;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// A verb that refuses says why through the channel a caller reads.
/// </summary>
/// <remarks>
/// <para>
/// <c>CliEnvelope.Diagnostics</c> documents itself as "everything worth telling the caller, in
/// order. <b>Never empty on a failure</b>", and <c>JsonOutputSink.Line</c> is a no-op carrying
/// the comment "the same information is carried as diagnostics and events". Six platform guards
/// said their piece with <c>Output.Line</c> and nothing else, so under <c>--json</c> they said
/// nothing at all - verified on the real CLI:
/// <c>{"verb":"scan","ok":false,"exitCode":1,"diagnostics":[]}</c>.
/// </para>
/// <para>
/// What the operator then has is the exit code alone, and <c>CliResult.Describe</c> turns 1 into
/// "Completed, but some files could not be rotated" - about a verb that rotates nothing, on a
/// platform it cannot run on.
/// </para>
/// </remarks>
public sealed partial class RefusalTests
{
    private sealed class Sink : Cli.Output.IOutputSink
    {
        private readonly List<CliDiagnostic> _diagnostics = [];

        public List<string> Lines { get; } = [];

        public bool Verbose => false;

        public IReadOnlyList<CliDiagnostic> Diagnostics => _diagnostics;

        public void Diagnostic(CliDiagnostic diagnostic) => _diagnostics.Add(diagnostic);

        public void Event(CliEvent evt)
        {
        }

        public void Line(string text) => Lines.Add(text);

        public int Complete<T>(string verb, int exitCode, T? result) => exitCode;
    }

    private static (Sink Sink, Cli.Commands.CommandContext Ctx) Context(params string[] args)
    {
        var sink = new Sink();
        return (sink, new Cli.Commands.CommandContext(sink, Cli.Commands.CommandTree.Build().Parse(args)));
    }

    /// <summary>Every verb that needs Windows says so as a diagnostic, not only as a line.</summary>
    /// <remarks>
    /// Ubuntu only, and deliberately: on Windows these verbs do not take the refusal at all, and
    /// <c>OperatingSystem.IsWindows()</c> has no seam to make it answer otherwise. Skipping is
    /// honest; passing vacuously there would not be.
    /// <c>TheWindowsGuardsAreAllAccountedFor</c> is what runs on both legs.
    /// </remarks>
    [Theory]
    [InlineData("scan")]
    [InlineData("probe")]
    [InlineData("host status")]
    [InlineData("host repair")]
    [InlineData("host use")]
    [InlineData("host path")]
    public void AVerbThatNeedsWindowsSaysSo(string verb)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "these verbs do not refuse on Windows.");

        var (sink, ctx) = Context(verb.Split(' '));

        var exit = verb switch
        {
            "scan" => Cli.Commands.ScanCommand.Run(ctx),
            "probe" => Cli.Commands.ProbeCommand.Run(ctx, "C:\\logs\\app.log"),
            "host status" => Cli.Commands.HostCommand.Status(ctx, null),
            "host repair" => Cli.Commands.HostCommand.Repair(ctx, acl: false, null),
            "host use" => Cli.Commands.HostCommand.Use(ctx, "task", null),
            _ => Cli.Commands.HostCommand.Path(ctx, add: true, machine: true),
        };

        exit.ShouldNotBe(ExitCode.Ok);

        var diagnostic = sink.Diagnostics.ShouldHaveSingleItem();

        diagnostic.Code.ShouldBe(DiagnosticCode.NotSupportedHere);
        diagnostic.Severity.ShouldBe(Severity.Error);
        diagnostic.Message.ShouldContain(verb);
        diagnostic.Remedy.ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// An argument a verb could not use is named, with a remedy, under its own code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These four exited 2 with an empty diagnostics array, so the only thing a caller had was
    /// the exit code - and <c>CliResult.Describe</c> turns 2 into "The configuration has errors,
    /// so nothing was attempted." The configuration is fine; somebody mistyped a date.
    /// </para>
    /// <para>
    /// <c>LR1007</c> rather than <c>LR1003 ConfigInvalid</c>, which is what the exit code is
    /// called and would have been the easy reach. The exit code is right - nothing was attempted -
    /// but the condition is not the machine's configuration, it is the words just typed, and an
    /// alert rule watching event 113 should not fire because an operator fumbled a prompt.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("journal", "garbage", "a date or time")]
    [InlineData("host pause", "banana", "a duration")]
    [InlineData("host use", "badkind", "a run model")]
    [InlineData("import", "/nonexistent/logrotate.conf", "a file that exists")]
    public void AnArgumentAVerbCouldNotUseIsNamed(string verb, string value, string what)
    {
        var (sink, ctx) = Context(verb.Split(' '));

        var exit = verb switch
        {
            "journal" => Cli.Commands.JournalCommand.Run(ctx, value, null, null),
            "host pause" => Cli.Commands.PauseCommand.Run(ctx, value, null),
            "host use" => Cli.Commands.HostCommand.Use(ctx, value, null),
            _ => Cli.Commands.ImportCommand.Run(ctx, value, null, null),
        };

        exit.ShouldBe(ExitCode.ConfigInvalid, "nothing was attempted, which is what 2 means");

        var diagnostic = sink.Diagnostics.ShouldHaveSingleItem();

        diagnostic.Code.ShouldBe(DiagnosticCode.ArgumentUnusable);
        diagnostic.Severity.ShouldBe(Severity.Error);
        diagnostic.Message.ShouldContain(value);
        diagnostic.Message.ShouldContain(what);

        // The half that makes it worth reading. Naming the value without saying what it should
        // have been is the same dead end as the exit code alone.
        diagnostic.Remedy.ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// A failing envelope is never empty.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The net, and it is a net rather than a workhorse: nine verbs broke
    /// <c>CliEnvelope.Diagnostics</c>' own sentence and every one of them is fixed at its own
    /// site, because a named condition with a remedy is worth incomparably more than a generic
    /// admission. This is what catches the tenth.
    /// </para>
    /// <para>
    /// Asserted on the collector rather than through a verb, deliberately. Every verb that could
    /// reach it has been fixed, so there is no longer an input that produces an empty failing
    /// envelope - and writing a test that needs one would mean keeping a defect alive to feed it.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(ExitCode.Errors)]
    [InlineData(ExitCode.ConfigInvalid)]
    [InlineData(ExitCode.LockHeld)]
    [InlineData(ExitCode.InternalError)]
    public void AFailingEnvelopeIsNeverEmpty(int exitCode)
    {
        var settled = new Cli.Output.DiagnosticCollector().Settled("probe", exitCode);

        var diagnostic = settled.ShouldHaveSingleItem();

        diagnostic.Code.ShouldBe(DiagnosticCode.FailedWithoutReason);
        diagnostic.Severity.ShouldBe(Severity.Error);
        diagnostic.Message.ShouldContain("probe");
        diagnostic.Message.ShouldContain(exitCode.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>A succeeding envelope is not padded, and a verb that spoke is not second-guessed.</summary>
    /// <remarks>
    /// The two halves a careless net loses. An empty diagnostics array on exit 0 is the ordinary
    /// shape of a quiet run - <c>EnvelopeDetailsTests.ARunWithNoDiagnosticsHasNoDetails</c> pins
    /// that the GUI shows nothing for it - and a verb that already explained itself must not have
    /// "no reason given" appended underneath.
    /// </remarks>
    [Fact]
    public void ASucceedingEnvelopeIsNotPaddedAndAVerbThatSpokeIsNotSecondGuessed()
    {
        new Cli.Output.DiagnosticCollector().Settled("probe", ExitCode.Ok)
            .ShouldBeEmpty("a quiet success says nothing, which is not the same as failing quietly");

        var spoke = new Cli.Output.DiagnosticCollector();
        spoke.Add(new CliDiagnostic
        {
            Severity = Severity.Error,
            Code = DiagnosticCode.NotSupportedHere,
            Message = "said something",
        });

        spoke.Settled("probe", ExitCode.Errors)
            .ShouldHaveSingleItem()
            .Code.ShouldBe(DiagnosticCode.NotSupportedHere);
    }

    [GeneratedRegex(@"Output\s*\.\s*Line\s*\(\s*\$?""[^""]*needs Windows", RegexOptions.Compiled)]
    private static partial Regex RefusalAsProse();

    [GeneratedRegex(@"!\s*OperatingSystem\.IsWindows\(\)", RegexOptions.Compiled)]
    private static partial Regex WindowsGuard();

    /// <summary>
    /// No verb refuses off Windows by writing a line, because under <c>--json</c> nobody hears it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The half that runs on both legs. <c>JsonOutputSink.Line</c> is a no-op and
    /// <c>EventLogSink</c> mirrors only <c>Diagnostic</c>, so "winlogrotate: 'scan' needs Windows"
    /// written as a line reached neither a script nor Event Viewer - it existed only for somebody
    /// already watching a terminal, who can see the exit code anyway.
    /// </para>
    /// <para>
    /// The file list beside it is a tripwire rather than a claim about counts: moving a guard
    /// between verbs inside one file changes nothing, and adding one to a new file asks its
    /// author which kind it is. Not every guard refuses - <c>doctor</c> reports the platform as a
    /// fact, <c>update</c> reads a policy key, and <c>CommandContext</c> decides whether to wrap
    /// the Event Log sink - so the set is named rather than counted.
    /// </para>
    /// </remarks>
    [Fact]
    public void NoVerbRefusesOffWindowsWithOnlyALine()
    {
        var commands = Path.Combine(
            RepoRoot.Find().FullName, "src", "WinLogRotate.Cli", "Commands");

        var files = Directory
            .EnumerateFiles(commands, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.Combine("obj", ""), StringComparison.Ordinal))
            .Select(f => (File: Path.GetFileName(f), Text: File.ReadAllText(f)))
            .ToArray();

        // Tracks a quantity that moves: a path that stopped resolving would read nothing and
        // report nothing, which is the shape this rule exists to refuse.
        files.Length.ShouldBeGreaterThan(10, "the command files should have been read");

        files.Where(x => RefusalAsProse().IsMatch(x.Text))
            .Select(x => x.File)
            .ShouldBeEmpty("say it with Refusals.NeedsWindows, which a --json caller can read");

        files.Where(x => WindowsGuard().IsMatch(x.Text))
            .Select(x => x.File)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray()
            .ShouldBe(
                ["CommandContext.cs", "DoctorCommand.cs", "HostCommand.cs", "ProbeCommand.cs",
                 "ScanCommand.cs", "UpdateCommand.cs"],
                customMessage: "a new guard needs deciding about: does this verb refuse, or report?");
    }

    /// <summary>
    /// A verb that declines to do what it is named after does not report success.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>update apply</c> never asked whether an update exists and never installed one, and
    /// returned exit 0 with <c>updateAvailable: false, latest: null</c> - the exact shape of "I
    /// checked, you are up to date". Measured against the real CLI before the fix:
    /// </para>
    /// <code>
    /// $ winlogrotate update apply --json
    /// {"verb":"update apply","ok":true,"exitCode":0,"result":{"updateAvailable":false,...},"diagnostics":[]}
    /// $ winlogrotate update apply &amp;&amp; echo updated
    /// updated
    /// </code>
    /// <para>
    /// Unlike Windows, this refusal happens on every platform, so there is nothing to skip: the
    /// verb declines because of what the product does not do, not because of what the machine is.
    /// </para>
    /// </remarks>
    [Fact]
    public void AVerbThatWillNotActDoesNotReportSuccess()
    {
        var (sink, ctx) = Context("update", "apply");

        var exit = Cli.Commands.UpdateCommand.Apply(ctx);

        exit.ShouldNotBe(Core.ExitCode.Ok, "'update apply && next' must not run next");

        var refusal = sink.Diagnostics.ShouldHaveSingleItem();
        refusal.Severity.ShouldBe(Severity.Error);
        refusal.Code.ShouldBe(DiagnosticCode.NotSupportedHere);
        refusal.Remedy.ShouldNotBeNull().ShouldContain("Download the installer");
    }

    /// <summary>
    /// It still says the useful thing on the console, where a person is reading.
    /// </summary>
    /// <remarks>
    /// The lines were the only place this verb ever explained itself, and they are worth keeping:
    /// the point of the change is that a script can tell too, not that a person is told less.
    /// </remarks>
    [Fact]
    public void ItStillExplainsItselfToAPerson()
    {
        var (sink, ctx) = Context("update", "apply");

        Cli.Commands.UpdateCommand.Apply(ctx);

        sink.Lines.ShouldContain(l => l.Contains("not available", StringComparison.Ordinal));
    }
}
