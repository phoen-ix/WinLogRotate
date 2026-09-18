using Shouldly;
using WinLogRotate.Cli.Commands;
using WinLogRotate.Gui.Cli;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// "What would happen…" reads a dry run of one job into a sentence and the lines behind it.
/// </summary>
/// <remarks>
/// The events are written by hand in the shape <c>envelope/event.txt</c> pins, because a run on
/// this leg has no Windows path to plan against; the sentences they render to are
/// <c>CliEventText</c>'s own, which the CLI's text output and the History page also use.
/// </remarks>
public sealed class DryRunPreviewTests
{
    private const string Envelope =
        "{\"schema\":1,\"product\":\"WinLogRotate\",\"version\":\"0.0.0\",\"verb\":\"run\",\"ok\":true,\"exitCode\":0,"
        + "\"result\":{\"runId\":\"R\",\"dryRun\":true,\"jobsConsidered\":1,\"jobsRun\":1,\"completed\":1,\"failed\":0,\"bytesFreed\":0,\"errors\":[]},"
        + "\"diagnostics\":[]}";

    private static string Event(string op, string src, string? dst = null, string? reason = null, string? result = null) =>
        "{\"ts\":\"2026-09-18T03:00:00.0000000+00:00\",\"run\":\"R\",\"operation\":\"" + op + "\",\"phase\":\"plan\","
        + (result is null ? "" : $"\"result\":\"{result}\",")
        + "\"job\":\"iis\",\"src\":\"" + src.Replace("\\", "\\\\") + "\""
        + (dst is null ? "" : ",\"dst\":\"" + dst.Replace("\\", "\\\\") + "\"")
        + (reason is null ? "" : ",\"reason\":\"" + reason + "\"")
        + "}";

    private static CliResult Run(int exitCode, params string[] lines) => new()
    {
        ExitCode = exitCode,
        StdOut = string.Join("\n", lines) + "\n",
        StdErr = "",
        Verb = "run",
    };

    [Fact]
    public void TheArgumentsAreTheDryRunThatShowsEverything()
    {
        var args = DryRunPreviewProjection.Arguments(null, "iis");

        args.ShouldBe(["run", "--dry-run", "--force", "--catchup", "--no-notify", "--job", "iis", "--json-stream"]);
        CommandTree.Build().Parse([.. args, "--no-color"]).Errors.ShouldBeEmpty();
        CommandTree.Build().Parse([.. DryRunPreviewProjection.Arguments(@"C:\pd", "iis"), "--no-color"]).Errors.ShouldBeEmpty();
    }

    [Fact]
    public void TheMovesAreCountedAndListed()
    {
        var result = Run(0,
            "{\"ts\":\"t\",\"run\":\"R\",\"operation\":\"run.start\",\"phase\":\"plan\",\"reason\":\"dry run\"}",
            "{\"ts\":\"t\",\"run\":\"R\",\"operation\":\"job.start\",\"phase\":\"plan\",\"job\":\"iis\"}",
            Event("delete", @"C:\logs\app.log.3.zip", reason: "rotate = 3 keeps 3 generation(s)"),
            Event("rename", @"C:\logs\app.log.2.zip", @"C:\logs\app.log.3.zip", "shifting generation 2 to 3"),
            Event("rename", @"C:\logs\app.log", @"C:\logs\app.log.1", "due; --force"),
            Event("compress", @"C:\logs\app.log.1", @"C:\logs\app.log.1.zip", "compress = zip"),
            Event("plan", @"C:\logs\other.log", reason: "due, but the log is empty and notifempty is set", result: "skipped"),
            "{\"ts\":\"t\",\"run\":\"R\",\"operation\":\"job.end\",\"phase\":\"plan\",\"job\":\"iis\"}",
            Envelope);

        var preview = DryRunPreviewProjection.From(result, "iis");

        preview.Tone.ShouldBe(CheckTone.Clean);
        preview.Message.ShouldBe("As if iis were due tonight: 2 renames, 1 compression, 1 deletion, 1 file left alone. Nothing has been changed.");
        preview.Details.ShouldContain("would rename C:\\logs\\app.log -> C:\\logs\\app.log.1", Case.Sensitive);
        preview.Details.ShouldContain("would compress", Case.Sensitive);
        preview.Details.ShouldContain("would delete", Case.Sensitive);
        preview.Details.ShouldContain("notifempty", Case.Sensitive);
        preview.Details.ShouldNotContain("run.start", Case.Sensitive);
        preview.Details.ShouldNotContain("job.start", Case.Sensitive);
    }

