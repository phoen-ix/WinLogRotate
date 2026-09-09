using WinLogRotate.Contracts;
using WinLogRotate.Core.Notify;
using WinLogRotate.Core.Safety;
using WinLogRotate.Core.Secrets;

namespace WinLogRotate.Core.Configuration;

/// <summary>Everything read from disk, with every diagnostic collected along the way.</summary>
public sealed record LoadedConfig
{
    public required IReadOnlyList<EffectiveJob> Jobs { get; init; }
    public required IReadOnlyList<ConfigDiagnostic> Diagnostics { get; init; }
    public required InstallPaths Paths { get; init; }

    /// <summary>Files moved aside because they could not be parsed.</summary>
    public required IReadOnlyList<string> Quarantined { get; init; }

    /// <summary>
    /// The <c>[notify]</c> table, bound and validated alongside everything else.
    /// </summary>
    /// <remarks>
    /// Carried here rather than re-read later so that <c>config check</c> reports a broken
    /// notification target. A target nobody validates is a target that turns out to be wrong on
    /// the night it was needed.
    /// </remarks>
    public NotifySettings Notify { get; init; } = NotifySettings.Default;

    /// <summary>The named <c>[notify.KIND.NAME]</c> provider tables.</summary>
    public IReadOnlyList<NotifyProvider> NotifyProviders { get; init; } = [];

    public bool HasErrors => Diagnostics.Any(d => d.Severity >= Severity.Error);
}

/// <summary>
/// Reads <c>config.toml</c> and every job in <c>conf.d</c>.
/// </summary>
/// <remarks>
/// One unparseable job file must never take the whole configuration down. A typo in an
/// experimental job should not stop forty healthy ones from rotating - on a busy server that
/// turns a typo into a full disk. So a broken file is quarantined to <c>.bad</c>, reported,
/// and skipped, and the rest of the run proceeds.
/// </remarks>
public static class ConfigLoader
{
    /// <param name="secrets">
    /// How to ask whether a named secret exists. Null means nobody asked and nothing is claimed -
    /// see <see cref="ISecretLookup.Exists"/> for why that is a third answer rather than "no".
    /// </param>
    public static LoadedConfig Load(
        InstallPaths paths, PathGuard guard, bool quarantineBadFiles = true, ISecretLookup? secrets = null)
    {
        var diagnostics = new DiagnosticBag();
        var quarantined = new List<string>();

        JobSettings? defaults = null;
        var notify = NotifySettings.Default;
        IReadOnlyList<NotifyProvider> providers = [];
        if (File.Exists(paths.ConfigFile))
        {
            var file = TomlFile.Load(paths.ConfigFile);
            if (file.HasErrors)
            {
                defaults = HandleUnparseable(
                    file, paths.ConfigFile, diagnostics, quarantined, quarantineBadFiles, null);
            }
            else
            {
                defaults = ConfigBinder.BindDefaults(file, diagnostics);
                notify = ConfigBinder.BindNotify(file, diagnostics);
                providers = ConfigBinder.BindProviders(file, diagnostics);
                ValidateNotifyTargets(notify, providers, paths.ConfigFile, diagnostics);
                ValidateCredentials(providers, paths.ConfigFile, diagnostics, secrets);
            }
        }

        var jobs = new List<EffectiveJob>();

        if (!Directory.Exists(paths.ConfigDirectory))
        {
            diagnostics.Add(new ConfigDiagnostic
            {
                Severity = Severity.Warning,
                Code = DiagnosticCode.NoJobsConfigured,
                Message = $"No job directory at {paths.ConfigDirectory}.",
                File = paths.ConfigDirectory,
                Remedy = "Create it and add one .toml file per job, or use the GUI to add one.",
            });

            return new LoadedConfig
            {
                Jobs = [],
                Diagnostics = diagnostics.Items,
                Paths = paths,
                Quarantined = quarantined,
                Notify = notify,
                NotifyProviders = providers,
            };
        }

        // Ordinal order so two machines given the same files load them the same way.
        var files = Directory
            .EnumerateFiles(paths.ConfigDirectory, "*.toml")
            .OrderBy(f => f, StringComparer.Ordinal);

        var seenNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in files)
        {
            TomlFile file;
            try
            {
                file = TomlFile.Load(path);
            }
            catch (IOException e)
            {
                diagnostics.Error(path, DiagnosticCode.ConfigUnreadable, e.Message);
                continue;
            }

            if (file.HasErrors)
            {
                HandleUnparseable(file, path, diagnostics, quarantined, quarantineBadFiles, null);
                continue;
            }

            var job = ConfigBinder.BindJob(file, diagnostics);
            if (job is null)
            {
                continue;
            }

            // Two jobs with one name make the journal and every diagnostic ambiguous.
            if (seenNames.TryGetValue(job.Name, out var firstFile))
            {
                diagnostics.Error(path, DiagnosticCode.ConfigInvalid,
                    $"A job called '{job.Name}' is already defined in {Path.GetFileName(firstFile)}.",
                    remedy: "Job names appear in the journal and in every diagnostic, so they have to be unique.");
                continue;
            }

            seenNames[job.Name] = path;

            var effective = SettingsMerge.Resolve(job, defaults);
            ConfigValidator.Validate(effective, guard, diagnostics);
            jobs.Add(effective);
        }

