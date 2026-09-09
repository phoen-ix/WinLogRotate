using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace WinLogRotate.Hosting.Security;

/// <summary>What was found, or had to be done, to the entropy key.</summary>
public enum EntropyKeyState
{
    /// <summary>Already present and readable only by SYSTEM and Administrators.</summary>
    Sound,

    /// <summary>Created just now, with the right descriptor.</summary>
    Created,

    /// <summary>Found readable by someone else, and tightened.</summary>
    Rehardened,

    /// <summary>Found readable by someone else and could not be tightened.</summary>
    StillOpen,

    /// <summary>Not present, and not created because this caller only wanted to read.</summary>
    Absent,
}

/// <summary>
/// The per-install random bytes mixed into every stored secret.
/// </summary>
/// <remarks>
/// <para>
/// DPAPI in machine scope is decryptable by anything running on the machine, so entropy is what
/// narrows that to anything that can read <c>HKLM\SOFTWARE\WinLogRotate</c>. That sentence is
/// only true if the key actually carries <see cref="Sddl.EntropyKey"/> - and for one release it
/// did not: the key was created with <c>CreateSubKey</c> and no descriptor, so it inherited
/// <c>HKLM\SOFTWARE</c>, where <c>BUILTIN\Users</c> has read. Entropy every local user can read
/// is not entropy. Hence <see cref="Harden"/>, and hence the check on every read rather than
/// only on create.
/// </para>
/// <para>
/// It has to be per install and random. Entropy compiled into the binary is in every copy of the
/// binary, and entropy stored beside the ciphertext travels with it - both are theatre.
/// </para>
/// <para>
/// <strong>Losing this value destroys every stored secret, irrecoverably.</strong> The installer
/// must never remove the key on upgrade, only under /PURGEDATA, and <see cref="Fingerprint"/>
/// exists so that losing it produces a sentence rather than a CryptographicException.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class MachineEntropy
{
    public const string KeyPath = @"SOFTWARE\WinLogRotate";
    public const string ValueName = "SecretEntropy";

    private const int Bytes = 32;

    /// <summary>The entropy, or null if it has not been provisioned or cannot be read.</summary>
    public static byte[]? Read(string keyPath = KeyPath)
    {
        using var key = Registry.LocalMachine.OpenSubKey(keyPath, writable: false);
        return key?.GetValue(ValueName) as byte[] is { Length: Bytes } value ? value : null;
    }

    /// <summary>
    /// Reads the entropy, creating it on first use, and makes sure the key is not readable by
    /// anyone but SYSTEM and Administrators. Needs administrator.
    /// </summary>
    /// <param name="state">What had to be done, so the caller can journal a re-hardening.</param>
    public static byte[] ReadOrCreate(out EntropyKeyState state, string keyPath = KeyPath)
    {
        if (Read(keyPath) is { } existing)
        {
            state = Harden(keyPath, onlyIfLoose: true);
            return existing;
        }

        var created = RandomNumberGenerator.GetBytes(Bytes);

        using (var key = Registry.LocalMachine.CreateSubKey(keyPath, writable: true))
        {
            key.SetValue(ValueName, created, RegistryValueKind.Binary);
        }

        // Applied after creation rather than through a RegistrySecurity passed to CreateSubKey:
        // the overload taking one is not available on every target, and this way the same code
        // path repairs an existing key.
        state = Harden(keyPath, onlyIfLoose: false) switch
        {
            EntropyKeyState.StillOpen => EntropyKeyState.StillOpen,
            _ => EntropyKeyState.Created,
        };

        return created;
    }

    /// <summary>
    /// Applies <see cref="Sddl.EntropyKey"/>, optionally only when the key is currently readable
    /// by someone who is not an administrator.
    /// </summary>
    public static EntropyKeyState Harden(string keyPath = KeyPath, bool onlyIfLoose = true)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                keyPath,
                RegistryKeyPermissionCheck.ReadWriteSubTree,
                RegistryRights.ReadKey | RegistryRights.ChangePermissions | RegistryRights.TakeOwnership);

            if (key is null)
            {
                return EntropyKeyState.Absent;
            }

            if (onlyIfLoose && !IsLoose(key))
            {
                return EntropyKeyState.Sound;
            }

            var security = new RegistrySecurity();
            security.SetSecurityDescriptorSddlForm(Sddl.EntropyKey);

            // Stated through the API as well as through D:P in the SDDL. Whether the protected
            // flag survives the managed persist path is not something to take on trust when the
            // consequence is a key that keeps inheriting HKLM\SOFTWARE's read-for-Users entry.
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            key.SetAccessControl(security);

            return onlyIfLoose ? EntropyKeyState.Rehardened : EntropyKeyState.Sound;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return EntropyKeyState.StillOpen;
        }
    }

    /// <summary>True if anyone but SYSTEM and Administrators is granted anything.</summary>
    /// <remarks>
    /// We own this key completely, so the right question is not "does BUILTIN\Users appear" but
    /// "does anything unexpected appear". A named account with read is just as much a disclosure
    /// as the group is, and checking a list of well-known SIDs would miss it.
    /// </remarks>
    private static bool IsLoose(RegistryKey key)
    {
        var security = key.GetAccessControl(AccessControlSections.Access);

        foreach (RegistryAccessRule rule in
                 security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow)
            {
                continue;
            }

            var sid = rule.IdentityReference.Value;
            if (sid != Sddl.WellKnown.LocalSystem && sid != Sddl.WellKnown.Administrators)
            {
                return true;
            }
        }

        return false;
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
