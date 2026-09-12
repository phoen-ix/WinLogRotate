using Microsoft.Extensions.Time.Testing;
using Shouldly;
using WinLogRotate.Contracts;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Engine;
using WinLogRotate.Core.Journaling;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// The journal must not leak. A log rotator that grows its own history without limit is the
/// least defensible bug this product could ship.
/// </summary>
public sealed class JournalMaintenanceTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("winlogrotate-jm-");
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
            // Best effort.
        }
    }

    /// <summary>Writes a journal file dated <paramref name="daysOld"/> days ago.</summary>
    private string Seed(int daysOld, int lines = 20)
    {
        var date = _clock.GetUtcNow().AddDays(-daysOld);
        var path = Path.Combine(_dir.FullName, $"journal-{date:yyyy-MM-dd}.ndjson");

        var content = string.Join(Environment.NewLine,
            Enumerable.Range(0, lines).Select(i =>
                $$"""{"ts":"{{date:O}}","run":"R","operation":"delete","phase":"apply","src":"C:\\logs\\a{{i}}.log"}"""));

        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, date.UtcDateTime);
        return path;
    }

    private static JournalSettings Settings(
        bool enabled = true, int retain = 30, CompressType compress = CompressType.Zip) =>
        new() { Enabled = enabled, Retain = retain, Compress = compress };

    /// <summary>
    /// The guarantee that makes this safe, and the same one that keeps us off the log IIS is
    /// currently writing: today's journal is being appended to right now.
    /// </summary>
    [Fact]
    public void TodaysJournalIsNeverTouched()
    {
        var today = Seed(0);
        Seed(1);
        Seed(2);

        var plan = JournalMaintenance.PlanFor(_dir.FullName, Settings(), _clock.GetUtcNow());

        plan.Destructive.ShouldNotContain(o => o.Source == today);
        plan.Operations.ShouldContain(o =>
            o.Source == today && o.Action == PlannedAction.Skip && o.Reason.Contains("still writing"));
    }

    [Fact]
    public void OlderJournalsAreCompressed()
    {
        Seed(0);
        Seed(1);
        Seed(2);

        var plan = JournalMaintenance.PlanFor(_dir.FullName, Settings(), _clock.GetUtcNow());

        plan.Operations.Count(o => o.Action == PlannedAction.Compress).ShouldBe(2);
    }

    [Fact]
    public void JournalsBeyondTheRetentionWindowAreDeleted()
    {
        for (var day = 0; day <= 10; day++)
        {
            Seed(day);
        }

        var plan = JournalMaintenance.PlanFor(_dir.FullName, Settings(retain: 3), _clock.GetUtcNow());

        var deleted = plan.Operations.Where(o => o.Action == PlannedAction.Delete).ToArray();
        deleted.ShouldNotBeEmpty();

        // Today is never among them, whatever the retention says.
        deleted.ShouldNotContain(o => o.Source.Contains($"{_clock.GetUtcNow():yyyy-MM-dd}.ndjson"));
    }

    [Fact]
    public void DisablingItLeavesEverythingAlone()
    {
        for (var day = 0; day <= 10; day++)
        {
            Seed(day);
        }

        var before = Directory.GetFiles(_dir.FullName).Length;

        JournalMaintenance.PlanFor(_dir.FullName, Settings(enabled: false), _clock.GetUtcNow())
            .Operations.ShouldBeEmpty();
        JournalMaintenance.Run(_dir.FullName, Settings(enabled: false), _clock)
            .DidAnything.ShouldBeFalse();

        Directory.GetFiles(_dir.FullName).Length.ShouldBe(before);
    }

    [Fact]
    public void CompressionCanBeTurnedOffWhileRetentionStaysOn()
    {
        Seed(0);
        Seed(1);
        Seed(40);

        var plan = JournalMaintenance.PlanFor(
            _dir.FullName, Settings(retain: 30, compress: CompressType.None), _clock.GetUtcNow());

        plan.Operations.ShouldNotContain(o => o.Action == PlannedAction.Compress);
        plan.Operations.Count(o => o.Action == PlannedAction.Delete).ShouldBe(1);   // the 40-day-old one
    }

    [Fact]
    public void AnEmptyOrAbsentDirectoryIsFine()
    {
        JournalMaintenance.PlanFor(_dir.FullName, Settings(), _clock.GetUtcNow())
            .Operations.ShouldBeEmpty();
        JournalMaintenance.PlanFor(Path.Combine(_dir.FullName, "nope"), Settings(), _clock.GetUtcNow())
            .Operations.ShouldBeEmpty();
    }

    /// <summary>
    /// A guard that refuses everything, on every platform.
    /// </summary>
    /// <remarks>
    /// The executor checks where a path really leads, not how it is spelled, so redirecting the
    /// directory into a protected root is enough to have every operation refused. It has to be
    /// done this way rather than by leaning on the Linux leg turning down a Unix path: the pure
    /// suite runs on windows-2025 too, where a temp directory is under no protected root and
    /// every operation would succeed - inverting each assertion below.
    /// </remarks>
    private sealed class Redirect : Io.ILinkResolver
    {
        public Io.LinkTarget Resolve(string path) => Io.LinkTarget.At(@"C:\Windows\System32");
    }

    /// <summary>
    /// A pass in which nothing succeeded reports nothing done.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The numbers reported here are printed to the operator by <c>run</c>, and they counted the
    /// <i>plan</i>: <c>Deleted</c> was the number of deletes the pass intended, so a journal
    /// directory the guard refuses - a portable install under Program Files is the ordinary way
    /// to get one - reported every file as removed while none was. <c>Compressed</c> subtracted
    /// the pass's whole failure count, across every action, from the number of compressions it
    /// planned, so one failed delete could make it negative.
    /// </para>
    /// <para>
    /// The refusal is the point: it is the only state in which the two arithmetic expressions
    /// and the truth diverge, and nothing in this file reached it, because every other fact here
    /// stops at <c>PlanFor</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public void APassInWhichEverythingWasRefusedReportsNothingDone()
    {
        for (var day = 0; day <= 10; day++)
        {
            Seed(day);
        }

        var plan = JournalMaintenance.PlanFor(_dir.FullName, Settings(retain: 3), _clock.GetUtcNow());
        plan.Operations.ShouldContain(o => o.Action == PlannedAction.Compress);
        plan.Operations.ShouldContain(o => o.Action == PlannedAction.Delete);

        var result = JournalMaintenance.Run(_dir.FullName, Settings(retain: 3), _clock, new Redirect());

        result.Errors.ShouldNotBeEmpty("the guard must have refused the pass for this to assert anything");
        result.Compressed.ShouldBe(0);
        result.Deleted.ShouldBe(0);
        result.DidAnything.ShouldBeFalse();

        // Still there, which is what makes the numbers above lies rather than merely odd.
        Directory.GetFiles(_dir.FullName, "journal-*.ndjson").Length.ShouldBe(11);
    }

    /// <summary>
    /// The compressed archives must still be readable, or the history is preserved in name
    /// only.
    /// </summary>
    [Fact]
    public void CompressedJournalsAreStillReadable()
    {
        // Needs the executor, which speaks Windows paths. The decision to compress is covered
        // platform-independently above; this asserts the bytes survive the round trip.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Executes file operations.");

        Seed(0, lines: 3);
        Seed(1, lines: 7);

        JournalMaintenance.Run(_dir.FullName, Settings(), _clock);

        var archive = Directory.GetFiles(_dir.FullName, "*.zip").ShouldHaveSingleItem();
        using var zip = System.IO.Compression.ZipFile.OpenRead(archive);
        using var reader = new StreamReader(zip.Entries.Single().Open());

        reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.ShouldBe(7);
    }

    [Fact]
    public void AVeryBusyDayRollsToANumberedSibling()
    {
        var today = Path.Combine(_dir.FullName, $"journal-{_clock.GetUtcNow():yyyy-MM-dd}.ndjson");
        File.WriteAllText(today, new string('x', 5000));

        using var writer = JournalWriter.Open(_dir.FullName, _clock, maxSize: 1000);

        writer.Path.ShouldNotBe(today);
        writer.Path.ShouldEndWith(".1.ndjson");

        // The date prefix survives, so the maintenance pass still recognises it as that day's.
        Path.GetFileName(writer.Path).ShouldStartWith($"journal-{_clock.GetUtcNow():yyyy-MM-dd}");
    }

    [Fact]
    public void TheRollIsOffByDefault()
    {
        var today = Path.Combine(_dir.FullName, $"journal-{_clock.GetUtcNow():yyyy-MM-dd}.ndjson");
        File.WriteAllText(today, new string('x', 5000));

        using var writer = JournalWriter.Open(_dir.FullName, _clock);
        writer.Path.ShouldBe(today);
    }
}

public class JournalSettingsBindingTests
{
    private static JournalSettings Bind(string toml) =>
        ConfigBinder.BindJournal(TomlFile.Parse(toml, "config.toml"), new DiagnosticBag());

    [Fact]
    public void DefaultsApplyWhenTheTableIsAbsent()
    {
        var settings = Bind("schema = 1\n");

        settings.Enabled.ShouldBeTrue();
        settings.Retain.ShouldBe(30);
        settings.Compress.ShouldBe(CompressType.Zip);
        settings.MaxSize.ShouldBe(50L << 20);
    }

    [Fact]
    public void EverySettingIsConfigurable()
    {
        var settings = Bind("""
            schema = 1

            [journal]
            enabled  = true
            retain   = 7
            compress = "gzip"
            maxsize  = "10M"
            """);

        settings.Retain.ShouldBe(7);
        settings.Compress.ShouldBe(CompressType.Gzip);
        settings.MaxSize.ShouldBe(10L << 20);
    }

    [Fact]
    public void ItCanBeTurnedOffEntirely() =>
        Bind("[journal]\nenabled = false\n").Enabled.ShouldBeFalse();
}
