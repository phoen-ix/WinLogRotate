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
}
