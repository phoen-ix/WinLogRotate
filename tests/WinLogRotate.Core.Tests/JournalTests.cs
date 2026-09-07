using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using WinLogRotate.Contracts;
using WinLogRotate.Core.Journaling;
using Xunit;

namespace WinLogRotate.Core.Tests;

public sealed class JournalTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("winlogrotate-journal-");
    private readonly FakeTimeProvider _clock =
        new(new DateTimeOffset(2026, 9, 7, 3, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        try
        {
            _dir.Delete(recursive: true);
        }
        catch (IOException)
        {
            // Best effort - a temp directory left behind is not worth failing a test over.
        }
    }

    private CliEvent Deletion(string src, string reason) => new()
    {
        Ts = string.Empty,
        Run = string.Empty,
        Operation = Op.Delete,
        Phase = Phase.Apply,
        Result = OpResult.Ok,
        Job = "iis",
        Src = src,
        Reason = reason,
    };

    [Fact]
    public void EntriesRoundTrip()
    {
        using (var writer = JournalWriter.Open(_dir.FullName, _clock))
        {
            writer.Write(Deletion(@"C:\logs\a.log.gz", "rotate=30 exceeded"));
            writer.Write(Deletion(@"C:\logs\b.log.gz", "maxage=90 exceeded"));
        }

        var read = new JournalReader(_dir.FullName).Read().ToArray();

        read.Length.ShouldBe(2);
        read[0].Src.ShouldBe(@"C:\logs\a.log.gz");
        read[0].Reason.ShouldBe("rotate=30 exceeded");
        read[1].Src.ShouldBe(@"C:\logs\b.log.gz");
    }

    [Fact]
    public void EveryEntryCarriesTheRunIdAndATimestamp()
    {
        string runId;
        using (var writer = JournalWriter.Open(_dir.FullName, _clock))
        {
            runId = writer.RunId;
            writer.Write(Deletion(@"C:\logs\a.log.gz", "rotate exceeded"));
        }

        var entry = new JournalReader(_dir.FullName).Read().Single();
        entry.Run.ShouldBe(runId);
        DateTimeOffset.Parse(entry.Ts, CultureInfo.InvariantCulture).ShouldBe(_clock.GetUtcNow());
    }

    /// <summary>
    /// A run killed mid-write leaves a torn final line. Losing that one entry is acceptable;
    /// losing the whole day's history because of it is not - and a crash is exactly when
    /// somebody needs to read this file.
    /// </summary>
    [Fact]
    public void ATornFinalLineCostsOneEntryNotTheWholeDay()
    {
        using (var writer = JournalWriter.Open(_dir.FullName, _clock))
        {
            writer.Write(Deletion(@"C:\logs\a.log.gz", "rotate exceeded"));
            writer.Write(Deletion(@"C:\logs\b.log.gz", "rotate exceeded"));
        }

        var file = Directory.GetFiles(_dir.FullName, "*.ndjson").Single();
        File.AppendAllText(file, "{\"operation\":\"delete\",\"src\":\"C:\\\\logs\\\\c.lo");

        var reader = new JournalReader(_dir.FullName);
        var entries = reader.Read().ToArray();

        entries.Length.ShouldBe(2);
        reader.SkippedLines.ShouldBe(1);
    }

    [Fact]
    public void AppendingAcrossTwoSessionsKeepsBothRuns()
    {
        using (var first = JournalWriter.Open(_dir.FullName, _clock))
        {
            first.Write(Deletion(@"C:\logs\a.log.gz", "first"));
        }

        using (var second = JournalWriter.Open(_dir.FullName, _clock))
        {
            second.Write(Deletion(@"C:\logs\b.log.gz", "second"));
        }

        var runs = new JournalReader(_dir.FullName).Runs().ToArray();
        runs.Length.ShouldBe(2);
        runs.SelectMany(r => r.Entries).Count().ShouldBe(2);
    }

    [Fact]
    public void FilteringNarrowsByJobAndOperation()
    {
        using (var writer = JournalWriter.Open(_dir.FullName, _clock))
        {
            writer.Write(Deletion(@"C:\logs\a.log.gz", "rotate exceeded"));
            writer.Write(Deletion(@"C:\logs\b.log.gz", "rotate exceeded") with { Job = "myapp" });
            writer.Write(Deletion(@"C:\logs\c.log", "n/a") with { Operation = Op.Compress });
        }

        var reader = new JournalReader(_dir.FullName);
        reader.Read(new JournalFilter { Job = "iis" }).Count().ShouldBe(2);
        reader.Read(new JournalFilter { Operations = new HashSet<string> { Op.Delete } }).Count().ShouldBe(2);
        reader.Read(new JournalFilter { PathPrefix = @"C:\logs\b" }).Count().ShouldBe(1);
    }

    // The GUI tails the live file while a run appends to it. If the writer denied read
    // sharing, the history view would show nothing until the run finished.
    [Fact]
    public void TheJournalIsReadableWhileARunIsStillWritingIt()
    {
        using var writer = JournalWriter.Open(_dir.FullName, _clock);
        writer.Write(Deletion(@"C:\logs\a.log.gz", "rotate exceeded"));

        new JournalReader(_dir.FullName).Read().Count().ShouldBe(1);

        writer.Write(Deletion(@"C:\logs\b.log.gz", "rotate exceeded"));
        new JournalReader(_dir.FullName).Read().Count().ShouldBe(2);
    }

    [Fact]
    public void RunIdsSortIntoChronologicalOrder()
    {
        var first = RunId.New(_clock);
        _clock.Advance(TimeSpan.FromSeconds(5));
        var second = RunId.New(_clock);

        string.CompareOrdinal(first, second).ShouldBeLessThan(0);
        first.Length.ShouldBe(26);
    }

    [Fact]
    public void ReadingAnAbsentDirectoryYieldsNothingRatherThanThrowing() =>
        new JournalReader(Path.Combine(_dir.FullName, "nope")).Read().ShouldBeEmpty();
}
