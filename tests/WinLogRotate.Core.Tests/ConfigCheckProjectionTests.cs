using Shouldly;
using WinLogRotate.Core;
using WinLogRotate.Gui.Cli;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// What the Jobs page says after a configuration check.
/// </summary>
/// <remarks>
/// <para>
/// The page said "No problems found." whenever the verb exited 0 - and `config check` exits 0
/// with warnings. Verified against the real CLI: a manage job carrying `olddir` prints
/// "1 job(s), 0 error(s), 1 warning(s)" on stdout, the warning itself on stderr, and exits 0.
/// </para>
/// <para>
/// The dialog was then handed `result.StdOut` as its detail, so an operator who opened the
/// details to find out what the warning was found a count of it.
/// </para>
/// </remarks>
public sealed class ConfigCheckProjectionTests
{
    private static CliResult Envelope(int exitCode, string json) =>
        new() { ExitCode = exitCode, StdOut = json, StdErr = "" };

    private static string Payload(int errors, int warnings, string diagnostics = "[]") =>
        $$"""
          { "result": { "errors": {{errors}}, "warnings": {{warnings}}, "jobs": 1,
            "root": "C:/pd", "diagnostics": {{diagnostics}} } }
          """;

    private const string OneWarning = """
        [ { "severity": "Warning", "code": "LR1003", "file": "C:/pd/conf.d/iis.toml", "line": 4,
            "message": "olddir is ignored on a manage job.",
            "remedy": "Remove it, or make this a rotate job." } ]
        """;

    /// <summary>
    /// A check that exits 0 with warnings does not report that nothing was found.
    /// </summary>
    /// <remarks>
    /// The defect, stated as a rule, and the details assertion is half of it: the message was
    /// wrong and the pane an operator would open to disbelieve it was empty of the warning too.
    /// </remarks>
    [Fact]
    public void WarningsAtExitZeroAreNotNoProblemsFound()
    {
        var view = ConfigCheckProjection.From(
            Envelope(ExitCode.Ok, Payload(errors: 0, warnings: 1, OneWarning)),
            ExitCode.ConfigInvalid);

        view.Tone.ShouldBe(CheckTone.Warning);
        view.Message.ShouldNotContain("No problems found");
        view.Message.ShouldContain("1 warning(s)");
        view.Message.ShouldContain("will still run", Case.Insensitive);

        view.Details.ShouldContain("olddir is ignored on a manage job.");
        view.Details.ShouldContain("iis.toml:4");
        view.Details.ShouldContain("Remove it, or make this a rotate job.");
    }

    /// <summary>A genuinely clean check still says so.</summary>
    [Fact]
    public void ACleanCheckStillSaysNoProblemsFound()
    {
        var view = ConfigCheckProjection.From(
            Envelope(ExitCode.Ok, Payload(errors: 0, warnings: 0)),
            ExitCode.ConfigInvalid);

        view.Tone.ShouldBe(CheckTone.Clean);
        view.Message.ShouldBe("No problems found.");
    }

    /// <summary>
    /// Exit 2 stops everything; exit 1 skips a job and runs the rest.
    /// </summary>
    /// <remarks>
    /// Both rows, because the sentence was written when 2 was the only failure and then told an
    /// operator nothing would run when almost everything would. Having two codes is the point.
    /// </remarks>
    [Theory]
    [InlineData(ExitCode.ConfigInvalid, "Nothing will run")]
    [InlineData(ExitCode.Errors, "The rest will still run")]
    public void TheTwoFailureCodesSayDifferentThings(int exitCode, string expected)
    {
        var view = ConfigCheckProjection.From(
            Envelope(exitCode, Payload(errors: 1, warnings: 0)),
            ExitCode.ConfigInvalid);

        view.Tone.ShouldBe(CheckTone.Error);
        view.Message.ShouldContain(expected);
    }

    /// <summary>Errors outrank warnings, rather than being counted alongside them.</summary>
    [Fact]
    public void AnErrorIsReportedEvenWithWarningsBesideIt()
    {
        ConfigCheckProjection.From(
                Envelope(ExitCode.ConfigInvalid, Payload(errors: 1, warnings: 3)),
                ExitCode.ConfigInvalid)
            .Tone.ShouldBe(CheckTone.Error);
    }

    /// <summary>An envelope this window cannot read says so, rather than "no problems found".</summary>
    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{ "result": { } }""")]
    public void AnUnreadableEnvelopeIsNotACleanBillOfHealth(string json)
    {
        var view = ConfigCheckProjection.From(Envelope(ExitCode.Ok, json), ExitCode.ConfigInvalid);

        view.Tone.ShouldBe(CheckTone.Error);
        view.Message.ShouldNotContain("No problems found");
    }

    /// <summary>A CLI that could not be launched at all is reported as itself.</summary>
    [Fact]
    public void AVerbThatNeverRanIsNotACleanBillOfHealth()
    {
        var view = ConfigCheckProjection.From(
            new CliResult
            {
                ExitCode = 0,
                StdOut = "",
                StdErr = "",
                Failure = CliFailure.NotFound,
            },
            ExitCode.ConfigInvalid);

        view.Tone.ShouldBe(CheckTone.Error);
        view.Message.ShouldNotContain("No problems found");
    }
}
