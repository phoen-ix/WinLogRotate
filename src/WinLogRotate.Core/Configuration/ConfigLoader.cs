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
    /// Files that would not parse, whether or not they were then moved aside.
    /// </summary>
    /// <remarks>
    /// Quarantine is optional - <c>config check</c> passes <c>quarantine: false</c> so that
    /// asking a question does not rearrange the answer - so <see cref="Quarantined"/> cannot be
    /// what a caller counts. This can: it is what makes <c>run</c> exit non-zero after carrying on
    /// past a broken file, which is the difference between "skipped it and said so" and "reported
    /// success".
    /// </remarks>
    public IReadOnlyList<string> Unloadable { get; init; } = [];

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

    /// <summary>
    /// The <c>[journal]</c> table.
    /// </summary>
    /// <remarks>
    /// Carried here so that config.toml is read once. RunCommand used to load and parse it a
    /// second time purely to reach this table; that was tolerable at one table and is not at
    /// three, and two readers of one file is two chances to disagree about it.
    /// </remarks>
    public JournalSettings Journal { get; init; } = JournalSettings.Default;

    /// <summary>
    /// Jobs that will not run, because validating them produced an error.
    /// </summary>
    /// <remarks>
    /// Named so the caller can say so and exit non-zero. A job silently absent from
    /// <see cref="Jobs"/> would be a log that stops being rotated with nobody told, which is the
    /// worst outcome this product has.
    /// </remarks>
    /// <remarks>
    /// The whole job, not just its name. A skipped job is absent from <see cref="Jobs"/>, so
    /// anything built from that list cannot see its settings - and the notification phase builds
    /// its muted set that way, which meant a job the operator had muted with
    /// <c>notify = false</c> mailed them precisely when it broke.
    /// </remarks>
    public IReadOnlyList<EffectiveJob> SkippedJobs { get; init; } = [];

    /// <summary>
    /// True when the configuration as a whole is unusable, so nothing should be attempted.
    /// </summary>
    /// <remarks>
    /// Errors that belong to one job are deliberately excluded. They cost that job - it is in
    /// <see cref="SkippedJobs"/> and reported - and they leave every other job on the machine
    /// rotating. A refused path in one file used to set this and return <c>ExitCode.ConfigInvalid</c>
    /// with nothing attempted anywhere, which turned one operator's typo into a disk-space
    /// incident; <c>ConfigValidator.CheckHooks</c> had already written that argument out in full
    /// for hooks, and it applies here word for word. The same goes for a job file that will not
    /// parse: there is no job to name, so <see cref="ConfigDiagnostic.FileScoped"/> names the file
    /// instead, and <see cref="Unloadable"/> is what makes the run still exit non-zero.
    /// <para>
    /// What still stops everything is a fault with nothing smaller to blame: an unparseable
    /// config.toml, a broken [notify] table, a duplicate job name, a job with no paths.
    /// </para>
    /// </remarks>
    public bool HasErrors =>
        Diagnostics.Any(d => d.Severity >= Severity.Error && d.Job is null && !d.FileScoped);
}

