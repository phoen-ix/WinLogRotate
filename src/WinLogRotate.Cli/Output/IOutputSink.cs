using WinLogRotate.Contracts;

namespace WinLogRotate.Cli.Output;

/// <summary>
/// Everything user-facing goes through here. Nothing in the CLI writes to
/// <see cref="Console"/> directly - an architecture test enforces that - because the same
/// verb has to render three ways: as human text, as one <c>--json</c> envelope, and as an
/// NDJSON <c>--json-stream</c> the GUI tails. Threading a sink through is what keeps those
/// three from drifting apart.
/// </summary>
public interface IOutputSink
{
    /// <summary>Suppressed unless --verbose. Warnings and above always render.</summary>
    bool Verbose { get; }

    /// <summary>A diagnostic. Collected into the envelope under --json.</summary>
    void Diagnostic(CliDiagnostic diagnostic);

    /// <summary>One event of a long-running verb.</summary>
    void Event(CliEvent evt);

    /// <summary>Free-form human output (help text, tables). Ignored by the JSON sinks,
    /// which carry the same information as structured data instead.</summary>
    void Line(string text);

    /// <summary>Finish the verb, emitting the envelope if this sink emits one.</summary>
    /// <returns>The process exit code.</returns>
    int Complete<T>(string verb, int exitCode, T? result);
}
