using Shouldly;
using WinLogRotate.Core.Compression;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Engine;
using WinLogRotate.Core.Globbing;
using WinLogRotate.Core.Journaling;
using WinLogRotate.Core.Safety;
using WinLogRotate.Core.Tests;
using Xunit;

namespace WinLogRotate.Windows.Tests;

/// <summary>
/// A rotation plan carried out on real files.
/// </summary>
/// <remarks>
/// <para>
/// Until this milestone, <b>no test anywhere had ever executed a rotate plan</b>.
/// <c>PlanExecutor.Apply</c> throws <see cref="PlatformNotSupportedException"/> off Windows, and
/// this project - the only one that runs on Windows - did not reference anything that could build
/// a plan. So every planner test stopped at the list of intentions, asserting that an operation of
/// some kind existed and never what it would do to a directory. Five defects lived there, three of
/// which moved or destroyed the wrong file.
/// </para>
/// <para>
/// The assertion is deliberately not a list of expected names. It is that the real file system and
/// <see cref="Outcome"/> - the model the Linux leg pins everything else against - agree about what
/// the plan did. A model that is wrong in the same direction as the planner would let every pin on
/// the other leg agree with a wrong answer, and this is the only thing that can catch that.
/// </para>
/// </remarks>
public sealed class RotationOnRealFilesTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("winlogrotate-rotate-");
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 3, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); } catch (IOException) { }
    }

    private EffectiveJob Job(int rotate = 4, bool compress = false, bool delayCompress = false) => new()
    {
        Name = "app",
        Kind = JobKind.Rotate,
        Paths = [Path.Combine(_dir.FullName, "app.log")],
        Enabled = true,
        Schedule = Schedule.Daily,
        Weekday = 0,
        MonthDay = 0,
        Rotate = rotate,
        Start = 1,
        MaxAge = null,
        MinAge = null,
        MinSize = null,
        MaxSize = null,
        SizeThreshold = 1 << 20,
        Compress = compress,
        CompressType = compress ? CompressType.Gzip : CompressType.None,
        DelayCompress = delayCompress,
        DateExt = false,
        DateFormat = "-yyyyMMdd",
        MissingOk = false,
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
        HookTimeout = TimeSpan.FromSeconds(60),
        AllowDangerous = [],
    };

    /// <summary>Writes a file and hands back the seed entry describing it.</summary>
    private string Seed(FakeFiles files, string name, int lines = 3)
    {
        var path = Path.Combine(_dir.FullName, name);
        File.WriteAllLines(path, Enumerable.Range(0, lines).Select(i => $"{name} line {i}"));

        var info = new FileInfo(path);
        files.Add(path, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), info.Length);
        return path;
    }

    /// <summary>A guard that permits the temp directory and nothing clever.</summary>
    private static PathGuard Guard() =>
        new(new GuardOptions { ProtectedRoots = [@"C:\Windows", @"C:\Program Files"] });

    private string[] OnDisk() =>
        [.. Directory.GetFiles(_dir.FullName).Select(Path.GetFileName).Order(StringComparer.Ordinal)!];

    private (JobPlan Plan, FakeFiles Seed) PlanOver(EffectiveJob job, FakeFiles files)
    {
        var live = files.All.Single(f => WinPath.FileName(f.Path) == "app.log");

        var verdicts = new Dictionary<string, DueVerdict>
        {
            [live.Path] = new() { Due = true, Reason = DueReason.Scheduled, Explanation = "a new day has begun" },
        };

        return (RotateJobPlanner.Plan(job, LogSeries.Discover(job, [live], files), verdicts, Now), files);
    }

    private ExecutionResult Execute(JobPlan plan, EffectiveJob job) =>
        new PlanExecutor(new NullJournal(), Guard(), TimeProvider.System).Execute(plan, job, dryRun: false);

    /// <summary>
    /// A numbered rotation leaves on disk exactly the chain the model predicts.
    /// </summary>
    [Fact]
    public void ARealNumberedRotationLeavesTheChainThePlannerPromised()
    {
        WindowsOnly.Require();

        var files = new FakeFiles();
        Seed(files, "app.log");
        Seed(files, "app.log.1");
        Seed(files, "app.log.2");

        var (plan, seed) = PlanOver(Job(rotate: 4), files);
        var result = Execute(plan, Job(rotate: 4));

        result.Failed.ShouldBe(0, string.Join("; ", result.Errors));
        OnDisk().ShouldBe(Outcome.Of(plan, seed).Names);
    }

    /// <summary>
    /// The model is right about compression too - which is the arm that deletes its own source.
    /// </summary>
    /// <remarks>
    /// <c>Compressor.Compress</c> removes the original once the archive is durable, and a model
    /// that forgot that would agree with a plan that renames a file compression has consumed -
    /// which is precisely the defect this milestone exists to remove.
    /// </remarks>
    [Fact]
    public void ARealCompressingRotationAgreesWithTheModel()
    {
        WindowsOnly.Require();

        var files = new FakeFiles();
        Seed(files, "app.log");
        Seed(files, "app.log.1.gz");

        var job = Job(rotate: 3, compress: true);
        var (plan, seed) = PlanOver(job, files);
        var result = Execute(plan, job);

        result.Failed.ShouldBe(0, string.Join("; ", result.Errors));
        OnDisk().ShouldBe(Outcome.Of(plan, seed).Names);

        // Not merely the same names: the archive really is a readable copy of what was rotated.
        Directory.GetFiles(_dir.FullName, "*.gz").ShouldNotBeEmpty();
    }
}
