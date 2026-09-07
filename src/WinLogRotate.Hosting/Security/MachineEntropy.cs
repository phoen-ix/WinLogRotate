using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace WinLogRotate.Hosting.Security;

/// <summary>
/// The per-install random bytes mixed into every stored secret.
/// </summary>
/// <remarks>
/// <para>
/// DPAPI in machine scope is decryptable by anything running on the machine, so entropy is what
/// narrows that to anything that can read <c>HKLM\SOFTWARE\WinLogRotate</c> - which the same
/// descriptor discipline as the configuration directory restricts to SYSTEM and Administrators.
/// It is not a defence against an administrator, and the documentation says so plainly.
/// </para>
/// <para>
/// It has to be per install and random. Entropy compiled into the binary is in every copy of
/// the binary, and entropy stored beside the ciphertext travels with it - both are theatre.
/// </para>
/// <para>
/// <strong>Losing this value destroys every stored secret, irrecoverably.</strong> The installer
/// must never remove the key on upgrade, only under /PURGEDATA, and
/// <see cref="Fingerprint"/> exists so that losing it produces a sentence rather than a
/// CryptographicException.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class MachineEntropy
{
    public const string KeyPath = @"SOFTWARE\WinLogRotate";
    public const string ValueName = "SecretEntropy";

    private const int Bytes = 32;

    /// <summary>The entropy, or null if it has not been provisioned or cannot be read.</summary>
    public static byte[]? Read()
    {
        using var key = Registry.LocalMachine.OpenSubKey(KeyPath, writable: false);
        return key?.GetValue(ValueName) as byte[] is { Length: Bytes } value ? value : null;
    }

    /// <summary>Reads the entropy, creating it on first use. Needs administrator.</summary>
    public static byte[] ReadOrCreate()
    {
        if (Read() is { } existing)
        {
            return existing;
        }

        var created = RandomNumberGenerator.GetBytes(Bytes);

        using var key = Registry.LocalMachine.CreateSubKey(KeyPath, writable: true);
        key.SetValue(ValueName, created, RegistryValueKind.Binary);
        return created;
    }

    /// <summary>
    /// Eight bytes of SHA-256 over the entropy, hex.
    /// </summary>
    /// <remarks>
    /// Recorded in the secret store so that an undecryptable file can say <em>which</em> thing
    /// went wrong. Not invertible, and 64 bits of a hash over 256 bits of uniform random is not
    /// a search anybody can perform.
    /// </remarks>
    public static string Fingerprint(byte[] entropy) =>
        Convert.ToHexStringLower(SHA256.HashData(entropy).AsSpan(0, 8));

    /// <summary>
    /// A stable, non-invertible identifier for this machine.
    /// </summary>
    /// <remarks>
    /// Derived from the Cryptography MachineGuid, and salted so that the value stored in our
    /// file is not the machine's actual GUID - which is used as an identifier by other software
    /// and has no business being copied into a file that ends up in support bundles.
    /// </remarks>
    public static string? MachineFingerprint()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography", writable: false);
        if (key?.GetValue("MachineGuid") is not string guid || guid.Length == 0)
        {
            return null;
        }

        var salted = Encoding.UTF8.GetBytes("WinLogRotate\0" + guid);
        return Convert.ToHexStringLower(SHA256.HashData(salted).AsSpan(0, 8));
    }
}