        if (jobs.Count == 0 && !diagnostics.HasErrors)
        {
            diagnostics.Add(new ConfigDiagnostic
            {
                Severity = Severity.Warning,
                Code = DiagnosticCode.NoJobsConfigured,
                Message = "No jobs are configured, so nothing will be rotated.",
                File = paths.ConfigDirectory,
            });
        }

        return new LoadedConfig
        {
            Jobs = jobs,
            Diagnostics = diagnostics.Items,
            Paths = paths,
            Quarantined = quarantined,
            Notify = notify,
            NotifyProviders = providers,
        };
    }

    private static JobSettings? HandleUnparseable(
        TomlFile file, string path, DiagnosticBag diagnostics,
        List<string> quarantined, bool quarantine, JobSettings? fallback)
    {
        foreach (var error in file.Errors)
        {
            diagnostics.Error(path, DiagnosticCode.ConfigInvalid, error.Message,
                error.Span.Start.Line + 1, error.Span.Start.Column + 1);
        }

        if (!quarantine)
        {
            return fallback;
        }

        try
        {
            var bad = TomlFile.Quarantine(path);
            quarantined.Add(bad);
            diagnostics.Warn(path, DiagnosticCode.ConfigInvalid,
                $"Moved aside to {Path.GetFileName(bad)} so the rest of the configuration still loads.",
                remedy: "Fix it and rename it back. It was preserved, not deleted.");
        }
        catch (IOException e)
        {
            diagnostics.Error(path, DiagnosticCode.ConfigUnreadable,
                $"Could not quarantine the unparseable file: {e.Message}");
        }

        return fallback;
    }
    /// <summary>
    /// Runs every configured notification target through the parser.
    /// </summary>
    /// <remarks>
    /// Warnings, never errors. A mistyped webhook must not stop a rotation: the logs still need
    /// rotating, and refusing to run because the alerting is misconfigured turns a notification
    /// problem into a disk-space problem.
    /// </remarks>
    private static void ValidateNotifyTargets(
        NotifySettings notify, IReadOnlyList<NotifyProvider> providers,
        string file, DiagnosticBag diagnostics)
    {
        foreach (var target in notify.To)
        {
            // A target is either a provider name or an inline scheme string. Provider names are
            // checked first: "email.relay" would otherwise be read as a command line, which is
            // a confusing way to learn you mistyped a provider.
            if (providers.Any(p => string.Equals(p.Name, target, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (target.Contains('.') && !target.Contains(':') && !target.Contains('\\')
                && !target.Contains('/'))
            {
                diagnostics.Warn(file, DiagnosticCode.NotifyMisconfigured,
                    $"'{target}' does not match any [notify.*] provider.",
                    remedy: providers.Count == 0
                        ? "Define one, e.g. [notify.email.relay], or write an inline target such as eventlog:."
                        : $"Defined providers: {string.Join(", ", providers.Select(p => p.Name))}.");
                continue;
            }

            var result = HookParser.Parse(target);

            if (result.IsOk)
            {
                if (HookSchemes.RequiresTarget(result.Action!.Scheme) && result.Action.Target.Length == 0)
                {
                    diagnostics.Warn(file, DiagnosticCode.NotifyMisconfigured,
                        $"'{target}' names a scheme but nothing to send to.",
                        remedy: "Write the destination after the colon, e.g. service:paramchange:nginx.");
                }

                continue;
            }

            diagnostics.Warn(file, DiagnosticCode.NotifyMisconfigured,
                result.Error switch
                {
                    HookParseError.Empty => "An empty notification target.",
                    HookParseError.UnknownScheme =>
                        $"'{result.Scheme}:' is not a notification scheme, so '{target}' would never be used.",
                    _ => $"'{target}' could not be understood.",
                },
                remedy: "Known schemes: http, https, smtp, pushover, eventlog, command, service, event. "
                      + "A target with no scheme is run as a command line.");
        }
    }

    /// <summary>
    /// Reports credentials written in the clear, and references to secrets that are not stored.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A literal is a warning, never an error. Forbidding them drives people to worse
    /// workarounds - a password typed into a scheduled task's arguments, or a config file kept
    /// somewhere even less private - whereas a warning at the exact line, every single run, with
    /// the two commands that fix it, does not.
    /// </para>
    /// <para>
    /// A missing secret is only reported when the lookup can actually answer. The store grants
    /// Users nothing, so an unelevated caller gets null from <see cref="ISecretLookup.Exists"/>
    /// and nothing is said - reporting "no secret called ses-smtp" to somebody who is simply not
    /// allowed to look would be a false alarm shown to the person least able to judge it.
    /// </para>
    /// </remarks>
    private static void ValidateCredentials(
        IReadOnlyList<NotifyProvider> providers, string file,
        DiagnosticBag diagnostics, ISecretLookup? secrets)
    {
        foreach (var provider in providers)
        {
            foreach (var (field, reference) in provider.Credentials())
            {
                switch (reference.Source)
                {
                    case SecretSource.Literal:
                        diagnostics.Warn(file, DiagnosticCode.SecretInPlainConfig,
                            $"The {field} for '{provider.Name}' is written in this file, which every "
                            + "local user on this machine can read.",
                            reference.Line, reference.Column,
                            remedy: $"winlogrotate secret set {Suggest(provider.Name, field)}   "
                                  + $"then set {field} = \"@secret:{Suggest(provider.Name, field)}\"");
                        break;

                    case SecretSource.Store when secrets?.Exists(reference.Key!) == false:
                        // Warning, not error, and the two validators in this file must agree on
                        // that. An error here would mean that the moment a real secret lookup is
                        // passed in, one mistyped Pushover token name stops every rotation on
                        // the machine - turning a notification problem into a disk-space
                        // problem, which is precisely what ValidateNotifyTargets says it exists
                        // to avoid.
                        diagnostics.Warn(file, DiagnosticCode.SecretMissing,
                            $"No secret called '{reference.Key}', so '{provider.Name}' cannot authenticate.",
                            reference.Line, reference.Column,
                            remedy: $"winlogrotate secret set {reference.Key}");
                        break;
                }
            }
        }
    }

    /// <summary>A secret name somebody would plausibly have chosen, for the remedy text.</summary>
    private static string Suggest(string provider, string field)
    {
        var stem = provider.Replace('.', '-');
        return field is "password" or "url" ? stem : $"{stem}-{field.Replace('_', '-')}";
    }

}
