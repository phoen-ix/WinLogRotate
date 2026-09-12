using Shouldly;
using WinLogRotate.Core;
using WinLogRotate.Hosting.Security;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// The gate that decides whether a hook may run, judged without a security descriptor in sight.
/// </summary>
/// <remarks>
/// <para>
/// The rule that stops a local user handing themselves SYSTEM lived inside a
/// <c>[SupportedOSPlatform("windows")]</c> class, welded to <c>GetAccessControl</c>. It could
/// therefore only ever be executed by windows-2025, which is why it stayed a
/// <i>directory</i> check for six milestones while the thing actually executed as SYSTEM was a
/// <i>file</i> - and why the four ordinary ways reading a descriptor can fail were never
/// considered at all.
/// </para>
/// <para>
/// <see cref="AclJudgement"/> and <c>ConfigSurfaceGuard</c> are the arithmetic, with the syscall
/// behind a delegate, so all of it runs on both legs. The projection from a real
/// <c>FileSecurity</c> into a <c>Subject</c> is still windows-only, and
/// <c>ConfDirHardeningTests</c> is still the only judge of it.
/// </para>
/// </remarks>
public sealed class ConfigSurfaceGuardTests
{
    private const string Everyone = "S-1-1-0";
    private const string Administrators = "S-1-5-32-544";
    private const string LocalSystem = "S-1-5-18";
    private const string LocalUser = "S-1-5-21-1-2-3-1001";

    private static AclJudgement.Subject Clean(string path, bool isDirectory = true) => new(
        path,
        isDirectory,
        Administrators,
        "BUILTIN\\Administrators",
        [
            new AclJudgement.Ace(LocalSystem, 0x1F01FF, "NT AUTHORITY\\SYSTEM : FullControl"),
            new AclJudgement.Ace(Administrators, 0x1F01FF, "BUILTIN\\Administrators : FullControl"),
            new AclJudgement.Ace("S-1-5-32-545", 0x1200A9, "BUILTIN\\Users : ReadAndExecute"),
        ],
        Protected: true);

    /// <summary>Answers from a dictionary, and null for anything it was not given.</summary>
    private static ConfigSurfaceGuard.Descriptor Reading(
        params (string Path, AclJudgement.Subject? Subject)[] known) =>
        (path, _) => known.FirstOrDefault(k => k.Path == path).Subject;

    private static AclFinding Verify(
        ConfigSurfaceGuard.Descriptor read, params (string Path, bool IsDirectory)[] surface) =>
        ConfigSurfaceGuard.Verify(surface, ConfigSurfaceGuard.Trusted(null), read);

    /// <summary>A hardened directory permits hooks. The floor under everything below.</summary>
    [Fact]
    public void AHardenedDirectoryPermitsHooks()
    {
        var finding = Verify(Reading((@"C:\pd\conf.d", Clean(@"C:\pd\conf.d"))), (@"C:\pd\conf.d", true));

        finding.Verdict.ShouldBe(AclVerdict.Hardened);
        finding.HooksAllowed.ShouldBeTrue();
    }

    /// <summary>
    /// A descriptor that cannot be read refuses the run rather than crashing it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>GetAccessControl</c> throws for four ordinary reasons on a live installation - the
    /// path was replaced between being enumerated and being read, which is what an editor's
    /// save looks like; it became unreachable; the descriptor needs a privilege this process
    /// does not hold; or the account may not read it, which is what a correctly protected path
    /// looks like from an unelevated one. Every one of those escaped to
    /// <c>CommandContext.Guarded</c> and came back as <c>LR1006</c>, exit 4, <i>"This is a
    /// defect"</i>, about a machine that was merely busy.
    /// </para>
    /// <para>
    /// It is <see cref="AclVerdict.Unknown"/> now, which refuses: the guard is biased toward
    /// refusing, and a hook is worth less than a wrong "yes". <c>Unknown</c> has been documented
    /// as "checked, and the answer could not be established" since it was written and was
    /// reachable only when the directory did not exist.
    /// </para>
    /// </remarks>
    [Fact]
    public void ADescriptorThatCannotBeReadRefusesTheRun()
    {
        var finding = Verify(Reading(), (@"C:\pd\conf.d", true));

        finding.Verdict.ShouldBe(AclVerdict.Unknown);
        finding.HooksAllowed.ShouldBeFalse();
        finding.Path.ShouldBe(@"C:\pd\conf.d");
        finding.FixCommand.ShouldNotBeNull();
    }

