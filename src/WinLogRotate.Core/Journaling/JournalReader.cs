using System.Globalization;
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
            .EnumerateFiles(journalDirectory, "journal-*.ndjson")
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

    private IEnumerable<CliEvent> ReadFile(string path)
    {
        // Share everything: a run may be appending to this very file while we read it.
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);

        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            CliEvent? entry;
            try
            {
                entry = JsonSerializer.Deserialize(line, JournalJsonContext.Default.CliEvent);
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
