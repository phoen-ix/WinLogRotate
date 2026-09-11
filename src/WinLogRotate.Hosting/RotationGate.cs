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

    /// <summary>
    /// What the gate grants, and therefore all it may ever ask for.
    /// </summary>
    /// <remarks>
    /// One constant for both sides, because the two disagreeing is the whole of the defect this
    /// replaced. The DACL granted exactly these rights and the create asked for
    /// <c>MUTEX_ALL_ACCESS</c>, which the DACL grants to nobody - not even the process that had
    /// just created it. So the first caller succeeded and every concurrent second caller was
    /// refused by the lock whose entire purpose is to be taken by a second caller.
    /// </remarks>
    private const MutexRights Rights = MutexRights.Synchronize | MutexRights.Modify;

    private readonly Mutex _mutex;
    private readonly bool _held;

    private RotationGate(Mutex mutex, bool held, GateOutcome outcome, bool createdNew)
    {
        _mutex = mutex;
        _held = held;
        Outcome = outcome;
        CreatedNew = createdNew;
    }

    public GateOutcome Outcome { get; }

    public bool Entered => Outcome != GateOutcome.Busy;

    /// <summary>
    /// Whether this process brought the gate into existence, rather than joining one already
    /// there.
    /// </summary>
    /// <remarks>
    /// Not needed to rotate - it is here because it is the one observable that separates opening
    /// an existing gate from creating a second one, and a fix that widened the DACL instead
    /// would look identical without it.
    /// </remarks>
    public bool CreatedNew { get; }

    /// <summary>Tries to enter the gate.</summary>
    /// <param name="wait">How long to wait; <see cref="TimeSpan.Zero"/> to fail immediately.</param>
    public static RotationGate Enter(TimeSpan wait) => Enter(MutexName, wait);

    /// <summary>
    /// The same, under a name a test may choose.
    /// </summary>
    /// <remarks>
    /// A machine-wide kernel object is a process-wide global of the most literal kind: the suite
    /// that drives the run verb and the suite that tests this class are separate processes, so no
    /// test-framework collection can keep them apart. The same reasoning gave
    /// <c>TextOutputSink</c> an injectable writer. The production name stays pinned against the
    /// installer by <c>TheRotationMutexIsGlobalAndNamed</c>.
    /// </remarks>
    internal static RotationGate Enter(string name, TimeSpan wait)
    {
        var (mutex, createdNew) = OpenOrCreate(name);

        try
        {
            return mutex.WaitOne(wait, exitContext: false)
                ? new RotationGate(mutex, true, GateOutcome.Acquired, createdNew)
                : new RotationGate(mutex, false, GateOutcome.Busy, createdNew);
        }
        catch (AbandonedMutexException)
        {
            // The previous holder was killed mid-rotation. We now own the mutex, and the caller
            // needs to know: there may be a half-renamed file or an uncompressed archive to
            // reconcile before carrying on.
            return new RotationGate(mutex, true, GateOutcome.AcquiredAfterAbandon, createdNew);
        }
        catch
        {
            // Unfiltered on purpose. A filter here is a list of exception types somebody has to
            // keep complete, and failing to keep one complete is the defect being fixed one
            // level up; what matters is that the handle does not outlive the attempt.
            mutex.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Joins the gate if it exists, and creates it if it does not.
    /// </summary>
    /// <remarks>
    /// Opening first, and asking for exactly <see cref="Rights"/>, because creating asks for full
    /// control - which this DACL deliberately grants nobody. The order also matters for the case
    /// the gate exists for: the scheduled task runs as SYSTEM and creates it, and the user's GUI
    /// then joins it, so the common path is the open and not the create.
    /// </remarks>
    private static (Mutex Mutex, bool CreatedNew) OpenOrCreate(string name)
    {
        if (MutexAcl.TryOpenExisting(name, Rights, out var existing))
        {
            return (existing, false);
        }

        try
        {
            return (MutexAcl.Create(initiallyOwned: false, name, out var createdNew, Descriptor()), createdNew);
        }
        catch (UnauthorizedAccessException)
        {
            // Somebody created it between the open and the create - the very race this method
            // exists to survive. The object is there now, so join it.
            return (MutexAcl.OpenExisting(name, Rights), false);
        }
    }

    /// <summary>
    /// Everyone may synchronise on it, and do nothing else to it.
    /// </summary>
    /// <remarks>
    /// The mutex protects file operations, not secrets, and the alternative - a default DACL -
    /// means the SYSTEM task and the user's GUI create two mutexes that cannot see each other,
    /// which defeats the entire purpose. Full control is withheld from Everyone just as
    /// deliberately: it carries <c>ChangePermissions</c> and <c>TakeOwnership</c>, so granting it
    /// would let any local account re-ACL the one thing standing between two rotations and the
    /// same files.
    /// </remarks>
    private static MutexSecurity Descriptor()
    {
        var security = new MutexSecurity();

        security.AddAccessRule(new MutexAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            Rights,
            AccessControlType.Allow));

        return security;
    }

    public void Dispose()
    {
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
