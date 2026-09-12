using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Shouldly;
using WinLogRotate.Contracts;
using WinLogRotate.Core.Journaling;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// What the reader does with a record it cannot place in time, and with a day that rolled.
/// </summary>
/// <remarks>
/// <para>
/// Three faults on the read side of the journal, all of them silent. A <c>--since</c> window
/// answered with records from outside it; a run reported as having started in the year 1; and a
/// busy day printed in the wrong order. None of them raises a diagnostic, none of them increments
/// <c>SkippedLines</c>, and the journal is the record an operator consults precisely when the
/// other channels have already failed them.
/// </para>
/// <para>
/// The files here are written by hand rather than through <c>JournalWriter</c>, which is the only
/// way to produce the fixtures: the writer stamps <c>Ts</c> from its clock, so it cannot emit the
/// malformed timestamp a torn or hand-edited journal contains, and it rolls to <c>.1</c> only
/// after a real file has reached <c>maxsize</c>.
/// </para>
/// </remarks>
public sealed class JournalReadSideTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("winlogrotate-readside-");

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); } catch (IOException) { }
    }

    private static CliEvent Entry(string ts, string job, string run = "01RUN") => new()
    {
        Ts = ts,
        Run = run,
        Operation = Op.Delete,
        Phase = Phase.Apply,
        Result = OpResult.Ok,
        Job = job,
        Src = $@"C:\logs\{job}.log.9",
        Reason = "rotate = 3 exceeded",
    };

    /// <summary>Writes one journal file, gzipped if the name says so.</summary>
    private void Write(string name, params CliEvent[] entries)
    {
        var text = new StringBuilder();
        foreach (var entry in entries)
        {
            text.AppendLine(JsonSerializer.Serialize(entry, CliEventJson.Default.CliEvent));
        }

        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text.ToString());
        var path = Path.Combine(_dir.FullName, name);

        if (name.EndsWith(".gz", StringComparison.Ordinal))
        {
            using var file = File.Create(path);
            using var gz = new GZipStream(file, CompressionMode.Compress);
            gz.Write(bytes);
        }
        else
        {
            File.WriteAllBytes(path, bytes);
        }
    }

    /// <summary>
    /// A record whose timestamp will not parse is outside every <c>--since</c> window.
    /// </summary>
    /// <remarks>
    /// The filter used to read <c>Since is not null &amp;&amp; TryParse(...) &amp;&amp; ts &lt; Since</c>.
    /// A failed parse made the whole condition false, fell through the remaining checks and
    /// returned <c>true</c> - so the one verb whose job is to narrow a window answered it with
    /// records it had just admitted it could not place. An operator reading
    /// <c>journal --since 09:00</c> after an incident got entries from outside the window with
    /// nothing to mark them as such.
    /// </remarks>
    [Fact]
    public void ARecordThatCannotBePlacedInTimeIsOutsideEveryWindow()
    {
        Write(
            "journal-2026-09-12.ndjson",
            Entry("2026-09-12T04:00:00.0000000+00:00", "after"),
            Entry("2026-09-12T01:00:00.0000000+00:00", "before"),
            Entry("whenever", "unplaceable"));

        var since = new DateTimeOffset(2026, 9, 12, 3, 0, 0, TimeSpan.Zero);

        new JournalReader(_dir.FullName)
            .Read(new JournalFilter { Since = since })
            .Select(e => e.Job)
            .ShouldBe(["after"]);
    }

    /// <summary>
    /// One malformed timestamp does not age the whole run to the year 1.
    /// </summary>
    /// <remarks>
    /// <c>Started</c> mapped an unparseable <c>Ts</c> to <c>default</c>, which is
    /// <see cref="DateTimeOffset.MinValue"/>, and then took the <c>Min</c> of the run - so one torn
    /// entry dated the entire run 0001-01-01. The GUI's history page and <c>journal --runs</c> both
    /// render that column.
    /// </remarks>
    [Fact]
    public void OneMalformedTimestampDoesNotAgeTheWholeRun()
    {
        Write(
            "journal-2026-09-12.ndjson",
            Entry("2026-09-12T04:00:00.0000000+00:00", "later"),
            Entry("whenever", "unplaceable"),
            Entry("2026-09-12T03:00:00.0000000+00:00", "earlier"));

        var run = new JournalReader(_dir.FullName).Runs().ShouldHaveSingleItem();

        run.Started.ShouldBe(new DateTimeOffset(2026, 9, 12, 3, 0, 0, TimeSpan.Zero));
        run.Entries.Count.ShouldBe(3, "the unplaceable entry is still part of the run");
    }

    /// <summary>
    /// A run with nothing readable in it keeps <see cref="DateTimeOffset.MinValue"/>, which is
    /// honest: it cannot be placed.
    /// </summary>
    [Fact]
    public void ARunWithNoReadableTimestampIsStillReturned()
    {
        Write("journal-2026-09-12.ndjson", Entry("whenever", "unplaceable"));

        new JournalReader(_dir.FullName).Runs().ShouldHaveSingleItem()
            .Started.ShouldBe(DateTimeOffset.MinValue);
    }

    /// <summary>
    /// A day that rolled is read in the order it was written.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The files were ordered by <c>StringComparer.Ordinal</c> over the whole name, under which
    /// <c>.1.</c> sorts before the base file - '1' is 0x31 and 'n' is 0x6E - and <c>.10.</c> sorts
    /// before <c>.2.</c>. So a day busy enough to hit <c>maxsize</c> was printed last part first,
    /// and the tenth roll in the middle of the morning.
    /// </para>
    /// <para>
    /// One roll is gzipped, because the maintenance pass compresses what it retains and the
    /// ordering has to see through the extension to the number underneath it.
    /// </para>
    /// </remarks>
    [Fact]
    public void ADayThatRolledIsReadInTheOrderItWasWritten()
    {
        Write("journal-2026-09-12.ndjson", Entry("2026-09-12T01:00:00.0000000+00:00", "base"));
        Write("journal-2026-09-12.1.ndjson.gz", Entry("2026-09-12T02:00:00.0000000+00:00", "roll-1"));
        Write("journal-2026-09-12.2.ndjson", Entry("2026-09-12T03:00:00.0000000+00:00", "roll-2"));
        Write("journal-2026-09-12.10.ndjson", Entry("2026-09-12T04:00:00.0000000+00:00", "roll-10"));

        new JournalReader(_dir.FullName)
            .Read()
            .Select(e => e.Job)
            .ShouldBe(["base", "roll-1", "roll-2", "roll-10"]);
    }

    /// <summary>Days are still read oldest first, whatever shape they are in on disk.</summary>
    [Fact]
    public void DaysAreReadOldestFirstAcrossCompressionExtensions()
    {
        Write("journal-2026-09-12.ndjson", Entry("2026-09-12T01:00:00.0000000+00:00", "today"));
        Write("journal-2026-09-11.ndjson.gz", Entry("2026-09-11T01:00:00.0000000+00:00", "yesterday"));

        new JournalReader(_dir.FullName)
            .Read()
            .Select(e => e.Job)
            .ShouldBe(["yesterday", "today"]);
    }
}
