using Shouldly;
using WinLogRotate.Contracts;
using WinLogRotate.Core;
using WinLogRotate.Core.Io;
using WinLogRotate.Core.State;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// <c>probe</c> answers the question it was asked, and the answer is not a failure of the verb.
/// </summary>
/// <remarks>
/// A file nothing can open raised an Error and then exited 0. <c>docs/automation.md</c> says
/// <c>ok</c> is <c>exitCode == 0</c> and that a verb which deliberately does nothing explains
/// itself in the diagnostics - so an Error riding on exit 0 was the one shape the contract has
/// no reading for. <c>CliResult</c> rendered a failure over a success, and an Event Log rule on
/// Error fired about a verb that had done what it was asked.
/// </remarks>
public sealed class ProbeSeverityTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("winlogrotate-probe-");

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// A file no strategy can touch is a Warning on exit 0, like the Copy verdict one step above it.
    /// </summary>
    /// <remarks>
    /// The verdict is handed in rather than asked of the OS, which is what lets this run on the
    /// Linux leg: what is under test is what the verb says about a verdict, not how Windows
    /// reaches one.
    /// </remarks>
    [Fact]
    public void AFileNothingCanOpenIsAWarningOnExitZero()
    {
        var path = Path.Combine(_dir.FullName, "app.log");
        File.WriteAllText(path, "held");

        var sink = new RecordingSink();
        var parse = Cli.Commands.CommandTree.Build().Parse(["probe", path]);

        var exit = Cli.Commands.ProbeCommand.Run(
            new Cli.Commands.CommandContext(sink, parse), path, p => new ProbeResult
            {
                Path = p,
                Verdict = ProbeVerdict.None,
                Explanation = "held exclusively",
                BlockingError = 32,
            });

        exit.ShouldBe(ExitCode.Ok, "the verb was asked what the file supports, and answered");

        var diagnostic = sink.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe(DiagnosticCode.FileLocked);
        diagnostic.Severity.ShouldBe(Severity.Warning);
        diagnostic.Remedy.ShouldNotBeNullOrWhiteSpace();

        sink.Diagnostics.ShouldNotContain(
            d => d.Severity >= Severity.Error,
            "an Error alongside exit 0 contradicts the envelope contract");
    }
}