    /// <summary>An owner who is not an administrator is a write grant wearing a disguise.</summary>
    [Fact]
    public void ANonAdministratorOwnerRefusesTheRun()
    {
        var subject = Clean(@"C:\pd\conf.d") with { OwnerSid = LocalUser, OwnerDescribe = "CONTOSO\\dana" };

        var finding = Verify(Reading((@"C:\pd\conf.d", subject)), (@"C:\pd\conf.d", true));

        finding.Verdict.ShouldBe(AclVerdict.LooseOwner);
        finding.Explanation.ShouldNotBeNull().ShouldContain("CONTOSO\\dana");
    }

    /// <summary>
    /// A generic-rights grant to Everyone is seen, which is the whole of the arithmetic this
    /// seam exists to expose to a test.
    /// </summary>
    [Fact]
    public void AWorldWritableDirectoryRefusesTheRun()
    {
        var subject = Clean(@"C:\pd\conf.d");
        var loose = subject with
        {
            Allow = [.. subject.Allow, new AclJudgement.Ace(Everyone, 0x10000000, "Everyone : GenericAll")],
        };

        var finding = Verify(Reading((@"C:\pd\conf.d", loose)), (@"C:\pd\conf.d", true));

        finding.Verdict.ShouldBe(AclVerdict.LooseWritable);
        finding.OffendingAces.ShouldHaveSingleItem().ShouldBe("Everyone : GenericAll");
    }

    /// <summary>
    /// The outermost refusal is the one reported, however many there are further in.
    /// </summary>
    /// <remarks>
    /// Not a severity ranking. Repairing a job file inside a directory a local user can write
    /// repairs nothing, so the container has to be the answer given first or the operator fixes
    /// the wrong thing and is told it is still broken.
    /// </remarks>
    [Fact]
    public void TheOutermostRefusalIsTheOneReported()
    {
        var loose = Clean(@"C:\pd") with { OwnerSid = LocalUser, OwnerDescribe = "CONTOSO\\dana" };
        var alsoLoose = Clean(@"C:\pd\conf.d") with { OwnerSid = LocalUser, OwnerDescribe = "CONTOSO\\dana" };

        var finding = Verify(
            Reading((@"C:\pd", loose), (@"C:\pd\conf.d", alsoLoose)),
            (@"C:\pd", true),
            (@"C:\pd\conf.d", true));

        finding.Path.ShouldBe(@"C:\pd");
    }

    /// <summary>
    /// A directory must sever inheritance; a file inside one must not be required to.
    /// </summary>
    /// <remarks>
    /// The repair re-enables inheritance on each job file on purpose, so that what a child gets
    /// stays exactly the rule when the rule changes. Demanding <c>D:P</c> per file would refuse
    /// precisely the state the repair leaves behind - the guard would condemn its own fix.
    /// </remarks>
    [Fact]
    public void OnlyADirectoryHasToSeverInheritance()
    {
        var directory = Clean(@"C:\pd\conf.d") with { Protected = false };
        var file = Clean(@"C:\pd\conf.d\app.toml", isDirectory: false) with { Protected = false };

        Verify(Reading((@"C:\pd\conf.d", directory)), (@"C:\pd\conf.d", true))
            .Verdict.ShouldBe(AclVerdict.Inherited);

        Verify(Reading((@"C:\pd\conf.d\app.toml", file)), (@"C:\pd\conf.d\app.toml", false))
            .Verdict.ShouldBe(AclVerdict.Hardened);
    }

