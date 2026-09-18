using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace WinLogRotate.Hosting;

/// <summary>
/// How often the gate tries to open its mutex before deciding that it cannot.
/// </summary>
/// <remarks>
/// <para>
/// Two things make <see cref="WaitHandleCannotBeOpenedException"/> escape the open. The name is
/// held by a kernel object that is not a mutex - a squatter, or a collision - so the create is
/// refused; or the holder of the real mutex closed its last handle between this process finding
/// it and joining it, so the object vanished under the join. The second is a race and one more
/// attempt wins it; the first is not, and no number of attempts changes it. Both used to end the
/// run with exit 4 and <c>LR1006</c>, "a defect in the product", from the one place the run's
/// containment does not reach.
/// </para>
/// <para>
/// Pure, so the policy - how many times, how long between - is pinned on the Linux leg without a
/// kernel object. A handful of attempts a tenth of a second apart is far more than the race
/// needs, and short enough that a squatter costs the run half a second rather than its slot.
/// </para>
/// </remarks>
internal static class GateOpenRetry
{
    /// <summary>How many times the open is attempted before the gate is given up as unopenable.</summary>
    public const int Attempts = 5;

    /// <summary>How long the gate waits between two attempts.</summary>
    public static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Runs <paramref name="attempt"/> until it stops throwing
    /// <see cref="WaitHandleCannotBeOpenedException"/>, at most <see cref="Attempts"/> times,
    /// pausing between attempts and never after the last.
    /// </summary>
    /// <returns>False when every attempt threw. Any other exception passes straight through.</returns>
    public static bool Try<T>(Func<T> attempt, Action<TimeSpan> pause, [MaybeNullWhen(false)] out T result)
    {
        for (var n = 1; ; n++)
        {
            try
            {
                result = attempt();
                return true;
            }
            catch (WaitHandleCannotBeOpenedException) when (n < Attempts)
            {
                pause(Pause);
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                result = default;
                return false;
            }
        }
    }
}
