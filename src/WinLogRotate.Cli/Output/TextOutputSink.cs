using WinLogRotate.Contracts;

namespace WinLogRotate.Cli.Output;

/// <summary>
/// The default sink: plain text for a human at a prompt. Warnings and errors go to stderr
/// so <c>winlogrotate run | Select-String ...</c> pipes only the actual output.
/// </summary>
internal sealed class TextOutputSink(bool verbose, bool color) : IOutputSink
{
    private readonly DiagnosticCollector _diagnostics = new();

    public bool Verbose { get; } = verbose;

    public IReadOnlyList<CliDiagnostic> Diagnostics => _diagnostics.Items;

    public void Diagnostic(CliDiagnostic d)
    {
        _diagnostics.Add(d);
        if (d.Severity == Severity.Info && !Verbose)
        {
            return;
        }

        var writer = d.Severity >= Severity.Warning ? Console.Error : Console.Out;
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
        // The plan half is the interesting one for a human: under --dry-run it is all there
        // is, and under a real run the apply half only repeats it unless something failed.
        if (e.Phase == Phase.Plan || e.Result == OpResult.Failed || Verbose)
        {
            Console.Out.WriteLine(Describe(e));
        }
    }

    public void Line(string text) => Console.Out.WriteLine(text);

    public int Complete<T>(string verb, int exitCode, T? result) => exitCode;

    private static string Describe(CliEvent e)
    {
        var verb = e.Phase == Phase.Plan ? "would" : "did";
        var job = e.Job is null ? "" : $"[{e.Job}] ";
        var body = e.Operation switch
        {
            Op.Delete => $"{verb} delete {e.Src}",
            Op.Compress => $"{verb} compress {e.Src} -> {e.Dst}",
            Op.Rename => $"{verb} rename {e.Src} -> {e.Dst}",
            Op.CopyTruncate => $"{verb} copytruncate {e.Src} -> {e.Dst}",
            Op.Copy => $"{verb} copy {e.Src} -> {e.Dst}",
            Op.Create => $"{verb} create {e.Dst}",
            Op.MoveOldDir => $"{verb} move {e.Src} -> {e.Dst}",
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
