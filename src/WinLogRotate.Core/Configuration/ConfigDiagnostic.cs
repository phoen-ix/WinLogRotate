using WinLogRotate.Contracts;

namespace WinLogRotate.Core.Configuration;

/// <summary>A problem found while reading configuration, located precisely enough to click.</summary>
public sealed record ConfigDiagnostic
{
    public required Severity Severity { get; init; }
    public required string Code { get; init; }
    public required string Message { get; init; }
    public required string File { get; init; }
    public int Line { get; init; }
    public int Column { get; init; }
    public string? Remedy { get; init; }

    /// <summary>
    /// The job this is about, when it is about exactly one.
    /// </summary>
    /// <remarks>
    /// What lets a validation failure cost one job instead of every rotation on the machine.
    /// <c>LoadedConfig.HasErrors</c> - which is what makes <c>run</c> attempt nothing at all -
    /// counts only errors with nothing smaller to blame, so an unparseable [notify] table still
    /// stops everything while one job's refused path stops that job.
    /// </remarks>
    public string? Job { get; init; }

    /// <summary>
    /// About one file rather than about the configuration as a whole.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same argument as <see cref="Job"/>, for the case where there is no job to name because
    /// the file did not parse far enough to have one. <c>ConfigLoader</c>'s own remarks promised
    /// this - "a broken file is quarantined to .bad, reported, and skipped, and the rest of the
    /// run proceeds" - and <c>TomlFile</c>'s promised it twice over: "a typo in an experimental
    /// job should not stop forty healthy ones from rotating". Neither was true.
    /// <c>DiagnosticBag.Error</c> sets no <see cref="Job"/>, so a syntax error in one conf.d file
    /// made <c>LoadedConfig.HasErrors</c> true and <c>run</c> exit 2 having attempted nothing on
    /// the machine.
    /// </para>
    /// <para>
    /// <b>Set only where the file could not be parsed at all.</b> A binder error - a job with no
    /// paths, a duplicate job name - still has nothing smaller than the configuration to blame
    /// and still stops everything, which is correct and is a separate question. Stamping this
    /// onto every diagnostic that happens to come from a file would silently demote real errors
    /// to a run that reports success.
    /// </para>
    /// </remarks>
    public bool FileScoped { get; init; }

    public override string ToString() =>
        Line > 0
            ? $"{File}({Line},{Column}): {Severity.ToString().ToLowerInvariant()}: {Message} [{Code}]"
            : $"{File}: {Severity.ToString().ToLowerInvariant()}: {Message} [{Code}]";
}

/// <summary>Collects diagnostics while a configuration is read.</summary>
public sealed class DiagnosticBag
{
    private readonly List<ConfigDiagnostic> _items = [];

    public IReadOnlyList<ConfigDiagnostic> Items => _items;

    public bool HasErrors => _items.Any(d => d.Severity >= Severity.Error);

    public void Add(ConfigDiagnostic d) => _items.Add(d);

    public void Error(string file, string code, string message, int line = 0, int column = 0, string? remedy = null) =>
        Add(new ConfigDiagnostic
        {
            Severity = Severity.Error,
            Code = code,
            Message = message,
            File = file,
            Line = line,
            Column = column,
            Remedy = remedy,
        });

    public void Warn(string file, string code, string message, int line = 0, int column = 0, string? remedy = null) =>
        Add(new ConfigDiagnostic
        {
            Severity = Severity.Warning,
            Code = code,
            Message = message,
            File = file,
            Line = line,
            Column = column,
            Remedy = remedy,
        });
}
