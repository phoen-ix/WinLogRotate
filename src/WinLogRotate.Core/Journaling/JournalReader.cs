using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using WinLogRotate.Contracts;

namespace WinLogRotate.Core.Journaling;

/// <summary>One run's worth of journal entries.</summary>
public sealed record JournalRun
{
    public required string RunId { get; init; }
    public required DateTimeOffset Started { get; init; }
    public required IReadOnlyList<CliEvent> Entries { get; init; }
}

/// <summary>Narrows what <see cref="JournalReader"/> returns.</summary>
public sealed record JournalFilter
{
    public string? Job { get; init; }
    public IReadOnlySet<string>? Operations { get; init; }
    public string? Result { get; init; }
    public string? PathPrefix { get; init; }
    public DateTimeOffset? Since { get; init; }

    public bool Matches(CliEvent e)
    {
        if (Job is not null && !string.Equals(e.Job, Job, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (Operations is not null && !Operations.Contains(e.Operation))
        {
            return false;
        }

        if (Result is not null && !string.Equals(e.Result, Result, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (PathPrefix is not null
            && !(e.Src?.StartsWith(PathPrefix, StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return false;
        }

        // Written with "O", so parsed with InvariantCulture and RoundtripKind. A culture-aware
        // parse would read this file differently on a German machine than the one that wrote it.
        if (Since is not null
            && DateTimeOffset.TryParse(e.Ts, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var ts)
            && ts < Since)
        {
            return false;
        }

        return true;
    }
}

/// <summary>
/// Streams journal entries back. The GUI's history view is a filter over this and needs no
/// separate index.
/// </summary>
public sealed class JournalReader(string journalDirectory)
{
    /// <summary>
    /// Lines that could not be parsed. A journal is a forensic record: a torn final line from
    /// a killed process is expected, and counting it is far more useful than throwing away the
    /// whole day's history because of it.
    /// </summary>
    public int SkippedLines { get; private set; }

    public IEnumerable<CliEvent> Read(JournalFilter? filter = null)
    {
        if (!Directory.Exists(journalDirectory))
        {
            yield break;
        }

        var files = Directory
            .EnumerateFiles(journalDirectory, "journal-*")
            .Where(IsJournal)
            .OrderBy(f => f, StringComparer.Ordinal);

        foreach (var file in files)
        {
            foreach (var entry in ReadFile(file))
            {
                if (filter is null || filter.Matches(entry))
                {
                    yield return entry;
                }
            }
        }
    }

    /// <summary>Entries grouped by run, newest first.</summary>
    public IEnumerable<JournalRun> Runs(int take = 50) =>
        Read()
            .GroupBy(e => e.Run)
            .Select(g => new JournalRun
            {
                RunId = g.Key,
                Started = g.Select(e => DateTimeOffset.TryParse(e.Ts, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var t) ? t : default).Min(),
                Entries = g.ToArray(),
            })
            .OrderByDescending(r => r.RunId, StringComparer.Ordinal)
            .Take(take);

    /// <summary>
    /// The three shapes a journal file takes on disk, and the only three.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reader used to enumerate <c>journal-*.ndjson</c> alone, and the maintenance pass
    /// compresses yesterday's file - <c>JournalSettings.Compress</c> defaults to <c>Zip</c>, so
    /// this is what a default install does on its third run. <c>journal-2026-09-11.ndjson.zip</c>
    /// does not match that pattern, nothing in the product decompressed, and
    /// <c>Compressor</c> had already deleted the original.
    /// </para>
    /// <para>
    /// So thirty days of forensic record became files the product could not open, while
    /// <c>docs/diagnostics.md</c> promised "The full record is always in the journal" and the
    /// Event Log's own allowance announcement told operators to run this verb for it.
    /// <c>SkippedLines</c> stayed 0, because a file that is never enumerated has no lines to skip.
    /// </para>
    /// </remarks>
    private static bool IsJournal(string path) =>
        path.EndsWith(".ndjson", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".ndjson.zip", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".ndjson.gz", StringComparison.OrdinalIgnoreCase);

    private IEnumerable<CliEvent> ReadFile(string path)
    {
        // Share everything: a run may be appending to this very file while we read it.
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        // Disposed in the reverse order they were opened, by the iterator, whichever branch ran.
        // A zip's entry stream outlives nothing: the archive has to stay open around it.
        using var archive = path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? new ZipArchive(stream, ZipArchiveMode.Read)
            : null;

        var content = archive is not null
            ? archive.Entries.Count > 0 ? archive.Entries[0].Open() : Stream.Null
            : path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
                ? new GZipStream(stream, CompressionMode.Decompress)
                : stream;

        using var reader = new StreamReader(content);

        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            CliEvent? entry;
            try
            {
                entry = JsonSerializer.Deserialize(line, CliEventJson.Default.CliEvent);
            }
            catch (JsonException)
            {
                SkippedLines++;
                continue;
            }

            if (entry is null)
            {
                SkippedLines++;
                continue;
            }

            yield return entry;
        }
    }
}
