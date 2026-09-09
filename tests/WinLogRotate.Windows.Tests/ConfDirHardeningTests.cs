using System.Security.AccessControl;
using System.Security.Principal;
using Shouldly;
using WinLogRotate.Hosting.Security;
using Xunit;

namespace WinLogRotate.Windows.Tests;

/// <summary>
/// The configuration directory hardening, against a parent shaped like ProgramData.
/// </summary>
/// <remarks>
/// This exists because a shipped release got it wrong in a way nothing caught. The installer
/// hardened the data root and the smoke test asserted the data root - and conf.d, the only
/// directory whose permissions actually decide anything, was checked by neither. The run host
/// executes what it finds in conf.d; the root just contains it.
/// </remarks>
[Collection("windows-secrets")]
public sealed class ConfDirHardeningTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("wlr-confdir-");

    public void Dispose()
    {
        try { _root.Delete(recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Gives a directory the entries that make ProgramData dangerous: Users may create things,
    /// and CREATOR OWNER hands the creator Full Control of whatever they create.
    /// </summary>
    private static void MakeItLookLikeProgramData(DirectoryInfo dir)
    {
        var security = dir.GetAccessControl();
        security.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);

        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.CreateDirectories | FileSystemRights.CreateFiles | FileSystemRights.ReadAndExecute,
            InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        // The one that actually causes the damage, and the reason propagation alone is not
        // enough: this is inherit-only, so it does nothing to this directory and grants the
        // creator Full Control over every child.
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.CreatorOwnerSid, null),
            FileSystemRights.FullControl,
            InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit,
            PropagationFlags.InheritOnly,
            AccessControlType.Allow));

        dir.SetAccessControl(security);
    }

    [Fact]
    public void AChildCreatedUnderAProgramDataLikeParentIsNotSafe()
    {
        WindowsOnly.Require();

        // The premise. If this ever stops being true, the hardening below is solving a problem
        // that no longer exists and the reason for it should be revisited, not assumed.
        MakeItLookLikeProgramData(_root);

        var confd = _root.CreateSubdirectory("conf.d");

        var finding = ConfDirGuard.Verify(confd.FullName);

        finding.HooksAllowed.ShouldBeFalse(
            "a directory created under ProgramData inherits CREATOR OWNER, which materialises "
            + $"as Full Control for the creating account. Found {Explain(finding)}");
    }

    /// <summary>Applies the shipped descriptor exactly as HostCommand.ApplyAcl does.</summary>
    private static void Harden(DirectoryInfo dir)
    {
        var security = new DirectorySecurity();
        security.SetSecurityDescriptorSddlForm(Sddl.ConfigDirectory);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        dir.SetAccessControl(security);
    }

    /// <summary>The verdict, plus the entries that produced it - so a failure here says which
    /// principal holds what, rather than only that something is wrong.</summary>
    private static string Explain(AclFinding f) =>
        $"{f.Verdict}: {string.Join(" | ", f.OffendingAces)} -- {f.Explanation}";

    [Fact]
    public void HardeningOnlyTheRootDoesNotSaveTheChild()
    {
        WindowsOnly.Require();

        // Exactly the bug that shipped: the root looks perfect and conf.d is still writable.
        MakeItLookLikeProgramData(_root);
        var confd = _root.CreateSubdirectory("conf.d");

        Harden(_root);

        var rootFinding = ConfDirGuard.Verify(_root.FullName);
        rootFinding.Verdict.ShouldBe(AclVerdict.Hardened, Explain(rootFinding));

        // No assertion on the child's verdict here: whether propagation reaches it is exactly
        // the platform behaviour that cannot be relied upon, and pinning it either way would
        // make this test a statement about Windows rather than about our code. What matters is
        // that applying it explicitly, below, always works.
    }

    [Fact]
    public void ApplyingTheDescriptorToTheChildDirectlyHardensIt()
    {
        WindowsOnly.Require();

        MakeItLookLikeProgramData(_root);
        var confd = _root.CreateSubdirectory("conf.d");

        Harden(confd);

        var finding = ConfDirGuard.Verify(confd.FullName);

        finding.Verdict.ShouldBe(AclVerdict.Hardened, Explain(finding));
        finding.HooksAllowed.ShouldBeTrue();

        // The load-bearing half, asserted directly: without the protected flag a correct DACL
        // re-inherits ProgramData's entries the moment anyone edits them.
        confd.GetAccessControl().AreAccessRulesProtected.ShouldBeTrue();
    }

    [Fact]
    public void TheHardenedDescriptorLeavesUsersReadButNotWrite()
    {
        WindowsOnly.Require();

        // Users read is deliberate - the unelevated read-only GUI is a shipped feature - so a
        // test that just asserted "Users absent" would be wrong about the design.
        var dir = _root.CreateSubdirectory("conf.d");
        Harden(dir);

        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null).Value;
        var rules = dir.GetAccessControl()
            .GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(r => r.IdentityReference.Value == users && r.AccessControlType == AccessControlType.Allow)
            .ToArray();

        rules.ShouldNotBeEmpty("the read-only GUI needs to read this directory unelevated");

        const FileSystemRights Writeish =
            FileSystemRights.WriteData | FileSystemRights.AppendData |
            FileSystemRights.Delete | FileSystemRights.ChangePermissions |
            FileSystemRights.TakeOwnership;

        foreach (var rule in rules)
        {
            (rule.FileSystemRights & Writeish).ShouldBe(default);
        }
    }
}
