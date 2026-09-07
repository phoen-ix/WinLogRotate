using Microsoft.Extensions.Time.Testing;
using Shouldly;
using WinLogRotate.Contracts;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Engine;
using WinLogRotate.Core.Journaling;
using WinLogRotate.Core.Safety;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>A journal that keeps everything in memory, so what the executor recorded can be
/// asserted directly.</summary>
internal sealed class RecordingJournal : IJournal
{
    public List<CliEvent> Entries { get; } = [];

    public string RunId => "TESTRUN";

    public void Write(CliEvent entry) => Entries.Add(entry);

    public void Dispose() { }
}

public sealed class PlanExecutorTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("winlogrotate-exec-");
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 7, 3, 0, 0, TimeSpan.Zero));
    private readonly RecordingJournal _journal = new();

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

    private static EffectiveJob Job() => new()
    {
        Name = "iis",
        Kind = JobKind.Manage,
        Paths = ["x"],
        Enabled = true,
        Schedule = Schedule.Daily,
        Weekday = 0,
        MonthDay = 0,
        Rotate = 3,
        Start = 1,
        MaxAge = null,
        MinAge = null,
        MinSize = null,
        MaxSize = null,
        SizeThreshold = 1 << 20,
        Compress = true,
        CompressType = CompressType.Zip,
        DelayCompress = false,
        DateExt = false,
        DateFormat = "-yyyyMMdd",
        MissingOk = true,
        NotIfEmpty = true,
        OldDir = null,
        CreateOldDir = false,
        LockStrategy = LockStrategy.Rename,
        LiveFiles = 1,
        MaxFiles = 1000,
        RetryCount = 5,
        RetryIntervalMs = 100,
        PreRotate = [],
        PostRotate = [],
        AllowDangerous = [],
    };

    private JobPlan PlanDeleting(params string[] names)
    {
        var ops = names.Select(n => new PlannedOp
        {
            Action = PlannedAction.Delete,
            Source = Path.Combine(_dir.FullName, n),
            Reason = "rotate = 3 exceeded",
            Bytes = 1024,
        }).ToArray();

        foreach (var op in ops)
        {
            File.WriteAllText(op.Source, "content");
        }

        return new JobPlan { JobName = "iis", Operations = ops, MatchedFiles = ops.Length };
    }

    /// <summary>
    /// The property that makes --dry-run worth trusting. It is the same code path as a real
    /// run, stopped one step earlier - not a separate description that could drift from what
    /// the executor actually does.
    /// </summary>
    [Fact]
    public void ADryRunChangesNothingOnDisk()
    {
        var plan = PlanDeleting("a.log.gz", "b.log.gz");
        var executor = new PlanExecutor(_journal, new PathGuard(new GuardOptions()), _clock);

        var result = executor.Execute(plan, Job(), dryRun: true);

        result.Completed.ShouldBe(0);
        result.Failed.ShouldBe(0);
        Directory.GetFiles(_dir.FullName).Length.ShouldBe(2);
    }

    /// <summary>
    /// A dry run emits only the "plan" half of each event. A real run emits both, so a crash
    /// between them leaves a record of an intention that was never carried out.
    /// </summary>
    [Fact]
    public void ADryRunJournalsIntentionsOnly()
    {
        var plan = PlanDeleting("a.log.gz", "b.log.gz");
        new PlanExecutor(_journal, new PathGuard(new GuardOptions()), _clock)
            .Execute(plan, Job(), dryRun: true);

        _journal.Entries.ShouldAllBe(e => e.Phase == Phase.Plan);
        _journal.Entries.Count(e => e.Operation == Op.Delete).ShouldBe(2);
    }

    /// <summary>"Which rule condemned this file?" is exactly what an operator asks when a log
    /// they wanted has gone, so every deletion carries its reason.</summary>
    [Fact]
    public void EveryJournalledDeletionCarriesItsReasonAndSize()
    {
        var plan = PlanDeleting("a.log.gz");
        new PlanExecutor(_journal, new PathGuard(new GuardOptions()), _clock)
            .Execute(plan, Job(), dryRun: true);

        var entry = _journal.Entries.Single(e => e.Operation == Op.Delete);
        entry.Reason.ShouldBe("rotate = 3 exceeded");
        entry.BytesBefore.ShouldBe(1024);
        entry.Job.ShouldBe("iis");
    }

    [Fact]
    public void SkippedFilesAreRecordedRatherThanOmitted()
    {
        var plan = new JobPlan
        {
            JobName = "iis",
            MatchedFiles = 1,
            Operations =
            [
                new PlannedOp
                {
                    Action = PlannedAction.Skip,
                    Source = @"C:\logs\live.log",
                    Reason = "newest file - the application is still writing it",
                },
            ],
        };

        var result = new PlanExecutor(_journal, new PathGuard(new GuardOptions()), _clock)
            .Execute(plan, Job(), dryRun: false);

        result.Skipped.ShouldBe(1);
        _journal.Entries.ShouldContain(e => e.Result == OpResult.Skipped);
    }
}
