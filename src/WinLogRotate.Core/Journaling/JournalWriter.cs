using System.Text;
using System.Text.Json;
using WinLogRotate.Contracts;

namespace WinLogRotate.Core.Journaling;

/// <summary>
/// Appends newline-delimited JSON to a per-day file under the journal directory.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why NDJSON and not a single JSON array.</b> A run that is killed mid-write must leave a
/// readable journal. With an array, an unclosed bracket makes the entire day unparseable -
/// losing exactly the history you would want after a crash. With one object per line, a torn
/// final line costs one entry, and the reader counts it rather than throwing.
/// </para>
/// <para>
/// <b>Why flush every line.</b> The GUI tails this file live, and a crash must not take the
/// buffered tail with it. A rotation writes on the order of tens of lines, so the cost is
/// irrelevant next to the I/O it is describing.
/// </para>
/// </remarks>
public sealed class JournalWriter : IJournal
{
    private readonly StreamWriter _writer;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();

    public string RunId { get; }

    public string Path { get; }

    private JournalWriter(StreamWriter writer, string path, string runId, TimeProvider clock)
    {
        _writer = writer;
        _clock = clock;
        Path = path;
        RunId = runId;
    }

    /// <summary>Opens today's journal file, creating the directory if needed.</summary>
    public static JournalWriter Open(string journalDirectory, TimeProvider clock, string? runId = null)
    {
        Directory.CreateDirectory(journalDirectory);

        var name = $"journal-{clock.GetUtcNow():yyyy-MM-dd}.ndjson";
        var path = System.IO.Path.Combine(journalDirectory, name);

        // FileShare.Read so the GUI can tail while we append; FileShare.Delete so the
        // journal's own retention pass can rename or remove an older file we are not holding.
        var stream = new FileStream(
            path, FileMode.Append, FileAccess.Write,
            FileShare.Read | FileShare.Delete);

        var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = false,
        };

        return new JournalWriter(writer, path, runId ?? Journaling.RunId.New(clock), clock);
    }

    public void Write(CliEvent entry)
    {
        var stamped = entry with
        {
            Run = entry.Run.Length == 0 ? RunId : entry.Run,
            Ts = entry.Ts.Length == 0 ? _clock.GetUtcNow().ToString("O") : entry.Ts,
        };

        var line = JsonSerializer.Serialize(stamped, JournalJsonContext.Default.CliEvent);

        // A newline embedded in a path would split one record into two unparseable ones. Paths
        // cannot legally contain one, but the journal is a forensic record and must stay
        // readable even when its input is not what we assumed.
        line = line.Replace("\r", string.Empty, StringComparison.Ordinal)
                   .Replace("\n", string.Empty, StringComparison.Ordinal);

        lock (_gate)
        {
            _writer.WriteLine(line);
            _writer.Flush();
        }
    }

    public void Dispose()
    {
        _writer.Flush();
        _writer.Dispose();
    }
}
