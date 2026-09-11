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
    /// counts only errors with no job to blame, so an unparseable [notify] table still stops
    /// everything while one job's refused path stops that job. The doctrine is <c>ConfigLoader</c>'s
    /// own, already applied to a file that will not parse: report it, skip it, carry on.
    /// </remarks>
    public string? Job { get; init; }

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
