using System.Text.Json.Serialization;
using WinLogRotate.Core.Notify;

namespace WinLogRotate.Core.State;

/// <summary>What was last reported about a job.</summary>
/// <remarks>
/// Written as a name rather than a number. This file lands in support bundles, and "outcome": 2
/// tells a reader nothing they can act on.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<NotifyOutcome>))]
public enum NotifyOutcome
{
    /// <summary>Never reported on. Not the same as healthy - see NotificationPlanner.</summary>
    Unknown,

    Healthy,
    Failing,
}

/// <summary>
/// What was last <em>told to somebody</em> about one job.
/// </summary>
/// <remarks>
/// Every field here except <see cref="FailingSince"/> means "as last successfully reported", not
/// "as last observed". That distinction is the whole design: if state recorded observations, a
/// run whose delivery failed would still mark the failure as known, and the next run would treat
/// a live incident as old news under <c>on = "change"</c>. Silence compounding into permanent
/// silence is the worst thing this feature can do.
/// </remarks>
public sealed record JobNotifyState
{
    public NotifyOutcome Outcome { get; init; } = NotifyOutcome.Unknown;

    /// <summary>The failure shape last reported. Empty when healthy.</summary>
    public string Fingerprint { get; init; } = string.Empty;

    /// <summary>When that report was delivered, which is what the reminder counts from.</summary>
    public DateTimeOffset? NotifiedAt { get; init; }

    /// <summary>
    /// The threshold in force when it was reported.
    /// </summary>
    /// <remarks>
    /// Kept so that raising the threshold can produce an honest "resolved (below the current
    /// threshold)" rather than either a false "recovered" or an outstanding failure message that
    /// is never closed.
    /// </remarks>
    public string? NotifiedThreshold { get; init; }

    /// <summary>
    /// When the job was first <em>seen</em> failing - an observation, not a report.
    /// </summary>
    /// <remarks>
    /// The one exception to the rule above, because the message wants "failing since 1 September"
    /// and that is when it broke, not when somebody was told about it.
    /// </remarks>
    public DateTimeOffset? FailingSince { get; init; }

    /// <summary>Last run in which this job was observed at all, for pruning.</summary>
    public DateTimeOffset? LastSeen { get; init; }
}

/// <summary>One channel's circuit breaker.</summary>
public sealed record ChannelNotifyState
{
    public int ConsecutiveFailures { get; init; }

    /// <summary>Runs still to be skipped before the next probe.</summary>
    public int SkipRunsRemaining { get; init; }

    /// <summary>How many runs the next open state will skip for.</summary>
    public int Cooldown { get; init; }

    /// <summary>How many times this channel has opened, for a growing cooldown.</summary>
    public int OpenCount { get; init; }

    public DateTimeOffset? OpenedAt { get; init; }

    /// <summary>Already redacted: this file ends up in support bundles.</summary>
    public string? LastError { get; init; }

    public DateTimeOffset? LastAttempt { get; init; }
}

/// <summary>The whole file.</summary>
public sealed record NotifyStateDocument
{
    /// <summary>
    /// Unlike the rotation state, a document from the future is IGNORED rather than refused.
    /// </summary>
    /// <remarks>
    /// <see cref="StateStore"/> throws on a version it does not understand, deliberately, because
    /// misreading a rotation clock means rotating on the wrong schedule. Losing notification
    /// state costs at most one redundant message. Two documents that must fail in opposite
    /// directions do not belong in one file, which is why this is a sibling of state.json rather
    /// than a section inside it.
    /// </remarks>
    public int Version { get; init; } = 1;

    public DateTimeOffset? Written { get; init; }

    /// <summary>
    /// Which fingerprint algorithm produced the stored values.
    /// </summary>
    /// <remarks>
    /// When this stops matching, stored fingerprints are adopted silently rather than compared.
    /// Without it, the first release that changes how a failure is identified reports a mass
    /// change on every installation on the night of the upgrade - every job newly broken, then
    /// newly recovered. One string turns that into a release note.
    /// </remarks>
    public string Algorithm { get; init; } = NotifyFingerprint.Algorithm;

    /// <summary>Keyed by job name, plus <see cref="RunScope"/> for run-wide diagnostics.</summary>
    public Dictionary<string, JobNotifyState> Jobs { get; init; } = [];

    /// <summary>Keyed by <c>HookAction.Key</c>.</summary>
    public Dictionary<string, ChannelNotifyState> Channels { get; init; } = [];

    /// <summary>
    /// The pseudo-job that owns diagnostics belonging to the run rather than to any one job -
    /// a configuration that would not load, an insecure directory, an unreadable secret store.
    /// Not a legal job name, so it cannot collide with a real one.
    /// </summary>
    public const string RunScope = "*";
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(NotifyStateDocument))]
internal sealed partial class NotifyStateJsonContext : JsonSerializerContext;
