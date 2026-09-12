using Microsoft.Extensions.Time.Testing;
using Shouldly;
using WinLogRotate.Contracts;
using WinLogRotate.Core;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Engine;
using WinLogRotate.Core.Globbing;
using WinLogRotate.Core.Journaling;
using WinLogRotate.Core.Safety;
using WinLogRotate.Core.State;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// A run writes down what it did, even when some of it failed.
/// </summary>
/// <remarks>
/// <c>docs/logrotate-compatibility.md</c> lists this among the behaviours reproduced exactly:
/// "Exit codes 0, 1, 3 - unchanged, and 1 still writes state - a failing job must not make the
/// healthy ones re-rotate forever." <c>RotationRunner</c> says the same in its own words. Nothing
/// asserted it, which the rule added alongside this test is what found.
/// </remarks>
public sealed class RunStateTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("winlogrotate-runstate-");
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 3, 1, 2, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); } catch (IOException) { }
    }

    private string StatePath => Path.Combine(_dir.FullName, "state.json");

    /// <summary>A run with a failure in it still records the clocks it advanced.</summary>
    /// <remarks>
    /// The alternative is the re-rotation storm: one permanently broken job, and every healthy log
    /// on the machine rotates again on every run because nothing was written down. That is
    /// upstream's reasoning too, and it is why exit 1 is not exit 2.
    /// </remarks>
    [Fact]
    public void AFailedRunStillWritesItsState()
    {
        var state = StateStore.Load(StatePath, out _);

        // A path the guard refuses, so the run has a genuine failure in it and no Win32 is
        // reached on this leg.
        var report = new RotationRunner(
                new NullJournal(), new PathGuard(new GuardOptions { ProtectedRoots = ["C:\\Windows"] }),
                state, _clock, new NoArchives(), null, null, null, new NoFiles())
            .Run(
                new LoadedConfig
                {
                    Jobs = [Fixtures.Job() with { Kind = JobKind.Rotate, Paths = [@"C:\Windows\*.log"] }],
                    Diagnostics = [],
                    Paths = InstallPaths.Resolve(_dir.FullName),
                    Quarantined = [],
                },
                new RunOptions { DryRun = false });

        report.Failed.ShouldBeGreaterThan(0, "the fixture has to actually fail for this to mean anything");
        report.ExitCode.ShouldBe(ExitCode.Errors);

        File.Exists(StatePath).ShouldBeTrue(
            "a failing job must not make the healthy ones re-rotate for ever");
    }

    /// <summary>A dry run writes nothing, which is the other half of the same promise.</summary>
    [Fact]
    public void ADryRunWritesNoState()
    {
        var state = StateStore.Load(StatePath, out _);

        new RotationRunner(
                new NullJournal(), new PathGuard(new GuardOptions()),
                state, _clock, new NoArchives(), null, null, null, new NoFiles())
            .Run(
                new LoadedConfig
                {
                    Jobs = [Fixtures.Job() with { Kind = JobKind.Rotate, Paths = [@"C:\logs\app.log"] }],
                    Diagnostics = [],
                    Paths = InstallPaths.Resolve(_dir.FullName),
                    Quarantined = [],
                },
                new RunOptions { DryRun = true, Force = true });

        File.Exists(StatePath).ShouldBeFalse("a dry run changes nothing, state included");
    }

    private sealed class NoArchives : IArchiveSource
    {
        public MatchedFile? Find(string path) => null;

        public IReadOnlyList<MatchedFile> Glob(string pattern) => [];
    }

    private sealed class NoFiles : IFileSource
    {
        public EnumerationResult Resolve(string pattern) => new() { Files = [], Refusals = [] };
    }
}
