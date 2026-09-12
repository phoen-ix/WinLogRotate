using Shouldly;
using WinLogRotate.Cli.Commands;
using WinLogRotate.Contracts;
using WinLogRotate.Core;
using WinLogRotate.Core.Engine;
using WinLogRotate.Core.State;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// A rotation gate held for longer than any rotation stops reporting success.
/// </summary>
/// <remarks>
/// <para>
/// <c>Global\WinLogRotate.Rotation</c> grants <c>Everyone</c> the right to synchronise on it,
/// and that cannot be narrowed: <c>SYNCHRONIZE</c> is the right to wait, a satisfied wait is
/// ownership, and there is no right that grants one without the other. Remove the entry and the
/// SYSTEM task and an unelevated GUI create two mutexes that cannot see each other - two
/// rotations over the same files. Narrow it to administrators and per-user and portable installs
/// stop working, because there the legitimate rotator is not one.
/// </para>
/// <para>
/// So any local account can hold it for ever, and that is not what is being fixed. A local
/// account can also stop a rotation by holding a log file open, which the product reports as
/// <c>LR3002</c>. What is being fixed is the silence: every refused run returned the scheduled
/// task's <c>--lock-held-exit</c>, so Task Scheduler's Last Run Result - the column an
/// administrator actually reads - said <c>0x0</c> every night for a week while nothing rotated.
/// </para>
/// <para>
/// Two pins, because the fix has two halves and neither covers the other. Revert the threshold
/// and the exit code is wired to a rule that never fires; revert the exit code and the rule
/// fires into nothing.
/// </para>
/// </remarks>
public sealed class GateHoldTests
{
    private static readonly DateTimeOffset Night = new(2026, 9, 12, 3, 0, 0, TimeSpan.Zero);

    /// <summary>A hold outliving every run that could have caused it is not a rotation.</summary>
    /// <remarks>
    /// Four hours is chosen against the scheduled task rather than guessed:
    /// <c>ExecutionTimeLimit</c> bounds a run, and Task Scheduler reports <c>0x41306</c> when it
    /// bites. The boundary is asserted from both sides so the threshold cannot drift into
    /// something that never fires or fires on a long rotation of a large tree.
    /// </remarks>
    [Fact]
    public void AGateHeldLongerThanAnyRotationStopsBeingAnOverlap()
    {
        GateHoldRule.Judge(Night, Night).ShouldBe(GateHold.Overlapping);

        GateHoldRule.Judge(Night, Night + GateHoldRule.Implausible - TimeSpan.FromSeconds(1))
            .ShouldBe(GateHold.Overlapping, "a rotation of a very large tree is still a rotation");

        GateHoldRule.Judge(Night, Night + GateHoldRule.Implausible)
            .ShouldBe(GateHold.Implausible);

        GateHoldRule.Judge(Night, Night + TimeSpan.FromDays(7))
            .ShouldBe(GateHold.Implausible);

        // Nothing on record is not evidence of a short hold. It is the first refusal, or a
        // record this machine is not entitled to believe.
        GateHoldRule.Judge(null, Night).ShouldBe(GateHold.Unknown);
    }

    /// <summary>
    /// An implausible hold does not borrow the scheduled task's zero exit code.
    /// </summary>
    /// <remarks>
    /// <c>--lock-held-exit 0</c> exists so that an overlapping manual run does not paint Last Run
    /// Result red, and <c>RunLockOptions.HeldExitCode</c> argues for that in its own words. It
    /// was never meant to cover a machine on which nothing rotates at all. The overlap row is
    /// asserted beside it, because a fix that simply stopped honouring the option would break
    /// the case the option was added for.
    /// </remarks>
    [Fact]
    public void AnImplausibleHoldDoesNotBorrowTheScheduledTasksZeroExitCode()
    {
        var (held, heldExit) = GateRefusal.For(GateHold.Implausible, Night, heldExitCode: 0);

        heldExit.ShouldBe(ExitCode.Errors);
        held.Severity.ShouldBe(Severity.Error);
        held.Code.ShouldBe(DiagnosticCode.RotationGateHeld);
        held.Message.ShouldContain("2026-09-12 03:00:00Z");

        var (overlap, overlapExit) = GateRefusal.For(GateHold.Overlapping, Night, heldExitCode: 0);

        overlapExit.ShouldBe(0, "an overlapping manual run is why --lock-held-exit exists");
        overlap.Severity.ShouldBe(Severity.Info);
        overlap.Code.ShouldBe(DiagnosticCode.AlreadyRunning);

        // And with no record at all, which is a first refusal: today's behaviour exactly.
        GateRefusal.For(GateHold.Unknown, null, heldExitCode: 0).ExitCode.ShouldBe(0);
    }

