using WinLogRotate.Contracts;

namespace WinLogRotate.Core.Notify;

/// <summary>When a notification is worth sending.</summary>
public enum NotifyOn
{
    /// <summary>Never. The whole feature off, without deleting the configuration.</summary>
    Never,

    /// <summary>
    /// Only when the outcome changes: working to failing, or failing to working.
    /// </summary>
    /// <remarks>
    /// The default, and the single highest-value anti-fatigue rule there is. Without it a
    /// permanently locked file sends the same message every night until somebody writes a mail
    /// rule for it - at which point the next, different failure is filed away unread too.
    /// </remarks>
    Change,

    /// <summary>Every run that produces anything at or above the threshold.</summary>
    Every,
}

/// <summary>
/// The <c>[notify]</c> table.
/// </summary>
/// <remarks>
/// Shaped like <see cref="Configuration.JournalSettings"/>: non-nullable members with a static
/// <see cref="Default"/>, so a missing table binds to something usable rather than to null.
/// </remarks>
public sealed record NotifySettings
{
    /// <summary>
    /// Defaults to true, with an empty <see cref="To"/> meaning nothing is sent.
    /// </summary>
    /// <remarks>
    /// The alternative - defaulting to false - means a half-written <c>[notify]</c> block is
    /// silently inert, and the person who wrote it finds out during the incident it was meant to
    /// warn them about. This way the block does nothing because it names no targets, which
    /// <c>notify show</c> states plainly.
    /// </remarks>
    public bool Enabled { get; init; } = true;

    public NotifyOn On { get; init; } = NotifyOn.Change;

    /// <summary>
    /// The severity worth telling somebody about. Binds to the existing
    /// <see cref="Severity"/> rather than inventing a second notion of importance.
    /// </summary>
    public Severity Threshold { get; init; } = Severity.Warning;

    /// <summary>
    /// How long a continuing failure stays quiet before it is repeated.
    /// </summary>
    /// <remarks>
    /// Without a reminder, "only on change" means a permanent failure produces exactly one
    /// message ever - and once somebody deletes that message the system is silently broken for
    /// good. A week is long enough not to be noise and short enough to be noticed.
    /// </remarks>
    public TimeSpan RemindAfter { get; init; } = TimeSpan.FromDays(7);

    /// <summary>The whole notification phase's wall clock, not a per-target timeout.</summary>
    public TimeSpan Budget { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Consecutive failures before a channel is suppressed.</summary>
    public int BreakerAfter { get; init; } = 5;

    /// <summary>Runs to skip once a channel's breaker has opened.</summary>
    public int BreakerCooldown { get; init; } = 5;

    /// <summary>Targets, as written: provider references or inline scheme strings.</summary>
    public IReadOnlyList<string> To { get; init; } = [];

    /// <summary>Extra strings to mask in any message - a customer name, an internal host.</summary>
    public IReadOnlyList<string> Redact { get; init; } = [];

    /// <summary>Explicit proxy, or "none". Unset means the system default.</summary>
    public string? Proxy { get; init; }

    public IReadOnlyList<string> NoProxy { get; init; } = [];

    /// <summary>
    /// SHA-256 thumbprint to pin. There is deliberately no option to skip TLS verification.
    /// </summary>
    /// <remarks>
    /// Pinning is strictly stronger than an <c>insecure</c> switch for the case people actually
    /// have - a self-signed certificate on an internal relay - and costs the same single line of
    /// configuration. An <c>insecure</c> flag, once shipped, is set once during an incident and
    /// never unset.
    /// </remarks>
    public string? ServerCertThumbprint { get; init; }

    public static NotifySettings Default { get; } = new();

    /// <summary>True when this configuration could actually send something.</summary>
    public bool WouldSend => Enabled && On != NotifyOn.Never && To.Count > 0;
}
