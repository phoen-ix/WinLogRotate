using WinLogRotate.Contracts;

namespace WinLogRotate.Cli.Output;

/// <summary>Shared diagnostic bookkeeping for the concrete sinks.</summary>
internal sealed class DiagnosticCollector
{
    private readonly List<CliDiagnostic> _items = [];

    public IReadOnlyList<CliDiagnostic> Items => _items;

    public void Add(CliDiagnostic d) => _items.Add(d);

    /// <summary>True once anything at <see cref="Severity.Error"/> or above was reported,
    /// which is what turns a run's exit code into 1.</summary>
    public bool HasErrors => _items.Any(d => d.Severity >= Severity.Error);
}
