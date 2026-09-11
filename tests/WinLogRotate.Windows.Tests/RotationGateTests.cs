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

        using var second = RotationGate.Enter(name, TimeSpan.Zero);

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
        using var second = RotationGate.Enter(name, TimeSpan.Zero);

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
        using var waiter = MutexAcl.OpenExisting(name, MutexRights.Synchronize | MutexRights.Modify);
        waiter.WaitOne(TimeSpan.Zero).ShouldBeFalse("the gate is held");
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
