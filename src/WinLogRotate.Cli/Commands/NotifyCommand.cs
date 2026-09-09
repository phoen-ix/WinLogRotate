using WinLogRotate.Cli.Output;
using WinLogRotate.Contracts;
using WinLogRotate.Core;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Notify;
using WinLogRotate.Core.Safety;
using WinLogRotate.Core.Secrets;
using WinLogRotate.Core.State;

namespace WinLogRotate.Cli.Commands;

/// <summary>
/// Inspecting notification configuration and history.
/// </summary>
/// <remarks>
/// Read-only, apart from <c>reset</c>. These exist because the alternative is an operator asking
/// "why did I not get an email?" and having no way to find out short of reading the source -
/// which is most of what makes a notification feature untrustworthy.
/// </remarks>
internal static class NotifyCommand
{
    /// <summary>Delivery lands in a later milestone. Said plainly rather than implied.</summary>
    private const bool DeliveryImplemented = false;

    public static int Show(CommandContext ctx, string? configDir)
    {
        var paths = InstallPaths.Resolve(configDir);
        var config = ConfigLoader.Load(paths, new PathGuard(new GuardOptions()), quarantineBadFiles: false);
        var settings = config.Notify;

        var targets = new List<NotifyTargetDto>();
        foreach (var raw in settings.To)
        {
            var provider = config.NotifyProviders
                .FirstOrDefault(p => string.Equals(p.Name, raw, StringComparison.OrdinalIgnoreCase));

            if (provider is not null)
            {
                targets.Add(new NotifyTargetDto
                {
                    Target = raw,
                    Display = raw,
                    Scheme = provider.Kind.ToString().ToLowerInvariant(),
                    Usable = provider.Enabled,
                    Problem = provider.Enabled ? null : "the provider is disabled",
                });
                continue;
            }

            var parsed = HookParser.Parse(raw);
            targets.Add(new NotifyTargetDto
            {
                Target = raw,
                // Falls back to what was written: eventlog: is a complete target with an empty
                // remainder, and printing a blank line for it looks like a defect.
                Display = parsed.Action?.Display is { Length: > 0 } shown ? shown : raw,
                Scheme = parsed.Action is { } a ? HookSchemes.Name(a.Scheme) : "?",
                Usable = parsed.IsOk,
                Problem = parsed.IsOk ? null : $"{parsed.Error}{(parsed.Scheme is { } s ? $" ({s}:)" : "")}",
            });
        }

        var providers = config.NotifyProviders.Select(p => new NotifyProviderDto
        {
            Name = p.Name,
            Kind = p.Kind.ToString().ToLowerInvariant(),
            Enabled = p.Enabled,
            Target = Describe(p),

            // Describe(), never a value. There is no branch in SecretRef that returns one.
            Credential = p.Credentials().FirstOrDefault().Reference?.Describe() ?? "none",
            CredentialFree = p.IsCredentialFree,
        }).ToArray();

        ctx.Output.Line($"enabled    {settings.Enabled}");
        ctx.Output.Line($"on         {settings.On.ToString().ToLowerInvariant()}");
        ctx.Output.Line($"threshold  {settings.Threshold.ToString().ToLowerInvariant()} and above");
        ctx.Output.Line($"remind     {(settings.RemindAfter <= TimeSpan.Zero ? "never" : settings.RemindAfter.ToString())}");
        ctx.Output.Line($"budget     {settings.Budget}, {settings.Retries} retr{(settings.Retries == 1 ? "y" : "ies")}");
        ctx.Output.Line(string.Empty);

        if (targets.Count == 0)
        {
            ctx.Output.Line("No targets are configured, so nothing would be sent.");
        }

        foreach (var t in targets)
        {
            var note = t.Usable ? string.Empty : $"   <- {t.Problem}";
            ctx.Output.Line($"  {t.Scheme,-9} {t.Display}{note}");
        }

        if (providers.Length > 0)
        {
            ctx.Output.Line(string.Empty);
            ctx.Output.Line("providers:");
            foreach (var p in providers)
            {
                var free = p.CredentialFree ? "  (nothing stored)" : string.Empty;
                ctx.Output.Line($"  {p.Name,-20} {p.Target,-40} {p.Credential}{free}");
            }
        }

        if (!DeliveryImplemented)
        {
            ctx.Output.Line(string.Empty);
            ctx.Output.Line("Delivery is not implemented in this build: the configuration above is");
            ctx.Output.Line("validated and change detection runs, but nothing is sent anywhere yet.");
        }

        return ctx.Output.Complete("notify show", ExitCode.Ok, new NotifyShowResult
        {
            Enabled = settings.Enabled,
            On = settings.On.ToString().ToLowerInvariant(),
            Threshold = settings.Threshold.ToString(),
            RemindAfter = settings.RemindAfter.ToString(),
            Budget = settings.Budget.ToString(),
            Retries = settings.Retries,
            WouldSend = settings.WouldSend,
            DeliveryImplemented = DeliveryImplemented,
            Targets = targets,
            Providers = providers,
        });
    }

