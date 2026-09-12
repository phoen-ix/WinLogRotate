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

    /// <summary>
    /// A journal file that cannot be read costs that day and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The read half of what the write half was given a milestone ago. Opened inside an
    /// iterator, the exception travelled through <c>JournalCommand</c>'s <c>.ToArray()</c> to
    /// <c>CommandContext.Guarded</c> and came back as <c>LR1006</c>, exit 4 - <i>"This is a
    /// defect. Nothing about what was or was not done can be relied on"</i> - about a directory
    /// in which twenty-nine other days were perfectly readable. <c>docs/diagnostics.md</c> has
    /// promised <c>LR3106</c> rather than exit 4 for the journal ever since; it meant the writer.
    /// </para>
    /// <para>
    /// Both corruptions are asserted, because they throw in different places and only one of
    /// them was ever going to be caught by guarding the open. A zip truncated to its signature
    /// fails in the <c>ZipArchive</c> constructor; a gzip with a valid header and a damaged body
    /// fails at the first <c>ReadLine</c>, as <c>InvalidDataException</c> - which does not
    /// derive from <c>IOException</c>, so catching only that would have left the commonest case
    /// reaching exit 4. A crash between <c>Compressor</c> writing its <c>.tmp</c> and moving it
    /// leaves exactly these.
    /// </para>
    /// <para>
    /// The surviving days are named, not counted, and the casualty is named too: a guard that
    /// swallowed the failure silently would pass an assertion about how many days came back.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("journal-2026-09-11.ndjson.zip", "PK\u0003\u0004 and then nothing at all")]
    [InlineData("journal-2026-09-11.ndjson.gz", "\u001f\u008b\u0008\u0000 and then nothing at all")]
    public void AJournalFileThatCannotBeReadCostsThatDayAndNothingElse(string name, string rubbish)
    {
        Write("journal-2026-09-10.ndjson", Entry("2026-09-10T01:00:00.0000000+00:00", "oldest", "01OLD"));
        Write("journal-2026-09-12.ndjson", Entry("2026-09-12T01:00:00.0000000+00:00", "today", "01NEW"));

        File.WriteAllBytes(
            Path.Combine(_dir.FullName, name),
            System.Text.Encoding.Latin1.GetBytes(rubbish));

        var reader = new JournalReader(_dir.FullName);
        var jobs = reader.Read().Select(e => e.Job).ToArray();

        jobs.ShouldBe(["oldest", "today"]);

        Path.GetFileName(reader.Unreadable.ShouldHaveSingleItem()).ShouldBe(name);
        reader.SkippedLines.ShouldBe(0, "a file that never opened has no lines to skip");
    }

    /// <summary>
    /// An archive holding anything but one entry is a casualty, not an empty day.
    /// </summary>
    /// <remarks>
    /// <c>Compressor</c> writes exactly one entry, named for the file it compressed. Zero
    /// entries used to read as <c>Stream.Null</c>: a day that silently contained nothing, with
    /// no skipped lines and no casualty, so the verb printed the other days and said nothing at
    /// all about this one. Reading the first of several would be worse - part of a record
    /// reported as the whole of it.
    /// </remarks>
    [Fact]
    public void AnArchiveThatIsNotOneJournalIsACasualty()
    {
        Write("journal-2026-09-12.ndjson", Entry("2026-09-12T01:00:00.0000000+00:00", "today"));

        var path = Path.Combine(_dir.FullName, "journal-2026-09-11.ndjson.zip");

        using (var file = File.Create(path))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        {
            // Empty on purpose. The two-entry case reaches the same throw by the same test.
        }

        var reader = new JournalReader(_dir.FullName);

        reader.Read().Select(e => e.Job).ShouldBe(["today"]);
        Path.GetFileName(reader.Unreadable.ShouldHaveSingleItem())
            .ShouldBe("journal-2026-09-11.ndjson.zip");
    }
}
