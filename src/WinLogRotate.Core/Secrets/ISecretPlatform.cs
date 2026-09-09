namespace WinLogRotate.Core.Secrets;

/// <summary>Whether the secret store on disk is actually protected.</summary>
/// <remarks>
/// A Core-side mirror of the Hosting verdict, because Core cannot reference Hosting, plus
/// <see cref="NotApplicable"/> for the platforms where the question is meaningless.
/// </remarks>
public enum SecretsProtection
{
    /// <summary>SYSTEM and Administrators - and, for a per-user install, its owner.</summary>
    Hardened,

    /// <summary>There is no file yet.</summary>
    Missing,

    /// <summary>Readable by principals that should not read it.</summary>
    TooOpen,

    /// <summary>The descriptor could not be read from this account, which is expected unelevated.</summary>
    Unknown,

    /// <summary>Not Windows. There is nothing to check and nothing is claimed.</summary>
    NotApplicable,
}

/// <summary>
/// Everything the secret verbs need from the operating system.
/// </summary>
/// <remarks>
/// <para>
/// The same move as <see cref="IByteProtector"/>, one level up. That interface put the store's
/// format and its whole failure matrix on the Linux CI leg; this one does the same for the
/// commands, which additionally need entropy, a machine fingerprint, an elevation check and
/// ACL hardening - every one of them Windows-only. Without it the argument handling, the
/// refusal paths and the diagnostics could only be exercised on a Windows runner.
/// </para>
/// <para>
/// <see cref="IsSupported"/> exists so a verb can refuse politely rather than throwing
/// <see cref="PlatformNotSupportedException"/> at somebody who ran it on the wrong machine.
/// </para>
/// </remarks>
public interface ISecretPlatform
{
    /// <summary>False off Windows. Every verb refuses instead of throwing.</summary>
    bool IsSupported { get; }

    bool IsElevated { get; }

    /// <summary>Fingerprint of this machine's entropy, recorded in the store.</summary>
    string? EntropyId { get; }

    /// <summary>Fingerprint of this machine, so a copied store is diagnosed as copied.</summary>
    string? MachineFingerprint { get; }

    /// <summary>The current user's SID, for a per-user installation's descriptor.</summary>
    string? CurrentUserSid { get; }

    /// <summary>
    /// Creates a protector.
    /// </summary>
    /// <param name="provision">
    /// True only for a writer. A reader must pass false: quietly minting a new key would strand
    /// every secret already stored under the old one, and it needs write access it is not
    /// entitled to.
    /// </param>
    IByteProtector? CreateProtector(bool provision);

    /// <summary>Applies the store's descriptor. Called on the temporary file, before the move.</summary>
    void Harden(string temporaryPath, string? ownerSid);

    /// <summary>Reports whether the file on disk is actually protected.</summary>
    SecretsProtection Verify(string path, string? ownerSid);

    /// <summary>
    /// True if the last <see cref="CreateProtector"/> call found the key protecting the store
    /// reachable by the wrong accounts and tightened it.
    /// </summary>
    /// <remarks>
    /// Phrased as "repaired something", not "re-hardened a registry key", so an implementation
    /// is only obliged to answer for itself rather than to have an opinion about a Windows
    /// concept. It is on the interface at all because a repair nobody is told about is
    /// indistinguishable from a problem that never existed - the caller reports it and the
    /// journal records it.
    /// </remarks>
    bool RepairedKeyProtection { get; }
}

/// <summary>
/// The platform where secrets cannot be stored.
/// </summary>
/// <remarks>
/// Shipping code, not test scaffolding - the same seam as <c>NullJournal</c>. It is what every
/// non-Windows caller gets, so a verb run on Linux prints one clear sentence instead of a stack
/// trace, and the tests that exercise the refusal paths use exactly what ships.
/// </remarks>
public sealed class UnsupportedSecretPlatform : ISecretPlatform
{
    public bool IsSupported => false;

    public bool IsElevated => false;

    public string? EntropyId => null;

    public string? MachineFingerprint => null;

    public string? CurrentUserSid => null;

    public IByteProtector? CreateProtector(bool provision) => null;

    public void Harden(string temporaryPath, string? ownerSid)
    {
        // Nothing to protect against here, and pretending otherwise would be a lie the caller
        // would then repeat to the operator.
    }

    public SecretsProtection Verify(string path, string? ownerSid) => SecretsProtection.NotApplicable;

    public bool RepairedKeyProtection => false;
}
