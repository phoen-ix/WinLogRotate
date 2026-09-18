using System.Text.Json;
using Shouldly;
using WinLogRotate.Cli.Output;
using WinLogRotate.Contracts;
using WinLogRotate.Core;
using WinLogRotate.Gui.Cli;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// What the Notifications page says after sending a test.
/// </summary>
/// <remarks>
/// <para>
/// The page showed one sentence - "Nothing was recorded: no history and no breaker counters." -
/// whether the test reached three channels, reached none, matched no channel at all, or never
/// ran. That sentence is true and is not an answer: it says what the verb did not touch, to
/// somebody who pressed a button to find out whether a message arrived.
/// </para>
/// <para>
/// The tone came from scraping stdout for the word FAILED, a text contract nothing pins, which
/// could not tell "sent to none" from "sent to three" either way.
/// </para>
/// </remarks>
public sealed class NotifyTestProjectionTests
{
    private static CliResult Test(int sent, int failed) => new()
    {
        // Always exit 0: a webhook outage is not a rotation failure, which is exactly why the
        // counts rather than the code have to be read.
        ExitCode = ExitCode.Ok,
        StdOut = $$"""{ "ok": true, "exitCode": 0, "diagnostics": [], "result": { "sent": {{sent}}, "failed": {{failed}}, "channels": [] } }""",
        StdErr = "",
        Verb = "notify test",
    };

    /// <summary>
    /// A test's channels, through the CLI's own serializer, so the shape is the verb's and not
    /// this file's.
    /// </summary>
    private static CliResult Tested(params NotifyTestChannelDto[] channels) => new()
    {
        ExitCode = ExitCode.Ok,
        StdOut = JsonSerializer.Serialize(
            new CliEnvelope<NotifyTestResult>
            {
                Schema = 1,
                Product = "winlogrotate",
                Version = "0.0.0",
                Verb = "notify test",
                Ok = true,
                ExitCode = 0,
                Result = new NotifyTestResult
                {
                    Sent = channels.Count(c => c.Ok),
                    Failed = channels.Count(c => !c.Ok),
                    Channels = channels,
                },
                Diagnostics = [],
            },
            typeof(CliEnvelope<NotifyTestResult>),
            Cli.CliJsonContext.Default),
        StdErr = "",
        Verb = "notify test",
    };

    /// <summary>
    /// The details name each channel and what became of it.
    /// </summary>
    /// <remarks>
    /// The verb reports every channel it reached for - whether it answered, how long it took,
    /// the HTTP status, an error it has already redacted, and whether a real run would have
    /// skipped it - and the page appended the JSON it came in. Somebody asking "which webhook did
    /// not answer, and why" was handed an envelope to read.
    /// </remarks>
    [Fact]
    public void TheDetailsNameEachChannelAndWhatBecameOfIt()
    {
        var view = NotifyTestProjection.From(Tested(
            new NotifyTestChannelDto
            {
                Channel = "webhook:ops",
                Display = "ops-webhook",
                Ok = true,
                Milliseconds = 120,
                Status = 200,
                WouldBeSkipped = false,
            },
            new NotifyTestChannelDto
            {
                Channel = "smtp:mail",
                Display = "mail-ops",
                Ok = false,
                Milliseconds = 3012,
                Error = "connection refused",
                WouldBeSkipped = true,
            }));

        view.Tone.ShouldBe(CheckTone.Warning);
        view.Details.ShouldContain("ops-webhook: delivered in 120 ms (HTTP 200)");
        view.Details.ShouldContain("mail-ops: failed in 3012 ms - connection refused");
        view.Details.ShouldContain("a real run would skip it");

        // The envelope the answer came in is not the answer.
        view.Details.ShouldNotContain("\"channels\"");
        view.Details.ShouldNotContain("{");
    }

    /// <summary>A test that reached every channel says how many.</summary>
    [Fact]
    public void ATestThatArrivedSaysHowManyChannelsGotIt()
    {
        var view = NotifyTestProjection.From(Test(sent: 3, failed: 0));

        view.Tone.ShouldBe(CheckTone.Clean);
        view.Message.ShouldContain("3 channel(s) received the test");

        // The old sentence was not wrong, only insufficient - it is kept where it belongs.
        view.Message.ShouldContain("no history and no breaker counters");
    }

    /// <summary>
    /// A test that matched nothing is not a test that passed.
    /// </summary>
    /// <remarks>
    /// The case the old sentence hid completely, and the likeliest one after a typo in a channel
    /// name. `notify test` exits 0 with `sent: 0, failed: 0`, which is indistinguishable from a
    /// clean run by every signal except the count.
    /// </remarks>
    [Fact]
    public void ATestThatMatchedNothingIsNotATestThatPassed()
    {
        var view = NotifyTestProjection.From(Test(sent: 0, failed: 0));

        view.Tone.ShouldBe(CheckTone.Warning);
        view.Message.ShouldContain("No channel was tested");
    }

    /// <summary>A partial delivery says both numbers.</summary>
    /// <remarks>
    /// Three channels configured and one broken is the ordinary shape of this. Reporting only
    /// the failure would read as total, and reporting only the success as clean.
    /// </remarks>
    [Fact]
    public void APartialDeliverySaysBothNumbers()
    {
        var view = NotifyTestProjection.From(Test(sent: 2, failed: 1));

        view.Tone.ShouldBe(CheckTone.Warning);
        view.Message.ShouldContain("2 channel(s) received the test");
        view.Message.ShouldContain("1 did not");
    }

    /// <summary>Every channel failing says nothing arrived.</summary>
    [Fact]
    public void EverythingFailingSaysNothingArrived()
    {
        var view = NotifyTestProjection.From(Test(sent: 0, failed: 2));

        view.Tone.ShouldBe(CheckTone.Warning);
        view.Message.ShouldContain("Nothing arrived");
    }

    /// <summary>A response this window cannot read is not a delivery.</summary>
    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{ "result": { } }""")]
    public void AnUnreadableResponseIsNotADelivery(string json)
    {
        var view = NotifyTestProjection.From(
            new CliResult { ExitCode = ExitCode.Ok, StdOut = json, StdErr = "", Verb = "notify test" });

        view.Tone.ShouldBe(CheckTone.Error);
        view.Message.ShouldNotContain("received the test");
    }
}
