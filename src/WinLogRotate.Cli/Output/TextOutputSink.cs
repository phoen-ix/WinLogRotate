using WinLogRotate.Contracts;

namespace WinLogRotate.Cli.Output;

/// <summary>
/// The default sink: plain text for a human at a prompt. Warnings and errors go to stderr
/// so <c>winlogrotate run | Select-String ...</c> pipes only the actual output.
/// </summary>
internal sealed class TextOutputSink(bool verbose, bool color, TextWriter? to = null) : IOutputSink
{
    private readonly DiagnosticCollector _diagnostics = new();

    // Taken once, like JsonOutputSink's own writer, and overridable for the same reason the
    // engine's seams are: what a run renders is now worth asserting, and Console.SetOut is a
    // process-wide global that two test classes running in parallel would take from each other.
    private readonly TextWriter _out = to ?? Console.Out;

    public bool Verbose { get; } = verbose;

    public IReadOnlyList<CliDiagnostic> Diagnostics => _diagnostics.Items;

    public void Diagnostic(CliDiagnostic d)
    {
        _diagnostics.Add(d);
        if (d.Severity == Severity.Info && !Verbose)
        {
            return;
        }

        var writer = d.Severity >= Severity.Warning ? Console.Error : _out;

        // The colour is this sink's business and the wording is the contract's. Passing the
        // painted label in is the whole of the difference between a terminal and the dialog the
        // GUI shows for the same diagnostic.
        writer.WriteLine(CliDiagnosticText.Describe(
            d, color ? Paint(CliDiagnosticText.Label(d.Severity), d.Severity) : null));
    }

    public void Event(CliEvent e)
    {
        // One line per operation, which is what "run" printed by hand until the sinks rendered.
        // The tee has already reduced each operation to its last word, so this does not have to
        // choose between the plan and apply halves - it only decides what is not the work: the
        // run and job brackets are scaffolding, and a notification that went out is already
        // reported by notify's own line. Both stay behind --verbose. Failures are never
        // scaffolding and always print.
        if (Verbose || !(IsBracket(e) || WasSent(e)))
        {
            _out.WriteLine(CliEventText.Describe(e));
        }
    }

    private static bool IsBracket(CliEvent e) =>
        e.Operation is Op.RunStart or Op.RunEnd or Op.JobStart or Op.JobEnd;

    private static bool WasSent(CliEvent e) => e.Operation == Op.Hook && e.Result == OpResult.Ok;

    public void Line(string text) => _out.WriteLine(text);

    public int Complete<T>(string verb, int exitCode, T? result) => exitCode;

    private static string Paint(string label, Severity s) => s switch
    {
        Severity.Critical or Severity.Error => $"\e[31m{label}\e[0m",
        Severity.Warning => $"\e[33m{label}\e[0m",
        _ => label,
    };
}
