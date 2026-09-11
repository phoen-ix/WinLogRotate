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
    /// Nothing closed it at all once; then it was closed here, on the one path that reaches the
    /// end of a verb. Both were incomplete for the same reason - an exception does not reach the
    /// end of a verb - which is why <c>CommandContext.Guarded</c> now brackets the invocation and
    /// this only has to be the ordinary case. On Windows a reader opening with the default
    /// <c>FileShare.Read</c> cannot tolerate an outstanding write handle, so a leaked one turns
    /// the next read of that file into a sharing violation.
    /// </remarks>
    private readonly TextWriter? _owned = streamTo;

    /// <summary>
    /// Whether the last word has been said.
    /// </summary>
    /// <remarks>
    /// The guard calls Complete after a verb throws, and a verb can throw <i>after</i> it has
    /// already emitted its envelope: every verb's disposals run as its method unwinds, which is
    /// after its own Complete and still inside the guard's try. Two envelopes in one stream is
    /// worse than none - a caller reads the first, sees a completed run, and never learns it did
    /// not finish.
    /// </remarks>
    private bool _closed;

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
        if (_closed)
        {
            return exitCode;
        }

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

        // Resolved before anything is written and before anything is closed. This throw is a
        // defect report, and it has to leave the sink able to report it: the guard catches it and
        // calls Complete<EmptyResult>, which is registered, and that call writes the envelope and
        // closes the file. Doing the lookup after a partial write, or after the dispose, would
        // take that second chance away.
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
        try
        {
            _events.WriteLine(JsonSerializer.Serialize(envelope, info));
            _events.Flush();
        }
        finally
        {
            // Closed even if the write failed. The file is readable by anything the instant the
            // verb returns, which is the whole contract of a flag whose only caller is another
            // program waiting to read it - and a half-written envelope that nobody can open is
            // strictly worse than a half-written one they can.
            _closed = true;
            _owned?.Dispose();
        }

        return exitCode;
    }
}
