using System.Security.AccessControl;
using System.Security.Principal;
using Shouldly;
using WinLogRotate.Hosting.Security;
using Xunit;

namespace WinLogRotate.Windows.Tests;

/// <summary>
/// The secrets file must not be readable by ordinary users, on a machine where the directory
/// containing it deliberately is.
/// </summary>
[Collection("windows-secrets")]
public sealed class SecretsAclTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("wlr-win-acl-");

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); } catch (IOException) { }
    }

    private string File(string name) => Path.Combine(_dir.FullName, name);

    /// <summary>Gives the directory the descriptor the installer applies to ProgramData.</summary>
    private void HardenDirectoryLikeTheInstaller()
    {
        var security = new DirectorySecurity();
        security.SetSecurityDescriptorSddlForm(Sddl.ConfigDirectory);
        _dir.SetAccessControl(security);
    }

    [Fact]
    public void AFileCreatedInTheConfigDirectoryInheritsUsersRead()
    {
        WindowsOnly.Require();

        // This is the hazard the whole design turns on, asserted directly rather than assumed.
        // Sddl.ConfigDirectory grants BUILTIN\Users read with OICI, so anything created there
        // arrives readable by every local user - which is correct for a job file and
        // catastrophic for a credential.
        HardenDirectoryLikeTheInstaller();

        var path = File("inherits.txt");
        System.IO.File.WriteAllText(path, "x");

        var rules = new FileInfo(path).GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));

        var users = rules.Cast<FileSystemAccessRule>()
            .Any(r => r.AccessControlType == AccessControlType.Allow
                      && r.IdentityReference.Value == Sddl.WellKnown.Users);

        users.ShouldBeTrue(
            "if this ever stops being true, SecretsFileGuard is solving a problem that no "
            + "longer exists - and the reason for it should be revisited rather than assumed");
    }

    [Fact]
    public void ApplyingTheSecretsDescriptorRemovesUsersEntirely()
    {
        WindowsOnly.Require();

        HardenDirectoryLikeTheInstaller();

        var path = File("secrets.dat");
        System.IO.File.WriteAllText(path, "{}");
        SecretsFileGuard.Apply(path);

        var rules = new FileInfo(path).GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(r => r.AccessControlType == AccessControlType.Allow)
            .Select(r => r.IdentityReference.Value)
            .ToArray();

        rules.ShouldNotContain(Sddl.WellKnown.Users);
        rules.ShouldContain(Sddl.WellKnown.LocalSystem);
        rules.ShouldContain(Sddl.WellKnown.Administrators);
    }

    [Fact]
    public void VerifyAgreesWithApply()
    {
        WindowsOnly.Require();

        HardenDirectoryLikeTheInstaller();

        var path = File("secrets.dat");
        System.IO.File.WriteAllText(path, "{}");
        SecretsFileGuard.Apply(path);

        SecretsFileGuard.Verify(path).Verdict.ShouldBe(SecretsAclVerdict.Hardened);
    }

    [Fact]
    public void VerifyCatchesAnUnhardenedFile()
    {
        WindowsOnly.Require();

        HardenDirectoryLikeTheInstaller();

        var path = File("secrets.dat");
        System.IO.File.WriteAllText(path, "{}");

        var finding = SecretsFileGuard.Verify(path);

        finding.Verdict.ShouldBe(SecretsAclVerdict.TooOpen);
        finding.IsSafe.ShouldBeFalse();
        finding.Remedy.ShouldNotBeNull().ShouldContain("icacls");
    }

    [Fact]
    public void AMissingFileIsNotAFinding()
    {
        WindowsOnly.Require();

        var finding = SecretsFileGuard.Verify(File("nothing-here.dat"));

        finding.Verdict.ShouldBe(SecretsAclVerdict.Missing);
        finding.IsSafe.ShouldBeTrue();
    }

    [Fact]
    public void APerUserStorePermitsItsOwner()
    {
        WindowsOnly.Require();

        HardenDirectoryLikeTheInstaller();

        var me = WindowsIdentity.GetCurrent().User!.Value;
        var path = File("secrets.dat");
        System.IO.File.WriteAllText(path, "{}");
        SecretsFileGuard.Apply(path, ownerSid: me);

        // Verify must be told which shape it expected, or it reports a false finding on every
        // per-user install.
        SecretsFileGuard.Verify(path, ownerSid: me).Verdict.ShouldBe(SecretsAclVerdict.Hardened);
        SecretsFileGuard.Verify(path).Verdict.ShouldBe(SecretsAclVerdict.TooOpen);
    }
}
