using System.Runtime.Versioning;
using System.Security.Principal;
using WinLogRotate.Core;
using WinLogRotate.Core.Secrets;

namespace WinLogRotate.Hosting.Security;

/// <summary>The real thing: DPAPI, the registry, and file ACLs.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSecretPlatform : ISecretPlatform
{
    /// <summary>
    /// What had to be done to the entropy key on the last <see cref="CreateProtector"/> call.
    /// </summary>
    /// <remarks>
    /// Surfaced rather than swallowed: finding the key readable by ordinary users and tightening
    /// it is worth a line in the journal. A repair nobody is told about is indistinguishable
    /// from a problem that never existed, and the next person to audit the machine deserves the
    /// record.
    /// </remarks>
    public EntropyKeyState LastEntropyState { get; private set; } = EntropyKeyState.Absent;

    public bool RepairedKeyProtection => LastEntropyState == EntropyKeyState.Rehardened;

    public bool IsSupported => true;

    public bool IsElevated => Privilege.IsElevated();

    public string? EntropyId =>
        MachineEntropy.Read() is { } entropy ? MachineEntropy.Fingerprint(entropy) : null;

    public string? MachineFingerprint => MachineEntropy.MachineFingerprint();

    public string? CurrentUserSid
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return identity.User?.Value;
            }
            catch (System.Security.SecurityException)
            {
                return null;
            }
        }
    }

    public IByteProtector? CreateProtector(bool provision)
    {
        var protector = DpapiMachineProtector.TryCreate(provision, out var state);
        LastEntropyState = state;
        return protector;
    }

    public void Harden(string temporaryPath, string? ownerSid) =>
        SecretsFileGuard.Apply(temporaryPath, ownerSid);

    public SecretsProtection Verify(string path, string? ownerSid) =>
        SecretsFileGuard.Verify(path, ownerSid).Verdict switch
        {
            SecretsAclVerdict.Hardened => SecretsProtection.Hardened,
            SecretsAclVerdict.Missing => SecretsProtection.Missing,
            SecretsAclVerdict.TooOpen => SecretsProtection.TooOpen,
            _ => SecretsProtection.Unknown,
        };
}

/// <summary>Picks the implementation for the machine this is running on.</summary>
public static class SecretPlatform
{
    public static ISecretPlatform ForThisMachine() =>
        OperatingSystem.IsWindows() ? new WindowsSecretPlatform() : new UnsupportedSecretPlatform();
}
