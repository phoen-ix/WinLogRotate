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

    /// <summary>
    /// The writer to close when the verb is done - the one <c>--output</c> opened, and never
    /// <c>Console.Out</c>.
    /// </summary>
    /// <remarks>
    /// Nothing closed it. <c>CommandContext.From</c> opens the file and is called inline at every
    /// SetAction in the tree, so the handle stayed open for the life of the process. On Linux
    /// nothing notices; on Windows a reader opening with the default <c>FileShare.Read</c> cannot
    /// tolerate an outstanding write handle, so reading the file during a run is a sharing
    /// violation. The GUI escaped it twice over - its tail shares write, and it reads the whole
    /// file only after the child has exited - which is why tests running the CLI in-process are
    /// what finally found it.
    /// </remarks>
    private readonly TextWriter? _owned = streamTo;

    public bool Verbose { get; } = verbose;

    public IReadOnlyList<CliDiagnostic> Diagnostics => _diagnostics.Items;

    public void Diagnostic(CliDiagnostic d) => _diagnostics.Add(d);

    public void Event(CliEvent e)
    {
        if (!stream)
        {
            return;
        }

        _events.WriteLine(JsonSerializer.Serialize(e, CliEventJson.Default.CliEvent));
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

        // To _events, not Console.Out. Where --output was not given these are the same writer,
        // so nothing changes for an ordinary --json caller. Where it was given, the caller is an
        // elevated "runas" child whose console cannot be read by the process that started it -
        // so writing the envelope there did not merely fail to reach the caller, it destroyed
        // it with the hidden console. --output says it writes the stream "instead of stdout";
        // until now it redirected the event half and left the result behind.
        _events.WriteLine(JsonSerializer.Serialize(envelope, info));
        _events.Flush();

        // Here, and not in a Dispose the caller would have to remember: Complete is the last
        // thing every verb does, and the envelope above is the last thing written. Closing it
        // makes the file readable by anything the instant the verb returns - which is the whole
        // contract of a flag whose only caller is another program waiting to read it.
        _owned?.Dispose();

        return exitCode;
    }
}
