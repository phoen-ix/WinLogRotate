using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace WinLogRotate.Hosting;

/// <summary>Why the gate was or was not entered.</summary>
public enum GateOutcome
{
    /// <summary>We hold it; the run may proceed.</summary>
    Acquired,

    /// <summary>Another run holds it. Not an error - the expected outcome when a manual run
    /// overlaps the scheduled one.</summary>
    Busy,

    /// <summary>Acquired, but the previous holder died without releasing it.</summary>
    AcquiredAfterAbandon,
}

/// <summary>
/// Ensures only one rotation runs at a time on the machine.
/// </summary>
/// <remarks>
/// <para>
/// The name is in the <c>Global\</c> namespace and carries an explicit DACL, and both of those
/// are necessary. The scheduled task runs as SYSTEM in session 0 while the GUI runs in the
/// user's session, so a <c>Local\</c> mutex would be two different objects that never see each
/// other - and a default DACL would leave the SYSTEM-created mutex unopenable by the user
/// session anyway. The result would be two rotations running over the same files, which is the
/// worst bug this product could have.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class RotationGate : IDisposable
{
    /// <summary>See <see cref="Names.RotationMutex"/>, which is where it is pinned against
    /// the installer script.</summary>
    public const string MutexName = Names.RotationMutex;

    private readonly Mutex? _mutex;
    private readonly bool _held;

    private RotationGate(Mutex? mutex, bool held, GateOutcome outcome)
    {
        _mutex = mutex;
        _held = held;
        Outcome = outcome;
    }

    public GateOutcome Outcome { get; }

    public bool Entered => Outcome != GateOutcome.Busy;

    /// <summary>Tries to enter the gate.</summary>
    /// <param name="wait">How long to wait; <see cref="TimeSpan.Zero"/> to fail immediately.</param>
    public static RotationGate Enter(TimeSpan wait)
    {
        var security = new MutexSecurity();

        // Everyone may synchronise on it. The mutex protects file operations, not secrets, and
        // the alternative - a default DACL - means the SYSTEM task and the user's GUI create
        // two mutexes that cannot see each other, which defeats the entire purpose.
        security.AddAccessRule(new MutexAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            MutexRights.Synchronize | MutexRights.Modify,
            AccessControlType.Allow));

        var mutex = MutexAcl.Create(initiallyOwned: false, MutexName, out _, security);

        try
        {
            return mutex.WaitOne(wait, exitContext: false)
                ? new RotationGate(mutex, true, GateOutcome.Acquired)
                : new RotationGate(mutex, false, GateOutcome.Busy);
        }
        catch (AbandonedMutexException)
        {
            // The previous holder was killed mid-rotation. We now own the mutex, and the caller
            // needs to know: there may be a half-renamed file or an uncompressed archive to
            // reconcile before carrying on.
            return new RotationGate(mutex, true, GateOutcome.AcquiredAfterAbandon);
        }
    }

    public void Dispose()
    {
        if (_mutex is null)
        {
            return;
        }

        if (_held)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not owned by this thread - nothing useful to do, and throwing from Dispose
                // would mask whatever real failure is already unwinding.
            }
        }

        _mutex.Dispose();

        // Without this the JIT may collect the mutex while it is still meant to be held,
        // because nothing else references it after the last use. A classic and very quiet bug.
        GC.KeepAlive(_mutex);
    }
}
