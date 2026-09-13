namespace WinLogRotate.Core.Configuration;

/// <summary>One job the configuration directory already has, and where it lives.</summary>
public sealed record JobIndexEntry
{
    public required string Name { get; init; }

    public required string File { get; init; }
}

/// <summary>
/// What a configuration directory already contains, for a caller about to change one job in it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ConfigLoader.Judge"/> can judge one job file on its own, but two of the things that
/// decide a verdict live outside it: the <c>[defaults]</c> table its inherited values come from,
/// and the job names other files have already taken. This is where both come from.
/// </para>
/// <para>
/// The names matter more than they look. A duplicate name is not file-scoped, so it does not stop
/// the job that has it - it stops <b>every</b> job on the machine, with <c>run</c> exiting 2
/// having attempted nothing. A verb that writes a job file has to ask before it writes; finding
/// out afterwards means the file is already there.
/// </para>
/// <para>
/// Binding is done into a bag that is thrown away. Another file's problems must never be reported
/// against the job being edited, and a file that will not parse contributes no name at all - so a
/// duplicate can hide behind an unparseable sibling. That is acceptable because the unparseable
/// file is already reported by every verb that loads the directory.
/// </para>
/// </remarks>
public sealed record JobIndex
{
    public required IReadOnlyList<JobIndexEntry> Entries { get; init; }

    /// <summary>The <c>[defaults]</c> table, or null when there is no config.toml to read.</summary>
    public required JobSettings? Defaults { get; init; }

    /// <summary>The file a job of this name is written in, or null if no job has it.</summary>
    public string? FileFor(string name) =>
        Entries.FirstOrDefault(
            e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))?.File;

    /// <summary>
    /// The names already taken, keyed for <see cref="ConfigLoader.Judge"/>.
    /// </summary>
    /// <param name="excludingFile">
    /// The file being edited. Without it, editing a job reports it as a duplicate of itself.
    /// </param>
    public IReadOnlyDictionary<string, string> NamesInUse(string? excludingFile)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in Entries)
        {
            if (excludingFile is not null
                && string.Equals(entry.File, excludingFile, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            names[entry.Name] = entry.File;
        }

        return names;
    }

    /// <summary>Reads a configuration directory, without judging anything in it.</summary>
    public static JobIndex Of(InstallPaths paths)
    {
        var thrownAway = new DiagnosticBag();

        JobSettings? defaults = null;
        if (File.Exists(paths.ConfigFile))
        {
            try
            {
                var config = TomlFile.Load(paths.ConfigFile);
                if (!config.HasErrors)
                {
                    defaults = ConfigBinder.BindDefaults(config, thrownAway);
                }
            }
            catch (IOException)
            {
                // A config.toml that cannot be read means no defaults, which is what a fresh
                // install has. Every verb that loads the directory reports it properly; this one
                // is only here to answer two questions about the neighbours.
            }
        }

        var entries = new List<JobIndexEntry>();

        foreach (var path in JobFiles.In(paths.ConfigDirectory))
        {
            try
            {
                var file = TomlFile.Load(path);
                if (file.HasErrors)
                {
                    continue;
                }

                if (ConfigBinder.BindJob(file, thrownAway) is { } job)
                {
                    entries.Add(new JobIndexEntry { Name = job.Name, File = path });
                }
            }
            catch (IOException)
            {
                // As above.
            }
        }

        return new JobIndex { Entries = entries, Defaults = defaults };
    }
}
