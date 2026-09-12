using System.Security.AccessControl;
using System.Security.Principal;
using Shouldly;
using WinLogRotate.Core;
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
    [Fact]
    public void APerUserDirectoryIsRefusedButNotReportedAsAFault()
    {
        WindowsOnly.Require();

        // A per-user installation keeps its configuration in the user's own profile, which that
        // user can necessarily write. Hooks must still be refused - knowing why a directory is
        // writable does not make executing its contents safer - but there is nothing to repair.
        MakeItLookLikeProgramData(_root);
        var confd = _root.CreateSubdirectory("conf.d");

        var finding = ConfDirGuard.Verify(confd.FullName, scope: InstallScope.PerUser);

        finding.HooksAllowed.ShouldBeFalse();
        finding.ExpectedForScope.ShouldBeTrue();
        finding.Explanation.ShouldNotBeNull().ShouldContain("per-user");
    }

    [Fact]
    public void ThePerUserRemedyDoesNotTellAnyoneToRunIcacls()
    {
        WindowsOnly.Require();

        // The remedy that used to be offered would have applied a descriptor granting Users
        // read-only to the owner's own AppData folder, taking away their write access to their
        // own jobs. Worse than the hooks they were not getting.
        MakeItLookLikeProgramData(_root);
        var confd = _root.CreateSubdirectory("conf.d");

        var finding = ConfDirGuard.Verify(confd.FullName, scope: InstallScope.PerUser);

        finding.FixCommand.ShouldNotBeNull().ShouldNotContain("icacls");
    }

    [Fact]
    public void PortableStaysStrict_BecauseThatIsWhatConfigDirProduces()
    {
        WindowsOnly.Require();

        // The installer verifies its own work by shelling "host repair --acl --config-dir ...",
        // and an explicit --config-dir resolves to Portable. Softening that scope would blind
        // the installer to a genuinely open ProgramData.
        MakeItLookLikeProgramData(_root);
        var confd = _root.CreateSubdirectory("conf.d");

        var finding = ConfDirGuard.Verify(confd.FullName, scope: InstallScope.Portable);

        finding.ExpectedForScope.ShouldBeFalse();
        finding.FixCommand.ShouldNotBeNull().ShouldContain("icacls");
    }

    [Fact]
    public void AHardenedDirectoryIsNeverReportedAsScopeExpected()
    {
        WindowsOnly.Require();

        // ExpectedForScope is about explaining a refusal, so it must never appear on a verdict
        // that is not a refusal - otherwise "expected" starts reading as "fine either way".
        var confd = _root.CreateSubdirectory("conf.d");
        Harden(confd);

        var finding = ConfDirGuard.Verify(confd.FullName, scope: InstallScope.PerUser);

        finding.Verdict.ShouldBe(AclVerdict.Hardened, Explain(finding));
        finding.ExpectedForScope.ShouldBeFalse();
    }


    /// <summary>
    /// A job file this product writes is left owned by Administrators.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The premise the gate's per-file owner rule stands on, and it is a premise about Windows,
    /// not about our arithmetic - so it can only be established here. An elevated
    /// administrator's created objects are owned by that account's own SID and not by
    /// <c>BUILTIN\Administrators</c>; this test asserts that after
    /// <see cref="ConfigFileOwner.Claim"/> the file is owned by the group.
    /// </para>
    /// <para>
    /// If this ever starts failing, hooks go off across the estate on the next edit of any job
    /// file, silently. The Linux leg pins that the writers call it; only this pins that calling
    /// it does anything.
    /// </para>
    /// </remarks>
    [Fact]
    public void AClaimedFileIsOwnedByAdministrators()
    {
        WindowsOnly.Require();

        var confd = _root.CreateSubdirectory("conf.d");
        Harden(confd);

        var path = Path.Combine(confd.FullName, "iis.toml");
        File.WriteAllText(path, "name = \"iis\"\n");

        var before = new FileInfo(path)
            .GetAccessControl(AccessControlSections.Owner)
            .GetOwner(typeof(SecurityIdentifier));

        ConfigFileOwner.Claim(path).ShouldBeTrue(
            "the runner is elevated and in the Administrators group, so it can give a file away");

        var after = new FileInfo(path)
            .GetAccessControl(AccessControlSections.Owner)
            .GetOwner(typeof(SecurityIdentifier));

        after.ShouldNotBeNull().Value.ShouldBe(Sddl.WellKnown.Administrators,
            $"the file was owned by {before} when it was created");
    }
}
