namespace WinLogRotate.Hosting.Security;

/// <summary>
/// Whether one security descriptor lets somebody who is not an administrator change what the
/// run host will execute.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not marked <c>[SupportedOSPlatform("windows")]</c>, the same choice
/// <see cref="AclMask"/> makes and for the same reason, scaled up from one mask to a whole
/// descriptor. The judgement is arithmetic over SIDs and access masks; a rule only windows-2025
/// can ever execute is a rule nobody keeps, and the directory-only gate this replaces survived
/// six milestones behind exactly that attribute.
/// </para>
/// <para>
/// Everything platform-shaped is reduced away before it arrives here: an
/// <see cref="Ace"/> is a SID, a mask, and the sentence to print if it turns out to be the
/// problem. Translating a SID to <c>DOMAIN\name</c> is a lookup that can fail, so it is done by
/// whoever read the descriptor and carried along as text.
/// </para>
/// </remarks>
internal static class AclJudgement
{
    /// <summary>One allow entry, reduced to the two facts that decide anything.</summary>
    /// <param name="Describe">
    /// How to name this entry to an operator - the account, the SID, and what it was granted.
    /// Prepared by the reader, because <c>IdentityReference.Translate</c> is a directory lookup
    /// and this type does no I/O.
    /// </param>
    internal readonly record struct Ace(string Sid, int Mask, string Describe);

    /// <summary>A path's descriptor, reduced to what a verdict is a function of.</summary>
    /// <param name="IsDirectory">
    /// Decides whether protection is required. A directory must sever inheritance or it
    /// re-inherits ProgramData's permissive entries the moment anyone edits them. A job file is
    /// meant to inherit from a conf.d that has already severed it - see
    /// <c>ConfDirGuard.Apply</c> - so demanding <c>D:P</c> per file would refuse precisely the
    /// state the repair leaves behind.
    /// </param>
    internal readonly record struct Subject(
        string Path,
        bool IsDirectory,
        string? OwnerSid,
        string OwnerDescribe,
        IReadOnlyList<Ace> Allow,
        bool Protected);

    /// <summary>What this descriptor is, and which entries said so.</summary>
    internal static (AclVerdict Verdict, IReadOnlyList<string> Offending) Judge(
        Subject subject, IReadOnlySet<string> trusted)
    {
        // An owner can rewrite the DACL whenever it likes, so a non-admin owner is a write grant
        // wearing a disguise - and on a file it is the grant that repairing the parent cannot
        // take away, because rewriting a directory's DACL changes neither a child's explicit
        // entries nor a child's owner.
        if (subject.OwnerSid is { } owner && !trusted.Contains(owner))
        {
            return (AclVerdict.LooseOwner, [$"owner: {subject.OwnerDescribe}"]);
        }

        // CREATOR OWNER is the specific ProgramData hole: it silently grants full control over
        // whatever a user creates, so a dropped job file is theirs to keep editing.
        var offending = subject.Allow
            .Where(ace => AclMask.GrantsWrite(ace.Mask) && !trusted.Contains(ace.Sid))
            .Select(ace => ace.Describe)
            .ToList();

        if (offending.Count > 0)
        {
            return (AclVerdict.LooseWritable, offending);
        }

        if (subject.IsDirectory && !subject.Protected)
        {
            return (AclVerdict.Inherited, []);
        }

        return (AclVerdict.Hardened, []);
    }
}
