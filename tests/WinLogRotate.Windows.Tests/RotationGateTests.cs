using System.Security.AccessControl;
using System.Threading;
using Shouldly;
using WinLogRotate.Hosting;
using Xunit;

namespace WinLogRotate.Windows.Tests;

/// <summary>
/// The machine-wide lock that stops two rotations running over the same files.
/// </summary>
/// <remarks>
/// <para>
/// It had no test at all, and it was broken in exactly the case it exists for. The descriptor
/// granted Everyone Synchronize and Modify; <c>MutexAcl.Create</c> asks for full control, which
/// that grants to nobody - so the first caller created the gate and every concurrent second
/// caller was refused with an UnauthorizedAccessException that nothing in the CLI caught. The
/// scheduled task rotating while an operator presses "Rotate now" is not an edge case; it is the
/// scenario the class was written for.
/// </para>
/// <para>
/// Every test here takes a gate under its own name. A kernel object in the Global namespace is a
/// global of the most literal kind, and the suite that drives the run verb is a separate process
/// from this one, so nothing a test framework offers can keep the two apart.
/// </para>
/// </remarks>
public sealed class RotationGateTests
{
    private static string Name() => $@"Global\WinLogRotate.Rotation.test-{Guid.NewGuid():N}";

    /// <summary>
    /// Takes the gate on a thread of its own, and reports what it found.
    /// </summary>
    /// <remarks>
    /// A Windows mutex is owned by a <i>thread</i>, and the owning thread may re-acquire it as
    /// often as it likes - so two Enter calls in a row on the test's own thread both succeed and
    /// prove nothing. "Another rotation is already running" is a statement about another thread
    /// or another process, and the first version of these tests asserted it from the one place it
    /// could never be true. Everything is read and disposed inside the thread, because releasing
    /// a mutex is also the owning thread's job.
    /// </remarks>
    private static (bool Entered, GateOutcome Outcome, bool CreatedNew) EnterElsewhere(string name)
    {
        (bool Entered, GateOutcome Outcome, bool CreatedNew) seen = default;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                using var gate = RotationGate.Enter(name, TimeSpan.Zero);
                seen = (gate.Entered, gate.Outcome, gate.CreatedNew);
            }
            catch (Exception e)
            {
                failure = e;
            }
        });

        thread.Start();
        thread.Join();

        return failure is null ? seen : throw failure;
    }

    /// <summary>
    /// A second caller joins the gate rather than being refused by it.
    /// </summary>
    /// <remarks>
    /// The defect, exactly. Before the fix this threw
    /// <c>UnauthorizedAccessException: Access to the path 'Global\WinLogRotate.Rotation' is
    /// denied</c> from the second Enter, and nothing between there and Main caught it.
    /// </remarks>
    [Fact]
    public void ASecondCallerOpensTheGateInsteadOfBeingRefused()
    {
        WindowsOnly.Require();

        var name = Name();

        using var first = RotationGate.Enter(name, TimeSpan.Zero);
        first.Entered.ShouldBeTrue();

        var second = EnterElsewhere(name);

        second.Entered.ShouldBeFalse("the first caller holds it");
        second.Outcome.ShouldBe(GateOutcome.Busy);
    }

    /// <summary>
    /// Only the first caller brings the gate into existence.
    /// </summary>
    /// <remarks>
    /// What separates the fix that was made from the one that was rejected. Widening the
    /// descriptor to full control would also let a second caller through - by letting it create a
    /// handle to the existing object - and would leave this product's machine-wide lock re-ACLable
    /// by any local account. This is the assertion that tells the two apart, and it is the only
    /// reader <c>CreatedNew</c> has.
    /// </remarks>
    [Fact]
    public void OnlyTheFirstCallerCreatesIt()
    {
        WindowsOnly.Require();

        var name = Name();

        using var first = RotationGate.Enter(name, TimeSpan.Zero);
        var second = EnterElsewhere(name);

        first.CreatedNew.ShouldBeTrue();
        second.CreatedNew.ShouldBeFalse("it joined the gate that was already there");
    }

    /// <summary>
    /// The gate grants everyone the right to wait on it, and nothing more.
    /// </summary>
    /// <remarks>
    /// Asserted by asking for full control and being refused, which is both the narrowest
    /// available statement of the security decision and the precise condition that caused the
    /// defect: full control is what <c>MutexAcl.Create</c> asks for, and it is what nobody has.
    /// </remarks>
    [Fact]
    public void NobodyMayTakeFullControlOfTheGate()
    {
        WindowsOnly.Require();

        var name = Name();
        using var gate = RotationGate.Enter(name, TimeSpan.Zero);

        Should.Throw<UnauthorizedAccessException>(
            () => MutexAcl.OpenExisting(name, MutexRights.FullControl));

        // And the rights it does grant are enough to wait on it, which is the whole contract.
        // From another thread, because the thread holding a mutex may always re-enter it.
        var waited = false;
        var waiter = new Thread(() =>
        {
            using var opened = MutexAcl.OpenExisting(name, MutexRights.Synchronize | MutexRights.Modify);
            waited = opened.WaitOne(TimeSpan.Zero);
        });

        waiter.Start();
        waiter.Join();

        waited.ShouldBeFalse("the gate is held, so waiting on it must not succeed");
    }

    /// <summary>
    /// A gate that was released can be taken again.
    /// </summary>
    /// <remarks>
    /// The ordinary case - one rotation after another - and the one that proves Dispose releases
    /// rather than merely closing the handle.
    /// </remarks>
    [Fact]
    public void AReleasedGateCanBeTakenAgain()
    {
        WindowsOnly.Require();

        var name = Name();

        using (var first = RotationGate.Enter(name, TimeSpan.Zero))
        {
            first.Outcome.ShouldBe(GateOutcome.Acquired);
        }

        using var second = RotationGate.Enter(name, TimeSpan.Zero);
        second.Outcome.ShouldBe(GateOutcome.Acquired);
    }
}
