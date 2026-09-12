using Microsoft.Extensions.Time.Testing;
using Shouldly;
using WinLogRotate.Contracts;
using WinLogRotate.Core.Compression;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Journaling;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// A journal the maintenance pass has compressed is still a journal.
/// </summary>
/// <remarks>
/// <para>
/// <c>docs/diagnostics.md</c> promises "The full record is always in the journal" and names it the
/// channel for "whoever is asking what happened to a specific file". The Event Log allowance
/// announcement tells operators, verbatim, to "Run <c>winlogrotate journal</c> for the full
/// record".
/// </para>
/// <para>
/// On a default install none of that was true past the third run.
/// <c>JournalSettings.Compress</c> defaults to <c>Zip</c>, so <c>JournalMaintenance</c> compressed
/// yesterday's file to <c>journal-….ndjson.zip</c> and deleted the original - and
/// <c>JournalReader</c> enumerated <c>journal-*.ndjson</c>, which cannot match a name ending
/// <c>.zip</c>. Nothing anywhere in <c>src/</c> decompressed. Thirty days of forensic record became
/// files the product could not open, and <c>SkippedLines</c> stayed 0, so nothing said so.
/// </para>
/// <para>
/// <c>JournalMaintenanceTests.CompressedJournalsAreStillReadable</c> was named for this property
/// and proved a different one: it opens the archive with <c>ZipFile.OpenRead</c> rather than with
/// <c>JournalReader</c>, so it asserts the bytes survive. The history was preserved in name only,
/// which is the phrase its own doc comment uses.
/// </para>
/// <para>
/// This one goes through the reader, and runs on both legs - the compression is built here with
/// <c>System.IO.Compression</c> rather than by executing a plan, so no Win32 path handling is
/// involved.
/// </para>
/// </remarks>
public sealed class JournalRoundTripTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("winlogrotate-roundtrip-");
    private readonly FakeTimeProvider _clock =
        new(new DateTimeOffset(2026, 9, 12, 3, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); } catch (IOException) { }
    }

    private static CliEvent Entry(string run, string job) => new()
    {
        Ts = "2026-09-11T03:00:00.0000000+00:00",
        Run = run,
        Operation = Op.Delete,
        Phase = Phase.Apply,
        Result = OpResult.Ok,
        Job = job,
        Src = $@"C:\logs\{job}.log.9",
        Reason = "rotate = 3 exceeded",
    };

    /// <summary>
    /// Writes one day's journal through the real writer, then compresses it exactly as the
    /// maintenance pass does - the planner's destination is the whole file name plus the
    /// extension, and <c>Compressor</c> deletes the source.
    /// </summary>
    private void SeedCompressed(DateTimeOffset day, CompressType type, params CliEvent[] entries)
    {
        // The writer names the file from its own clock, so the older day needs its own.
        var then = new FakeTimeProvider(day);
        var path = Path.Combine(_dir.FullName, $"journal-{day:yyyy-MM-dd}.ndjson");

        using (var writer = JournalWriter.Open(_dir.FullName, then, runId: entries[0].Run))
        {
            foreach (var entry in entries)
            {
                writer.Write(entry);
            }
        }

        File.Exists(path).ShouldBeTrue("the fixture must produce the file the maintenance pass sees");

        Compressor.Compress(path, type);

        File.Exists(path).ShouldBeFalse("the maintenance pass deletes the original, and so does this");
    }

    [Theory]
    [InlineData(CompressType.Zip)]
    [InlineData(CompressType.Gzip)]
    public void ACompressedJournalIsStillReadable(CompressType type)
    {
        SeedCompressed(_clock.GetUtcNow().AddDays(-1), type, Entry("01OLD", "yesterday"));

        // And today's, uncompressed, so the assertion cannot pass by reading nothing at all.
        using (var today = JournalWriter.Open(_dir.FullName, _clock, runId: "01NEW"))
        {
            today.Write(Entry("01NEW", "today"));
        }

        var reader = new JournalReader(_dir.FullName);
        var jobs = reader.Read().Select(e => e.Job).ToArray();

        jobs.ShouldContain("today", "the fixture has to produce something readable either way");
        jobs.ShouldContain("yesterday", "a compressed journal is still a journal");

        reader.SkippedLines.ShouldBe(0, "nothing here is torn");
    }
}
