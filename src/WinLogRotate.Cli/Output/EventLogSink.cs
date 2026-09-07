using System.Runtime.Versioning;
using WinLogRotate.Contracts;
using WinLogRotate.Hosting;
using WinLogRotate.Hosting.Diagnostics;

namespace WinLogRotate.Cli.Output;

/// <summary>
/// Mirrors Warning and above to the Windows Event Log, and passes everything through.
/// </summary>
/// <remarks>
/// <para>
/// A decorator rather than a fourth sink, so every verb keeps rendering exactly as it did and
/// no command file changes. It lives under <c>Output/</c> because that is where the
/// architecture test permits console and diagnostic plumbing to live.
/// </para>
/// <para>
/// This is the channel for the run nobody watched. The scheduled task runs
/// <c>run --config-dir ... --lock-held-exit 0</c> as SYSTEM, and its stdout goes nowhere at
/// all; without this, a rotation that failed every night for a month left no trace an
/// administrator would ever encounter.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class EventLogSink(IOutputSink inner) : IOutputSink
{
    public bool Verbose => inner.Verbose;

    public IReadOnlyList<CliDiagnostic> Diagnostics => inner.Diagnostics;

    public void Diagnostic(CliDiagnostic d)
    {
        inner.Diagnostic(d);

        // Warning and above, matching what the README has always promised. Info is progress
        // reporting; a system log is not where progress belongs.
        if (d.Severity >= Severity.Warning)
        {
            EventLogWriter.TryWrite(
                Names.EventLogSource, d.Severity, EventIds.For(d.Code), EventIds.Render(d));
        }
    }

    public void Event(CliEvent evt) => inner.Event(evt);

    public void Line(string text) => inner.Line(text);

    public int Complete<T>(string verb, int exitCode, T? result) => inner.Complete(verb, exitCode, result);
}
