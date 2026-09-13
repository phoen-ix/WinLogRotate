namespace WinLogRotate.Core.Notify;

/// <summary>Something a provider is missing, as a clause, and the line that fixes it.</summary>
public readonly record struct ProviderLack(string Clause, string Remedy);

/// <summary>Which provider an inline target borrows, or why it cannot.</summary>
public readonly record struct ProviderPairing(NotifyProvider? Provider, string? Problem, string? Remedy);

/// <summary>
/// What a provider must carry before its transport can use it, and which provider an inline
/// target borrows.
/// </summary>
/// <remarks>
/// <para>
/// One spelling for two callers. <c>ChannelResolver</c> applies these at run time, where a
/// provider that fails them is dropped with LR5001 before anything is sent; <c>ConfigLoader</c>
/// applies them at load time so <c>config check</c> says the same thing on the same line. The
/// resolver used to check one thing - login without a password - and the loader nothing, so a
/// webhook table with no <c>url</c> reached the sender as an empty target, the sender caught the
/// resulting exception as a 400, and the dispatcher recorded the message as reported.
/// </para>
/// <para>
/// The pairing rule is the documented one made real. docs/notifications.md has said since the
/// feature shipped that <c>smtp:ops@example.com</c> "needs a <c>[notify.email.*]</c> provider for
/// the relay" and <c>pushover:KEY</c> one for the token; the resolver never handed an inline target
/// any provider, so neither documented target had ever worked.
/// </para>
/// </remarks>
public static class NotifyProviderRules
{
    /// <summary>
    /// What this provider lacks that its transport cannot do without, or null when it has everything.
    /// </summary>
    /// <param name="userKeyFromTarget">
    /// True for an inline <c>pushover:KEY</c> target, which brings its own user key and borrows
    /// only the token.
    /// </param>
    public static ProviderLack? Missing(NotifyProvider provider, bool userKeyFromTarget = false) => provider.Kind switch
    {
        NotifyProviderKind.Webhook when !provider.Url.HasValue => new ProviderLack(
            "names no url",
            "Set url = \"@secret:NAME\" - a webhook URL is a credential, so store it rather than "
            + "writing it in the file."),

        NotifyProviderKind.Pushover when !provider.Token.HasValue => new ProviderLack(
            "names no token",
            "Set token = \"@secret:NAME\" to the application token from pushover.net."),

        NotifyProviderKind.Pushover when !userKeyFromTarget && !provider.UserKey.HasValue => new ProviderLack(
            "names no user_key",
            "Set user_key = \"@secret:NAME\", or write the key into the target as pushover:KEY."),

        NotifyProviderKind.Email when provider.Delivery == SmtpDelivery.PickupDirectory
                                      && string.IsNullOrWhiteSpace(provider.PickupDirectory) => new ProviderLack(
            "uses delivery = \"pickup\" but names no pickup_directory",
            "Set pickup_directory to the folder IIS SMTP or Exchange collects from."),

        NotifyProviderKind.Email when provider.Delivery == SmtpDelivery.Network
                                      && string.IsNullOrWhiteSpace(provider.Host) => new ProviderLack(
            "names no host",
            "Set host to the relay, or delivery = \"pickup\" with a pickup_directory."),

        NotifyProviderKind.Email when provider.Delivery == SmtpDelivery.Network
                                      && provider.Auth == SmtpAuth.Login
                                      && !provider.Password.HasValue => new ProviderLack(
            "uses auth = \"login\" but names no password",
            "Set password = \"@secret:NAME\", or auth = \"none\" for an IP-authorised relay."),

        _ => null,
    };

    /// <summary>The provider kind an inline scheme borrows from, or null for one complete on its own.</summary>
    public static NotifyProviderKind? BorrowsFrom(HookScheme scheme) => scheme switch
    {
        HookScheme.Smtp => NotifyProviderKind.Email,
        HookScheme.Pushover => NotifyProviderKind.Pushover,
        _ => null,
    };

    /// <summary>
    /// The one enabled provider an inline target pairs with, or the problem that stops it.
    /// </summary>
    /// <remarks>
    /// Exactly one, by decision. <c>smtp:ops@example.com</c> says nothing about which relay, so the
    /// only unambiguous reading is "the relay" - and with two configured, taking the one written
    /// higher up would route mail through whichever table happened to come first, with nothing to
    /// say so. Zero and several are both reported, each with its own fix. A scheme that borrows
    /// nothing pairs with nothing and has no problem.
    /// </remarks>
    public static ProviderPairing Pair(HookScheme scheme, string display, IReadOnlyList<NotifyProvider> providers)
    {
        if (BorrowsFrom(scheme) is not { } kind)
        {
            return default;
        }

        var candidates = providers.Where(p => p.Enabled && p.Kind == kind).ToArray();
        var table = kind == NotifyProviderKind.Email ? "[notify.email.*]" : "[notify.pushover.*]";
        var lends = kind == NotifyProviderKind.Email ? "the relay" : "the application token";

        return candidates.Length switch
        {
            1 => new ProviderPairing(candidates[0], null, null),

            0 => new ProviderPairing(null,
                $"'{display}' needs a {table} provider for {lends}, and none is enabled.",
                $"Define one, e.g. {table.Replace("*", kind == NotifyProviderKind.Email ? "relay" : "app", StringComparison.Ordinal)}, "
                + "or name a provider in 'to' instead of writing the target inline."),

            _ => new ProviderPairing(null,
                $"'{display}' could borrow {lends} from any of "
                + $"{string.Join(", ", candidates.Select(c => c.Name))}, and does not say which.",
                "Keep one of them enabled, or name the provider in 'to' and put this address in "
                + "its own 'to' list."),
        };
    }
}
