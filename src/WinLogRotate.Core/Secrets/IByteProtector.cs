namespace WinLogRotate.Core.Secrets;

/// <summary>
/// Encrypts and decrypts a stored secret.
/// </summary>
/// <remarks>
/// <para>
/// An interface for what is, in the shipping build, exactly one implementation - DPAPI in
/// machine scope. It exists so the store's own logic (its format, its versioning, and every one
/// of its failure modes) is testable on the Linux CI leg, where roughly seventy percent of this
/// suite runs and where <c>ProtectedData</c> throws.
/// </para>
/// <para>
/// It also makes the scheme a recorded property of the file rather than an assumption of the
/// code, so a file written under one scheme is never silently misread under another.
/// </para>
/// </remarks>
public interface IByteProtector
{
    /// <summary>Names this scheme in the file. See <see cref="ProtectionScheme"/>.</summary>
    string Scheme { get; }

    byte[] Protect(ReadOnlySpan<byte> plaintext);

    byte[] Unprotect(ReadOnlySpan<byte> ciphertext);
}

/// <summary>The values <see cref="IByteProtector.Scheme"/> may take.</summary>
public static class ProtectionScheme
{
    /// <summary>
    /// DPAPI, machine scope, with per-install entropy.
    /// </summary>
    /// <remarks>
    /// Machine scope rather than user scope because there are two principals: the GUI writes as
    /// the interactive user and the scheduled task reads as LocalSystem. Anything keyed on the
    /// caller's identity - user-scope DPAPI, Credential Manager - is disqualified by
    /// construction, not by preference.
    /// </remarks>
    public const string DpapiLocalMachine = "dpapi-localmachine";

    /// <summary>Test-only. A shipped build never writes this, and refuses to read it.</summary>
    public const string Test = "test-insecure";
}
