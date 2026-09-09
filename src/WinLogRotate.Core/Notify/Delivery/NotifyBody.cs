using System.Text;
using System.Text.Json;

namespace WinLogRotate.Core.Notify.Delivery;

/// <summary>
/// Builds a webhook's request body, substituting into an operator's template.
/// </summary>
/// <remarks>
/// <para>
/// The escaping is the whole point, and it is a security property rather than a nicety. A template
/// of <c>{"text": "{body}"}</c> is what every Slack, Teams, Discord and ntfy example looks like,
/// and the substituted text contains file paths and error messages the operator did not write. A
/// log called <c>a"b.log</c> would end the JSON string early; something crafted could append
/// fields. So every value is escaped for the declared content type before it is placed, and the
/// template supplies the quotes.
/// </para>
/// <para>
/// No serializer is involved for the template case - there is no object to serialize, only a
/// string with holes. The default body, where there is no template, does go through a
/// source-generated context: reflection-based serialization throws in a NativeAOT binary.
/// </para>
/// </remarks>
public static class NotifyBody
{
    /// <summary>The fields a template may name.</summary>
    public static readonly string[] Placeholders =
        ["subject", "body", "severity", "job", "machine", "reason", "run", "fingerprint"];

    public static string Render(
        string? template, string contentType, PlannedNotification message,
        string subject, string body, RunSummary run)
    {
        var job = message.Job == State.NotifyStateDocument.RunScope ? "configuration" : message.Job;

        if (string.IsNullOrWhiteSpace(template))
        {
            return JsonSerializer.Serialize(
                new NotifyPayload
                {
                    Subject = subject,
                    Body = body,
                    Severity = message.Severity.ToString().ToLowerInvariant(),
                    Job = job,
                    Machine = run.Machine,
                    Reason = message.Reason.ToString().ToLowerInvariant(),
                    Run = run.RunId,
                    Fingerprint = message.Fingerprint,
                },
                NotifyPayloadJsonContext.Default.NotifyPayload);
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["subject"] = subject,
            ["body"] = body,
            ["severity"] = message.Severity.ToString().ToLowerInvariant(),
            ["job"] = job,
            ["machine"] = run.Machine,
            ["reason"] = message.Reason.ToString().ToLowerInvariant(),
            ["run"] = run.RunId,
            ["fingerprint"] = message.Fingerprint,
        };

        return Substitute(template, values, Escaper(contentType));
    }

    /// <summary>How a value has to be written so it cannot escape its slot.</summary>
    private static Func<string, string> Escaper(string contentType)
    {
        var type = contentType.Split(';')[0].Trim();

        if (type.EndsWith("json", StringComparison.OrdinalIgnoreCase))
        {
            return JsonInner;
        }

        if (type.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
        {
            return Uri.EscapeDataString;
        }

        if (type.EndsWith("xml", StringComparison.OrdinalIgnoreCase))
        {
            return Xml;
        }

        // text/plain and anything unrecognised. There is no structure to break out of, so
        // escaping would corrupt the message rather than protect it.
        return static v => v;
    }

    /// <summary>A JSON string body, without its quotes - the template supplies those.</summary>
    private static string JsonInner(string value)
    {
        var quoted = JsonSerializer.Serialize(value, NotifyPayloadJsonContext.Default.String);
        return quoted[1..^1];
    }

    private static string Xml(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);

    /// <summary>
    /// Replaces <c>{name}</c>, leaving anything else exactly as written.
    /// </summary>
    /// <remarks>
    /// Hand-written rather than <c>string.Format</c> or a regex: a JSON template is full of braces
    /// that are not placeholders, and <c>string.Format</c> throws on the first one. An unknown
    /// <c>{name}</c> is left in place rather than blanked, so a typo is visible in the delivered
    /// message instead of silently producing an empty field.
    /// </remarks>
    private static string Substitute(
        string template, IReadOnlyDictionary<string, string> values, Func<string, string> escape)
    {
        var output = new StringBuilder(template.Length + 256);

        for (var i = 0; i < template.Length; i++)
        {
            if (template[i] != '{')
            {
                output.Append(template[i]);
                continue;
            }

            var close = template.IndexOf('}', i + 1);
            if (close < 0)
            {
                output.Append(template[i..]);
                break;
            }

            var name = template[(i + 1)..close];

            if (values.TryGetValue(name, out var value))
            {
                output.Append(escape(value));
                i = close;
            }
            else
            {
                output.Append(template[i]);
            }
        }

        return output.ToString();
    }
}

/// <summary>The default webhook body, when a provider declares no template of its own.</summary>
public sealed record NotifyPayload
{
    public required string Subject { get; init; }
    public required string Body { get; init; }
    public required string Severity { get; init; }
    public required string Job { get; init; }
    public required string Machine { get; init; }
    public required string Reason { get; init; }
    public required string Run { get; init; }
    public required string Fingerprint { get; init; }
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false)]
[System.Text.Json.Serialization.JsonSerializable(typeof(NotifyPayload))]
[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class NotifyPayloadJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
