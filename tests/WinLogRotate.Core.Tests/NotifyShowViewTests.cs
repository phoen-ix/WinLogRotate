using System.Text.Json;
using Shouldly;
using WinLogRotate.Gui.Cli;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// What the Notifications page says above its grid.
/// </summary>
/// <remarks>
/// It said "Reporting is on, change." and "reminder after 7.00:00:00." - the envelope's enum name
/// and TimeSpan, appended as they came. The envelope here is the verb's own, run in this process,
/// so a change to how either value is written reaches these tests.
/// </remarks>
public sealed class NotifyShowViewTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("winlogrotate-notifyshow-");

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); } catch (IOException) { }
    }

    /// <summary>The page's summary for a <c>[notify]</c> table, through <c>notify show --json</c>.</summary>
    private string Summary(string notify)
    {
        File.WriteAllText(Path.Combine(_dir.FullName, "config.toml"), $"schema = 1\n\n[notify]\n{notify}\n");

        // To a file, not Console.Out, which other classes swap while this one runs.
        var file = Path.Combine(_dir.FullName, "show.json");
        var parse = Cli.Commands.CommandTree.Build().Parse(
            ["notify", "show", "--json", "--output", file, "--no-event-log", "--config-dir", _dir.FullName]);

        parse.Errors.ShouldBeEmpty();
        parse.Invoke();

        using var document = JsonDocument.Parse(File.ReadAllText(file));
        return NotifyShowView.Summary(document.RootElement.GetProperty("result"));
    }

    [Fact]
    public void TheDefaultsReadAsSentences()
    {
        var summary = Summary("to = [\"https://hooks.example/notify\"]");

        summary.ShouldStartWith("Reporting is on: a job is reported when its outcome changes.");
        summary.ShouldContain("Threshold: warning and above. A failure that continues is reported again after 7 days.");
        summary.ShouldNotContain("7.00:00:00");
        summary.ShouldNotContain(", change");
    }

    [Theory]
    [InlineData("on = \"every\"", "Reporting is on: every run reports the jobs that failed.")]
    [InlineData("on = \"never\"", "Nothing would be sent: [notify] has on = \"never\".")]
    public void TheModeIsAPhrase(string setting, string expected)
    {
        var summary = Summary($"to = [\"https://hooks.example/notify\"]\n{setting}");

        summary.ShouldStartWith(expected);
        summary.ShouldNotContain("reported again", Case.Sensitive, "only \"change\" holds a failure back to remind about it");
    }

    [Fact]
    public void ADisabledTableSaysSo()
    {
        Summary("to = [\"https://hooks.example/notify\"]\nenabled = false")
            .ShouldStartWith("Nothing would be sent: [notify] has enabled = false.");
    }

    [Fact]
    public void NoTargetsIsTheReason()
    {
        Summary("threshold = \"error\"").ShouldStartWith(
            "Nothing would be sent: no targets are configured." + Environment.NewLine
            + "Threshold: error and above.");
    }

    [Theory]
    [InlineData("36h", "after 1 day 12 hours.")]
    [InlineData("90m", "after 1 hour 30 minutes.")]
    [InlineData("0s", "A failure that continues is not reported again.")]
    public void TheReminderIsADuration(string remind, string expected)
    {
        Summary($"to = [\"https://hooks.example/notify\"]\nremind_after = \"{remind}\"").ShouldContain(expected);
    }
}
