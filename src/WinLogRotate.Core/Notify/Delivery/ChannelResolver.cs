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
/// So is a provider that lacks what its transport needs - a webhook with no <c>url</c>, a relay
/// with no <c>host</c> - and an inline <c>smtp:</c> or <c>pushover:</c> target with no provider to
/// borrow from. Each of those used to reach its sender and come back as a 4xx, which the dispatcher
/// then recorded as the message having been reported. <see cref="NotifyProviderRules"/> holds the
/// list, and <c>ConfigLoader</c> applies the same one at load time.
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
                ? FromInline(target, settings, providers, secrets, senders, diagnostics)
                : FromProvider(provider, inline: null, settings, secrets, senders, diagnostics);

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

    /// <summary>
    /// A channel backed by a provider table - named in <c>to</c>, or borrowed by an inline target.
    /// </summary>
    /// <param name="inline">
    /// The <c>smtp:</c> or <c>pushover:</c> target borrowing this provider, or null when the target
    /// named the provider itself. An inline target keeps its own action, and so its own breaker key
    /// and display: a breaker opened by <c>smtp:oncall@example.com</c> must not suppress
    /// <c>email.relay</c> named beside it, and <c>notify status</c> has to tell them apart.
    /// </param>
    private static ResolvedChannel? FromProvider(
        NotifyProvider provider, HookAction? inline, NotifySettings settings, ISecretResolver secrets,
        SenderTable senders, List<CliDiagnostic> diagnostics)
    {
        if (!provider.Enabled)
        {
            return null;
        }

        var scheme = inline?.Scheme ?? provider.Kind switch
        {
            NotifyProviderKind.Email => HookScheme.Smtp,
            NotifyProviderKind.Pushover => HookScheme.Pushover,
            _ => HookScheme.Http,
        };

        var display = inline is null ? provider.Name : DisplayOf(inline);
        var userKeyFromTarget = inline?.Scheme == HookScheme.Pushover;

        // Before any credential is fetched: a provider that cannot be used is not worth opening
        // the secret store for, and the message has to name the field rather than the symptom.
        if (NotifyProviderRules.Missing(provider, userKeyFromTarget) is { } lack)
        {
            diagnostics.Add(new CliDiagnostic
            {
                Severity = Severity.Warning,
                Code = DiagnosticCode.NotifyMisconfigured,
                Message = inline is null
                    ? $"'{provider.Name}' will not be used: it {lack.Clause}."
                    : $"'{display}' will not be used: '{provider.Name}' {lack.Clause}.",
                Line = provider.Line == 0 ? null : provider.Line,
                Column = provider.Column == 0 ? null : provider.Column,
                Remedy = lack.Remedy,
            });

            return null;
        }

        // Resolved in the order the transports need them, and the first failure drops the whole
        // provider - a webhook with a URL but no token is not half usable.
        var credential = SecretString.None;
        var address = SecretString.None;

        foreach (var (field, reference) in provider.Credentials())
        {
            // An inline pushover:KEY brings its own user key; the provider's, if it has one, is
            // for the target that names the provider.
            if (userKeyFromTarget && field == "user_key")
            {
                continue;
            }

            var resolution = secrets.Resolve(reference);

            if (!resolution.Ok)
            {
                diagnostics.Add(new CliDiagnostic
                {
                    Severity = Severity.Warning,
                    Code = DiagnosticCode.NotifyMisconfigured,
                    Message = inline is null
                        ? $"'{provider.Name}' will not be used: its {field} could not be read ({resolution.Error})."
                        : $"'{display}' will not be used: the {field} of '{provider.Name}' could not be read "
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

        if (Refused(scheme, display, senders, diagnostics))
        {
            return null;
        }

        // An inline target's address is the target itself: the URL of an http: target, the user
        // key of a pushover: one. An smtp: target's address is a recipient, not a secret, and the
        // sender reads it from the action.
        if (inline is not null && inline.Scheme is HookScheme.Http or HookScheme.Pushover)
        {
            address = SecretString.From(inline.Target);
        }

        return new ResolvedChannel
        {
            Action = inline ?? HookAction.Create(
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
        string target, NotifySettings settings, IReadOnlyList<NotifyProvider> providers,
        ISecretResolver secrets, SenderTable senders, List<CliDiagnostic> diagnostics)
    {
        var parsed = HookParser.Parse(target);

        if (!parsed.IsOk || parsed.Action is not { } action)
        {
            // ConfigLoader already reported the parse failure against the file and line. Repeating
            // it here would double every message an operator sees for one typo.
            return null;
        }

        var display = DisplayOf(action);

        if (Refused(action.Scheme, display, senders, diagnostics))
        {
            return null;
        }

        // smtp: and pushover: are complete only with a provider behind them - the relay, the
        // application token. Exactly one enabled provider of the kind, or the target is dropped
        // and the diagnostic says which of the two ways to fix it applies.
        var pairing = NotifyProviderRules.Pair(action.Scheme, display, providers);

        if (pairing.Problem is { } problem)
        {
            diagnostics.Add(new CliDiagnostic
            {
                Severity = Severity.Warning,
                Code = DiagnosticCode.NotifyMisconfigured,
                Message = problem,
                Remedy = pairing.Remedy,
            });

            return null;
        }

        if (pairing.Provider is { } borrowed)
        {
            return FromProvider(borrowed, action, settings, secrets, senders, diagnostics);
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

    /// <summary>What was written, masked - falling back to the raw text for <c>eventlog:</c>, whose remainder is empty.</summary>
    private static string DisplayOf(HookAction action) =>
        action.Display is { Length: > 0 } shown ? shown : action.Raw;

    /// <summary>Reports a scheme that parses but is not a notification target, rather than ignoring it.</summary>
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
                : $"'{display}' names {name}:, which is a hook rather than a notification target.",
            Remedy = senders.WhyNot(scheme) is not null

                // Nothing to fix: the target is correct, this machine simply cannot serve it.
                // Suggesting alternatives here would be advice to change a working configuration.
                ? null
                : Instead(scheme),
        });

        return true;
    }

    /// <summary>
    /// Where a scheme that runs code belongs instead, and why it is not a target.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These used to say the scheme would "land with the configuration-directory check they
    /// require". That check landed - job hooks run, gated by it - so the sentence became a promise
    /// of a build that is not coming, which is worse than saying no. The rule is permanent and it
    /// is the mirror of the one <c>HookPlan</c> applies in the other direction: a hook that
    /// reports is refused there, a target that acts is refused here.
    /// </para>
    /// <para>
    /// <c>command:</c> is refused for a different and more specific reason. A program could carry a
    /// message, so the rule alone does not exclude it - what excludes it is that
    /// <see cref="HookDispatcher"/> gives each channel an even share of the phase's time budget,
    /// and starting a process costs enough of a 30-second budget split three ways that messages
    /// would be dropped. That is not hypothetical: it is the defect
    /// <see cref="ChannelOutcome.Unattempted"/> was added to make visible.
    /// </para>
    /// </remarks>
    private static string Instead(HookScheme scheme) => scheme switch
    {
        HookScheme.Service or HookScheme.Event =>
            $"{HookSchemes.Name(scheme)}: acts on this machine, and a notification target has to "
            + "carry a message - a service control code and a kernel event carry nothing. Put it in "
            + "a job's prerotate or postrotate instead (docs/hooks.md), and report with http:, "
            + "smtp:, pushover: or eventlog:.",

        HookScheme.Command =>
            "command: runs a program, and each notification channel gets only an even share of the "
            + "phase's time budget - a process start would spend enough of it to drop messages. Put "
            + "it in a job's prerotate or postrotate instead (docs/hooks.md), and report with http:, "
            + "smtp:, pushover: or eventlog:.",

        _ => "Use http:, smtp:, pushover: or eventlog:.",
    };

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
