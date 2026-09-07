using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using WinLogRotate.Contracts;
using WinLogRotate.Core;

namespace WinLogRotate.Cli.Output;

/// <summary>
/// Emits one <see cref="CliEnvelope{T}"/> on completion (<c>--json</c>), or a line-delimited
/// stream of <see cref="CliEvent"/> as work happens (<c>--json-stream</c>).
/// <para>
/// The stream form exists because of how the GUI elevates. A "runas" child cannot have its
/// pipes redirected, so it writes NDJSON to a file the GUI tails - and that only works if
/// each event is a complete line the moment it is written. Hence line-delimited rather than
/// one big array, and hence the flush.
/// </para>
/// </summary>
internal sealed class JsonOutputSink(bool verbose, bool stream, TextWriter? streamTo = null) : IOutputSink
{
    private readonly DiagnosticCollector _diagnostics = new();
    private readonly TextWriter _events = streamTo ?? Console.Out;

    public bool Verbose { get; } = verbose;

    public void Diagnostic(CliDiagnostic d) => _diagnostics.Add(d);

    public void Event(CliEvent e)
    {
        if (!stream)
        {
            return;
        }

        _events.WriteLine(JsonSerializer.Serialize(e, CliJsonContext.Default.CliEvent));
        _events.Flush();
    }

    // Human-readable extras have no place in a machine-readable stream; the same information
    // is carried as diagnostics and events.
    public void Line(string text) { }

    public int Complete<T>(string verb, int exitCode, T? result)
    {
        var envelope = new CliEnvelope<T>
        {
            Schema = ProductInfo.ContractSchema,
            Product = ProductInfo.Name,
            Version = ProductInfo.Version,
            Verb = verb,
            Ok = exitCode == ExitCode.Ok,
            ExitCode = exitCode,
            Result = result,
            Diagnostics = _diagnostics.Items,
        };

        if (CliJsonContext.Default.GetTypeInfo(typeof(CliEnvelope<T>)) is not JsonTypeInfo<CliEnvelope<T>> info)
        {
            // Reached only by a developer who added a verb and forgot to register its result
            // type. Failing loudly here beats a NativeAOT-only runtime failure in the field.
            throw new InvalidOperationException(
                $"CliEnvelope<{typeof(T).Name}> is not registered in {nameof(CliJsonContext)}. " +
                "Add a [JsonSerializable] attribute for it.");
        }

        Console.Out.WriteLine(JsonSerializer.Serialize(envelope, info));
        return exitCode;
    }
}
