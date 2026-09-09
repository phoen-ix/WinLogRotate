using WinLogRotate.Core.Secrets;

namespace WinLogRotate.Core.Notify.Delivery;

/// <summary>
/// One destination, with its credentials already fetched.
/// </summary>
/// <remarks>
/// <para>
/// Resolution happens once, before anything is sent, and deliberately not inside a sender. A
/// sender that could reach the secret store would be a sender that has to be trusted with it, and
/// there would then be four of them; this way the store is opened once per run by one type and the
/// senders receive only what they are about to authenticate with.
/// </para>
/// <para>
/// A channel whose credential could not be resolved is never constructed - the target is dropped
/// with an <c>LR5001</c> naming the field and the reason. Sending anonymously to a relay that was
/// configured with a password is worse than not sending: it can succeed.
/// </para>
/// </remarks>
public sealed record ResolvedChannel
{
    public required HookAction Action { get; init; }

    /// <summary>The provider behind this channel, when the target named one.</summary>
    public NotifyProvider? Provider { get; init; }

    /// <summary>Where the message goes: a URL, an address, a user key. Never printed unmasked.</summary>
    public SecretString Target { get; init; }

    /// <summary>The application token or password, when the transport needs one.</summary>
    public SecretString Credential { get; init; }

    /// <summary>How much of a message this destination will actually accept.</summary>
    public int Limit { get; init; } = MessageComposer.NoLimit;

    /// <summary>
    /// The SHA-256 certificate to insist on, from <c>[notify] server_cert_thumbprint</c>.
    /// </summary>
    /// <remarks>
    /// Carried on the channel rather than read from settings by each transport, because the SMTP
    /// sender is stateless - it installs and removes a process-global callback around every send -
    /// while the HTTP one bakes the pin into a handler it keeps.
    /// </remarks>
    public string? Pin { get; init; }

    /// <summary>The breaker key, and what <c>notify reset</c> takes.</summary>
    public string Key => Action.Key;

    /// <summary>Masked, and the only safe thing to print.</summary>
    /// <remarks>
    /// Falls back to what was written, because <c>eventlog:</c> is a complete target with an empty
    /// remainder and <c>HookAction.Display</c> renders it as nothing at all - which reads as a
    /// defect in every message that names a channel.
    /// </remarks>
    public string Display => Provider?.Name
        ?? (Action.Display is { Length: > 0 } shown ? shown : Action.Raw);
}
