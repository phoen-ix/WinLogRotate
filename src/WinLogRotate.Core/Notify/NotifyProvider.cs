using WinLogRotate.Core.Secrets;

namespace WinLogRotate.Core.Notify;

/// <summary>Which kind of provider a named table describes.</summary>
public enum NotifyProviderKind
{
    Email,
    Pushover,
    Webhook,
}

/// <summary>How a mail relay expects to be authenticated to.</summary>
public enum SmtpAuth
{
    /// <summary>
    /// None. The relay authorises by source IP, which every corporate network already has.
    /// </summary>
    None,

    /// <summary>
    /// The run host's own identity.
    /// </summary>
    /// <remarks>
    /// The scheduled task runs as LocalSystem, so it presents <c>DOMAIN\HOST$</c> - all of a
    /// gMSA's benefit without giving up the file access LocalSystem provides, and with nothing
    /// stored anywhere.
    /// </remarks>
    Integrated,

    /// <summary>A username and password. The only shape that stores a credential.</summary>
    Login,
}

/// <summary>How the message leaves this machine.</summary>
public enum SmtpDelivery
{
    Network,

    /// <summary>
    /// Drop an .eml into a pickup directory for IIS SMTP or Exchange to collect.
    /// </summary>
    /// <remarks>
    /// The best option where it exists: no credential, and <b>no network call at all</b>, so no
    /// timeout can delay or wedge a rotation.
    /// </remarks>
    PickupDirectory,
}

public enum SmtpTls
{
    None,
    Opportunistic,
    Required,
}

/// <summary>
/// One named provider, from a <c>[notify.KIND.NAME]</c> table.
/// </summary>
/// <remarks>
/// One record for all three kinds rather than a hierarchy: they overlap heavily, the set is
/// closed and small, and a type per kind would mean a discriminated union in a language without
/// one. <see cref="Kind"/> says which members mean anything.
/// </remarks>
public sealed record NotifyProvider
{
    /// <summary>Dotted name as written and as referenced from <c>to</c>: <c>email.relay</c>.</summary>
    public required string Name { get; init; }

    public required NotifyProviderKind Kind { get; init; }

    public bool Enabled { get; init; } = true;

    public int Line { get; init; }

    public int Column { get; init; }

    // ---- email ---------------------------------------------------------------------------

    public string? Host { get; init; }
    public int Port { get; init; } = 25;
    public SmtpAuth Auth { get; init; } = SmtpAuth.None;
    public SmtpTls Tls { get; init; } = SmtpTls.Opportunistic;
    public SmtpDelivery Delivery { get; init; } = SmtpDelivery.Network;
    public string? PickupDirectory { get; init; }
    public string? From { get; init; }
    public IReadOnlyList<string> To { get; init; } = [];
    public string? Username { get; init; }
    public string? SubjectPrefix { get; init; }

    // ---- pushover ------------------------------------------------------------------------

    public SecretRef Token { get; init; } = SecretRef.None;
    public SecretRef UserKey { get; init; } = SecretRef.None;
    public int Priority { get; init; }

    // ---- webhook -------------------------------------------------------------------------

    /// <summary>
    /// A webhook URL is a credential, not an address.
    /// </summary>
    /// <remarks>
    /// A Slack or Teams incoming-webhook URL grants whoever holds it the right to post. It is
    /// carried as a <see cref="SecretRef"/> for exactly that reason, so it can live in the
    /// store like any other password.
    /// </remarks>
    public SecretRef Url { get; init; } = SecretRef.None;

    public string Method { get; init; } = "POST";
    public string ContentType { get; init; } = "application/json";
    public string? Body { get; init; }

    /// <summary>
    /// Characters this endpoint will accept, or null for no limit.
    /// </summary>
    /// <remarks>
    /// Needed because a webhook can point anywhere, and the destinations that matter disagree:
    /// Discord discards anything over <see cref="MessageComposer.DiscordLimit"/> characters and
    /// ntfy over <see cref="MessageComposer.NtfyLimit"/> - <b>silently</b>, so an unset limit does
    /// not fail loudly, it simply never arrives. Unlimited by default because truncating an
    /// operator's diagnostic is a real cost and most endpoints do not need it.
    /// </remarks>
    public int? MaxMessage { get; init; }

    // ---- shared --------------------------------------------------------------------------

    /// <summary>The SMTP password, or any other single credential this provider needs.</summary>
    public SecretRef Password { get; init; } = SecretRef.None;

    /// <summary>
    /// True when this provider needs nothing stored anywhere.
    /// </summary>
    /// <remarks>
    /// The best password is the one you never store, and this is what lets
    /// <c>notify show</c> say which providers have already achieved that.
    /// </remarks>
    public bool IsCredentialFree =>
        Kind == NotifyProviderKind.Email
        && (Delivery == SmtpDelivery.PickupDirectory || Auth is SmtpAuth.None or SmtpAuth.Integrated);

    /// <summary>Every credential reference this provider carries, for validation and display.</summary>
    public IEnumerable<(string Field, SecretRef Reference)> Credentials()
    {
        if (Password.HasValue)
        {
            yield return ("password", Password);
        }

        if (Token.HasValue)
        {
            yield return ("token", Token);
        }

        if (UserKey.HasValue)
        {
            yield return ("user_key", UserKey);
        }

        if (Url.HasValue)
        {
            yield return ("url", Url);
        }
    }
}