    [Fact]
    public void ACopyOutIsSaidAsSuch()
    {
        var preview = DryRunPreviewProjection.From(
            Run(0, Event("copytruncate", @"C:\logs\app.log", @"C:\logs\app.log.1", "due; --force"), Envelope), "iis");

        preview.Message.ShouldBe("As if iis were due tonight: 1 copy out. Nothing has been changed.");
    }

    [Fact]
    public void ARefusedPathIsAWarningWithItsLine()
    {
        var preview = DryRunPreviewProjection.From(
            Run(0,
                Event("guard.refuse", @"C:\Windows\x.log", reason: "resolves inside C:\\\\Windows", result: "skipped"),
                Event("rename", @"C:\logs\app.log", @"C:\logs\app.log.1", "due; --force"),
                Envelope),
            "iis");

        preview.Tone.ShouldBe(CheckTone.Warning);
        preview.Message.ShouldContain("1 path refused", Case.Sensitive);
    }

    [Fact]
    public void AGateSomebodyElseHoldsIsSaidPlainly()
    {
        var preview = DryRunPreviewProjection.From(Run(Core.ExitCode.LockHeld,
            "{\"schema\":1,\"product\":\"WinLogRotate\",\"version\":\"0.0.0\",\"verb\":\"run\",\"ok\":false,\"exitCode\":3,\"diagnostics\":[]}"), "iis");

        preview.Tone.ShouldBe(CheckTone.Warning);
        preview.Message.ShouldBe("Another rotation is running right now; try again in a moment.");
    }

    [Fact]
    public void ADisabledOrUnknownJobIsSaidInTheVerbsWords()
    {
        var disabled = "{\"schema\":1,\"product\":\"WinLogRotate\",\"version\":\"0.0.0\",\"verb\":\"run\",\"ok\":true,\"exitCode\":0,"
                       + "\"result\":{\"runId\":\"R\",\"dryRun\":true,\"jobsConsidered\":0,\"jobsRun\":0,\"completed\":0,\"failed\":0,\"bytesFreed\":0,\"errors\":[]},"
                       + "\"diagnostics\":[{\"severity\":\"Warning\",\"code\":\"LR2001\",\"message\":\"'iis' is disabled, so nothing ran.\",\"remedy\":\"winlogrotate job enable iis\"}]}";

        var preview = DryRunPreviewProjection.From(Run(0, disabled), "iis");

        preview.Tone.ShouldBe(CheckTone.Warning);
        preview.Message.ShouldBe("'iis' is disabled, so nothing ran.");
        preview.Details.ShouldContain("job enable iis", Case.Sensitive);

        var unknown = "{\"schema\":1,\"product\":\"WinLogRotate\",\"version\":\"0.0.0\",\"verb\":\"run\",\"ok\":false,\"exitCode\":2,"
                      + "\"diagnostics\":[{\"severity\":\"Error\",\"code\":\"LR1007\",\"message\":\"'iss' is not a configured job.\",\"remedy\":\"The configured jobs are: iis.\"}]}";

        DryRunPreviewProjection.From(Run(2, unknown), "iss").Tone.ShouldBe(CheckTone.Error);
    }

    [Fact]
    public void NothingPlannedIsSaidRatherThanShownAsAnEmptyList()
    {
        var preview = DryRunPreviewProjection.From(Run(0, Envelope), "iis");

        preview.Tone.ShouldBe(CheckTone.Warning);
        preview.Message.ShouldStartWith("Nothing would happen to iis");
    }

    [Fact]
    public void AChildThatDidNotRunIsItsOwnSentence()
    {
        var preview = DryRunPreviewProjection.From(new CliResult
        {
            ExitCode = -1,
            StdOut = "",
            StdErr = "",
            Failure = CliFailure.NotFound,
        }, "iis");

        preview.Tone.ShouldBe(CheckTone.Error);
        preview.Message.ShouldContain("could not be found", Case.Sensitive);
    }
}
