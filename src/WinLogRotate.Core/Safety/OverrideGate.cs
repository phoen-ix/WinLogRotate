namespace WinLogRotate.Core.Safety;

/// <summary>
/// Whether this run may honour the dangerous-path overrides its configuration names.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately shaped after <c>HookGate</c>, because it answers the same question about the same
/// directory: may what is written in <c>conf.d</c> be trusted to relax a safety rule? An override
/// and a hook are the two things a configuration file can say that reach past validation into
/// what this process is willing to do, and <c>LR9001</c> has always promised operators that a
/// writable <c>conf.d</c> refuses both. Hooks honoured that promise; overrides had nothing to
/// honour, because nothing had ever asked.
/// </para>
/// <para>
/// Taken at a different moment from the hook gate, and that is the point rather than an oversight.
/// This one qualifies <b>the read</b> - "is the directory these overrides came from one a
/// non-administrator can write?" - so it belongs immediately before the configuration is loaded,
/// against the permissions the file had when it was read. The hook gate qualifies <b>the
/// execution</b> - "may this run start a process now?" - and is taken an hour later for the reason
/// its own remarks give. One verdict cannot do both jobs, and reading a directory ACL twice costs
/// nothing against a run that walks the disk.
/// </para>
/// </remarks>
public sealed record OverrideGate
{
    public required bool Honoured { get; init; }

    /// <summary>Why not, as a sentence. Null when overrides are honoured.</summary>
    public string? Reason { get; init; }

    /// <summary>How to make overrides work, or null where there is nothing to repair.</summary>
    public string? Remedy { get; init; }

    /// <summary>Overrides in this configuration may be honoured.</summary>
    public static OverrideGate Open { get; } = new() { Honoured = true };

    /// <summary>Overrides are ignored, for the stated reason.</summary>
    public static OverrideGate Shut(string reason, string? remedy = null) =>
        new() { Honoured = false, Reason = reason, Remedy = remedy };

    /// <summary>
    /// Overrides are ignored because nothing has established that they may be honoured.
    /// </summary>
    /// <remarks>
    /// The default wherever a caller has not supplied a verdict. Refusing by default is the only
    /// safe direction, for the reason <c>HookGate.Unknown</c> gives: a gate that opens when nobody
    /// asked is indistinguishable from no gate at all. The cost of getting this backwards is that
    /// a local user who can write one file in conf.d can have a SYSTEM-privileged process delete
    /// from a protected location, which is the escalation LR9001 exists to describe.
    /// </remarks>
    public static OverrideGate Unknown { get; } = new()
    {
        Honoured = false,
        Reason = "this run has not established that the configuration directory is safe to read "
               + "an override from",
        Remedy = "Run 'winlogrotate doctor' to see what the configuration directory's permissions are.",
    };
}
