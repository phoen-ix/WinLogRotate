using WinLogRotate.Contracts;
using WinLogRotate.Core.Secrets;

namespace WinLogRotate.Core.Notify.Delivery;

/// <summary>The channels a run will actually attempt, and why the others were dropped.</summary>
public sealed record ResolvedChannels
{
    public required IReadOnlyList<ResolvedChannel> Channels { get; init; }

    /// <summary>One per target that will not be used, already safe to print.</summary>
    public required IReadOnlyList<CliDiagnostic> Diagnostics { get; init; }
}

/// <summary>
/// Turns <c>[notify] to</c> into destinations with their credentials in hand.
/// </summary>
/// <remarks>
/// <para>
/// Every credential is fetched here, once, before anything is sent. A target whose credential
/// cannot be resolved is <b>dropped</b> rather than attempted: sending anonymously to a relay that
/// was configured with a password is worse than not sending, because it can succeed - and then the
/// operator believes authentication is working right up until the relay tightens.
/// </para>
/// <para>
/// Provider names are matched before inline schemes, exactly as <c>ConfigLoader</c> does when it
/// validates the same list. The two must agree: a target that validates as a provider and resolves
/// as a command line would pass <c>config check</c> and then do something else entirely.
/// </para>
/// </remarks>
public static class ChannelResolver
{
    public static ResolvedChannels Resolve(
        NotifySettings settings,
        IReadOnlyList<NotifyProvider> providers,
        ISecretResolver secrets,
        SenderTable senders)
    {
        var channels = new List<ResolvedChannel>();
        var diagnostics = new List<CliDiagnostic>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in settings.To)
        {
            var provider = providers.FirstOrDefault(
                p => string.Equals(p.Name, target, StringComparison.OrdinalIgnoreCase));

            var channel = provider is null
                ? FromInline(target, settings, senders, diagnostics)
                : FromProvider(provider, settings, secrets, senders, diagnostics);

            if (channel is null)
            {
                continue;
            }

            // Two targets that reach the same place share a breaker key, and attempting both
            // would double every message and count two failures for one outage.
            if (!seen.Add(channel.Key))
            {
                continue;
            }

            channels.Add(channel);
        }

