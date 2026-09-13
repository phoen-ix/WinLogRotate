using Shouldly;
using WinLogRotate.Core.Secrets;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// What a sender is told when a stored credential cannot be reached.
/// </summary>
/// <remarks>
/// The resolver said "the secret store needs Windows" whenever no protector could be made. On
/// Windows that happens for an unelevated caller - the key protecting the store grants SYSTEM and
/// Administrators only - and the sentence sent them looking for a build that had it.
/// </remarks>
public sealed class SecretResolverTests
{
    /// <summary>A Windows that will not hand this account the key.</summary>
    private sealed class KeyNotReadable : ISecretPlatform
    {
        public bool IsSupported => true;

        public bool IsElevated => false;

        public string? EntropyId => null;

        public string? MachineFingerprint => null;

        public string? CurrentUserSid => null;

        public IByteProtector? CreateProtector(bool provision) => null;

        public void Harden(string temporaryPath, string? ownerSid) { }

        public SecretsProtection Verify(string path, string? ownerSid) => SecretsProtection.Unknown;

        public bool RepairedKeyProtection => false;
    }

    [Fact]
    public void AKeyThisAccountCannotReadIsSaidAsSuch()
    {
        var resolution = new SecretResolver(new KeyNotReadable(), "/nowhere/secrets.dat")
            .Resolve(SecretRef.Parse("@secret:relay", 1, 1));

        resolution.Ok.ShouldBeFalse();
        resolution.Error.ShouldNotBeNull().ShouldContain("administrator rights");
        resolution.Error.ShouldNotContain("Windows");
    }

    [Fact]
    public void OffWindowsTheStoreStillSaysItNeedsWindows()
    {
        var resolution = new SecretResolver(new UnsupportedSecretPlatform(), "/nowhere/secrets.dat")
            .Resolve(SecretRef.Parse("@secret:relay", 1, 1));

        resolution.Ok.ShouldBeFalse();
        resolution.Error.ShouldNotBeNull().ShouldContain("Windows");
    }
}
