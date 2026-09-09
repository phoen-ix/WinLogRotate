using System.Runtime.Versioning;
using System.Security.Cryptography;
using WinLogRotate.Core.Secrets;

namespace WinLogRotate.Hosting.Security;

/// <summary>
/// DPAPI in machine scope, with per-install entropy.
/// </summary>
/// <remarks>
/// <para>
/// Machine scope, not user scope, because there are two principals: the GUI writes secrets as
/// the interactive user and the scheduled task reads them as LocalSystem. Anything keyed on the
/// caller's identity - <see cref="DataProtectionScope.CurrentUser"/>, Windows Credential
/// Manager - cannot serve both, which is a fact about the deployment rather than a preference.
/// </para>
/// <para>
/// <strong>What this does and does not buy.</strong> Machine-scope DPAPI is <em>binding</em>,
/// not access control: anything running on this machine can ask Windows to decrypt, and the
/// entropy narrows that to anything that can read an administrators-only registry key. It is
/// not protection from a local administrator, who can already drop a job file carrying a hook
/// that runs as SYSTEM. What it protects against is the file <em>leaving</em> the machine -
/// support tickets, screenshots, backup agents, deployment repositories - where a copied
/// <c>secrets.dat</c> is inert.
/// </para>
/// <para>
/// This type exists rather than calling <see cref="ProtectedData"/> directly because that class
/// carries no <c>[SupportedOSPlatform]</c> attribute. Without one, CA1416 stays silent and the
/// only signal is a PlatformNotSupportedException at runtime, on a test suite that mostly runs
/// on Linux. Annotating our own wrapper puts the analyzer back in play.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class DpapiMachineProtector : IByteProtector
{
    private readonly byte[] _entropy;

    private DpapiMachineProtector(byte[] entropy) => _entropy = entropy;

    public string Scheme => ProtectionScheme.DpapiLocalMachine;

    /// <summary>
    /// Creates a protector, or returns null when one cannot exist here.
    /// </summary>
    /// <param name="provision">
    /// True to create the entropy if it is missing, which needs administrator. Readers pass
    /// false: a non-elevated caller must fail to decrypt, never quietly mint a new key and
    /// thereby strand every secret already stored under the old one.
    /// </param>
    public static DpapiMachineProtector? TryCreate(bool provision = false) =>
        TryCreate(provision, out _);

    /// <param name="state">
    /// What had to be done to the entropy key. A caller that provisions should report
    /// <see cref="EntropyKeyState.Rehardened"/> rather than swallowing it: the key having been
    /// readable by ordinary users is worth a line in the journal, not silence.
    /// </param>
    public static DpapiMachineProtector? TryCreate(bool provision, out EntropyKeyState state)
    {
        state = EntropyKeyState.Absent;

        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            byte[]? entropy;
            if (provision)
            {
                entropy = MachineEntropy.ReadOrCreate(out state);
            }
            else
            {
                entropy = MachineEntropy.Read();

                // A reader never provisions and never repairs - repairing needs write access it
                // is not entitled to - but it can still say what it saw.
                state = entropy is null ? EntropyKeyState.Absent : EntropyKeyState.Sound;
            }

            return entropy is null ? null : new DpapiMachineProtector(entropy);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    public byte[] Protect(ReadOnlySpan<byte> plaintext) =>
        ProtectedData.Protect(plaintext.ToArray(), _entropy, DataProtectionScope.LocalMachine);

    public byte[] Unprotect(ReadOnlySpan<byte> ciphertext) =>
        ProtectedData.Unprotect(ciphertext.ToArray(), _entropy, DataProtectionScope.LocalMachine);

    /// <summary>The fingerprint recorded in the store, so a key change can be diagnosed.</summary>
    public string EntropyId => MachineEntropy.Fingerprint(_entropy);
}