        return new ResolvedChannels { Channels = channels, Diagnostics = diagnostics };
    }

    private static ResolvedChannel? FromProvider(
        NotifyProvider provider, NotifySettings settings, ISecretResolver secrets,
        SenderTable senders, List<CliDiagnostic> diagnostics)
    {
        if (!provider.Enabled)
        {
            return null;
        }

        var scheme = provider.Kind switch
        {
            NotifyProviderKind.Email => HookScheme.Smtp,
            NotifyProviderKind.Pushover => HookScheme.Pushover,
            _ => HookScheme.Http,
        };

        // Resolved in the order the transports need them, and the first failure drops the whole
        // provider - a webhook with a URL but no token is not half usable.
        var credential = SecretString.None;
        var address = SecretString.None;

        foreach (var (field, reference) in provider.Credentials())
        {
            var resolution = secrets.Resolve(reference);

            if (!resolution.Ok)
            {
                diagnostics.Add(new CliDiagnostic
                {
                    Severity = Severity.Warning,
                    Code = DiagnosticCode.NotifyMisconfigured,
                    Message = $"'{provider.Name}' will not be used: its {field} could not be read "
                            + $"({resolution.Error}).",
                    Line = reference.Line == 0 ? null : reference.Line,
                    Column = reference.Column == 0 ? null : reference.Column,
                    Remedy = reference.Source == SecretSource.Store
                        ? $"winlogrotate secret set {reference.Key}"
                        : "Fix the reference, or remove it if the destination needs no credential.",
                });

                return null;
            }

            switch (field)
            {
                case "url" or "user_key":
                    address = resolution.Value;
                    break;

                default:
                    credential = resolution.Value;
                    break;
            }
        }

        if (provider.Kind == NotifyProviderKind.Email
            && provider.Delivery == SmtpDelivery.Network
            && provider.Auth == SmtpAuth.Login
            && !credential.HasValue)
        {
            diagnostics.Add(new CliDiagnostic
            {
                Severity = Severity.Warning,
                Code = DiagnosticCode.NotifyMisconfigured,
                Message = $"'{provider.Name}' uses auth = \"login\" but names no password.",
                Line = provider.Line == 0 ? null : provider.Line,
                Remedy = "Set password = \"@secret:NAME\", or auth = \"none\" for an IP-authorised relay.",
            });

            return null;
        }

        return Refused(scheme, provider.Name, senders, diagnostics)
            ? null
            : new ResolvedChannel
            {
                Action = HookAction.Create(
                    scheme, provider.Name, provider.Name, provider: provider.Name,
                    line: provider.Line, column: provider.Column),
                Provider = provider,
                Target = address,
                Credential = credential,
                Limit = LimitFor(scheme, provider),
                Pin = settings.ServerCertThumbprint,
            };
    }

    private static ResolvedChannel? FromInline(
        string target, NotifySettings settings, SenderTable senders, List<CliDiagnostic> diagnostics)
    {
        var parsed = HookParser.Parse(target);

        if (!parsed.IsOk || parsed.Action is not { } action)
        {
            // ConfigLoader already reported the parse failure against the file and line. Repeating
            // it here would double every message an operator sees for one typo.
            return null;
        }

        var display = action.Display is { Length: > 0 } shown ? shown : action.Raw;

        if (Refused(action.Scheme, display, senders, diagnostics))
        {
            return null;
        }

        // An inline http: target carries its own URL, and it is still a credential.
        var address = action.Scheme == HookScheme.Http
            ? SecretString.From(action.Target)
            : SecretString.None;

        return new ResolvedChannel
        {
            Action = action,
            Target = address,
            Limit = LimitFor(action.Scheme, provider: null),
            Pin = settings.ServerCertThumbprint,
        };
    }

    /// <summary>Reports a scheme this build parses but does not deliver, rather than ignoring it.</summary>
    private static bool Refused(
        HookScheme scheme, string display, SenderTable senders, List<CliDiagnostic> diagnostics)
    {
        if (senders.Handles(scheme))
        {
            return false;
        }

        var name = HookSchemes.Name(scheme);

        diagnostics.Add(new CliDiagnostic
        {
            Severity = Severity.Warning,
            Code = DiagnosticCode.NotifyMisconfigured,
            Message = senders.WhyNot(scheme) is { } why
                ? $"'{display}' names {name}:, which {why}."
                : $"'{display}' names {name}:, which this build does not deliver.",
            Remedy = senders.WhyNot(scheme) is not null

                // Nothing to fix: the target is correct, this machine simply cannot serve it.
                // Suggesting alternatives here would be advice to change a working configuration.
                ? null
                : HookSchemes.NeedsHardenedConfDir(scheme)
                    ? "Targets that run code on this machine land with the configuration-directory "
                    + "check they require. Use http:, smtp:, pushover: or eventlog: in the meantime."
                    : "Use http:, smtp:, pushover: or eventlog:.",
        });

        return true;
    }

    /// <summary>
    /// What the destination will accept.
    /// </summary>
    /// <remarks>
    /// Only Pushover and the Event Log are known from the scheme alone. A webhook could be
    /// pointed at anything, so it is unlimited unless the provider says otherwise - silently
    /// truncating an operator's diagnostic is a real cost, paid only where the destination would
    /// otherwise discard the whole message.
    /// </remarks>
    private static int LimitFor(HookScheme scheme, NotifyProvider? provider) => scheme switch
    {
        HookScheme.Pushover => MessageComposer.PushoverLimit,
        HookScheme.EventLog => MessageComposer.EventLogLimit,
        _ => provider?.MaxMessage ?? MessageComposer.NoLimit,
    };
}
