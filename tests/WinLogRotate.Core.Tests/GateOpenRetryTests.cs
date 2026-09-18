using System.Threading;
using Shouldly;
using WinLogRotate.Hosting;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// The gate's retry when its mutex cannot be opened.
/// </summary>
/// <remarks>
/// A <c>WaitHandleCannotBeOpenedException</c> from the open used to escape <c>run</c> as exit 4
/// and <c>LR1006</c>. It has two causes: the gate's name is held by a kernel object that is not
/// a mutex, which no retry fixes, and the holder vanished between this process finding the gate
/// and joining it, which one more attempt fixes. The policy is pinned here, on the leg with no
/// kernel objects, by counting attempts and pauses; <c>RotationGateTests</c> on the Windows leg
/// drives the real gate into a squatted name.
/// </remarks>
public sealed class GateOpenRetryTests
{
    private static Func<int> FailingUntil(int attempt, Action<int> onCall)
    {
        var calls = 0;
        return () =>
        {
            onCall(++calls);
            return calls >= attempt ? calls : throw new WaitHandleCannotBeOpenedException();
        };
    }

    /// <summary>The race the retry exists for: the gate vanished once and is there again.</summary>
    [Fact]
    public void AGateThatVanishedOnceIsJoinedOnTheNextAttempt()
    {
        var calls = 0;
        var pauses = new List<TimeSpan>();

        GateOpenRetry.Try(FailingUntil(2, n => calls = n), pauses.Add, out var result).ShouldBeTrue();

        result.ShouldBe(2);
        calls.ShouldBe(2);
        pauses.ShouldBe([GateOpenRetry.Pause]);
    }

    /// <summary>A name that is not a mutex never becomes one, and the retry gives up.</summary>
    /// <remarks>
    /// No pause after the last attempt: the caller is about to refuse the run, and there is
    /// nothing left to wait for.
    /// </remarks>
    [Fact]
    public void ANameThatIsNeverAMutexIsGivenUpAfterAHandfulOfAttempts()
    {
        var calls = 0;
        var pauses = 0;

        GateOpenRetry.Try(FailingUntil(int.MaxValue, n => calls = n), _ => pauses++, out _).ShouldBeFalse();

        calls.ShouldBe(GateOpenRetry.Attempts);
        pauses.ShouldBe(GateOpenRetry.Attempts - 1);
    }

    /// <summary>The ordinary case pays nothing.</summary>
    [Fact]
    public void AGateThatOpensFirstTimeIsNotPausedFor()
    {
        var pauses = 0;

        GateOpenRetry.Try(() => 1, _ => pauses++, out var result).ShouldBeTrue();

        result.ShouldBe(1);
        pauses.ShouldBe(0);
    }

    /// <summary>
    /// Only the one exception is retried. A refusal is an answer, and repeating the question
    /// would only delay reporting it.
    /// </summary>
    [Fact]
    public void ARefusalIsNotRetried()
    {
        var calls = 0;

        Should.Throw<UnauthorizedAccessException>(() => GateOpenRetry.Try<int>(
            () => { calls++; throw new UnauthorizedAccessException(); },
            _ => throw new InvalidOperationException("nothing should pause"),
            out _));

        calls.ShouldBe(1);
    }
}
