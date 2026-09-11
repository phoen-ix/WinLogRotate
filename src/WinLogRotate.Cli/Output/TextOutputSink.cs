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
        var label = d.Severity switch
        {
            Severity.Critical => "critical",
            Severity.Error => "error",
            Severity.Warning => "warning",
            _ => "info",
        };

        var where = d.Path is null ? "" :
            d.Line is null ? $"{d.Path}: " : $"{d.Path}({d.Line},{d.Column ?? 1}): ";

        writer.WriteLine(color
            ? $"{Paint(label, d.Severity)} {where}{d.Message} [{d.Code}]"
            : $"{label}: {where}{d.Message} [{d.Code}]");

        if (d.Remedy is not null)
        {
            writer.WriteLine($"        {d.Remedy}");
        }
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
            _out.WriteLine(Describe(e));
        }
    }

    private static bool IsBracket(CliEvent e) =>
        e.Operation is Op.RunStart or Op.RunEnd or Op.JobStart or Op.JobEnd;

    private static bool WasSent(CliEvent e) => e.Operation == Op.Hook && e.Result == OpResult.Ok;

    public void Line(string text) => _out.WriteLine(text);

    public int Complete<T>(string verb, int exitCode, T? result) => exitCode;

    private static string Describe(CliEvent e)
    {
        // Three states, not two. Until the tee existed the only events reaching a sink were
        // notify's, which are never "failed to" in a way a human reads on this line - so "did
        // delete" was printed for a delete that threw, with the truth left to the diagnostic
        // underneath it.
        var verb = e.Result == OpResult.Failed ? "failed to"
            : e.Phase == Phase.Plan ? "would"
            : "did";
        var job = e.Job is null ? "" : $"[{e.Job}] ";
        var body = e.Operation switch
        {
            // The only operation a skip is ever recorded as: PlanExecutor maps every action it
            // can carry out to a named op, and PlannedAction.Skip is the one that is left.
            Op.Plan => $"skip {e.Src}",
            Op.Delete => $"{verb} delete {e.Src}",
            Op.Compress => $"{verb} compress {e.Src} -> {e.Dst}",
            Op.Rename => $"{verb} rename {e.Src} -> {e.Dst}",
            Op.CopyTruncate => $"{verb} copytruncate {e.Src} -> {e.Dst}",
            Op.Copy => $"{verb} copy {e.Src} -> {e.Dst}",
            Op.Create => $"{verb} create {e.Dst}",
            Op.CreateDir => $"{verb} mkdir {e.Src}",
            Op.Hook => $"{verb} run hook {e.Src}",
            _ => $"{e.Operation} {e.Src}".TrimEnd(),
        };
        var why = e.Reason is null ? "" : $"  ({e.Reason})";
        return $"  {job}{body}{why}";
    }

    private static string Paint(string label, Severity s) => s switch
    {
        Severity.Critical or Severity.Error => $"\e[31m{label}\e[0m",
        Severity.Warning => $"\e[33m{label}\e[0m",
        _ => label,
    };
}
