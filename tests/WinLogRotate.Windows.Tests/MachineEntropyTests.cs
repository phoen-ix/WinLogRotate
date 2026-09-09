using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using Shouldly;
using WinLogRotate.Hosting.Security;
using Xunit;

namespace WinLogRotate.Windows.Tests;

/// <summary>
/// The entropy key's permissions.
/// </summary>
/// <remarks>
/// For one release <c>Sddl.EntropyKey</c> was declared and never applied: the key was created
/// with CreateSubKey and no descriptor, so it inherited HKLM\SOFTWARE, where BUILTIN\Users has
/// read. Entropy that every local user can read is not entropy, and the doc comment claiming
/// otherwise made it worse. These tests drive a scratch key rather than the product's real one,
/// so running them cannot disturb an installation on the same machine.
/// </remarks>
[Collection("windows-secrets")]
public sealed class MachineEntropyTests : IDisposable
{
    // A root of its own, deliberately NOT a subkey of SOFTWARE\WinLogRotate: creating a child
    // there would bring the product's real key into existence carrying HKLM\SOFTWARE's
    // inherited permissions, which is the very state these tests exist to catch. The GUID keeps
    // concurrent runs apart.
    private readonly string _keyPath = $@"SOFTWARE\WinLogRotate-test-{Guid.NewGuid():N}";

    public void Dispose()
    {
        // Dispose runs even for a skipped test, and Registry.LocalMachine is null off Windows -
        // so without this guard the whole class fails on the Linux leg with a
        // NullReferenceException wrapped around the skip that was working correctly.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            Registry.LocalMachine.DeleteSubKeyTree(_keyPath, throwOnMissingSubKey: false);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Not elevated; nothing was created either.
        }
    }

    private static void RequireAdmin()
    {
        WindowsOnly.Require();
        using var probe = Registry.LocalMachine.OpenSubKey(@"SOFTWARE", writable: true);
        Assert.SkipWhen(probe is null, "needs administrator: this writes under HKLM\\SOFTWARE");
    }

    private IReadOnlyList<string> AllowedSids()
    {
        using var key = Registry.LocalMachine.OpenSubKey(_keyPath, writable: false);
        key.ShouldNotBeNull();

        return [.. key.GetAccessControl(AccessControlSections.Access)
            .GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<RegistryAccessRule>()
            .Where(r => r.AccessControlType == AccessControlType.Allow)
            .Select(r => r.IdentityReference.Value)];
    }

    [Fact]
    public void ACreatedKeyIsNotReadableByOrdinaryUsers()
    {
        RequireAdmin();

        MachineEntropy.ReadOrCreate(out var state, _keyPath);

        state.ShouldBe(EntropyKeyState.Created);

        var sids = AllowedSids();
        sids.ShouldNotContain(Sddl.WellKnown.Users);
        sids.ShouldContain(Sddl.WellKnown.LocalSystem);
        sids.ShouldContain(Sddl.WellKnown.Administrators);
    }

    [Fact]
    public void InheritanceIsSevered()
    {
        RequireAdmin();

        MachineEntropy.ReadOrCreate(out _, _keyPath);

        using var key = Registry.LocalMachine.OpenSubKey(_keyPath, writable: false);
        key.ShouldNotBeNull();

        // Without the protected flag the key re-inherits HKLM\SOFTWARE's read-for-Users entry
        // the moment anybody edits that ACL, which is the same failure arriving later.
        key.GetAccessControl(AccessControlSections.Access)
            .AreAccessRulesProtected.ShouldBeTrue();
    }

    [Fact]
    public void ALooseKeyIsFoundAndTightened()
    {
        RequireAdmin();

        // Exactly the state every machine installed before this fix is in: the key exists,
        // carries a value, and inherits its parent.
        using (var key = Registry.LocalMachine.CreateSubKey(_keyPath, writable: true))
        {
            key.SetValue(MachineEntropy.ValueName, new byte[32], RegistryValueKind.Binary);
        }

        AllowedSids().ShouldContain(Sddl.WellKnown.Users, "the premise: it inherits HKLM\\SOFTWARE");

        MachineEntropy.ReadOrCreate(out var state, _keyPath);

        state.ShouldBe(EntropyKeyState.Rehardened);
        AllowedSids().ShouldNotContain(Sddl.WellKnown.Users);
    }

    [Fact]
    public void RehardeningKeepsTheExistingValue()
    {
        RequireAdmin();

        // The repair must never look like a rotation. Replacing the entropy would silently make
        // every stored secret undecryptable, which is a far worse outcome than the exposure.
        var original = new byte[32];
        Random.Shared.NextBytes(original);

        using (var key = Registry.LocalMachine.CreateSubKey(_keyPath, writable: true))
        {
            key.SetValue(MachineEntropy.ValueName, original, RegistryValueKind.Binary);
        }

        var after = MachineEntropy.ReadOrCreate(out var state, _keyPath);

        state.ShouldBe(EntropyKeyState.Rehardened);
        after.ShouldBe(original);
    }

    [Fact]
    public void ASoundKeyIsLeftAlone()
    {
        RequireAdmin();

        MachineEntropy.ReadOrCreate(out _, _keyPath);
        var first = MachineEntropy.Read(_keyPath);

        var second = MachineEntropy.ReadOrCreate(out var state, _keyPath);

        state.ShouldBe(EntropyKeyState.Sound);
        second.ShouldBe(first);
    }

    [Fact]
    public void HardeningAKeyThatIsNotThereSaysSoRatherThanCreatingOne()
    {
        RequireAdmin();

        MachineEntropy.Harden(_keyPath).ShouldBe(EntropyKeyState.Absent);
        MachineEntropy.Read(_keyPath).ShouldBeNull();
    }
}