/// <summary>
/// Reads <c>config.toml</c> and every job in <c>conf.d</c>.
/// </summary>
/// <remarks>
/// <para>
/// One unparseable job file must never take the whole configuration down. A typo in an
/// experimental job should not stop forty healthy ones from rotating - on a busy server that
/// turns a typo into a full disk. So a broken file is quarantined to <c>.bad</c>, reported,
/// and skipped, and the rest of the run proceeds.
/// </para>
/// <para>
/// That was written before it was true. The diagnostic carried no
/// <see cref="ConfigDiagnostic.Job"/>, because a file that will not parse has no job to name, so
/// <see cref="LoadedConfig.HasErrors"/> counted it as a fault with the configuration as a whole
/// and <c>run</c> returned <c>ExitCode.ConfigInvalid</c> having attempted nothing. The file was
/// then renamed on the way out, so the next run looked healthy and the outage did not repeat.
/// <see cref="ConfigDiagnostic.FileScoped"/> is what makes the sentence above true; it applies to
/// a file that will not <i>parse</i>, and deliberately not to a binder error, which still has
/// nothing smaller than the machine to blame.
/// </para>
/// </remarks>
public static class ConfigLoader
{
    /// <param name="secrets">
    /// How to ask whether a named secret exists. Required, and third rather than last, so that
    /// every caller has to state its answer: this was an optional trailing parameter for nine
    /// milestones and every production caller omitted it, so <c>LR9005</c> was documented, wired,
    /// and never once armed. A caller that genuinely cannot look passes
    /// <see cref="UnknownSecretLookup"/> and says why - see <see cref="ISecretLookup.Exists"/> for
    /// why "cannot tell" is a third answer rather than "no".
    /// </param>
    public static LoadedConfig Load(
        InstallPaths paths, PathGuard guard, ISecretLookup secrets, bool quarantineBadFiles = true)
    {
        var diagnostics = new DiagnosticBag();
        var quarantined = new List<string>();
        var unloadable = new List<string>();

        JobSettings? defaults = null;
        var notify = NotifySettings.Default;
        var journal = JournalSettings.Default;
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
                journal = ConfigBinder.BindJournal(file, diagnostics);
                providers = ConfigBinder.BindProviders(file, diagnostics);
                ValidateNotifyTargets(notify, providers, paths.ConfigFile, diagnostics);
                ValidateCredentials(providers, paths.ConfigFile, diagnostics, secrets);
            }
        }

        var jobs = new List<EffectiveJob>();
        var skipped = new List<EffectiveJob>();

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
                Unloadable = unloadable,
                Notify = notify,
                NotifyProviders = providers,
                Journal = journal,
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
                HandleUnparseable(
                    file, path, diagnostics, quarantined, quarantineBadFiles, null, unloadable);
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

            // Into a bag of its own, so what validation says about this job can be attributed to
            // it and weighed separately from a fault with the configuration as a whole.
            var jobBag = new DiagnosticBag();
            ConfigValidator.Validate(effective, guard, jobBag);

            foreach (var item in jobBag.Items)
            {
                diagnostics.Add(item with { Job = effective.Name });
            }

            if (jobBag.HasErrors)
            {
                skipped.Add(effective);
                continue;
            }

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
            SkippedJobs = skipped,
            Diagnostics = diagnostics.Items,
            Paths = paths,
            Quarantined = quarantined,
            Unloadable = unloadable,
            Notify = notify,
            NotifyProviders = providers,
            Journal = journal,
        };
    }

    /// <param name="unloadable">
    /// The list one job file's failure is recorded in, or null when the file is <c>config.toml</c>
    /// itself. That is the whole difference between "this file is out" and "nothing can run": a
    /// job file has something smaller than the machine to blame, and the root file does not.
    /// </param>
    private static JobSettings? HandleUnparseable(
        TomlFile file, string path, DiagnosticBag diagnostics,
        List<string> quarantined, bool quarantine, JobSettings? fallback,
        List<string>? unloadable = null)
    {
        foreach (var error in file.Errors)
        {
            diagnostics.Add(new ConfigDiagnostic
            {
                Severity = Severity.Error,
                Code = DiagnosticCode.ConfigInvalid,
                Message = error.Message,
                File = path,
                Line = error.Span.Start.Line + 1,
                Column = error.Span.Start.Column + 1,
                FileScoped = unloadable is not null,
            });
        }

        unloadable?.Add(path);

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
            diagnostics.Error(path, DiagnosticCode.ConfigUnwritable,
                $"Could not quarantine the unparseable file: {e.Message}",
                remedy: "The file was left where it is, so this will be reported again next run. "
                      + "Check free space and the permissions on the conf.d directory.");
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
        DiagnosticBag diagnostics, ISecretLookup secrets)
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
                            remedy: $"winlogrotate secret set {SuggestSecretName(provider.Name, field)}   "
                                  + $"then set {field} = \"@secret:{SuggestSecretName(provider.Name, field)}\"");
                        break;

                    // Written out rather than as "secrets?.Exists(key) == false". That collapsed
                    // the three-way answer this interface was designed to give into two: with a
                    // null lookup it evaluated (bool?)null == false, which is false, so the arm
                    // could never match - and every production caller passed null. The guard was
                    // unreachable rather than merely unarmed.
                    case SecretSource.Store when secrets.Exists(reference.Key!) is false:
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

    /// <summary>
    /// A secret name somebody would plausibly have chosen, for the remedy text.
    /// </summary>
    /// <remarks>
    /// Public because <c>notify set-secret</c> derives the same name when it stores the value for
    /// real. The remedy that tells an operator what to type and the command that does it for them
    /// must not disagree about what the secret is called.
    /// </remarks>
    public static string SuggestSecretName(string provider, string field)
    {
        var stem = provider.Replace('.', '-');
        return field is "password" or "url" ? stem : $"{stem}-{field.Replace('_', '-')}";
    }

}