    /// <summary>
    /// A job file owned by a local user refuses the run, even when conf.d is spotless.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The defect, stated as a rule. The gate inspected one <c>DirectoryInfo</c>; the thing
    /// executed as SYSTEM is the file. Rewriting a directory's DACL recomputes only what a child
    /// inherits - it changes neither a child's explicit entries nor a child's owner, and an
    /// owner holds implicit WRITE_DAC. ProgramData's <c>CREATOR OWNER</c> makes a local user the
    /// owner of anything they drop into conf.d, so after <c>winlogrotate host repair --acl</c>
    /// the guard said <c>Hardened</c>, <c>doctor</c> printed that same command as the fix, and
    /// the attacker went on editing their own file's <c>postrotate</c>.
    /// </para>
    /// <para>
    /// The <c>Path</c> assertion is not decoration: without it this passes as soon as anything
    /// earlier in the surface refuses, and the file loop could be deleted whole.
    /// </para>
    /// </remarks>
    [Fact]
    public void AJobFileOwnedByALocalUserRefusesTheRunEvenWhenConfDIsHardened()
    {
        var file = Clean(@"C:\pd\conf.d\app.toml", isDirectory: false) with
        {
            OwnerSid = LocalUser,
            OwnerDescribe = "CONTOSO\\dana",
            Protected = false,
        };

        var finding = Verify(
            Reading(
                (@"C:\pd", Clean(@"C:\pd")),
                (@"C:\pd\conf.d", Clean(@"C:\pd\conf.d")),
                (@"C:\pd\conf.d\app.toml", file)),
            (@"C:\pd", true),
            (@"C:\pd\conf.d", true),
            (@"C:\pd\conf.d\app.toml", false));

        finding.HooksAllowed.ShouldBeFalse();
        finding.Verdict.ShouldBe(AclVerdict.LooseOwner);
        finding.Path.ShouldEndWith("app.toml");
    }

    /// <summary>
    /// config.toml is in the surface, because <c>[defaults]</c> is where a hook is written once
    /// and inherited by every job.
    /// </summary>
    [Fact]
    public void AConfigTomlAnyoneCanWriteRefusesTheRunEvenWhenConfDIsClean()
    {
        var subject = Clean(@"C:\pd\config.toml", isDirectory: false);
        var loose = subject with
        {
            Allow = [.. subject.Allow, new AclJudgement.Ace(Everyone, 0x10000000, "Everyone : GenericAll")],
        };

        var finding = Verify(
            Reading(
                (@"C:\pd", Clean(@"C:\pd")),
                (@"C:\pd\conf.d", Clean(@"C:\pd\conf.d")),
                (@"C:\pd\config.toml", loose)),
            (@"C:\pd", true),
            (@"C:\pd\conf.d", true),
            (@"C:\pd\config.toml", false));

        finding.HooksAllowed.ShouldBeFalse();
        finding.Path.ShouldEndWith("config.toml");
    }

    /// <summary>
    /// The surface is the root, conf.d, config.toml, and every job file - in that order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The list is the substance of the gate. Its entire content was one directory, which is why
    /// the file actually executed as SYSTEM was judged by nobody; asserting the judgement while
    /// leaving the list unpinned would restate the defect in a place a test cannot see.
    /// </para>
    /// <para>
    /// Outermost first, because the first refusal is the one reported and fixing a job file
    /// inside a directory an ordinary account can write fixes nothing. Named exactly, so that
    /// dropping the files or dropping <c>config.toml</c> turns this red rather than quietly
    /// narrowing what the product looks at.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheSurfaceIsEveryPathARunTakesItsConfigurationFrom()
    {
        var root = Directory.CreateTempSubdirectory("winlogrotate-surface-");

        try
        {
            var paths = new InstallPaths { Scope = InstallScope.Portable, Root = root.FullName };

            Directory.CreateDirectory(paths.ConfigDirectory);
            File.WriteAllText(paths.ConfigFile, "schema = 1\n");
            File.WriteAllText(Path.Combine(paths.ConfigDirectory, "web.toml"), "");
            File.WriteAllText(Path.Combine(paths.ConfigDirectory, "apache.toml"), "");
            File.WriteAllText(Path.Combine(paths.ConfigDirectory, "broken.toml.bad"), "");

            ConfigSurfaceGuard.SurfaceOf(paths).ShouldBe([
                (paths.Root, true),
                (paths.ConfigDirectory, true),
                (paths.ConfigFile, false),
                (Path.Combine(paths.ConfigDirectory, "apache.toml"), false),
                (Path.Combine(paths.ConfigDirectory, "web.toml"), false),
            ]);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>
    /// A portable copy with no config.toml is judged on what is there, not on what is not.
    /// </summary>
    [Fact]
    public void AConfigurationFileThatIsNotThereIsNotJudged()
    {
        var root = Directory.CreateTempSubdirectory("winlogrotate-surface-");

        try
        {
            var paths = new InstallPaths { Scope = InstallScope.Portable, Root = root.FullName };
            Directory.CreateDirectory(paths.ConfigDirectory);

            ConfigSurfaceGuard.SurfaceOf(paths)
                .ShouldBe([(paths.Root, true), (paths.ConfigDirectory, true)]);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
