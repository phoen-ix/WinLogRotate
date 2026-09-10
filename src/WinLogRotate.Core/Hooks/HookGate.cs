namespace WinLogRotate.Core.Hooks;

/// <summary>
/// Whether this run may execute anything a configuration file names.
/// </summary>
/// <remarks>
/// <para>
/// The verdict comes from <c>ConfDirGuard.Verify</c>, which is Windows-only and lives in Hosting -
/// so it is reduced to this before it crosses into Core, the same way <c>SenderTable</c> carries
/// the Event Log transport across the same boundary.
/// </para>
/// <para>
/// Taken <b>once per run, immediately before the first hook is dispatched</b>, and carried from
/// there. Not at configuration load: a run loads its configuration and then rotates for an hour,
/// and the directory's permissions can be changed in between by exactly the person the check
/// exists to stop. Not per hook either, because nothing inside one run can change the answer
/// between two of them, and re-reading an ACL for every hook only adds ways to fail.
/// </para>
/// </remarks>
public sealed record HookGate
{
    public required bool Allowed { get; init; }

    /// <summary>Why not, as a sentence. Null when hooks are allowed.</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// How to make hooks work, or null where there is nothing to repair.
    /// </summary>
    /// <remarks>
    /// Null on a per-user installation. Its configuration directory is writable by its owner by
    /// design, and telling that operator to run icacls against their own AppData folder would be
    /// advice that damages a working installation - <c>AclFinding.ExpectedForScope</c> makes the
    /// same point about the same directory.
    /// </remarks>
    public string? Remedy { get; init; }

    /// <summary>Hooks may run.</summary>
    public static HookGate Open { get; } = new() { Allowed = true };

    /// <summary>Hooks are refused, for the stated reason.</summary>
    public static HookGate Shut(string reason, string? remedy = null) =>
        new() { Allowed = false, Reason = reason, Remedy = remedy };

    /// <summary>
    /// Hooks are refused because nothing has established that they may run.
    /// </summary>
    /// <remarks>
    /// The default wherever a caller has not supplied a verdict - a unit test, a platform with no
    /// ACLs, an engine constructed the four-argument way. Refusing by default is the only safe
    /// direction: a gate that opens when nobody asked is indistinguishable from no gate at all.
    /// </remarks>
    public static HookGate Unknown { get; } = new()
    {
        Allowed = false,
        Reason = "this run has not established that the configuration directory is safe to "
               + "execute from",
        Remedy = "Run 'winlogrotate doctor' to see what the configuration directory's permissions are.",
    };
}
