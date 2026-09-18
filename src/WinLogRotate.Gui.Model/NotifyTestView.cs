using System.Text;
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
        string channels;

        try
        {
            using var document = JsonDocument.Parse(result.StdOut);
            var payload = document.RootElement.GetProperty("result");

            sent = payload.GetProperty("sent").GetInt32();
            failed = payload.GetProperty("failed").GetInt32();
            channels = Channels(payload);
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

        // The channels, one per line, under whatever the verb had to say about the run as a
        // whole. The raw envelope used to be appended here instead, which put a JSON document
        // in front of somebody who wanted to know which webhook had not answered.
        var details = EnvelopeDiagnostics.Render(
            EnvelopeDiagnostics.From(result.StdOut, "diagnostics")) + channels;

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

    /// <summary>
    /// Each channel the test reached for, and what came of it, one per line.
    /// </summary>
    /// <remarks>
    /// <c>result.channels[]</c> carries a display name, whether the channel answered, how long it
    /// took, the HTTP status where the transport had one, an error the verb has already redacted,
    /// and whether a real run would have skipped the channel because its breaker is open. That
    /// is the whole answer to "did my message arrive, and if not, why not". Every field is read
    /// as optional, because the GUI ships separately and may be reading an older verb's answer.
    /// </remarks>
    private static string Channels(JsonElement payload)
    {
        if (!payload.TryGetProperty("channels", out var channels)
            || channels.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var text = new StringBuilder();

        foreach (var channel in channels.EnumerateArray())
        {
            var name = Text(channel, "display") is { Length: > 0 } display
                ? display
                : Text(channel, "channel");

            var ok = channel.TryGetProperty("ok", out var answered)
                && answered.ValueKind == JsonValueKind.True;

            var took = channel.TryGetProperty("milliseconds", out var ms)
                && ms.ValueKind == JsonValueKind.Number
                && ms.TryGetInt64(out var milliseconds)
                    ? milliseconds
                    : 0;

            var line = $"{name}: {(ok ? "delivered" : "failed")} in {took} ms";

            if (channel.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.Number
                && status.TryGetInt32(out var http))
            {
                line += $" (HTTP {http})";
            }

            if (Text(channel, "error") is { Length: > 0 } error)
            {
                line += $" - {error}";
            }

            if (channel.TryGetProperty("wouldBeSkipped", out var skipped)
                && skipped.ValueKind == JsonValueKind.True)
            {
                line += " - suppressed after repeated failures, so a real run would skip it";
            }

            text.AppendLine(line);
        }

        return text.ToString();
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
