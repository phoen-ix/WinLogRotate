using Microsoft.Extensions.Time.Testing;
using Shouldly;
using WinLogRotate.Contracts;
using WinLogRotate.Core.Engine;
using WinLogRotate.Core.Hooks;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// The bracket's own rules: timeouts, the run deadline, dry runs, and the boundary that stops a
/// hook host taking the rotation down with it.
/// </summary>
/// <remarks>
/// All of it runs on the Linux leg. <see cref="HookBracketTests"/> asks the one question this
/// cannot - whether <see cref="RotationRunner"/> actually calls any of it - and needs Windows to
/// resolve a path, so these two are read together.
/// </remarks>
public sealed class HookRunnerTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 10, 3, 0, 0, TimeSpan.Zero));
    private readonly RecordingJournal _journal = new();

    private const string Reload = @"command:C:\tools\reload.exe --now";

    private HookRunner Runner(IHookHost? host, HookGate? gate = null) =>
        new(_journal, host, gate ?? HookGate.Open, _clock);

    private HookStageResult Run(
        IHookHost? host, TimeSpan timeout, TimeSpan? deadline = null,
        DateTimeOffset? started = null, bool dryRun = false, string entry = Reload) =>
        Runner(host).Run(
            "app", HookStage.PostRotate, [entry], timeout, dryRun,
            started ?? _clock.GetUtcNow(), deadline);

    // ---- the timeout -------------------------------------------------------------------------

    [Fact]
    public void TheConfiguredTimeoutIsWhatTheHostIsGiven()
    {
        var host = new FakeHookHost();

        Run(host, TimeSpan.FromSeconds(30));

        host.LastTimeout.ShouldBe(TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// A hook never gets more time than the run itself has left.
    /// </summary>
    /// <remarks>
    /// Without the clamp a sixty-second hook at minute fifty-nine of a one-hour task reaches
    /// ExecutionTimeLimit, and Task Scheduler reports that as 0x41306 - which is indistinguishable
    /// from an operator pressing Stop. The rotation succeeded and the only machine-readable
    /// evidence of it says it was killed.
    /// </remarks>
    [Fact]
    public void AHookCannotOutliveTheRunDeadline()
    {
        var started = _clock.GetUtcNow();
        _clock.Advance(TimeSpan.FromMinutes(59));

        var host = new FakeHookHost();

        Run(host, TimeSpan.FromSeconds(60), TimeSpan.FromHours(1), started);

        // Fifty-five seconds of the hour remain, less the five-second reserve.
        host.LastTimeout.ShouldBe(TimeSpan.FromSeconds(55));
    }

    [Fact]
    public void ADeadlineWithRoomToSpareDoesNotShortenTheHook()
    {
        var started = _clock.GetUtcNow();
        _clock.Advance(TimeSpan.FromMinutes(5));

        var host = new FakeHookHost();
        Run(host, TimeSpan.FromSeconds(30), TimeSpan.FromHours(1), started);

        host.LastTimeout.ShouldBe(TimeSpan.FromSeconds(30));
    }

    /// <summary>A run with no time left runs no hook, and says why.</summary>
    [Fact]
    public void AHookIsNotStartedWhenTheDeadlineIsSpent()
    {
        var started = _clock.GetUtcNow();
        _clock.Advance(TimeSpan.FromHours(1));

        var host = new FakeHookHost();
        var result = Run(host, TimeSpan.FromSeconds(30), TimeSpan.FromHours(1), started);

        host.Ran.ShouldBeEmpty();
        result.Ok.ShouldBeFalse();
        result.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(DiagnosticCode.HookFailed);
        result.Diagnostics[0].Message.ShouldContain("no time left");
    }

    /// <summary>A hand-run rotation is never truncated, because nothing is going to kill it.</summary>
    [Fact]
    public void NoDeadlineMeansNoClamp()
    {
        var host = new FakeHookHost();

        Run(host, TimeSpan.FromMinutes(10), deadline: null);

        host.LastTimeout.ShouldBe(TimeSpan.FromMinutes(10));
    }

    // ---- dry run -----------------------------------------------------------------------------

    /// <summary>A dry run records what it would do and does none of it.</summary>
    [Fact]
    public void ADryRunPlansTheHookWithoutRunningIt()
    {
        var host = new FakeHookHost();

        Run(host, TimeSpan.FromSeconds(30), dryRun: true).Ok.ShouldBeTrue();

        host.Ran.ShouldBeEmpty();
        _journal.Entries.ShouldHaveSingleItem().Phase.ShouldBe(Phase.Plan);
    }

    // ---- what reaches the journal ------------------------------------------------------------

    /// <summary>
    /// Both halves, so a crash between them leaves a readable record.
    /// </summary>
    /// <remarks>
    /// The same contract every destructive operation follows, and the first <c>hook</c> records the
    /// journal has ever held: <c>NotifyPhase</c>'s two Op.Hook emitters write to the CLI output
    /// sink, after the journal has already been closed.
    /// </remarks>
    [Fact]
    public void AHookIsJournalledTwice()
    {
        Run(new FakeHookHost(), TimeSpan.FromSeconds(30));

        _journal.Entries.Count.ShouldBe(2);
        _journal.Entries.ShouldAllBe(e => e.Operation == Op.Hook);
        _journal.Entries[0].Phase.ShouldBe(Phase.Plan);
        _journal.Entries[1].Phase.ShouldBe(Phase.Apply);
        _journal.Entries[1].Result.ShouldBe(OpResult.Ok);

        // Attributed to the job and named by the stage, which is what tells a hook record apart
        // from a notification one when both say "hook".
        _journal.Entries.ShouldAllBe(e => e.Job == "app");
        _journal.Entries[0].Reason.ShouldBe("postrotate");
    }

    [Fact]
    public void AFailedHookIsJournalledAsFailed()
    {
        Run(new FakeHookHost(HookResult.Failed), TimeSpan.FromSeconds(30));

        _journal.Entries[1].Result.ShouldBe(OpResult.Failed);
        _journal.Entries[1].Error.ShouldNotBeNull().ShouldContain("exited 1");
    }

    /// <summary>A refused hook is never journalled as an operation, because none happened.</summary>
    [Fact]
    public void ARefusedHookIsReportedButNotJournalled()
    {
        var result = Runner(new FakeHookHost(), HookGate.Shut("conf.d is loose")).Run(
            "app", HookStage.PostRotate, [Reload], TimeSpan.FromSeconds(30), dryRun: false,
            _clock.GetUtcNow(), null);

        result.Failed.ShouldBe(1);
        result.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(DiagnosticCode.HookRefused);
        _journal.Entries.ShouldBeEmpty();
    }

    // ---- the boundary ------------------------------------------------------------------------

    /// <summary>
    /// A hook host that throws is converted, not propagated.
    /// </summary>
    /// <remarks>
    /// IHookHost says it never throws; this is what makes that true rather than merely asked for.
    /// Nothing above here has a catch - not RotationRunner, not RunCommand, not Program - so an
    /// exception escaping would end the process with the journal's run.end record never written.
    /// </remarks>
    [Fact]
    public void AnExceptionFromTheHostBecomesAFailure()
    {
        var host = new FakeHookHost { Throw = new InvalidOperationException("boom") };

        var result = Run(host, TimeSpan.FromSeconds(30));

        result.Ok.ShouldBeFalse();
        result.Diagnostics.ShouldHaveSingleItem().Message.ShouldContain("boom");
        _journal.Entries[1].Result.ShouldBe(OpResult.Failed);
    }

    /// <summary>No host at all is a refusal to run, not a silent success.</summary>
    /// <remarks>
    /// The Linux leg, and anywhere else a caller built a runner without one. Reporting success
    /// would be the exact shape of defect this milestone exists to remove: a hook that appears to
    /// work and does nothing.
    /// </remarks>
    [Fact]
    public void NoHostMeansTheHookDidNotRun()
    {
        var result = Run(host: null, TimeSpan.FromSeconds(30));

        result.Ok.ShouldBeFalse();
        result.Diagnostics.ShouldHaveSingleItem().Message.ShouldContain("no hook host");
    }

    // ---- nothing configured ------------------------------------------------------------------

    [Fact]
    public void AJobWithNoHooksCostsNothing()
    {
        var result = Runner(null).Run(
            "app", HookStage.PreRotate, [], TimeSpan.FromSeconds(30), dryRun: false,
            _clock.GetUtcNow(), null);

        result.Ok.ShouldBeTrue();
        result.Failed.ShouldBe(0);
        _journal.Entries.ShouldBeEmpty();
    }

    /// <summary>Every hook in a stage is attempted, so one failure does not hide the next.</summary>
    [Fact]
    public void OneFailingHookDoesNotStopTheOthersInItsStage()
    {
        var host = new FakeHookHost(HookResult.Failed, HookResult.Ok);

        var result = Runner(host).Run(
            "app", HookStage.PostRotate, [Reload, @"command:C:\tools\other.exe"],
            TimeSpan.FromSeconds(30), dryRun: false, _clock.GetUtcNow(), null);

        host.Ran.Count.ShouldBe(2);
        result.Failed.ShouldBe(1);
    }

    // ---- the wiring ---------------------------------------------------------------------------

    /// <summary>
    /// <see cref="RotationRunner"/> still holds a bracket.
    /// </summary>
    /// <remarks>
    /// Structural rather than behavioural, and deliberately so: the tests that prove the bracket
    /// actually fires need Windows path resolution, so on the leg that runs on every push this is
    /// the only thing standing between "hooks work" and milestone 12 all over again - a complete,
    /// well-tested subsystem with no caller.
    /// </remarks>
    [Fact]
    public void TheRotationRunnerStillHasAHookBracket()
    {
        typeof(RotationRunner)
            .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .ShouldContain(f => f.FieldType == typeof(HookRunner));

        typeof(RotationRunner).GetConstructors()
            .ShouldContain(c => c.GetParameters().Any(p => p.ParameterType == typeof(IHookHost)));
    }
}