    public static int Status(CommandContext ctx, string? configDir)
    {
        var paths = InstallPaths.Resolve(configDir);
        var path = Path.Combine(paths.Root, "notify.json");
        var state = NotifyStateStore.Load(path);
        var settings = ConfigLoader.Load(paths, new PathGuard(new GuardOptions()), quarantineBadFiles: false).Notify;

        var jobs = state.Jobs
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new NotifyJobStatusDto
            {
                Job = kv.Key,
                Outcome = kv.Value.Outcome.ToString(),
                NotifiedAt = kv.Value.NotifiedAt,
                FailingSince = kv.Value.FailingSince,
            })
            .ToArray();

        var channels = state.Channels
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new NotifyChannelStatusDto
            {
                Channel = kv.Key,
                State = BreakerPolicy.Verdict(kv.Value, settings).ToString(),
                ConsecutiveFailures = kv.Value.ConsecutiveFailures,
                SkipRunsRemaining = kv.Value.SkipRunsRemaining,
                LastError = kv.Value.LastError,
                LastAttempt = kv.Value.LastAttempt,
            })
            .ToArray();

        ctx.Output.Line(path);
        if (state.Warning is { } warning)
        {
            ctx.Output.Line($"  history was reset: {warning}");
        }

        ctx.Output.Line(string.Empty);
        ctx.Output.Line(jobs.Length == 0 ? "No job has been reported on yet." : "jobs:");
        foreach (var j in jobs)
        {
            var since = j.FailingSince is { } f ? $"  since {f:u}" : string.Empty;
            ctx.Output.Line($"  {j.Job,-24} {j.Outcome}{since}");
        }

        if (channels.Length > 0)
        {
            ctx.Output.Line(string.Empty);
            ctx.Output.Line("channels:");
            foreach (var c in channels)
            {
                // The cooldown is counted in runs, so what it means in hours depends entirely on
                // how often this machine rotates. Saying both is how that asymmetry is disclosed
                // rather than left for somebody to discover.
                var skip = c.SkipRunsRemaining > 0
                    ? $", {c.SkipRunsRemaining} more run(s)"
                    : string.Empty;
                ctx.Output.Line($"  {c.Channel,-32} {c.State}{skip}");
                if (c.LastError is { } error)
                {
                    ctx.Output.Line($"      last error: {error}");
                }
            }
        }

        return ctx.Output.Complete("notify status", ExitCode.Ok, new NotifyStatusResult
        {
            Path = path,
            Jobs = jobs,
            Channels = channels,
        });
    }

    /// <summary>
    /// Closes a suppressed channel, or all of them.
    /// </summary>
    /// <remarks>
    /// The operator has just fixed the firewall. Without this they wait out a cooldown counted in
    /// runs, which on a daily rotation is days - and that is the whole argument for counting in
    /// runs surviving contact with reality.
    /// </remarks>
    public static int Reset(CommandContext ctx, string? channel, string? configDir)
    {
        var paths = InstallPaths.Resolve(configDir);
        var path = Path.Combine(paths.Root, "notify.json");
        var state = NotifyStateStore.Load(path);

        var targets = channel is null
            ? state.Channels.Keys.ToArray()
            : state.Channels.Keys
                .Where(k => string.Equals(k, channel, StringComparison.OrdinalIgnoreCase))
                .ToArray();

        if (channel is not null && targets.Length == 0)
        {
            ctx.Output.Diagnostic(new CliDiagnostic
            {
                Severity = Severity.Warning,
                Code = DiagnosticCode.NotifyMisconfigured,
                Message = $"No channel called '{channel}' has any recorded history.",
                Path = path,
                Remedy = "Run 'winlogrotate notify status' to see the channels that do.",
            });
            return ctx.Output.Complete<NotifyStatusResult>("notify reset", ExitCode.Errors, null);
        }

        foreach (var key in targets)
        {
            state.SetChannel(key, BreakerPolicy.Reset());
        }

        try
        {
            state.Save(TimeProvider.System);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ctx.Output.Diagnostic(new CliDiagnostic
            {
                Severity = Severity.Error,
                Code = DiagnosticCode.NotifyStateUnreadable,
                Message = $"Could not save: {e.Message}",
                Path = path,
            });
            return ctx.Output.Complete<NotifyStatusResult>("notify reset", ExitCode.Errors, null);
        }

        ctx.Output.Line(targets.Length == 0
            ? "No channel had been suppressed."
            : $"Reset {targets.Length} channel(s): {string.Join(", ", targets)}");

        return ctx.Output.Complete("notify reset", ExitCode.Ok, new NotifyStatusResult
        {
            Path = path,
            Jobs = [],
            Channels = [],
            Reset = targets,
        });
    }

    private static string Describe(NotifyProvider p) => p.Kind switch
    {
        NotifyProviderKind.Email when p.Delivery == SmtpDelivery.PickupDirectory =>
            p.PickupDirectory ?? "(no pickup directory)",
        NotifyProviderKind.Email => $"{p.Host}:{p.Port} auth={p.Auth.ToString().ToLowerInvariant()}",
        NotifyProviderKind.Webhook => p.Url.HasValue ? "(url in the secret store)" : "(no url)",
        _ => "pushover",
    };
}
