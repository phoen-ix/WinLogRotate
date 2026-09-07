using Microsoft.Extensions.Time.Testing;
using Shouldly;
using WinLogRotate.Core.Secrets;
using WinLogRotate.Hosting.Security;
using Xunit;

namespace WinLogRotate.Windows.Tests;

/// <summary>
/// The half of the secret store that cannot be tested anywhere else: real DPAPI, real
/// registry entropy, real ACLs.
/// </summary>
/// <remarks>
/// Everything about the store's format, versioning and failure handling is covered on the Linux
/// leg behind IByteProtector. What is left here is exactly the part where reality can disagree
/// with the design - which is the only reason this project targets net10.0-windows.
/// </remarks>
[Collection("windows-secrets")]
public sealed class SecretsTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("wlr-win-secret-");
    private readonly FakeTimeProvider _clock =
        new(new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero));

    private string Path => System.IO.Path.Combine(_dir.FullName, "secrets.dat");

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// Provisioning entropy writes to HKLM, which needs administrator. CI runs elevated; a
    /// developer's machine may not, and a test that silently passes because it could not run is
    /// worse than one that says it was skipped.
    /// </summary>
    private static DpapiMachineProtector RequireProtector()
    {
        var protector = DpapiMachineProtector.TryCreate(provision: true);
        Assert.SkipWhen(protector is null,
            "needs administrator: the per-install entropy lives in HKLM\\SOFTWARE\\WinLogRotate");
        return protector!;
    }

    [Fact]
    public void ASecretRoundTripsThroughRealDpapi()
    {
        WindowsOnly.Require();

        var protector = RequireProtector();

        SecretStore.Load(Path, protector, protector.EntropyId, MachineEntropy.MachineFingerprint())
            .Set("ses-smtp", SecretString.From("hunter2-correct-horse"), "test", _clock)
            .Save(_clock);

        var store = SecretStore.Load(
            Path, protector, protector.EntropyId, MachineEntropy.MachineFingerprint());

        store.Status.ShouldBe(SecretStoreStatus.Ok);
        store.TryGet("ses-smtp", out var value, out var error).ShouldBeTrue();
        error.ShouldBeNull();
        value.Reveal().ShouldBe("hunter2-correct-horse");
    }

    [Fact]
    public void TheCiphertextIsNotTheValue()
    {
        WindowsOnly.Require();

        var protector = RequireProtector();

        SecretStore.Load(Path, protector)
            .Set("k", SecretString.From("super-secret-value"), null, _clock)
            .Save(_clock);

        File.ReadAllText(Path).ShouldNotContain("super-secret-value");
        File.ReadAllBytes(Path).ShouldNotContain((byte)0);
    }

    [Fact]
    public void ACiphertextEncryptedUnderDifferentEntropyWillNotDecrypt()
    {
        WindowsOnly.Require();

        // What losing HKLM\SOFTWARE\WinLogRotate actually does. The store reports it as a
        // sentence rather than as CryptographicException, which is the whole point of
        // recording an entropy fingerprint.
        var protector = RequireProtector();

        SecretStore.Load(Path, protector, protector.EntropyId, "same-machine")
            .Set("k", SecretString.From("x"), null, _clock)
            .Save(_clock);

        var store = SecretStore.Load(Path, protector, "0000000000000000", "same-machine");

        store.Status.ShouldBe(SecretStoreStatus.KeyChanged);
        store.Detail.ShouldNotBeNull().ShouldContain("machine key has changed");
    }

    [Fact]
    public void EntropyIsStableAcrossCalls()
    {
        WindowsOnly.Require();

        var a = RequireProtector().EntropyId;
        var b = RequireProtector().EntropyId;

        // ReadOrCreate must not mint a new key on every call. If it did, every secret stored
        // would be unreadable by the very next process.
        a.ShouldBe(b);
    }

    [Fact]
    public void AReaderNeverProvisionsEntropy()
    {
        WindowsOnly.Require();

        // The reader path passes provision: false on purpose. A non-elevated caller quietly
        // minting a new key would strand every secret already stored under the old one.
        RequireProtector();
        DpapiMachineProtector.TryCreate(provision: false).ShouldNotBeNull();
    }

    [Fact]
    public void TheMachineFingerprintIsNotTheMachineGuid()
    {
        WindowsOnly.Require();

        var fingerprint = MachineEntropy.MachineFingerprint().ShouldNotBeNull();

        using var key = Microsoft.Win32.Registry.LocalMachine
            .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography", writable: false);
        var guid = key?.GetValue("MachineGuid") as string;

        // The GUID identifies the machine to other software and has no business being copied
        // verbatim into a file that ends up in support bundles.
        fingerprint.ShouldNotBe(guid);
        fingerprint.Length.ShouldBe(16);
    }
}
