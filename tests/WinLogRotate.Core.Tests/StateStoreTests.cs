using Microsoft.Extensions.Time.Testing;
using Shouldly;
using WinLogRotate.Core.State;
using Xunit;

namespace WinLogRotate.Core.Tests;

public sealed class StateStoreTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("winlogrotate-state-");
    private readonly FakeTimeProvider _clock =
        new(new DateTimeOffset(2026, 9, 7, 3, 0, 0, TimeSpan.Zero));

    private string StatePath => Path.Combine(_dir.FullName, "state.json");

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

    [Fact]
    public void RoundTrips()
    {
        var store = StateStore.Load(StatePath, out _);
        store.Set(@"C:\logs\app.log", new PathState
        {
            Path = @"C:\logs\app.log",
            LastRotated = _clock.GetUtcNow(),
            Probe = ProbeVerdict.CopyTruncate,
            ProbedAs = ProbeIdentity.Elevated,
            NulFill = NulFillVerdict.Clean,
        });
        store.Save(_clock);

        var reloaded = StateStore.Load(StatePath, out var corrupt);
        corrupt.ShouldBeNull();

        var entry = reloaded.Get(@"C:\logs\app.log").ShouldNotBeNull();
        entry.LastRotated.ShouldBe(_clock.GetUtcNow());
        entry.Probe.ShouldBe(ProbeVerdict.CopyTruncate);
        entry.ProbedAs.ShouldBe(ProbeIdentity.Elevated);
        entry.NulFill.ShouldBe(NulFillVerdict.Clean);
    }

    /// <summary>
    /// Without canonical keying these become two entries with independent clocks, and the same
    /// log rotates twice as often as configured.
    /// </summary>
    [Fact]
    public void CaseAndSeparatorVariantsAreOneEntry()
    {
        var store = StateStore.Load(StatePath, out _);
        store.Set(@"C:\Logs\App.log", new PathState { Path = @"C:\Logs\App.log", LastRotated = _clock.GetUtcNow() });

        store.Get(@"c:\logs\app.log").ShouldNotBeNull();
        store.Get("C:/Logs/App.log").ShouldNotBeNull();
        store.Paths.Count.ShouldBe(1);
    }

    /// <summary>
    /// logrotate's first-run rule, replicated: a log seen for the first time gets a baseline
    /// and is not rotated. Anyone who has deleted a state file and wondered why nothing
    /// happened that night has met this.
    /// </summary>
    [Fact]
    public void AFirstSightingRecordsABaselineAndNothingMore()
    {
        var store = StateStore.Load(StatePath, out _);

        store.RecordFirstSighting(@"C:\logs\app.log", _clock.GetUtcNow()).ShouldBeTrue();
        store.RecordFirstSighting(@"C:\logs\app.log", _clock.GetUtcNow()).ShouldBeFalse();

        var entry = store.Get(@"C:\logs\app.log").ShouldNotBeNull();
        entry.FirstSeen.ShouldBe(_clock.GetUtcNow());
        entry.LastRotated.ShouldBe(_clock.GetUtcNow());
    }

    /// <summary>
    /// The property that makes "switch run host freely" safe. If switching disturbed the
    /// clocks, changing from a Scheduled Task to a Service would silently re-rotate every log.
    /// </summary>
    [Fact]
    public void TheClockFingerprintSurvivesASaveLoadCycle()
    {
        var store = StateStore.Load(StatePath, out _);
        store.Set(@"C:\logs\a.log", new PathState { Path = @"C:\logs\a.log", LastRotated = _clock.GetUtcNow() });
        store.Set(@"C:\logs\b.log", new PathState { Path = @"C:\logs\b.log", LastRotated = _clock.GetUtcNow().AddDays(-3) });
        var before = store.ClockFingerprint();
        store.Save(_clock);

        _clock.Advance(TimeSpan.FromHours(6));

        var reloaded = StateStore.Load(StatePath, out _);
        reloaded.Save(_clock);                       // as a host switch would: rewrite, change nothing
        StateStore.Load(StatePath, out _).ClockFingerprint().ShouldBe(before);
    }

    private static readonly DateTimeOffset Now = new(2026, 9, 11, 3, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Ninety = TimeSpan.FromDays(StateStore.ForgetAfterDays);

    /// <summary>A path no run has seen for a long time is forgotten.</summary>
    /// <remarks>
    /// By age, not by absence. A rotation emits the rename and the recreate as two operations, so
    /// "the file is not there" is a state a run produces about itself - which is why the predicate
    /// this replaced would have been wrong even though it was never called.
    /// </remarks>
    [Fact]
    public void PruningForgetsAPathNotSeenForAWhile()
    {
        var store = StateStore.Load(StatePath, out _);
        store.Set(@"C:\logs\live.log", new PathState { Path = @"C:\logs\live.log", LastSeen = Now });
        store.Set(@"C:\logs\old.log", new PathState
        {
            Path = @"C:\logs\old.log",
            LastSeen = Now - TimeSpan.FromDays(120),
        });

        store.Prune(Now, Ninety).ShouldBe(1);
        store.Paths.Count.ShouldBe(1);
    }

    /// <summary>
    /// A confirmed NUL-fill is never forgotten, however old.
    /// </summary>
    /// <remarks>
    /// The verdict means copytruncate destroyed this log once already. Its enum calls it permanent
    /// and LockChoice preserves it even when the file has gone, because the defect belongs to the
    /// writer rather than to the inode. Ageing it out would re-enable, on a ninety-day timer, the
    /// one operation known to corrupt this path - and rebuilding the verdict requires another
    /// corrupted rotation.
    /// </remarks>
    [Fact]
    public void PruningNeverForgetsAConfirmedNulFill()
    {
        var store = StateStore.Load(StatePath, out _);
        store.Set(@"C:\logs\log4net.log", new PathState
        {
            Path = @"C:\logs\log4net.log",
            LastSeen = Now - TimeSpan.FromDays(400),
            NulFill = NulFillVerdict.Confirmed,
        });

        store.Prune(Now, Ninety).ShouldBe(0);
        store.Get(@"C:\logs\log4net.log").ShouldNotBeNull()
            .NulFill.ShouldBe(NulFillVerdict.Confirmed);
    }

    /// <summary>A row with no timestamps at all is kept, not treated as infinitely old.</summary>
    /// <remarks>
    /// The upgrade trap: a state file written before LastSeen existed must not be emptied by its
    /// first prune on the new build.
    /// </remarks>
    [Fact]
    public void APathWithNoTimestampsIsKept()
    {
        var store = StateStore.Load(StatePath, out _);
        store.Set(@"C:\logs\a.log", new PathState { Path = @"C:\logs\a.log" });

        store.Prune(Now, Ninety).ShouldBe(0);
        store.Paths.Count.ShouldBe(1);
    }

    /// <summary>A file written before LastSeen existed survives its first prune.</summary>
    [Fact]
    public void AStateFileWrittenBeforeLastSeenExistedSurvivesItsFirstPrune()
    {
        // Doubled, because this is JSON inside a raw string: the file on disk must carry \\ so
        // the deserializer reads a single separator.
        File.WriteAllText(StatePath, """
            { "version": 1, "paths": { "c:\\logs\\a.log": {
                "path": "C:\\logs\\a.log", "lastRotated": "2026-09-01T03:00:00+00:00" } } }
            """);

        var store = StateStore.Load(StatePath, out var corrupt);
        corrupt.ShouldBeNull();

        store.Prune(Now, Ninety).ShouldBe(0, "it was rotated well inside the window");
        store.Paths.Count.ShouldBe(1);
    }

    /// <summary>
    /// The document version is still 1.
    /// </summary>
    /// <remarks>
    /// Adding an optional property is not a breaking change. A bump would make an older build
    /// refuse the file outright and re-baseline every log on the machine, which is a real cost to
    /// pay for a field that older builds can simply ignore.
    /// </remarks>
    [Fact]
    public void TheStateDocumentVersionIsStillOne() =>
        new StateDocument().Version.ShouldBe(1);

    /// <summary>
    /// A truncated state file must not stop the tool from running. Starting from a fresh
    /// baseline delays each log by one interval; refusing to run at all lets disks fill.
    /// </summary>
    [Fact]
    public void ACorruptStateFileStartsFreshAndSaysSo()
    {
        File.WriteAllText(StatePath, "{ \"version\": 1, \"paths\": { \"C:\\\\A\": ");

        var store = StateStore.Load(StatePath, out var corrupt);

        corrupt.ShouldNotBeNull();
        store.Paths.ShouldBeEmpty();
    }

    /// <summary>
    /// A file from a future version is refused rather than misread - misreading it means
    /// rotating on the wrong schedule, which is worse than starting from a known baseline.
    /// </summary>
    [Fact]
    public void AFutureVersionIsRefusedRatherThanGuessedAt()
    {
        File.WriteAllText(StatePath, "{\"version\": 99, \"paths\": {}}");

        Should.Throw<InvalidOperationException>(() => StateStore.Load(StatePath, out _))
            .Message.ShouldContain("version 99");
    }

    [Fact]
    public void SavingLeavesNoTemporaryFileBehind()
    {
        var store = StateStore.Load(StatePath, out _);
        store.Set(@"C:\logs\a.log", new PathState { Path = @"C:\logs\a.log" });
        store.Save(_clock);

        File.Exists(StatePath).ShouldBeTrue();
        File.Exists(StatePath + ".tmp").ShouldBeFalse();
    }
}
