using WinLogRotate.Contracts;
using WinLogRotate.Core.Safety;

namespace WinLogRotate.Core.Configuration;

/// <summary>Everything read from disk, with every diagnostic collected along the way.</summary>
public sealed record LoadedConfig
{
    public required IReadOnlyList<EffectiveJob> Jobs { get; init; }
    public required IReadOnlyList<ConfigDiagnostic> Diagnostics { get; init; }
    public required InstallPaths Paths { get; init; }

    /// <summary>Files moved aside because they could not be parsed.</summary>
    public required IReadOnlyList<string> Quarantined { get; init; }

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
    public static LoadedConfig Load(InstallPaths paths, PathGuard guard, bool quarantineBadFiles = true)
    {
        var diagnostics = new DiagnosticBag();
        var quarantined = new List<string>();

        JobSettings? defaults = null;
        if (File.Exists(paths.ConfigFile))
        {
            var file = TomlFile.Load(paths.ConfigFile);
            defaults = file.HasErrors
                ? HandleUnparseable(file, paths.ConfigFile, diagnostics, quarantined, quarantineBadFiles, null)
                : ConfigBinder.BindDefaults(file, diagnostics);
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
}