    /// <summary>
    /// A record in a directory anyone can write is not evidence.
    /// </summary>
    /// <remarks>
    /// The account this record exists to detect is a local one. Until this milestone nothing
    /// created <c>run\</c> at all, so it appeared lazily under whatever ProgramData handed out -
    /// and an attacker who owns <c>gate.json</c> can hold the first refusal forward for ever, so
    /// the span never reaches four hours and the judgement never fires. The repair and the
    /// installer now create and harden it, and this is the belt: an untrusted store reads as
    /// nothing on record.
    /// </remarks>
    [Fact]
    public void ARecordInADirectoryAnyoneCanWriteIsNotEvidence()
    {
        var dir = Directory.CreateTempSubdirectory("winlogrotate-gate-");

        try
        {
            GateHoldStore.Record(dir.FullName, Night, trusted: true);

            GateHoldStore.Read(dir.FullName, trusted: true).ShouldBe(Night);
            GateHoldStore.Read(dir.FullName, trusted: false).ShouldBeNull();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>The first refusal is the one kept, and taking the gate forgets it.</summary>
    [Fact]
    public void TheFirstRefusalIsTheOneKept()
    {
        var dir = Directory.CreateTempSubdirectory("winlogrotate-gate-");

        try
        {
            GateHoldStore.Record(dir.FullName, Night, trusted: true);
            GateHoldStore.Record(dir.FullName, Night.AddHours(6), trusted: true);

            // The later refusal must not move the clock forward. If it did, the span would reset
            // on every run and could never reach the threshold - the rule would be unreachable
            // by construction, which is the shape of defect this milestone keeps finding.
            GateHoldStore.Read(dir.FullName, trusted: true).ShouldBe(Night);

            GateHoldStore.Clear(dir.FullName);
            GateHoldStore.Read(dir.FullName, trusted: true).ShouldBeNull();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>A run that takes the gate leaves no record behind at all.</summary>
    /// <remarks>
    /// The common path, and worth pinning: clearing a record that is not there must not create
    /// the file, or every successful rotation on every machine writes one.
    /// </remarks>
    [Fact]
    public void ClearingNothingWritesNothing()
    {
        var dir = Directory.CreateTempSubdirectory("winlogrotate-gate-");

        try
        {
            var run = Path.Combine(dir.FullName, "run");

            GateHoldStore.Clear(run);

            Directory.Exists(run).ShouldBeFalse("a successful run has nothing to record");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Doctor answers "why is nothing rotating" when the answer is the gate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The question has two answers and doctor only ever gave one - that nothing is registered
    /// to run rotations. The other is that something is registered, runs every night, and is
    /// turned away at the gate; until now only <c>run</c> observed that, and <c>run</c> is the
    /// verb whose output an operator does not read.
    /// </para>
    /// <para>
    /// Both the free row and the overlapping row are asserted beside the held one. A verdict
    /// that reported every state as a fault would be no more use than one that reported none,
    /// and an overlapping run is the ordinary case on a machine somebody is working on.
    /// </para>
    /// </remarks>
    [Fact]
    public void AGateHeldTooLongIsReportedAsAFaultAndAnOverlapIsNot()
    {
        var (held, heldExpected) = DoctorCommand.GateVerdict(GateHold.Implausible, Night);

        heldExpected.ShouldBeFalse();
        held.ShouldContain("nothing has rotated");
        held.ShouldContain("2026-09-12 03:00:00Z");

        var (overlap, overlapExpected) = DoctorCommand.GateVerdict(GateHold.Overlapping, Night);

        overlapExpected.ShouldBeTrue("a rotation running is what the gate is for");
        overlap.ShouldContain("a rotation is running");

        var (free, freeExpected) = DoctorCommand.GateVerdict(GateHold.Unknown, null);

        freeExpected.ShouldBeTrue();
        free.ShouldBe("free");
    }
}
