using WinLogRotate.Contracts;
using WinLogRotate.Core;

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

    /// <summary>
    /// What the envelope should carry, with the promise on <c>CliEnvelope.Diagnostics</c> kept.
    /// </summary>
    /// <remarks>
    /// <para>
    /// That property documents itself as "everything worth telling the caller, in order. Never
    /// empty on a failure", and under <c>--json</c> it is the only channel there is - Line is a
    /// no-op. Nine verbs broke the promise, every one of them fixed at its own site, because a
    /// named condition with a remedy is worth incomparably more than this.
    /// </para>
    /// <para>
    /// So this is a net and not a workhorse. If it ever fires in the wild it is a bug report
    /// rather than a diagnosis, and its wording says so.
    /// </para>
    /// <para>
    /// <c>ExitCode.LockHeld</c> is not exempt. It is not a failure - its own summary calls it "the
    /// expected outcome when a manual run overlaps the scheduled one" - but the run that takes it
    /// raises <c>LR1008</c> for itself, so the net never sees it. Exempting the code here would
    /// have been the same mistake one level down: a rule with a hole shaped like the case somebody
    /// happened to think of.
    /// </para>
    /// </remarks>
    public IReadOnlyList<CliDiagnostic> Settled(string verb, int exitCode)
    {
        if (exitCode == ExitCode.Ok || _items.Count > 0)
        {
            return _items;
        }

        return
        [
            new CliDiagnostic
            {
                Severity = Severity.Error,
                Code = DiagnosticCode.FailedWithoutReason,
                Message = $"'{verb}' failed with exit code {exitCode} and reported no reason. "
                        + "That is a defect in WinLogRotate.",
                Remedy = "Please report it, with the command line you used. "
                       + "Run it without --json to see whether anything was printed.",
            },
        ];
    }
}
