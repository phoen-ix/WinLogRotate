using System.Text.Json;

namespace WinLogRotate.Gui.Cli;

/// <summary>
/// Turns a <c>notify test --json</c> response into what the operator is told.
/// </summary>
/// <remarks>
/// <para>
/// The page showed one sentence - "Nothing was recorded: no history and no breaker counters." -
/// whether the test delivered to three channels, delivered to none, matched no channel at all,
/// or never ran. That sentence is true and is not an answer: it says what the verb did not touch,
/// where the operator pressed a button to find out whether a message arrived.
/// </para>
/// <para>
/// The tone was decided by scraping stdout for the word FAILED, which is a text contract nothing
/// pins. <c>result.sent</c>, <c>result.failed</c> and <c>result.channels</c> are on the wire.
/// </para>
/// </remarks>
public static class NotifyTestProjection
{
    public static ConfigCheckView From(CliResult result)
    {
        if (result.Failure != CliFailure.None)
        {
            return new ConfigCheckView
            {
                Tone = CheckTone.Error,
                Message = result.Describe(),
                Details = result.Details,
            };
        }

        int sent;
        int failed;

        try
        {
            using var document = JsonDocument.Parse(result.StdOut);
            var payload = document.RootElement.GetProperty("result");

            sent = payload.GetProperty("sent").GetInt32();
            failed = payload.GetProperty("failed").GetInt32();
        }
        catch (Exception e) when (e is JsonException
                                      or KeyNotFoundException
                                      or InvalidOperationException)
        {
            return new ConfigCheckView
            {
                Tone = CheckTone.Error,
                Message = "Could not read the response from winlogrotate.exe.",
                Details = result.Details,
            };
        }

        var details = EnvelopeDiagnostics.Render(
            EnvelopeDiagnostics.From(result.StdOut, "diagnostics")) + result.StdOut;

        if (failed > 0)
        {
            return new ConfigCheckView
            {
                Tone = CheckTone.Warning,
                Message = sent > 0
                    ? $"{sent} channel(s) received the test; {failed} did not."
                    : $"{failed} channel(s) failed. Nothing arrived.",
                Details = details,
            };
        }

        if (sent == 0)
        {
            // The case the old sentence hid completely, and the likeliest one after a typo: a
            // test that matched no channel is not a test that passed. The verb exits 0 either
            // way, because a webhook outage is not a rotation failure.
            return new ConfigCheckView
            {
                Tone = CheckTone.Warning,
                Message = "No channel was tested. Check that a notification target is configured, "
                        + "and that any name you gave matches one.",
                Details = details,
            };
        }

        return new ConfigCheckView
        {
            Tone = CheckTone.Clean,
            Message = $"{sent} channel(s) received the test. Nothing was recorded: no history "
                    + "and no breaker counters.",
            Details = details,
        };
    }
}
