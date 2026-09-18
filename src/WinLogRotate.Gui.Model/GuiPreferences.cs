using System.Globalization;
using System.Text;
using System.Text.Json;

namespace WinLogRotate.Gui.Cli;

/// <summary>When the console looks for a newer release on its own.</summary>
public enum UpdateCheckMode
{
    /// <summary>Only when Check now is pressed. The default: a log rotator that reaches the
    /// internet on its own is something an operator opts into, never something they find out
    /// about.</summary>
    Manual,

    /// <summary>Once a day, when the window opens. Never more often, never while it is open,
    /// and never anything more than a check: installing is always a press.</summary>
    Daily,
}

/// <summary>
/// What the console remembers about its user's choices, and nothing about the machine's.
/// </summary>
/// <remarks>
/// <para>
/// A per-user file, because the configuration directory of a per-machine install is writable by
/// administrators only and this window usually is not one. It holds two things: whether to check
/// for updates on its own, and when it last did - so an offline machine asks again tomorrow
/// rather than on every start.
/// </para>
/// <para>
/// Read leniently and written completely. A value that is not one of the spellings here means
/// the default, and the default is never consent: a file that has been damaged, hand-edited or
/// written by a newer console cannot turn the daily check on by accident.
/// </para>
/// </remarks>
public sealed record GuiPreferences
{
    public const string FileName = "gui.json";

    /// <summary>A check is due again after this long. A day, because a release is not an
    /// event anyone needs to hear about within the hour.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);

    public UpdateCheckMode UpdateCheck { get; init; } = UpdateCheckMode.Manual;

    public DateTimeOffset? LastUpdateCheckUtc { get; init; }

    public static GuiPreferences Default { get; } = new();

    /// <summary>
    /// Whether a scheduled check should run now.
    /// </summary>
    /// <remarks>
    /// Never checked is due. A stamp in the future is due too: a clock that went backwards
    /// would otherwise silence the check until it caught up, which could be years.
    /// </remarks>
    public static bool IsCheckDue(DateTimeOffset? last, DateTimeOffset now) =>
        last is null || last > now || now - last >= CheckInterval;

    public static GuiPreferences Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return Default;
            }

            var mode = root.TryGetProperty("updateCheck", out var m) && m.ValueKind == JsonValueKind.String
                && string.Equals(m.GetString(), "daily", StringComparison.OrdinalIgnoreCase)
                    ? UpdateCheckMode.Daily
                    : UpdateCheckMode.Manual;

            DateTimeOffset? last = root.TryGetProperty("lastUpdateCheckUtc", out var l) && l.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(l.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                    ? parsed.ToUniversalTime()
                    : null;

            return new GuiPreferences { UpdateCheck = mode, LastUpdateCheckUtc = last };
        }
        catch (JsonException)
        {
            return Default;
        }
    }

    public string ToJson()
    {
        var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("updateCheck", UpdateCheck == UpdateCheckMode.Daily ? "daily" : "manual");

            if (LastUpdateCheckUtc is { } last)
            {
                writer.WriteString("lastUpdateCheckUtc", last.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }
}
